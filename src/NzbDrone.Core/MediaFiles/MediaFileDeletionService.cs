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
using NzbDrone.Core.Datastore.Events;
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
        List<BookFile> DeleteAuthorFiles(List<Author> authors, BookMediaType? mediaType = null, bool deleteOtherAuthorsFiles = false);
        AuthorDeletionPreview PreviewAuthorFiles(List<Author> authors, BookMediaType? mediaType = null);
        List<BookFile> DeleteBookFiles(List<Book> books);
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
                                        IExtraFileRepository<MetadataFile> metadataFiles,
                                        IExtraFileRepository<OtherExtraFile> otherFiles,
                                        IMetadataFileService metadataFileService,
                                        IOtherExtraFileService otherFileService)
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
            if (RetainUnresolvedFile(bookFile)) return;

            if (_diskProvider.FileExistsCanonical(fullPath))
            {
                _logger.Info("Deleting book file: {0}", fullPath);
                DeleteFile(bookFile, subfolder, rootFolder);
            }

            // Unmapped files do not publish a BookFileDeletedEvent. Finish the explicitly
            // selected row's replica cleanup before removing its inventory in either case.
            DeleteManagedEbookReplicas(bookFile, bookFile.ReplicaPaths ?? new List<string>(), failOnError: true);

            // Delete the track file from the database to clean it up even if the file was already deleted
            _mediaFileService.Delete(bookFile, DeleteMediaFileReason.Manual);

            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private bool RetainUnresolvedFile(BookFile file, string reason = null)
        {
            reason ??= TrackedFileDeletionCheck.GetRetentionReason(_diskProvider, file.Path);
            if (reason == null) return false;
            file.EditionId = 0;
            file.MatchDetails = reason;
            _mediaFileService.Update(file);
            _eventAggregator.PublishEvent(new BookFileUpdatedEvent(file));
            _logger.Warn("Kept file and unmapped record for '{0}': {1}", file.Path, reason);
            return true;
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
                    DeleteCalibreFormats(GetCalibreFormats(new[] { bookFile }), rootFolder.CalibreSettings);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Unable to delete book file: {0}", bookFile.Path);
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete book file: {0}", bookFile.Path);
            }
        }

        private static (int BookId, string[] Formats)[] GetCalibreFormats(IEnumerable<BookFile> files)
        {
            return files.GroupBy(file => file.CalibreId).Select(book =>
            {
                if (book.Key <= 0)
                {
                    throw new InvalidOperationException("Cannot remove a Calibre format without its Calibre book ID. The tracked file was kept.");
                }

                var formats = book.Select(file => Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (formats.Any(string.IsNullOrWhiteSpace))
                {
                    throw new InvalidOperationException("Cannot determine which Calibre format to remove. The tracked file was kept.");
                }

                return (book.Key, formats);
            }).ToArray();
        }

        private void DeleteCalibreFormats(IEnumerable<(int BookId, string[] Formats)> books, CalibreSettings settings)
        {
            foreach (var book in books)
            {
                _calibre.RemoveFormats(book.BookId, book.Formats, settings);
            }
        }

        private sealed class PreparedAuthorDeletion
        {
            public AuthorDeletionPreview Preview;
            public List<RootFolder> Roots;
            public List<BookFile> Files;
            public Dictionary<int, List<BookFile>> FilesByAuthor;
            public List<BookFile> Ownership;
            public List<string> Folders;
            public List<(string Path, RootFolder Root)> StandalonePaths;
            public List<(BookFile File, RootFolder Root)> CalibreFiles;
            public Dictionary<BookFile, string> KeptFiles;
            public List<BookFile> OtherFiles;
        }

        public AuthorDeletionPreview PreviewAuthorFiles(List<Author> authors, BookMediaType? mediaType = null)
        {
            return PrepareAuthorDeletion(authors, mediaType).Preview;
        }

        private PreparedAuthorDeletion PrepareAuthorDeletion(List<Author> authors, BookMediaType? mediaType)
        {
            var preview = new AuthorDeletionPreview();
            var keptFiles = new Dictionary<BookFile, string>();
            var rootFolders = _rootFolderService.All();
            var rootWarnings = new Dictionary<int, string>();
            string RootProblem(RootFolder root, string path)
            {
                if (root == null) return GetUnavailableRootReason(root, path);
                if (!rootWarnings.TryGetValue(root.Id, out var reason))
                    rootWarnings[root.Id] = reason = GetUnavailableRootReason(root, path);
                return reason;
            }
            var selectedIds = authors.Select(author => author.Id).ToHashSet();
            var inventoryWatch = Stopwatch.StartNew();
            var filesByAuthor = authors.ToDictionary(author => author.Id, author => _mediaFileService.GetFilesByAuthor(author.Id)
                .Where(file => !mediaType.HasValue || (file.MediaType ?? file.Edition?.Book?.MediaType.ToString()).EqualsIgnoreCase(mediaType.Value.ToString())).ToList());
            var files = filesByAuthor.Values.SelectMany(items => items)
                .DistinctBy(file => file.Id).ToList();
            var selectedFileIds = files.Select(file => file.Id).ToHashSet();
            var selectedBookIds = files.Where(file => file.Edition != null).Select(file => file.Edition.BookId).ToHashSet();
            // Sidecars can escape an author's base only into a selected file's directory.
            var deletionDirectories = authors.SelectMany(ExtraFilePathHelper.GetAuthorBasePaths)
                .Concat(files.SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetDirectoryName))
                .TopmostFolders();
            var sidecarChecks = 0;
            // Calibre IDs are library-local; include the selected libraries so another
            // owner of the same Calibre format remains protected even outside these folders.
            var ownershipFolders = deletionDirectories.Concat(files.Select(file =>
                    _rootFolderService.GetBestRootFolder(file.Path, rootFolders))
                .Where(root => root?.IsCalibreLibrary == true).Select(root => root.Path))
                .Distinct(PathEqualityComparer.Instance).ToList();
            var ownership = _mediaFileService.GetFilePathOwnership(selectedIds, ownershipFolders);
            var authorPaths = _authorRepository.AllAuthorPaths();
            var retainedAuthors = authorPaths.Where(author => mediaType.HasValue || !selectedIds.Contains(author.Id)).ToList();
            var retainedPaths = ownership.Where(file => file.Author != null && !selectedFileIds.Contains(file.Id))
                .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetFullPath)
                .ToHashSet(PathEqualityComparer.Instance);

            // Relative sidecar paths can escape an author's base via '..'. Inspect their
            // ownership once before filtering physical probes to the selected directories.
            var overlapWatch = Stopwatch.StartNew();
            var extraAuthorIds = authorPaths.Where(author => selectedIds.Contains(author.Id) ||
                    ExtraFilePathHelper.GetAuthorBasePaths(author).Any(path => deletionDirectories.Any(folder =>
                        folder.PathEquals(path) || folder.IsParentPath(path) || path.IsParentPath(folder))))
                .Select(author => author.Id).Concat(selectedIds).Distinct().ToArray();
            _logger.Debug("Author deletion overlap: authors={0}, folders={1}, overlapping={2}, elapsedMs={3}",
                authorPaths.Count, deletionDirectories.Count, extraAuthorIds.Length, overlapWatch.ElapsedMilliseconds);
            var allExtras = _metadataFiles.GetFilesForAuthorDeletion(extraAuthorIds).Cast<ExtraFile>()
                .Concat(_otherFiles.GetFilesForAuthorDeletion(extraAuthorIds)).ToList();
            var retainedSidecarOwners = new Dictionary<string, int>(PathEqualityComparer.Instance);
            var retainedExtras = allExtras.Where(extra => !selectedIds.Contains(extra.AuthorId) || (mediaType.HasValue &&
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
                            retainedSidecarOwners[path] = author.Id;
                        }
                    }
                }
            }

            _logger.Info("Author deletion inventory: {0} selected authors, {1} selected files, {2} library file rows, {3} retained physical paths, {4} retained sidecar checks in {5}ms; {6} author paths, {7} scoped authors, {8} sidecar rows",
                selectedIds.Count, files.Count, ownership.Count, retainedPaths.Count, sidecarChecks, inventoryWatch.ElapsedMilliseconds,
                authorPaths.Count, extraAuthorIds.Length, allExtras.Count);

            // Explicit folder deletion includes untracked and unmapped contents. A real ownership
            // or boundary conflict must refuse the request, not silently turn it into file-only deletion.
            var folders = new List<string>();
            foreach (var folder in authors.SelectMany(author => !mediaType.HasValue
                    ? ExtraFilePathHelper.GetAuthorBasePaths(author)
                    : new[] { (mediaType == BookMediaType.Audiobook ? author.AudiobookPath : author.EbookPath) ??
                        (author.AudiobookPath.IsNullOrWhiteSpace() && author.EbookPath.IsNullOrWhiteSpace() ? author.Path : null) }
                        .Where(path => path.IsNotNullOrWhiteSpace()))
                .Select(Path.GetFullPath).Distinct(PathEqualityComparer.Instance))
            {
                if (IsPathUnsafeToDelete(folder, rootFolders))
                {
                    throw RefuseAuthorDeletion(folder, "the folder is outside configured roots, or is a root or its ancestor");
                }

                var root = _rootFolderService.GetBestRootFolder(folder, rootFolders);
                var folderPreview = new AuthorDeletionFolder { Path = folder };
                preview.Folders.Add(folderPreview);
                var unavailable = RootProblem(root, folder);
                if (unavailable != null)
                {
                    folderPreview.Reason = unavailable;
                    preview.Warnings.Add(unavailable);
                    foreach (var file in files.Where(file => folder.IsParentPath(file.Path))) keptFiles[file] = unavailable;
                    continue;
                }

                try
                {
                    if (HasDirectoryLink(folder, root.Path) ||
                        (_diskProvider.FolderExists(folder) && ContainsDirectoryLink(folder)))
                        throw RefuseAuthorDeletion(folder, "the folder contains or traverses a directory link");
                    if (_diskProvider.FolderExists(folder)) folderPreview.OtherFiles = _diskProvider.GetFiles(folder, true).ToList();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    var reason = $"Chaptarr cannot access {folder}. Removing this author may leave files there: {error.Message}";
                    folderPreview.Reason = reason;
                    preview.Warnings.Add(reason);
                    foreach (var file in files.Where(file => folder.IsParentPath(file.Path))) keptFiles[file] = reason;
                    continue;
                }

                var shared = mediaType.HasValue && (retainedSidecarOwners.Any(item => selectedIds.Contains(item.Value) && folder.IsParentPath(item.Key)) || authors.Any(author =>
                {
                    var other = mediaType == BookMediaType.Audiobook ? author.EbookPath : author.AudiobookPath;
                    return other.IsNotNullOrWhiteSpace() && (folder.PathEquals(other) || folder.IsParentPath(other) || other.IsParentPath(folder));
                }) || ownership.Any(file => !selectedFileIds.Contains(file.Id) &&
                    (file.MediaType ?? file.Edition?.Book?.MediaType.ToString()).IsNotNullOrWhiteSpace() &&
                    !(file.MediaType ?? file.Edition?.Book?.MediaType.ToString()).EqualsIgnoreCase(mediaType.Value.ToString()) &&
                    (file.ReplicaPaths ?? new List<string>()).Append(file.Path).Any(path => path.IsNotNullOrWhiteSpace() && folder.IsParentPath(path))));
                if (shared || root.IsCalibreLibrary)
                {
                    folderPreview.Reason = shared ? "Shared with the other format; only selected files will be removed." : "Managed by Calibre; only known Calibre entries will be removed.";
                    continue;
                }

                folderPreview.DeleteContents = true;
                folders.Add(folder);
            }

            folders = folders.Where(folder => !folders.Any(parent => parent.PathNotEquals(folder) && parent.IsParentPath(folder))).ToList();

            foreach (var file in files.Where(file => !keptFiles.ContainsKey(file) && !folders.Any(folder => folder.IsParentPath(file.Path))))
            {
                var reason = TrackedFileDeletionCheck.GetRetentionReason(_diskProvider, file.Path);
                var root = _rootFolderService.GetBestRootFolder(file.Path, rootFolders);
                reason ??= RootProblem(root, file.Path);
                if (root?.IsCalibreLibrary == true && file.CalibreId <= 0)
                    reason = "Kept in Calibre because its Calibre book ID is unknown.";
                if (reason != null) keptFiles[file] = reason;
            }
            var kept = keptFiles.Keys.Select(file => file.Id).ToHashSet();
            files.RemoveAll(file => kept.Contains(file.Id));
            foreach (var owned in filesByAuthor.Values) owned.RemoveAll(file => kept.Contains(file.Id));
            selectedFileIds.ExceptWith(kept);

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
                var extras = allExtras.Where(extra => extra.AuthorId == author.Id);
                foreach (var extra in extras)
                {
                    if (mediaType.HasValue && !(extra.BookFileId.HasValue ? selectedFileIds.Contains(extra.BookFileId.Value) :
                        extra.BookId.HasValue && selectedBookIds.Contains(extra.BookId.Value)))
                    {
                        continue;
                    }

                    var path = ExtraFilePathHelper.ResolveOwnedSidecarPath(author, extra, ownedFiles, _diskProvider);
                    if (path != null && !retainedPaths.Contains(path))
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
                if (item.Root == null) throw RefuseAuthorDeletion(item.Path, "the file is outside configured roots");
                if (RootProblem(item.Root, item.Path) is string unavailable)
                {
                    preview.Warnings.Add(unavailable);
                    continue;
                }

                if (HasDirectoryLink(item.Path, item.Root.Path))
                {
                    throw RefuseAuthorDeletion(item.Path, "the file traverses a directory link");
                }
            }

            standalonePaths.RemoveAll(item => RootProblem(item.Root, item.Path) != null);

            // Calibre IDs are library-local; never send one library's IDs to another.
            var calibreFiles = files.Select(file => (File: file, Root: _rootFolderService.GetBestRootFolder(file.Path, rootFolders)))
                .Where(item => item.Root?.IsCalibreLibrary == true && !retainedPaths.Contains(Path.GetFullPath(item.File.Path))).ToList();
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

            var otherFiles = ownership.Where(file => file.Author != null && !selectedIds.Contains(file.Author.Id) &&
                (file.ReplicaPaths ?? new List<string>()).Append(file.Path).Any(path => path.IsNotNullOrWhiteSpace() &&
                    folders.Any(folder => folder.IsParentPath(path)))).ToList();
            foreach (var other in retainedAuthors.Where(author => !selectedIds.Contains(author.Id) &&
                (otherFiles.Any(file => file.Author.Id == author.Id) || retainedSidecarOwners.Any(item => item.Value == author.Id && folders.Any(folder => folder.IsParentPath(item.Key))) || ExtraFilePathHelper.GetAuthorBasePaths(author).Any(path =>
                    folders.Any(folder => folder.PathEquals(path) || folder.IsParentPath(path) || path.IsParentPath(folder))))))
                preview.OtherAuthors.Add(new AuthorDeletionOtherAuthor { AuthorId = other.Id, Name = other.Name, FileCount = otherFiles.Count(file => file.Author.Id == other.Id) });

            foreach (var folder in preview.Folders)
            {
                var folderFiles = ownership.Where(file => file.Path.IsNotNullOrWhiteSpace() && folder.Path.IsParentPath(file.Path) &&
                    (folder.DeleteContents || selectedFileIds.Contains(file.Id))).ToList();
                folder.BookFileCount = folderFiles.Count;
                folder.Size = folderFiles.Sum(file => file.Size);
                var tracked = ownership.SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace()).ToHashSet(PathEqualityComparer.Instance);
                folder.OtherFiles = folder.DeleteContents ? folder.OtherFiles.Where(path => !tracked.Contains(path)).ToList() : new List<string>();
            }
            preview.Files = exactPaths.Concat(ownership.SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace() && folders.Any(folder => folder.IsParentPath(path))))
                .Where(path => path.IsNotNullOrWhiteSpace() && !keptFiles.Keys.Any(file => file.Path.PathEquals(path)))
                .Distinct(PathEqualityComparer.Instance).Select(path => new AuthorDeletionFile { Path = path }).ToList();
            preview.Files.AddRange(keptFiles.Select(item => new AuthorDeletionFile { Path = item.Key.Path, Reason = item.Value }));
            preview.Warnings = preview.Warnings.Concat(rootWarnings.Values.Where(reason => reason != null)).Distinct().ToList();
            preview.RecycleBin = _configService.RecycleBin;
            return new PreparedAuthorDeletion
            {
                Preview = preview, Roots = rootFolders, Files = files, FilesByAuthor = filesByAuthor,
                Ownership = ownership, Folders = folders, StandalonePaths = standalonePaths,
                CalibreFiles = calibreFiles, KeptFiles = keptFiles, OtherFiles = otherFiles
            };
        }

        public List<BookFile> DeleteAuthorFiles(List<Author> authors, BookMediaType? mediaType = null, bool deleteOtherAuthorsFiles = false)
        {
            if (authors.Count == 0) return new List<BookFile>();
            var plan = PrepareAuthorDeletion(authors, mediaType);
            if (plan.Preview.OtherAuthors.Count > 0 && !deleteOtherAuthorsFiles)
                throw RefuseAuthorDeletion(plan.Folders.First(), "other authors have files here; preview the deletion and set deleteOtherAuthorsFiles=true to include them");
            foreach (var item in plan.KeptFiles) RetainUnresolvedFile(item.Key, item.Value);
            foreach (var warning in plan.Preview.Warnings.Distinct()) _logger.Warn(warning);
            var files = plan.Files;
            var filesByAuthor = plan.FilesByAuthor;
            var rootFolders = plan.Roots;
            var folders = plan.Folders;
            var standalonePaths = plan.StandalonePaths;
            var calibreFiles = plan.CalibreFiles;
            var ownership = plan.Ownership;
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
                    var author = authors.FirstOrDefault(author => filesByAuthor[author.Id].Any(file =>
                        file.Path.PathEquals(item.Path) || (file.ReplicaPaths ?? new List<string>()).Any(path => path.PathEquals(item.Path)))) ?? authors.First();
                    _recycleBinProvider.DeleteFile(item.Path, Path.GetFileName(author.Path));
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
            var unmappedFiles = ownership.Where(file => file.Author == null &&
                (file.ReplicaPaths ?? new List<string>()).Append(file.Path).Any(path => path.IsNotNullOrWhiteSpace() &&
                    folders.Any(folder => folder.IsParentPath(path)))).ToList();
            var affectedOtherFiles = plan.OtherFiles.Concat(unmappedFiles).DistinctBy(file => file.Id).ToList();
            var removedOthers = new List<BookFile>();
            if (affectedOtherFiles.Count > 0)
            {
                foreach (var file in _mediaFileService.Get(affectedOtherFiles.Select(file => file.Id)))
                {
                    var primaryRemoved = folders.Any(folder => folder.IsParentPath(file.Path));
                    file.ReplicaPaths = (file.ReplicaPaths ?? new List<string>()).Where(path => !folders.Any(folder => folder.IsParentPath(path))).ToList();
                    if (primaryRemoved) removedOthers.Add(file);
                    else
                    {
                        _mediaFileService.Update(file);
                        _eventAggregator.PublishEvent(new BookFileUpdatedEvent(file));
                    }
                }
            }
            _mediaFileService.DeleteRecordsForAuthorDeletion(files.Concat(removedOthers).DistinctBy(file => file.Id).ToList());
            foreach (var file in removedOthers) _eventAggregator.PublishEvent(new ModelEvent<BookFile>(file, ModelAction.Deleted));
            foreach (var bookId in removedOthers.Where(file => file.Edition != null).Select(file => file.Edition.BookId).Distinct())
                _eventAggregator.PublishEvent(new ModelEvent<Book>(bookId, ModelAction.Updated));
            foreach (var author in plan.Preview.OtherAuthors)
            {
                _eventAggregator.PublishEvent(new ModelEvent<Author>(author.AuthorId, ModelAction.Updated));
                _logger.Info("Deleted {0} files belonging to {1} from the explicitly selected author folders", author.FileCount, author.Name);
            }
            return files;
        }

        private string GetUnavailableRootReason(RootFolder root, string path)
        {
            if (root == null) throw RefuseAuthorDeletion(path, "the path is outside configured roots");
            try
            {
                if (_diskProvider.FolderExists(root.Path) && !_diskProvider.FolderEmpty(root.Path) &&
                    (!root.IsCalibreLibrary || root.CalibreSettings != null)) return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.Debug(error, "Cannot inspect deletion root {0}", root.Path);
            }
            return $"Chaptarr cannot access {root.Path}. Removing this author may leave files there.";
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

        public List<BookFile> DeleteBookFiles(List<Book> books)
        {
            var watch = Stopwatch.StartNew();
            var files = books.SelectMany(book => (book.BookFiles ?? new List<BookFile>()).Where(file =>
                !RetainUnresolvedFile(file, file.MediaType.IsNotNullOrWhiteSpace() &&
                    !file.MediaType.EqualsIgnoreCase(book.MediaType.ToString())
                        ? "The file's media type differs from the book being removed." : null)))
                .DistinctBy(file => file.Id).ToList();
            var selectedPaths = files.SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                .Where(path => path.IsNotNullOrWhiteSpace()).ToHashSet(PathEqualityComparer.Instance);
            var roots = _rootFolderService.All();
            var rootsByPath = selectedPaths.ToDictionary(path => path,
                path => _rootFolderService.GetBestRootFolder(path, roots), PathEqualityComparer.Instance);
            foreach (var group in rootsByPath.GroupBy(item => item.Value?.Path, PathEqualityComparer.Instance))
            {
                EnsureDeletionRootAvailable(group.First().Value, group.First().Key);
            }

            // Validate every Calibre group before deleting any selected media or sidecars.
            var groups = files.GroupBy(file => rootsByPath[file.Path]).Select(group => (
                Root: group.Key,
                Files: group.ToList(),
                Formats: group.Key.IsCalibreLibrary && group.Key.CalibreSettings != null
                    ? GetCalibreFormats(group) : null)).ToList();

            // Mandatory disk work runs while its inventory still exists. Events only announce
            // the committed deletion; they cannot swallow a disk failure and erase ownership.
            foreach (var book in books)
            {
                _metadataFileService.DeleteBookFiles(book);
                _otherFileService.DeleteBookFiles(book);
            }

            foreach (var group in groups)
            {
                if (group.Formats != null)
                {
                    DeleteCalibreFormats(group.Formats, group.Root.CalibreSettings);
                }

                foreach (var file in group.Files)
                {
                    DeleteManagedEbookReplicas(file, file.ReplicaPaths ?? new List<string>(), failOnError: true);
                    if (group.Formats == null)
                    {
                        DeleteFile(file, rootFolder: group.Root);
                    }
                }
            }

            foreach (var book in books)
            {
                foreach (var folder in (book.BookFiles ?? new List<BookFile>()).Where(file => files.Contains(file))
                    .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .Where(path => path.IsNotNullOrWhiteSpace()).Select(Path.GetDirectoryName).Distinct(PathEqualityComparer.Instance))
                {
                    CleanupEmptyFolders(book.Author, folder, roots);
                }
            }

            _logger.Info("Deleted files for {0} book rows: {1} media files, {2} owned paths in {3}ms",
                books.Count, files.Count, selectedPaths.Count, watch.ElapsedMilliseconds);
            return files;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(BookFileDeletedEvent message)
        {
            if (!message.Reason.RemovesFiles())
            {
                return;
            }

            DeleteManagedEbookReplicas(message.BookFile, message.ConfirmedAssociatedPaths ??
                message.BookFile.ReplicaPaths ?? new List<string>());

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

        private void DeleteManagedEbookReplicas(BookFile bookFile, IReadOnlyCollection<string> confirmedPaths, bool failOnError = false)
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
                if (!confirmedPaths.Any(confirmed => confirmed.PathEquals(replicaPath)))
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
