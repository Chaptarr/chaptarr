using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDeleteMediaFiles
    {
        void DeleteTrackFile(Author author, BookFile bookFile);
        void DeleteTrackFile(BookFile bookFile, string subfolder = "");
    }

    public class MediaFileDeletionService : IDeleteMediaFiles,
                                            IHandle<AuthorDeletedEvent>,
                                            IHandleAsync<AuthorDeletedEvent>,
                                            IHandleAsync<BookDeletedEvent>,
                                            IHandle<BookFileDeletedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAuthorService _authorService;
        private readonly IConfigService _configService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibre;
        private readonly Logger _logger;

        public MediaFileDeletionService(IDiskProvider diskProvider,
                                        IRecycleBinProvider recycleBinProvider,
                                        IMediaFileService mediaFileService,
                                        IAuthorService authorService,
                                        IConfigService configService,
                                        IEventAggregator eventAggregator,
                                        IRootFolderService rootFolderService,
                                        ICalibreProxy calibre,
                                        Logger logger)
        {
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _authorService = authorService;
            _configService = configService;
            _eventAggregator = eventAggregator;
            _rootFolderService = rootFolderService;
            _calibre = calibre;
            _logger = logger;
        }

        public void DeleteTrackFile(Author author, BookFile bookFile)
        {
            var fullPath = bookFile.Path;

            // The file's own path is the only reliable authority for which configured root holds it.
            // Chaptarr has per-media-type roots, so the author's stored path is only ever one of them,
            // and Organize rewrites a file's path after that author path was recorded. Media type does
            // not prove containment either: a colocated ebook can live under the audiobook root.
            var rootFolder = _rootFolderService.GetBestRootFolder(fullPath);

            if (rootFolder == null)
            {
                _logger.Warn("Book file ({0}) is not inside any configured root folder.", fullPath);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Book file ({0}) is not inside any configured root folder.", fullPath);
            }

            if (!_diskProvider.FolderExists(rootFolder.Path))
            {
                _logger.Warn("Root folder ({0}) doesn't exist.", rootFolder.Path);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Root folder ({0}) doesn't exist.", rootFolder.Path);
            }

            if (_diskProvider.GetDirectories(rootFolder.Path).Empty())
            {
                _logger.Warn("Root folder ({0}) is empty.", rootFolder.Path);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Root folder ({0}) is empty.", rootFolder.Path);
            }

            var fileFolder = _diskProvider.GetParentFolder(fullPath);

            // A file sitting directly in the root has no subfolder; GetRelativePath treats equal paths
            // as unrelated and would throw.
            var subfolder = rootFolder.Path.PathEquals(fileFolder)
                ? string.Empty
                : rootFolder.Path.GetRelativePath(fileFolder);

            DeleteTrackFile(bookFile, subfolder, rootFolder);
        }

        public void DeleteTrackFile(BookFile bookFile, string subfolder = "")
        {
            DeleteTrackFile(bookFile, subfolder, null);
        }

        private void DeleteTrackFile(BookFile bookFile, string subfolder, RootFolder rootFolder)
        {
            var fullPath = bookFile.Path;

            if (_diskProvider.FileExistsCanonical(fullPath))
            {
                _logger.Info("Deleting book file: {0}", fullPath);
                DeleteFile(bookFile, subfolder, rootFolder);
            }

            // Delete the track file from the database to clean it up even if the file was already deleted
            _mediaFileService.Delete(bookFile, DeleteMediaFileReason.Manual);

            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private void DeleteFile(BookFile bookFile, string subfolder = "", RootFolder rootFolder = null)
        {
            rootFolder ??= _rootFolderService.GetBestRootFolder(bookFile.Path);

            // Outside the try on purpose: the catch below turns everything into a 500, and this is a
            // deliberate refusal. Without a containing root there is no Calibre setting to consult, so
            // recycling the file would be guessing at the one decision we cannot make safely.
            if (rootFolder == null)
            {
                _logger.Warn("Book file ({0}) is not inside any configured root folder.", bookFile.Path);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Book file ({0}) is not inside any configured root folder.", bookFile.Path);
            }

            var isCalibre = rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null;

            try
            {
                if (!isCalibre)
                {
                    if (_diskProvider.FileExistsCanonical(bookFile.Path))
                    {
                        _recycleBinProvider.DeleteFile(bookFile.Path, subfolder);
                    }
                }
                else
                {
                    if (bookFile.CalibreId == 0)
                    {
                        bookFile.CalibreId = _calibre.GetCalibreIdForPath(bookFile.Path, rootFolder.CalibreSettings);
                    }

                    if (bookFile.CalibreId != 0)
                    {
                        var format = System.IO.Path.GetExtension(bookFile.Path).TrimStart('.');
                        var calibreBook = _calibre.GetBook(bookFile.CalibreId, rootFolder.CalibreSettings);

                        if (format.IsNotNullOrWhiteSpace() && calibreBook?.Formats?.Count > 1)
                        {
                            // Deleting one of several formats must not remove the whole book
                            _calibre.RemoveFormats(bookFile.CalibreId, new[] { format }, rootFolder.CalibreSettings);
                        }
                        else
                        {
                            _calibre.DeleteBook(bookFile, rootFolder.CalibreSettings);
                        }
                    }
                    else if (_diskProvider.FileExistsCanonical(bookFile.Path))
                    {
                        _logger.Warn("Calibre does not know book file ({0}), deleting it from disk instead.", bookFile.Path);
                        _recycleBinProvider.DeleteFile(bookFile.Path, subfolder);
                    }
                }
            }
            catch (Exception e)
            {
                if (bookFile?.Path != null && !_diskProvider.FileExistsCanonical(bookFile.Path))
                {
                    _logger.Debug(e, "Book file was already removed: {0}", bookFile.Path);
                    return;
                }

                _logger.Error(e, "Unable to delete book file");
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete book file");
            }
        }

        // Shared by both handlers below so a Calibre-routed path gets exactly the same refusal
        // checks as a plain recycle-bin path - it used to skip them entirely, which only mattered
        // for the single legacy Path but now applies to up to three paths per author.
        // allAuthorsCache is fetched lazily, once per method call, only if some path actually needs
        // it - an author whose only path(s) are already refused as unsafe should never touch it.
        // Uses AllAuthorMediaPaths (Path + AudiobookPath + EbookPath), not the legacy single-path
        // AllAuthorPaths - otherwise a dual-format author's AudiobookPath could collide with another
        // author's separately-configured EbookPath and never be caught.
        private bool ShouldRefuseToDeletePath(string path, Author author, ref List<KeyValuePair<int, string>> allAuthorsCache)
        {
            if (IsPathUnsafeToDelete(path))
            {
                _logger.Error("Refusing to delete '{0}' for author '{1}' because it matches or contains a configured root folder. This indicates the author path was misconfigured and deleting would risk data loss.",
                    path, author.Name);
                return true;
            }

            allAuthorsCache ??= _authorService.AllAuthorMediaPaths();

            foreach (var s in allAuthorsCache)
            {
                if (s.Key == author.Id)
                {
                    continue;
                }

                if (path.IsParentPath(s.Value))
                {
                    _logger.Error("Author path: '{0}' is a parent of another author, not deleting files.", path);
                    return true;
                }

                if (path.PathEquals(s.Value))
                {
                    _logger.Error("Author path: '{0}' is the same as another author, not deleting files.", path);
                    return true;
                }
            }

            return false;
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorDeletedEvent message)
        {
            if (!message.DeleteFiles)
            {
                return;
            }

            var author = message.Author;
            List<KeyValuePair<int, string>> allAuthors = null;

            foreach (var path in ExtraFilePathHelper.GetAuthorBasePaths(author))
            {
                if (ShouldRefuseToDeletePath(path, author, ref allAuthors))
                {
                    continue;
                }

                var rootFolder = _rootFolderService.GetBestRootFolder(path);
                if (rootFolder?.IsCalibreLibrary != true || rootFolder.CalibreSettings == null)
                {
                    continue;
                }

                DeleteCalibreBooksUnderPath(path, rootFolder.CalibreSettings, author);
            }
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            if (!message.DeleteFiles)
            {
                return;
            }

            var author = message.Author;
            List<KeyValuePair<int, string>> allAuthors = null;

            foreach (var path in ExtraFilePathHelper.GetAuthorBasePaths(author))
            {
                if (ShouldRefuseToDeletePath(path, author, ref allAuthors))
                {
                    continue;
                }

                var rootFolder = _rootFolderService.GetBestRootFolder(path);
                if (rootFolder?.IsCalibreLibrary == true)
                {
                    // The synchronous handler deletes tracked Calibre records before the author
                    // rows are removed. Do not repeat the external deletion or recycle the library folder.
                    continue;
                }

                try
                {
                    if (_diskProvider.FolderExists(path))
                    {
                        _recycleBinProvider.DeleteFolder(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to delete '{0}' for author '{1}'.", path, author.Name);
                }
            }

            // Flush external library refresh queues once even when a path is refused or a disk
            // operation fails; queued handlers are harmless when there is nothing to process.
            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private void DeleteCalibreBooksUnderPath(string path, CalibreSettings settings, Author author)
        {
            if (_calibre == null || settings == null)
            {
                return;
            }

            var files = _mediaFileService.GetFilesWithBasePath(path) ?? new List<BookFile>();
            foreach (var file in files.Where(file => file.CalibreId == 0 && file.Path.IsNotNullOrWhiteSpace()))
            {
                try
                {
                    file.CalibreId = _calibre.GetCalibreIdForPath(file.Path, settings);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not resolve Calibre id for {0}", file.Path);
                }
            }

            var calibreBooks = files
                .Where(file => file.CalibreId > 0)
                .GroupBy(file => file.CalibreId)
                .Select(group => group.First())
                .ToList();

            if (!calibreBooks.Any())
            {
                return;
            }

            try
            {
                _logger.Info("Deleting {0} Calibre books for author {1} under {2}", calibreBooks.Count, author?.Name, path);
                _calibre.DeleteBooks(calibreBooks, settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete Calibre books at '{0}' for author '{1}'.", path, author?.Name);
            }
        }

        private bool IsPathUnsafeToDelete(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return true;
            }

            try
            {
                var rootFolders = _rootFolderService.All();

                // Never delete a configured root folder (or a parent of one) as part of author deletion.
                // If this triggers, the author path is corrupted (e.g., set to the root folder path).
                if (rootFolders.Any(r => r.Path.PathEquals(path)))
                {
                    return true;
                }

                if (rootFolders.Any(r => path.IsParentPath(r.Path)))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to validate delete path '{0}' against configured root folders; refusing deletion to be safe", path);
                return true;
            }

            return false;
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            if (!message.DeleteFiles || message.PartOfAuthorDelete)
            {
                return;
            }

            // BookService snapshots the files onto the event before deleting the row, and
            // MediaFileService purges those rows on this same event — asynchronously. Re-querying
            // here races that purge and can come back empty, leaving the files on disk. Prefer the
            // snapshot, exactly as MediaFileService does, and only fall back to a query for callers
            // that published without one.
            var files = message.Book?.BookFiles;

            if (files == null || files.Count == 0)
            {
                files = _mediaFileService.GetFilesByBook(message.Book.Id);
            }

            var folders = new List<string>();

            foreach (var file in files)
            {
                CollectFolder(folders, file?.Path?.GetParentPath());

                // No BookFileDeletedEvent is published from here, so the replica cleanup that hangs
                // off that event never runs for a whole-book delete. Colocated ebook copies would be
                // left behind — call it directly, exactly as the per-file handler does.
                foreach (var replicaPath in file?.ReplicaPaths ?? new List<string>())
                {
                    CollectFolder(folders, replicaPath?.GetParentPath());
                }

                DeleteManagedEbookReplicas(file);
                DeleteFile(file);
            }

            // Per-file cleanup runs while the book's other files are still there, so the folder is
            // only ever empty once the whole book is gone. Sweep once at the end.
            var author = message.Book?.Author ?? files.FirstOrDefault()?.Author;

            foreach (var folder in folders)
            {
                CleanupEmptyFolders(author, folder);
            }

            // Providers queue work on OnBookDelete and drain it on this event; author
            // deletes already publish it.
            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private static void CollectFolder(List<string> folders, string folder)
        {
            if (folder.IsNotNullOrWhiteSpace() && !folders.Any(f => f.PathEquals(folder)))
            {
                folders.Add(folder);
            }
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(BookFileDeletedEvent message)
        {
            DeleteManagedEbookReplicas(message.BookFile);

            if (message.Reason == DeleteMediaFileReason.Upgrade)
            {
                return;
            }

            CleanupEmptyFolders(message.BookFile.Author, message.BookFile.Path.GetParentPath());
        }

        /// <summary>
        /// Removes folders emptied by a deletion, walking up from the file's own folder to the
        /// author root that contains it. RemoveEmptySubfolders only removes CHILDREN of the path it
        /// is given, so a book folder can only be cleaned from its parent — cleaning the book folder
        /// itself leaves it standing forever. Bounded by the audiobook/ebook root the file actually
        /// lives under, so an ebook deletion can never reach into the audiobook tree.
        /// </summary>
        private void CleanupEmptyFolders(Author author, string startingFolder)
        {
            if (!_configService.DeleteEmptyFolders || author == null || startingFolder.IsNullOrWhiteSpace())
            {
                return;
            }

            var basePath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                .Where(p => p.IsNotNullOrWhiteSpace() && (p.IsParentPath(startingFolder) || p.PathEquals(startingFolder)))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();

            if (basePath.IsNullOrWhiteSpace())
            {
                return;
            }

            var folder = startingFolder;

            while (basePath.IsParentPath(folder))
            {
                if (_diskProvider.FolderExists(folder))
                {
                    _diskProvider.RemoveEmptySubfolders(folder);
                }

                folder = folder.GetParentPath();
            }

            if (_diskProvider.FolderExists(basePath))
            {
                _diskProvider.RemoveEmptySubfolders(basePath);

                if (_diskProvider.GetFiles(basePath, true).Empty())
                {
                    _diskProvider.DeleteFolder(basePath, true);
                }
            }
        }

        private void DeleteManagedEbookReplicas(BookFile bookFile)
        {
            if (bookFile?.ReplicaPaths == null || bookFile.ReplicaPaths.Count == 0)
            {
                return;
            }

            foreach (var replicaPath in bookFile.ReplicaPaths
                         .Where(p => p.IsNotNullOrWhiteSpace())
                         .Distinct(PathEqualityComparer.Instance)
                         .Where(p => p.PathNotEquals(bookFile.Path)))
            {
                try
                {
                    if (_diskProvider.FileExistsCanonical(replicaPath))
                    {
                        _logger.Info("Deleting managed ebook replica: {0}", replicaPath);
                        _recycleBinProvider.DeleteFile(replicaPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to delete managed ebook replica: {0}", replicaPath);
                }
            }
        }
    }
}
