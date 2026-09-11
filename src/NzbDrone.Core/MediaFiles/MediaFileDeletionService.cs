using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
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
        void DeleteAuthorFiles(List<Author> authors);
    }

    public class MediaFileDeletionService : IDeleteMediaFiles,
                                            IHandleAsync<BookDeletedEvent>,
                                            IHandle<BookFileDeletedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAuthorRepository _authorRepository;
        private readonly IExtraFileRepository<MetadataFile> _metadataFiles;
        private readonly IExtraFileRepository<OtherExtraFile> _otherFiles;
        private readonly IConfigService _configService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibre;
        private readonly Logger _logger;

        public MediaFileDeletionService(IDiskProvider diskProvider,
                                        IRecycleBinProvider recycleBinProvider,
                                        IMediaFileService mediaFileService,
                                        IAuthorRepository authorRepository,
                                        IConfigService configService,
                                        IEventAggregator eventAggregator,
                                        IRootFolderService rootFolderService,
                                        ICalibreProxy calibre,
                                        Logger logger,
                                        IExtraFileRepository<MetadataFile> metadataFiles = null,
                                        IExtraFileRepository<OtherExtraFile> otherFiles = null)
        {
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _authorRepository = authorRepository;
            _metadataFiles = metadataFiles;
            _otherFiles = otherFiles;
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
                    _calibre.DeleteBook(bookFile, rootFolder.CalibreSettings);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Unable to delete book file");
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete book file");
            }
        }

        public void DeleteAuthorFiles(List<Author> authors)
        {
            if (authors.Count == 0)
            {
                return;
            }

            var selectedIds = authors.Select(author => author.Id).ToHashSet();
            var inventoryWatch = Stopwatch.StartNew();
            var ownership = _mediaFileService.GetFilePathOwnership();
            var retainedAuthors = _authorRepository.All().Where(author => !selectedIds.Contains(author.Id)).ToList();
            var retainedPaths = ownership.Where(file => file.Author != null && !selectedIds.Contains(file.Author.Id))
                .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetFullPath)
                .ToHashSet(PathEqualityComparer.Instance);
            var retainedBases = retainedAuthors.SelectMany(ExtraFilePathHelper.GetAuthorBasePaths)
                .Select(Path.GetFullPath).ToList();
            var retainedExtras = (_metadataFiles?.All().Cast<ExtraFile>() ?? Enumerable.Empty<ExtraFile>())
                .Concat(_otherFiles?.All().Cast<ExtraFile>() ?? Enumerable.Empty<ExtraFile>())
                .Where(extra => !selectedIds.Contains(extra.AuthorId)).ToLookup(extra => extra.AuthorId);
            foreach (var author in retainedAuthors)
            {
                foreach (var extra in retainedExtras[author.Id])
                {
                    if (extra.RelativePath.IsNullOrWhiteSpace() || Path.IsPathRooted(extra.RelativePath))
                    {
                        continue;
                    }

                    // Ambiguous relative paths cannot authorize deletion of another owner's extras.
                    foreach (var basePath in ExtraFilePathHelper.GetAuthorBasePaths(author))
                    {
                        var path = Path.GetFullPath(Path.Combine(basePath, extra.RelativePath));
                        if (_diskProvider.FileExistsCanonical(path))
                        {
                            retainedPaths.Add(path);
                        }
                    }
                }
            }

            var filesByAuthor = authors.ToDictionary(author => author.Id, author => _mediaFileService.GetFilesByAuthor(author.Id));
            var files = filesByAuthor.Values.SelectMany(items => items)
                .DistinctBy(file => file.Id).ToList();
            _logger.Debug("Author deletion inventory: {0} selected authors, {1} selected files, {2} library file rows, {3} retained physical paths in {4}ms",
                selectedIds.Count, files.Count, ownership.Count, retainedPaths.Count, inventoryWatch.ElapsedMilliseconds);

            // Explicit folder deletion includes untracked and unmapped contents. A real ownership
            // or boundary conflict must refuse the request, not silently turn it into file-only deletion.
            var folders = new List<string>();
            foreach (var folder in authors.SelectMany(ExtraFilePathHelper.GetAuthorBasePaths)
                .Select(Path.GetFullPath).Distinct(PathEqualityComparer.Instance))
            {
                if (IsPathUnsafeToDelete(folder))
                {
                    throw RefuseAuthorDeletion(folder, "the folder is outside configured roots, or is a root or its ancestor");
                }

                var root = _rootFolderService.GetBestRootFolder(folder);
                if (!_diskProvider.FolderExists(root.Path))
                {
                    throw RefuseAuthorDeletion(folder, "the containing root folder is unavailable");
                }

                if (root.IsCalibreLibrary)
                {
                    if (root.CalibreSettings == null)
                    {
                        throw RefuseAuthorDeletion(folder, "the containing Calibre library has no settings");
                    }

                    continue;
                }

                var blockingPath = retainedBases.FirstOrDefault(other => other.PathEquals(folder) ||
                    folder.IsParentPath(other) || other.IsParentPath(folder)) ??
                    retainedPaths.FirstOrDefault(other => folder.PathEquals(other) || folder.IsParentPath(other));
                if (blockingPath != null)
                {
                    throw RefuseAuthorDeletion(folder, "another retained author uses " + blockingPath);
                }

                if (HasDirectoryLink(folder, root.Path) ||
                    (_diskProvider.FolderExists(folder) && ContainsDirectoryLink(folder)))
                {
                    throw RefuseAuthorDeletion(folder, "the folder contains or traverses a directory link");
                }

                folders.Add(folder);
            }

            folders = folders.Where(folder => !folders.Any(parent => parent.PathNotEquals(folder) && parent.IsParentPath(folder))).ToList();

            var exactPaths = new HashSet<string>(PathEqualityComparer.Instance);
            foreach (var file in files)
            {
                foreach (var path in (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                {
                    if (path.IsNotNullOrWhiteSpace() && !retainedPaths.Contains(Path.GetFullPath(path)))
                    {
                        exactPaths.Add(Path.GetFullPath(path));
                    }
                }
            }

            // Snapshot sidecars before dependent rows disappear. Unknown/shared ownership does
            // not authorize removing a lookalike under a preferred media root.
            foreach (var author in authors)
            {
                var ownedFiles = filesByAuthor[author.Id];
                var extras = (_metadataFiles?.GetFilesByAuthor(author.Id).Cast<ExtraFile>() ?? Enumerable.Empty<ExtraFile>())
                    .Concat(_otherFiles?.GetFilesByAuthor(author.Id).Cast<ExtraFile>() ?? Enumerable.Empty<ExtraFile>());
                foreach (var extra in extras)
                {
                    var path = ExtraFilePathHelper.ResolveOwnedSidecarPath(author, extra, ownedFiles, _diskProvider);
                    if (path != null && !retainedPaths.Contains(path) &&
                        !_diskProvider.GetFiles(Path.GetDirectoryName(path), false).Any(other =>
                            MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(other)) && !exactPaths.Contains(other)))
                    {
                        exactPaths.Add(path);
                    }
                }
            }

            // Validate every standalone path before the first disk or Calibre operation. A later
            // configuration refusal must not leave an earlier target already deleted.
            var standalonePaths = exactPaths.Where(path => !folders.Any(folder => folder.IsParentPath(path)))
                .Select(path => (Path: path, Root: _rootFolderService.GetBestRootFolder(path))).ToList();
            foreach (var item in standalonePaths)
            {
                if (item.Root == null || !_diskProvider.FolderExists(item.Root.Path))
                {
                    throw RefuseAuthorDeletion(item.Path, "no available configured root contains the file");
                }

                if (item.Root.IsCalibreLibrary && item.Root.CalibreSettings == null)
                {
                    throw RefuseAuthorDeletion(item.Path, "the containing Calibre library has no settings");
                }

                if (HasDirectoryLink(item.Path, item.Root.Path))
                {
                    throw RefuseAuthorDeletion(item.Path, "the file traverses a directory link");
                }
            }

            // Calibre IDs are library-local; never send one library's IDs to another.
            var calibreFiles = files.Select(file => (File: file, Root: _rootFolderService.GetBestRootFolder(file.Path)))
                .Where(item => item.Root?.IsCalibreLibrary == true && !retainedPaths.Contains(Path.GetFullPath(item.File.Path))).ToList();
            foreach (var group in calibreFiles.GroupBy(item => item.Root.Path, PathEqualityComparer.Instance))
            {
                _calibre.DeleteBooks(group.Select(item => item.File).DistinctBy(file => file.CalibreId).ToList(), group.First().Root.CalibreSettings);
            }

            foreach (var item in standalonePaths.Where(item => !item.Root.IsCalibreLibrary))
            {
                if (_diskProvider.FileExistsCanonical(item.Path))
                {
                    _recycleBinProvider.DeleteFile(item.Path);
                }
            }

            foreach (var folder in folders)
            {
                if (_diskProvider.FolderExists(folder))
                {
                    _recycleBinProvider.DeleteFolder(folder);
                    if (_diskProvider.FolderExists(folder))
                    {
                        throw new IOException("Author folder deletion did not complete: " + folder);
                    }
                }
            }

            foreach (var author in authors)
            {
                foreach (var folder in filesByAuthor[author.Id]
                    .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Select(Path.GetDirectoryName).Distinct(PathEqualityComparer.Instance))
                {
                    if (_rootFolderService.GetBestRootFolder(folder)?.IsCalibreLibrary != true)
                    {
                        CleanupEmptyFolders(author, folder);
                    }
                }
            }

            // Folder contents need no book identity to be deleted. Remove their unmapped inventory
            // only after physical success, without following those rows to paths outside the folders.
            var unmappedFiles = ownership.Where(file => file.Author == null && file.Path.IsNotNullOrWhiteSpace() &&
                folders.Any(folder => folder.IsParentPath(Path.GetFullPath(file.Path)))).ToList();
            _mediaFileService.DeleteRecordsForAuthorDeletion(files.Concat(unmappedFiles).DistinctBy(file => file.Id).ToList());
            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private NzbDroneClientException RefuseAuthorDeletion(string path, string reason)
        {
            var error = new NzbDroneClientException(HttpStatusCode.Conflict, "Cannot delete '{0}': {1}. No files were deleted.", path, reason);
            _logger.Warn(error.Message);
            return error;
        }

        private bool HasDirectoryLink(string path, string root)
        {
            var folder = Path.GetDirectoryName(path);
            while (folder != null && (root.PathEquals(folder) || root.IsParentPath(folder)))
            {
                if (_diskProvider.FolderExists(folder) && _diskProvider.GetFileAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                if (folder.PathEquals(root))
                {
                    break;
                }

                folder = Path.GetDirectoryName(folder);
            }

            return false;
        }

        private bool ContainsDirectoryLink(string folder)
        {
            if (_diskProvider.GetFileAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            return _diskProvider.GetDirectories(folder).Any(ContainsDirectoryLink);
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
                path = Path.GetFullPath(path);
                if (!rootFolders.Any(root => Path.GetFullPath(root.Path).IsParentPath(path)))
                {
                    return true;
                }

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
        /// author folder that physically contains it. A colocated ebook can live in the audio tree;
        /// neither its media type nor a stale author path authorizes cleanup elsewhere.
        /// </summary>
        private void CleanupEmptyFolders(Author author, string startingFolder)
        {
            if (!_configService.DeleteEmptyFolders || author == null || startingFolder.IsNullOrWhiteSpace())
            {
                return;
            }

            var basePath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                .Where(p => p.IsParentPath(startingFolder) || p.PathEquals(startingFolder))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();

            if (basePath.IsNotNullOrWhiteSpace() && !IsPathUnsafeToDelete(basePath))
            {
                ExtraFilePathHelper.CleanupEmptySourceFolders(_diskProvider, _logger,
                    new[] { (startingFolder, basePath) });
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
