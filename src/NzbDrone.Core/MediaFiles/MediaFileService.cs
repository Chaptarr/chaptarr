using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileService
    {
        BookFile Add(BookFile bookFile);
        void AddMany(List<BookFile> bookFiles);
        void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason)
        {
            if (replacedFiles?.Count > 0)
            {
                throw new InvalidOperationException("This media-file service does not support atomic replacement");
            }

            AddMany(bookFiles ?? new List<BookFile>());
        }
        void Update(BookFile bookFile);
        void Update(List<BookFile> bookFiles);
        void Delete(BookFile bookFile, DeleteMediaFileReason reason);
        void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason);
        List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter);
        List<BookFile> GetFilesByAuthor(int authorId);
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
        private readonly Logger _logger;

        public MediaFileService(IMediaFileRepository mediaFileRepository, IEventAggregator eventAggregator, IIngestQueueRepository ingestQueueRepository, Logger logger, IRootFolderService rootFolderService = null)
        {
            _mediaFileRepository = mediaFileRepository;
            _eventAggregator = eventAggregator;
            _ingestQueueRepository = ingestQueueRepository;
            _rootFolderService = rootFolderService;
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

        public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason)
        {
            bookFiles ??= new List<BookFile>();
            replacedFiles ??= new List<BookFile>();

            var eventBookFiles = HydrateForDeleteEvents(replacedFiles);

            _mediaFileRepository.ReplaceMany(bookFiles, replacedFiles);
            PurgeIngestQueueEntries(replacedFiles);

            foreach (var replacedFile in eventBookFiles)
            {
                try
                {
                    _eventAggregator.PublishEvent(new BookFileDeletedEvent(replacedFile, reason));
                }
                catch (Exception ex)
                {
                    // Persistence has committed. Never report an atomic replacement as failed
                    // (and trigger disk rollback) because a downstream observer failed.
                    _logger.Error(ex, "Failed publishing BookFileDeletedEvent after committed replacement for {0}", replacedFile.Path);
                }
            }

            if (bookFiles.Count > 0)
            {
                try
                {
                    _eventAggregator.PublishEvent(new BookFilesAddedEvent(bookFiles));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed publishing BookFilesAddedEvent after committed replacement of {0} files", bookFiles.Count);
                }
            }
        }

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
            PurgeIngestQueueEntry(bookFile?.Path);
            var eventBookFile = HydrateForDeleteEvent(bookFile);
            _mediaFileRepository.Delete(bookFile);

            if (eventBookFile?.EditionId > 0)
            {
                _eventAggregator.PublishEvent(new BookFileDeletedEvent(eventBookFile, reason));
            }
        }

        public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason)
        {
            PurgeIngestQueueEntries(bookFiles);
            var eventBookFiles = HydrateForDeleteEvents(bookFiles);
            _mediaFileRepository.DeleteMany(bookFiles);

            foreach (var bookFile in eventBookFiles)
            {
                _eventAggregator.PublishEvent(new BookFileDeletedEvent(bookFile, reason));
            }
        }

        private void PurgeIngestQueueEntries(IEnumerable<BookFile> bookFiles)
        {
            foreach (var path in bookFiles?
                         .Where(f => !string.IsNullOrWhiteSpace(f?.Path))
                         .Select(f => f.Path)
                         .Distinct(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>())
            {
                PurgeIngestQueueEntry(path);
            }
        }

        private void PurgeIngestQueueEntry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                _ingestQueueRepository.PurgeUnderPath(path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to purge ingest queue entry for deleted file {0}", path);
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
            var files = _mediaFileRepository.GetFilesWithBasePath(message.SourcePath);

            foreach (var file in files)
            {
                var newPath = message.DestinationPath + file.Path.Substring(message.SourcePath.Length);
                file.Path = newPath;
            }

            Update(files);
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            var bookFiles = message.Book?.BookFiles;
            if ((bookFiles == null || bookFiles.Count == 0) && message.Book?.Id > 0)
            {
                try
                {
                    bookFiles = _mediaFileRepository.GetFilesByBook(message.Book.Id);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to load book files for deleted book ingest queue purge: {0}", message.Book.Id);
                }
            }

            PurgeIngestQueueEntries(bookFiles);

            if (message.DeleteFiles)
            {
                _mediaFileRepository.DeleteFilesByBook(message.Book.Id);
            }
            else
            {
                _mediaFileRepository.UnlinkFilesByBook(message.Book.Id);
            }
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
                DeleteMany(files, DeleteMediaFileReason.Manual);
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
