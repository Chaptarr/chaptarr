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

            var authorPath = IsNull(bookFile.MediaType) ? author.Path
                : bookFile.MediaType == "ebook" ? author.EbookRootFolderPath
                : author.AudiobookRootFolderPath;

            var rootFolder = _diskProvider.GetParentFolder(authorPath);

            if (!_diskProvider.FolderExists(rootFolder))
            {
                _logger.Warn("Author's root folder ({0}) doesn't exist.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Author's root folder ({0}) doesn't exist.", rootFolder);
            }

            if (_diskProvider.GetDirectories(rootFolder).Empty())
            {
                _logger.Warn("Author's root folder ({0}) is empty.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Author's root folder ({0}) is empty.", rootFolder);
            }

            if (_diskProvider.FolderExists(authorPath))
            {
                var subfolder = _diskProvider.GetParentFolder(authorPath).GetRelativePath(_diskProvider.GetParentFolder(fullPath));
                DeleteTrackFile(bookFile, subfolder);
            }
            else
            {
                // delete from db even if the author folder is missing
                _mediaFileService.Delete(bookFile, DeleteMediaFileReason.Manual);
            }
        }

        public void DeleteTrackFile(BookFile bookFile, string subfolder = "")
        {
            var fullPath = bookFile.Path;

            if (_diskProvider.FileExistsCanonical(fullPath))
            {
                _logger.Info("Deleting book file: {0}", fullPath);
                DeleteFile(bookFile, subfolder);
            }

            // Delete the track file from the database to clean it up even if the file was already deleted
            _mediaFileService.Delete(bookFile, DeleteMediaFileReason.Manual);

            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private void DeleteFile(BookFile bookFile, string subfolder = "")
        {
            var rootFolder = _rootFolderService.GetBestRootFolder(bookFile.Path);
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
                    _calibre.DeleteBook(bookFile, rootFolder.CalibreSettings);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Unable to delete book file");
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete book file");
            }
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var author = message.Author;

                var rootFolder = _rootFolderService.GetBestRootFolder(message.Author.Path);
                var isCalibre = rootFolder?.IsCalibreLibrary == true && rootFolder.CalibreSettings != null;

                if (isCalibre)
                {
                    // use authorId for the query
                    var books = _mediaFileService.GetFilesByAuthor(author.Id);
                    _calibre.DeleteBooks(books, rootFolder.CalibreSettings);
                }
            }
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var author = message.Author;

                var rootFolder = _rootFolderService.GetBestRootFolder(message.Author.Path);
                var isCalibre = rootFolder?.IsCalibreLibrary == true && rootFolder.CalibreSettings != null;

                if (!isCalibre)
                {
                    if (IsPathUnsafeToDelete(author.Path))
                    {
                        _logger.Error("Refusing to delete '{0}' for author '{1}' because it matches or contains a configured root folder. This indicates the author path was misconfigured and deleting would risk data loss.",
                            author.Path, author.Name);
                        _eventAggregator.PublishEvent(new DeleteCompletedEvent());
                        return;
                    }

                    var allAuthors = _authorService.AllAuthorPaths();

                    foreach (var s in allAuthors)
                    {
                        if (s.Key == author.Id)
                        {
                            continue;
                        }

                        if (author.Path.IsParentPath(s.Value))
                        {
                            _logger.Error("Author path: '{0}' is a parent of another author, not deleting files.", author.Path);
                            return;
                        }

                        if (author.Path.PathEquals(s.Value))
                        {
                            _logger.Error("Author path: '{0}' is the same as another author, not deleting files.", author.Path);
                            return;
                        }
                    }

                    if (_diskProvider.FolderExists(message.Author.Path))
                    {
                        _recycleBinProvider.DeleteFolder(message.Author.Path);
                    }

                    _eventAggregator.PublishEvent(new DeleteCompletedEvent());
                }
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
            if (!message.DeleteFiles)
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
