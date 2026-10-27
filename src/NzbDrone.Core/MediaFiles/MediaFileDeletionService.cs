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
        List<BookFile> DeleteAuthorFiles(List<Author> authors, BookMediaType? mediaType = null);
        void DeleteBookFiles(List<Book> books);
    }

    public class MediaFileDeletionService : IDeleteMediaFiles,
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
        private readonly IMetadataFileService _metadataFileService;
        private readonly IOtherExtraFileService _otherFileService;
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
                                        IExtraFileRepository<OtherExtraFile> otherFiles = null,
                                        IMetadataFileService metadataFileService = null,
                                        IOtherExtraFileService otherFileService = null)
        {
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _authorRepository = authorRepository;
            _metadataFiles = metadataFiles;
            _otherFiles = otherFiles;
            _metadataFileService = metadataFileService;
            _otherFileService = otherFileService;
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
                _logger.Error(e, "Unable to delete book file: {0}", bookFile.Path);
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete book file: {0}", bookFile.Path);
            }
        }

        public List<BookFile> DeleteAuthorFiles(List<Author> authors, BookMediaType? mediaType = null)
        {
            if (authors.Count == 0)
            {
                return new List<BookFile>();
            }

            var rootFolders = _rootFolderService.All();
            var selectedIds = authors.Select(author => author.Id).ToHashSet();
            var inventoryWatch = Stopwatch.StartNew();
            var filesByAuthor = authors.ToDictionary(author => author.Id, author => _mediaFileService.GetFilesByAuthor(author.Id)
                .Where(file => !mediaType.HasValue || file.MediaType.EqualsIgnoreCase(mediaType.Value.ToString())).ToList());
            var files = filesByAuthor.Values.SelectMany(items => items)
                .DistinctBy(file => file.Id).ToList();
            var selectedFileIds = files.Select(file => file.Id).ToHashSet();
            var selectedBookIds = files.Where(file => file.Edition != null).Select(file => file.Edition.BookId).ToHashSet();
            // Sidecars can escape an author's base only into a selected file's directory.
            var deletionDirectories = authors.SelectMany(ExtraFilePathHelper.GetAuthorBasePaths)
                .Concat(files.SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetDirectoryName))
                .Select(Path.GetFullPath).Distinct(PathEqualityComparer.Instance).ToList();
            var sidecarChecks = 0;
            var ownership = _mediaFileService.GetFilePathOwnership();
            var retainedAuthors = _authorRepository.All().Where(author => mediaType.HasValue || !selectedIds.Contains(author.Id)).ToList();
            var retainedPaths = ownership.Where(file => file.Author != null && !selectedFileIds.Contains(file.Id))
                .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetFullPath)
                .ToHashSet(PathEqualityComparer.Instance);
            var retainedBases = retainedAuthors.SelectMany(ExtraFilePathHelper.GetAuthorBasePaths)
                .Select(Path.GetFullPath).ToList();
            var retainedExtras = (_metadataFiles?.All().Cast<ExtraFile>() ?? Enumerable.Empty<ExtraFile>())
                .Concat(_otherFiles?.All().Cast<ExtraFile>() ?? Enumerable.Empty<ExtraFile>())
                .Where(extra => !selectedIds.Contains(extra.AuthorId) || (mediaType.HasValue &&
                    !(extra.BookFileId.HasValue ? selectedFileIds.Contains(extra.BookFileId.Value) :
                        extra.BookId.HasValue && selectedBookIds.Contains(extra.BookId.Value))))
                .ToLookup(extra => extra.AuthorId);
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
                        if (!deletionDirectories.Any(folder => folder.PathEquals(path) || folder.IsParentPath(path)))
                        {
                            continue;
                        }

                        sidecarChecks++;
                        if (_diskProvider.FileExistsCanonical(path))
                        {
                            retainedPaths.Add(path);
                        }
                    }
                }
            }

            _logger.Info("Author deletion inventory: {0} selected authors, {1} selected files, {2} library file rows, {3} retained physical paths, {4} retained sidecar checks in {5}ms",
                selectedIds.Count, files.Count, ownership.Count, retainedPaths.Count, sidecarChecks, inventoryWatch.ElapsedMilliseconds);

            // Explicit folder deletion includes untracked and unmapped contents. A real ownership
            // or boundary conflict must refuse the request, not silently turn it into file-only deletion.
            var folders = new List<string>();
            foreach (var folder in authors.Where(_ => !mediaType.HasValue).SelectMany(ExtraFilePathHelper.GetAuthorBasePaths)
                .Select(Path.GetFullPath).Distinct(PathEqualityComparer.Instance))
            {
                if (IsPathUnsafeToDelete(folder, rootFolders))
                {
                    throw RefuseAuthorDeletion(folder, "the folder is outside configured roots, or is a root or its ancestor");
                }

                var root = _rootFolderService.GetBestRootFolder(folder, rootFolders);
                EnsureDeletionRootAvailable(root, folder);

                if (root.IsCalibreLibrary)
                {
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
                    if (mediaType.HasValue && !(extra.BookFileId.HasValue ? selectedFileIds.Contains(extra.BookFileId.Value) :
                        extra.BookId.HasValue && selectedBookIds.Contains(extra.BookId.Value)))
                    {
                        continue;
                    }

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
                .Select(path => (Path: path, Root: _rootFolderService.GetBestRootFolder(path, rootFolders))).ToList();
            foreach (var item in standalonePaths)
            {
                EnsureDeletionRootAvailable(item.Root, item.Path);

                if (HasDirectoryLink(item.Path, item.Root.Path))
                {
                    throw RefuseAuthorDeletion(item.Path, "the file traverses a directory link");
                }
            }

            // Calibre IDs are library-local; never send one library's IDs to another.
            var calibreFiles = files.Select(file => (File: file, Root: _rootFolderService.GetBestRootFolder(file.Path, rootFolders)))
                .Where(item => item.Root?.IsCalibreLibrary == true && !retainedPaths.Contains(Path.GetFullPath(item.File.Path))).ToList();
            var unidentifiedCalibreFile = calibreFiles.Select(item => item.File).FirstOrDefault(file => file.CalibreId == 0);
            if (unidentifiedCalibreFile != null)
            {
                throw RefuseAuthorDeletion(unidentifiedCalibreFile.Path, "the file has no Calibre book identity");
            }

            var unidentifiedCalibreReplica = files.SelectMany(file => file.ReplicaPaths ?? new List<string>())
                .FirstOrDefault(path => path.IsNotNullOrWhiteSpace() &&
                    _rootFolderService.GetBestRootFolder(path, rootFolders)?.IsCalibreLibrary == true &&
                    !retainedPaths.Contains(Path.GetFullPath(path)) && !calibreFiles.Any(item => item.File.Path.PathEquals(path)));
            if (unidentifiedCalibreReplica != null)
            {
                throw RefuseAuthorDeletion(unidentifiedCalibreReplica, "the replica has no independently tracked Calibre identity");
            }

            if (mediaType.HasValue && calibreFiles.Any())
            {
                var calibreIds = calibreFiles.Select(item => item.File.CalibreId).ToHashSet();
                var retainedCalibreFiles = ownership.Where(file => !selectedFileIds.Contains(file.Id) && calibreIds.Contains(file.CalibreId) && file.Path.IsNotNullOrWhiteSpace())
                    .Select(file => (File: file, Root: _rootFolderService.GetBestRootFolder(file.Path, rootFolders))).ToList();
                var sharedFormat = calibreFiles.FirstOrDefault(item => retainedCalibreFiles.Any(retained =>
                    retained.File.CalibreId == item.File.CalibreId && retained.Root?.Path.PathEquals(item.Root.Path) == true &&
                    Path.GetExtension(retained.File.Path).Equals(Path.GetExtension(item.File.Path), StringComparison.OrdinalIgnoreCase)));
                if (sharedFormat.File != null)
                {
                    throw RefuseAuthorDeletion(sharedFormat.File.Path, "another retained file owns the same Calibre format");
                }
            }

            foreach (var group in calibreFiles.GroupBy(item => item.Root.Path, PathEqualityComparer.Instance))
            {
                if (mediaType.HasValue)
                {
                    foreach (var book in group.GroupBy(item => item.File.CalibreId))
                    {
                        _calibre.RemoveFormats(book.Key, book.Select(item => Path.GetExtension(item.File.Path).TrimStart('.').ToUpperInvariant())
                            .Distinct(StringComparer.OrdinalIgnoreCase), group.First().Root.CalibreSettings);
                    }
                }
                else
                {
                    _calibre.DeleteBooks(group.Select(item => item.File).DistinctBy(file => file.CalibreId).ToList(), group.First().Root.CalibreSettings);
                }
            }

            foreach (var item in standalonePaths.Where(item => !item.Root.IsCalibreLibrary))
            {
                if (_diskProvider.FileExistsCanonical(item.Path))
                {
                    _recycleBinProvider.DeleteFile(item.Path);
                }
                else
                {
                    _logger.Warn("Stored media file path ({0}) was not found or could not be resolved safely; continuing database cleanup without deleting a file.", item.Path);
                }
            }

            foreach (var folder in folders)
            {
                if (_diskProvider.FolderExists(folder))
                {
                    _recycleBinProvider.DeleteFolder(folder);
                }
            }

            foreach (var author in authors)
            {
                foreach (var folder in filesByAuthor[author.Id]
                    .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Select(Path.GetDirectoryName).Distinct(PathEqualityComparer.Instance))
                {
                    if (_rootFolderService.GetBestRootFolder(folder, rootFolders)?.IsCalibreLibrary != true)
                    {
                        CleanupEmptyFolders(author, folder, rootFolders);
                    }
                }
            }

            // Folder contents need no book identity to be deleted. Remove their unmapped inventory
            // only after physical success, without following those rows to paths outside the folders.
            var unmappedFiles = ownership.Where(file => file.Author == null && file.Path.IsNotNullOrWhiteSpace() &&
                folders.Any(folder => folder.IsParentPath(Path.GetFullPath(file.Path)))).ToList();
            _mediaFileService.DeleteRecordsForAuthorDeletion(files.Concat(unmappedFiles).DistinctBy(file => file.Id).ToList());
            return files;
        }

        private void EnsureDeletionRootAvailable(RootFolder root, string path)
        {
            if (root == null || !_diskProvider.FolderExists(root.Path))
            {
                throw RefuseAuthorDeletion(path, "no available configured root contains the path");
            }

            // A missing share can leave an empty mount directory. Check all entries so a
            // populated flat root is not mistaken for an unavailable share and stripped of inventory.
            if (_diskProvider.FolderEmpty(root.Path))
            {
                throw RefuseAuthorDeletion(path, "the containing root folder is empty and may be unavailable: " + root.Path);
            }

            if (root.IsCalibreLibrary && root.CalibreSettings == null)
            {
                throw RefuseAuthorDeletion(path, "the containing Calibre library has no settings");
            }
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

        private bool IsPathUnsafeToDelete(string path, List<RootFolder> rootFolders = null)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return true;
            }

            try
            {
                rootFolders ??= _rootFolderService.All();
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

        public void DeleteBookFiles(List<Book> books)
        {
            var watch = Stopwatch.StartNew();
            var files = books.SelectMany(book => book.BookFiles ?? new List<BookFile>()).DistinctBy(file => file.Id).ToList();
            var selectedPaths = files.SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                .Where(path => path.IsNotNullOrWhiteSpace()).ToHashSet(PathEqualityComparer.Instance);
            var roots = _rootFolderService.All();
            var rootsByPath = selectedPaths.ToDictionary(path => path,
                path => _rootFolderService.GetBestRootFolder(path, roots), PathEqualityComparer.Instance);
            foreach (var group in rootsByPath.GroupBy(item => item.Value?.Path, PathEqualityComparer.Instance))
            {
                EnsureDeletionRootAvailable(group.First().Value, group.First().Key);
            }

            // Mandatory disk work runs while its inventory still exists. Events only announce
            // the committed deletion; they cannot swallow a disk failure and erase ownership.
            foreach (var book in books)
            {
                _metadataFileService?.DeleteBookFiles(book, selectedPaths);
                _otherFileService?.DeleteBookFiles(book, selectedPaths);
            }

            foreach (var file in files)
            {
                DeleteManagedEbookReplicas(file, failOnError: true);
                DeleteFile(file, rootFolder: rootsByPath[file.Path]);
            }

            foreach (var book in books)
            {
                foreach (var folder in (book.BookFiles ?? new List<BookFile>())
                    .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetDirectoryName).Distinct(PathEqualityComparer.Instance))
                {
                    CleanupEmptyFolders(book.Author, folder, roots);
                }
            }

            _logger.Info("Deleted files for {0} book rows: {1} media files, {2} owned paths in {3}ms",
                books.Count, files.Count, selectedPaths.Count, watch.ElapsedMilliseconds);
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(BookFileDeletedEvent message)
        {
            DeleteManagedEbookReplicas(message.BookFile, message.ConfirmedAssociatedPaths);

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
        private void CleanupEmptyFolders(Author author, string startingFolder, List<RootFolder> rootFolders = null)
        {
            if (!_configService.DeleteEmptyFolders || author == null || startingFolder.IsNullOrWhiteSpace())
            {
                return;
            }

            var basePath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                .Where(p => p.IsParentPath(startingFolder) || p.PathEquals(startingFolder))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();

            if (basePath.IsNotNullOrWhiteSpace() && !IsPathUnsafeToDelete(basePath, rootFolders))
            {
                ExtraFilePathHelper.CleanupEmptySourceFolders(_diskProvider, _logger,
                    new[] { (startingFolder, basePath) });
            }
        }

        private void DeleteManagedEbookReplicas(BookFile bookFile, IReadOnlyCollection<string> confirmedPaths = null, bool failOnError = false)
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
                if (confirmedPaths != null && !confirmedPaths.Any(confirmed => confirmed.PathEquals(replicaPath)))
                {
                    _logger.Warn("Keeping ebook replica not included in replacement confirmation: {0}", replicaPath);
                    continue;
                }

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
                    if (failOnError)
                    {
                        _logger.Error(ex, "Unable to delete managed ebook replica: {0}", replicaPath);
                        throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete managed ebook replica: {0}", replicaPath);
                    }

                    _logger.Debug(ex, "Failed to delete managed ebook replica: {0}", replicaPath);
                }
            }
        }
    }
}
