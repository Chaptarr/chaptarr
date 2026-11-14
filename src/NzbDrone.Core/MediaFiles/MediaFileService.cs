using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Data;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileService
    {
        BookFile Add(BookFile bookFile);
        void AddMany(List<BookFile> bookFiles);
        void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles = null)
        {
            if (replacedFiles?.Count > 0)
            {
                throw new InvalidOperationException("This media-file service does not support atomic replacement");
            }

            AddMany(bookFiles ?? new List<BookFile>());
        }
        void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason,
            IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles, Action<Action<IDbConnection, IDbTransaction>> persist);
        void ValidateReplacementFiles(List<BookFile> replacedFiles, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles);
        void Update(BookFile bookFile);
        void Update(List<BookFile> bookFiles);
        (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, AudioProductionCorrection correction, IDbConnection connection, IDbTransaction transaction);
        void Delete(BookFile bookFile, DeleteMediaFileReason reason);
        void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason);
        List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter);
        List<BookFile> GetFilesByAuthor(int authorId);
        List<BookFile> GetFilePathOwnership(IReadOnlyCollection<int> authorIds, IReadOnlyCollection<string> folders) => throw new NotSupportedException();
        void DeleteRecordsForAuthorDeletion(List<BookFile> files) => throw new NotSupportedException();
        List<BookFile> GetMappedFilePathEvidenceByAuthor(int authorId, string mediaType)
        {
            var requestedMediaType = (mediaType ?? "all").Trim().ToLowerInvariant();
            var files = requestedMediaType is "audiobook" or "ebook"
                ? GetFilesByAuthorAndMediaType(authorId, requestedMediaType)
                : GetFilesByAuthor(authorId);
            return files
                .Where(file => file != null && file.EditionId > 0)
                .Select(file => new BookFile
                {
                    Path = file.Path,
                    MediaType = file.MediaType,
                    EditionId = file.EditionId
                })
                .ToList();
        }
        List<BookFile> GetFilesByBook(int bookId);
        List<BookFile> GetFilesByBooks(List<int> bookIds);
        List<BookFile> GetFilesByEdition(int editionId);
        List<BookFile> GetUnmappedFiles();
        List<BookFile> GetUnmappedFiles(string mediaType)
        {
            var files = GetUnmappedFiles();
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return files;
            }

            return files.Where(f => string.Equals(f.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType)
        {
            var requestedIds = ids?.Where(id => id > 0).ToHashSet() ?? new HashSet<int>();
            if (!requestedIds.Any())
            {
                return new List<BookFile>();
            }

            return GetUnmappedFiles(mediaType)
                .Where(f => requestedIds.Contains(f.Id))
                .ToList();
        }
        BookFile Get(int id);
        List<BookFile> Get(IEnumerable<int> ids);
        List<BookFile> GetExisting(IEnumerable<int> ids);
        List<BookFile> GetFilesWithBasePath(string path);
        List<BookFile> GetFilesWithBasePath(string path, string mediaType);
        List<BookFile> GetFileWithPath(List<string> path);
        BookFile GetFileWithPath(string path);
        void UpdateMediaInfo(List<BookFile> bookFiles);
        List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType);
    }

    public class MediaFileService : IMediaFileService,
        IHandle<AuthorMovedEvent>,
        IHandle<AuthorDeletedEvent>,
        IHandleAsync<BookDeletedEvent>,
        IHandleAsync<ModelEvent<RootFolder>>,
        IHandle<EditionDeletedEvent>
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IMediaFileRepository _mediaFileRepository;
        private readonly IIngestQueueRepository _ingestQueueRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskProvider _diskProvider;
        private readonly IManualImportReplacementFiles _replacementFiles;
        private readonly Logger _logger;

        public MediaFileService(IMediaFileRepository mediaFileRepository, IEventAggregator eventAggregator, IIngestQueueRepository ingestQueueRepository, IDiskProvider diskProvider, Logger logger, IRootFolderService rootFolderService = null, IManualImportReplacementFiles replacementFiles = null)
        {
            _mediaFileRepository = mediaFileRepository;
            _eventAggregator = eventAggregator;
            _ingestQueueRepository = ingestQueueRepository;
            _rootFolderService = rootFolderService;
            _diskProvider = diskProvider;
            _replacementFiles = replacementFiles;
            _logger = logger;
        }

        public BookFile Add(BookFile bookFile)
        {
            var addedFile = _mediaFileRepository.Insert(bookFile);
            _eventAggregator.PublishEvent(new BookFileAddedEvent(addedFile));
            return addedFile;
        }

        public void AddMany(List<BookFile> bookFiles)
        {
            var __repoSw = System.Diagnostics.Stopwatch.StartNew();
            _mediaFileRepository.InsertMany(bookFiles);
            __repoSw.Stop();
            _logger.Debug("[IMPORT-TIMING] StepE1 Repository.InsertMany(BookFiles) count={0} elapsed={1}ms", bookFiles.Count, __repoSw.ElapsedMilliseconds);

            var __evtSw = System.Diagnostics.Stopwatch.StartNew();
            // Publish a single aggregate event for the batch to reduce overhead
            _eventAggregator.PublishEvent(new BookFilesAddedEvent(bookFiles));
            __evtSw.Stop();
            _logger.Debug("[IMPORT-TIMING] StepE2 Publish BookFilesAddedEvent x{0} elapsed={1}ms", bookFiles.Count, __evtSw.ElapsedMilliseconds);
        }

        public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles = null)
        {
            ReplaceMany(bookFiles, replacedFiles, reason, confirmedFiles, null);
        }

        public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason,
            IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles, Action<Action<IDbConnection, IDbTransaction>> persist)
        {
            bookFiles ??= new List<BookFile>();
            replacedFiles ??= new List<BookFile>();
            var newFiles = bookFiles.Where(file => file.Id == 0).ToList();

            var eventBookFiles = HydrateForDeleteEvents(replacedFiles);
            var associatedById = ValidateReplacementAssociations(eventBookFiles, confirmedFiles);

            try
            {
                if (persist == null)
                {
                    _mediaFileRepository.ReplaceMany(bookFiles, replacedFiles);
                }
                else
                {
                    persist((connection, transaction) => _mediaFileRepository.ReplaceMany(bookFiles, replacedFiles, connection, transaction));
                }
            }
            catch
            {
                // An outer lifecycle transaction may roll back after InsertMany assigned IDs.
                foreach (var file in newFiles) file.Id = 0;
                throw;
            }

            PurgeIngestQueueEntries(replacedFiles);

            var deletionContext = new BookFileDeletionContext(eventBookFiles.Select(file => file.Id), bookFiles);
            var successorPathsByBook = bookFiles.Where(file => file.Edition?.BookId > 0)
                .GroupBy(file => file.Edition.BookId)
                .ToDictionary(group => group.Key, group => group
                    .SelectMany(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path))
                    .ToHashSet(PathEqualityComparer.Instance));
            // A committed successor now owns these ebook copies. Its postcommit reconciliation
            // decides which to retain or replace; the old row's deletion must not recycle them.
            foreach (var replacedFile in eventBookFiles)
            {
                if (successorPathsByBook.TryGetValue(replacedFile.Edition?.BookId ?? 0, out var successorPaths))
                {
                    replacedFile.ReplicaPaths = (replacedFile.ReplicaPaths ?? new List<string>())
                        .Where(path => !successorPaths.Contains(path)).ToList();
                }
                try
                {
                    _eventAggregator.PublishEvent(new BookFileDeletedEvent(replacedFile, reason,
                        associatedById.GetValueOrDefault(replacedFile.Id), bookFiles, deletionContext));
                }
                catch (Exception ex)
                {
                    // Persistence has committed. Never report an atomic replacement as failed
                    // (and trigger disk rollback) because a downstream observer failed.
                    _logger.Error(ex, "Failed publishing BookFileDeletedEvent after committed replacement for {0}", replacedFile.Path);
                }
            }

            if (newFiles.Count > 0)
            {
                try
                {
                    _eventAggregator.PublishEvent(new BookFilesAddedEvent(newFiles));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed publishing BookFilesAddedEvent after committed replacement of {0} files", bookFiles.Count);
                }
            }
        }

        public void ValidateReplacementFiles(List<BookFile> replacedFiles, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles)
        {
            ValidateReplacementAssociations(HydrateForDeleteEvents(replacedFiles ?? new List<BookFile>()), confirmedFiles);
        }

        private Dictionary<int, IReadOnlyCollection<string>> ValidateReplacementAssociations(
            List<BookFile> files, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles)
        {
            var associatedById = new Dictionary<int, IReadOnlyCollection<string>>();
            if (confirmedFiles == null || files.Count == 0)
            {
                return associatedById;
            }

            var current = (_replacementFiles ?? throw new InvalidOperationException("Replacement preview service is unavailable"))
                .Describe(files).ToDictionary(file => file.Id);
            var confirmedById = confirmedFiles.ToLookup(file => file.Id);
            foreach (var file in files)
            {
                var confirmed = confirmedById[file.Id].ToList();
                var allowed = ManualImportReplacementFiles.ConfirmedAssociatedPaths(file, confirmed);
                associatedById[file.Id] = allowed;
                if (!ManualImportReplacementFiles.IsConfirmed(file, confirmed) ||
                    current[file.Id].AssociatedPaths.Any(path => !allowed.Contains(path, PathEqualityComparer.Instance)))
                {
                    throw new CommandFailedException("Associated files changed since confirmation. Review the replacement files again.");
                }
            }

            return associatedById;
        }

        public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, AudioProductionCorrection correction, IDbConnection connection, IDbTransaction transaction) =>
            _mediaFileRepository.RelinkAndClassifyFilesByBook(bookId, editionId, mediaType, correction, connection, transaction);

        public void Update(BookFile bookFile)
        {
            _mediaFileRepository.Update(bookFile);
        }

        public void Update(List<BookFile> bookFiles)
        {
            _mediaFileRepository.UpdateMany(bookFiles);
        }

        public void Delete(BookFile bookFile, DeleteMediaFileReason reason)
        {
            PurgeIngestQueueEntries(new[] { bookFile });
            var eventBookFile = HydrateForDeleteEvent(bookFile);
            if (reason != DeleteMediaFileReason.MissingFromDisk || !PreserveManagedReplicas(new List<BookFile> { bookFile }, new[] { eventBookFile }))
            {
                _mediaFileRepository.Delete(bookFile);
            }

            if (eventBookFile?.EditionId > 0)
            {
                _eventAggregator.PublishEvent(new BookFileDeletedEvent(eventBookFile, reason));
            }
        }

        public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason)
        {
            PurgeIngestQueueEntries(bookFiles);
            var eventBookFiles = HydrateForDeleteEvents(bookFiles);
            if (reason != DeleteMediaFileReason.MissingFromDisk || !PreserveManagedReplicas(bookFiles, eventBookFiles))
            {
                _mediaFileRepository.DeleteMany(bookFiles);
            }

            var context = reason == DeleteMediaFileReason.MissingFromDisk || reason == DeleteMediaFileReason.Manual || reason == DeleteMediaFileReason.ManualOverride
                ? new BookFileDeletionContext(eventBookFiles.Select(file => file.Id), deletedFiles: eventBookFiles) : null;
            foreach (var bookFile in eventBookFiles)
            {
                _eventAggregator.PublishEvent(new BookFileDeletedEvent(bookFile, reason, context: context));
            }
        }

        private bool PreserveManagedReplicas(List<BookFile> deleted, IEnumerable<BookFile> hydrated)
        {
            var replicas = hydrated.Where(file => file != null)
                .SelectMany(file => (file.ReplicaPaths ?? new List<string>())
                    .Where(path => path.IsNotNullOrWhiteSpace() && path.PathNotEquals(file.Path))
                    .Select(path => (Path: path, File: file)))
                .DistinctBy(replica => replica.Path, PathEqualityComparer.Instance).ToList();
            if (replicas.Count == 0) return false;

            var existing = _mediaFileRepository.GetFileWithPath(replicas.Select(replica => replica.Path).ToList())
                .Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
            var kept = new List<BookFile>();
            foreach (var replica in replicas.Where(replica => !existing.Contains(replica.Path)))
            {
                // Probe before the transaction: a read failure leaves the original inventory intact.
                if (!_diskProvider.FileExistsCanonical(replica.Path)) continue;
                var file = replica.File.CloneForImport();
                file.Id = 0;
                file.Path = replica.Path;
                file.EditionId = 0;
                file.Edition = null;
                file.Author = null;
                file.ReplicaPaths = new List<string>();
                file.MatchProvenance = null;
                file.MatchDetails = "The primary file is missing. This managed copy was preserved for manual import.";
                kept.Add(file);
            }

            // The missing primary and its surviving copies change inventory atomically.
            _mediaFileRepository.ReplaceMany(kept, deleted);
            if (kept.Count > 0)
            {
                _eventAggregator.PublishEvent(new BookFilesAddedEvent(kept));
                _logger.Warn("Preserved {0} managed copies as unmapped files after {1} primary files went missing", kept.Count, deleted.Count);
            }

            return true;
        }

        private void PurgeIngestQueueEntries(IEnumerable<BookFile> bookFiles)
        {
            try
            {
                _ingestQueueRepository.PurgePaths(bookFiles?.Select(file => file?.Path));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to purge ingest queue entries for {0} deleted files", bookFiles?.Count() ?? 0);
            }
        }

        private void PurgeIngestQueueUnderAuthorPaths(Author author)
        {
            var paths = new[]
                {
                    author.Path,
                    author.AudiobookPath,
                    author.EbookPath
                }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in paths)
            {
                if (IsUnsafeAuthorPurgePath(path))
                {
                    _logger.Warn("Refusing to purge ingest queue under unsafe author path '{0}' for deleted author '{1}'", path, author.Name);
                    continue;
                }

                try
                {
                    var purged = _ingestQueueRepository.PurgeUnderPath(path);
                    if (purged > 0)
                    {
                        _logger.Info("Purged {0} stale ingest queue rows under deleted author path: {1}", purged, path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to purge ingest queue under deleted author path {0}", path);
                }
            }
        }

        private bool IsUnsafeAuthorPurgePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                return true;
            }

            var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var rootFolders = _rootFolderService?.All();
                if (rootFolders == null)
                {
                    return false;
                }

                return rootFolders.Any(r => r.Path.PathEquals(path) || path.IsParentPath(r.Path));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to validate author purge path '{0}' against root folders; refusing purge", path);
                return true;
            }
        }

        private BookFile HydrateForDeleteEvent(BookFile bookFile)
        {
            if (bookFile == null || bookFile.EditionId <= 0 || bookFile.Id <= 0)
            {
                return bookFile;
            }

            return _mediaFileRepository.Get(bookFile.Id) ?? bookFile;
        }

        private List<BookFile> HydrateForDeleteEvents(List<BookFile> bookFiles)
        {
            var mappedFiles = bookFiles?
                .Where(file => file != null && file.EditionId > 0)
                .ToList() ?? new List<BookFile>();

            if (mappedFiles.Count == 0)
            {
                return mappedFiles;
            }

            var hydratedById = _mediaFileRepository.Get(mappedFiles.Where(file => file.Id > 0).Select(file => file.Id))
                .ToDictionary(file => file.Id);

            return mappedFiles
                .Select(file => file.Id > 0 && hydratedById.TryGetValue(file.Id, out var hydrated) ? hydrated : file)
                .ToList();
        }

        public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter)
        {
            if (filter == FilterFilesType.None)
            {
                return files;
            }

            _logger.Debug("Filtering {0} files for unchanged files", files.Count);

            var knownFiles = GetFileWithPath(files.Select(x => x.FullName).ToList());
            _logger.Trace("Got {0} existing files", knownFiles.Count);

            if (!knownFiles.Any())
            {
                return files;
            }

            var combined = files
                .Join(knownFiles,
                      f => f.FullName,
                      af => af.Path,
                      (f, af) => new { DiskFile = f, DbFile = af },
                      PathEqualityComparer.Instance)
                .ToList();
            _logger.Trace("Matched paths for {0} files", combined.Count);

            List<IFileInfo> unwanted = null;
            if (filter == FilterFilesType.Known)
            {
                unwanted = combined
                    .Where(x => MediaFileFreshness.IsUnchanged(x.DbFile, x.DiskFile))
                    .Select(x => x.DiskFile)
                    .ToList();
                _logger.Trace("{0} unchanged existing files", unwanted.Count);
            }
            else if (filter == FilterFilesType.Matched)
            {
                unwanted = combined
                    // "Matched" means the file is already mapped in the DB (EditionId > 0).
                    // This filter is used for "Unmapped Files Only" views where we want to hide mapped files
                    // regardless of timestamp drift (e.g. file date settings, retagging, network mounts).
                    .Where(x => x.DbFile.EditionId > 0)
                    .Select(x => x.DiskFile)
                    .ToList();
                _logger.Trace("{0} matched files", unwanted.Count);
            }
            else
            {
                throw new ArgumentException("Unrecognised value of FilterFilesType filter");
            }

            return files.Except(unwanted).ToList();
        }


        public BookFile Get(int id)
        {
            return _mediaFileRepository.Get(id);
        }

        public List<BookFile> GetExisting(IEnumerable<int> ids)
        {
            var timer = Stopwatch.StartNew();
            var files = _mediaFileRepository.GetExisting(ids);
            _logger.Debug("Loaded {0} existing selected files in {1} ms", files.Count, timer.ElapsedMilliseconds);
            return files;
        }

        public List<BookFile> Get(IEnumerable<int> ids)
        {
            return _mediaFileRepository.Get(ids).ToList();
        }

        public List<BookFile> GetFilesWithBasePath(string path)
        {
            return _mediaFileRepository.GetFilesWithBasePath(path);
        }

        public List<BookFile> GetFilesWithBasePath(string path, string mediaType)
        {
            return _mediaFileRepository.GetFilesWithBasePath(path, mediaType);
        }

        public List<BookFile> GetFileStatsWithBasePath(string path, string mediaType = null)
        {
            if (_mediaFileRepository is MediaFileRepository concreteRepository)
            {
                return concreteRepository.GetFileStatsWithBasePath(path, mediaType);
            }

            return _mediaFileRepository.GetFilesWithBasePath(path, mediaType);
        }

        public List<BookFile> GetFileWithPath(List<string> path)
        {
            return _mediaFileRepository.GetFileWithPath(path);
        }

        public BookFile GetFileWithPath(string path)
        {
            return _mediaFileRepository.GetFileWithPath(path);
        }

        public List<BookFile> GetFilesByAuthor(int authorId)
        {
            return _mediaFileRepository.GetFilesByAuthor(authorId);
        }

        public List<BookFile> GetFilePathOwnership(IReadOnlyCollection<int> authorIds, IReadOnlyCollection<string> folders) =>
            _mediaFileRepository.GetFilePathOwnership(authorIds, folders);

        public void DeleteRecordsForAuthorDeletion(List<BookFile> files)
        {
            // The author deletion executor has already handled physical files and extras.
            // Do not publish per-file delete events that would attempt those operations again.
            PurgeIngestQueueEntries(files);
            if (!PreserveManagedReplicas(files, files))
            {
                _mediaFileRepository.DeleteMany(files);
            }
        }

        public List<BookFile> GetMappedFilePathEvidenceByAuthor(int authorId, string mediaType)
        {
            return _mediaFileRepository.GetMappedFilePathEvidenceByAuthor(authorId, mediaType);
        }

        public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType)
        {
            var files = GetFilesByAuthor(authorId);
            return files.Where(f => f.MediaType == mediaType).ToList();
        }

        public List<BookFile> GetFilesByBook(int bookId)
        {
            return _mediaFileRepository.GetFilesByBook(bookId);
        }

        public List<BookFile> GetFilesByBooks(List<int> bookIds)
        {
            return _mediaFileRepository.GetFilesByBooks(bookIds);
        }

        public List<BookFile> GetFilesByEdition(int editionId)
        {
            return _mediaFileRepository.GetFilesByEdition(editionId);
        }

        public List<BookFile> GetUnmappedFiles()
        {
            return _mediaFileRepository.GetUnmappedFiles();
        }

        public List<BookFile> GetUnmappedFiles(string mediaType)
        {
            return _mediaFileRepository.GetUnmappedFiles(mediaType);
        }

        public List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType)
        {
            return _mediaFileRepository.GetUnmappedFiles(ids, mediaType);
        }

        public void UpdateMediaInfo(List<BookFile> bookFiles)
        {
            _mediaFileRepository.SetFields(bookFiles, t => t.MediaInfo);
        }

        public void Handle(AuthorMovedEvent message)
        {
            var stopwatch = Stopwatch.StartNew();
            var files = _mediaFileRepository.GetFilesWithBasePath(message.SourcePath);
            var movedFiles = new List<BookFile>();
            // A removed source folder needs one check, not path recovery through its root per file.
            var sourceFolderExists = _diskProvider.FolderExists(message.SourcePath);

            foreach (var file in files)
            {
                var newPath = message.DestinationPath + file.Path.Substring(message.SourcePath.Length);
                // Keep paths for physically retained files. Missing rows must follow the completed
                // move too, so the destination's normal rescan can reclaim their stale inventory.
                var transferConfirmed = false;
                for (var path = file.Path; path != null && message.MovedPaths != null; path = Path.GetDirectoryName(path))
                {
                    if (message.MovedPaths.Contains(path))
                    {
                        transferConfirmed = true;
                        break;
                    }
                }

                if (!transferConfirmed && sourceFolderExists && _diskProvider.FileExistsCanonical(file.Path))
                {
                    _logger.Debug("Author folder moved, but book file '{0}' was not moved to '{1}'; keeping its recorded path", file.Path, newPath);
                    continue;
                }

                file.Path = newPath;
                movedFiles.Add(file);
            }

            var retainedCount = files.Count - movedFiles.Count;
            if (retainedCount > 0)
            {
                // Keep partial moves visible without one warning per file; debug retains every path.
                _logger.Warn("Author folder moved from '{0}' to '{1}', but {2} of {3} tracked book files were not moved; keeping their recorded paths. See debug log for individual paths.",
                    message.SourcePath, message.DestinationPath, retainedCount, files.Count);
            }

            if (movedFiles.Count > 0)
            {
                Update(movedFiles);
            }

            _logger.Debug("[AUTHOR-MOVE-TIMING] Checked {0} tracked book files from '{1}' to '{2}': updated {3}, retained {4} in {5}ms",
                files.Count, message.SourcePath, message.DestinationPath, movedFiles.Count, retainedCount, stopwatch.ElapsedMilliseconds);
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            PurgeIngestQueueEntries(message.Book?.BookFiles);
        }

        public void Handle(AuthorDeletedEvent message)
        {
            if (message?.Author == null)
            {
                return;
            }

            PurgeIngestQueueUnderAuthorPaths(message.Author);
        }

        public void HandleAsync(ModelEvent<RootFolder> message)
        {
            if (message.Action == ModelAction.Deleted)
            {
                var files = GetFilesWithBasePath(message.Model.Path);
                var remainingRoots = (_rootFolderService?.All() ?? new List<RootFolder>())
                    .Where(root => root.Id != message.Model.Id).ToList();
                files = files.Where(file => !remainingRoots.Any(root => root.Path.IsParentPath(file.Path))).ToList();
                // Removing a configured root withdraws tracking, never permission to delete bytes.
                if (files.Count > 0) DeleteMany(files, DeleteMediaFileReason.NoLinkedEpisodes);
            }
        }

        public void Handle(EditionDeletedEvent message)
        {
            var files = GetFilesByEdition(message.Edition.Id);
            if (files.Any())
            {
                _logger.Info("Unlinking {0} files from deleted edition {1}", files.Count, message.Edition.Id);
                foreach (var file in files)
                {
                    file.EditionId = 0;  // Unlink instead of delete
                }

                Update(files);
            }
        }
    }
}
