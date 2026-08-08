using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using NUnit.Framework;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class DrainPendingAuthorImportDeferralFixture
    {
        private sealed class RecordingIngestQueueRepository : IIngestQueueRepository
        {
            public readonly List<IngestQueueItem> QueueItems = new();
            public readonly List<(int Id, string Status, string Error)> StatusUpdates = new();
            public readonly List<(List<int> Ids, string Error)> RequeueCalls = new();
            public readonly List<(int QueueItemId, ImportOutcome Outcome, string ErrorMessage)> ImportResults = new();

            private static IEnumerable<IngestQueueItem> QueueItemsOrItems(RecordingIngestQueueRepository repository) => repository.QueueItems;
            private static IngestQueueItem CloneItem(IngestQueueItem item) => item;

            public void BeginSession(int commandId)
            {
            }

            public void InsertBatch(List<IngestQueueItem> items)
            {
                throw new NotImplementedException();
            }

            public List<IngestQueueItem> GetQueuedItems(int limit = 100)
            {
                return QueueItems.Where(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
            }

            public List<IngestQueueItem> GetQueuedItemsUnderPath(string pathPrefix, int limit = 100, int afterId = 0)
            {
                return QueueItems
                    .Where(item => item.Id > afterId)
                    .Where(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    .Where(item => item.Path != null && item.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .ToList();
            }

            public int GetActiveCountUnderPath(string pathPrefix)
            {
                return QueueItems.Count(item =>
                    item.Path != null &&
                    item.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Status, "done", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Status, "error", StringComparison.OrdinalIgnoreCase));
            }

            public List<IngestQueueStatusCount> GetActiveStatusCountsUnderPath(string pathPrefix)
            {
                return GetActiveItemsUnderPath(pathPrefix, int.MaxValue)
                    .GroupBy(item => item.Status ?? string.Empty)
                    .Select(group => new IngestQueueStatusCount { Status = group.Key, Count = group.Count() })
                    .ToList();
            }

            public List<IngestQueueItem> GetActiveItemsUnderPath(string pathPrefix, int limit = 20)
            {
                return QueueItems
                    .Where(item => item.Path != null && item.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(item.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .ToList();
            }

            public List<IngestQueueItem> GetActiveItems(int limit = 1000, int afterId = 0)
            {
                return QueueItemsOrItems(this)
                    .Where(item => item.Id > afterId)
                    .Where(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(item.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .Select(CloneItem)
                    .ToList();
            }

            public List<IngestQueueItem> GetActiveItemsForSweepUnderPath(string pathPrefix, int limit = 1000, int afterId = 0)
            {
                if (string.IsNullOrWhiteSpace(pathPrefix))
                {
                    return new List<IngestQueueItem>();
                }

                return QueueItemsOrItems(this)
                    .Where(item => item.Id > afterId)
                    .Where(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(item.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                    .Where(item => item.Path != null && item.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .Select(CloneItem)
                    .ToList();
            }

            public int RecoverStaleInProgress(string pathPrefix, int staleMinutes = 10)
            {
                return 0;
            }

            public int RecoverInProgressUpdatedBefore(string pathPrefix, long updatedBefore, string error = null)
            {
                return 0;
            }

            public bool TryClaimItem(int id, out IngestQueueItem item)
            {
                item = QueueItems.FirstOrDefault(x => x.Id == id && string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    return false;
                }

                item.Status = "in_progress";
                return true;
            }

            public List<IngestQueueItem> TryClaimUnit(string folderPath)
            {
                return QueueItems
                    .Where(item => item.Path != null && item.Path.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                    .Where(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    .Select(item =>
                    {
                        item.Status = "in_progress";
                        return item;
                    })
                    .ToList();
            }

            public void UpdateStatus(int id, string status, string error = null)
            {
                StatusUpdates.Add((id, status, error));
                var item = QueueItems.FirstOrDefault(x => x.Id == id);
                if (item != null)
                {
                    item.Status = status;
                    item.Err = error;
                }
            }

            public void UpdateBatchTagsJson(IEnumerable<(int Id, string TagsJson)> items)
            {
                throw new NotImplementedException();
            }

            public void UpdateBatchTagsAndDuration(IEnumerable<(int Id, string TagsJson, int? DurationSeconds)> items)
            {
                throw new NotImplementedException();
            }

            public void UpdateBatchStatus(List<int> ids, string status)
            {
                throw new NotImplementedException();
            }

            public void RequeueInProgress(List<int> ids, string error = null)
            {
                var uniqueIds = ids?.Distinct().ToList() ?? new List<int>();
                RequeueCalls.Add((uniqueIds, error));

                foreach (var id in uniqueIds)
                {
                    var item = QueueItems.FirstOrDefault(x => x.Id == id && string.Equals(x.Status, "in_progress", StringComparison.OrdinalIgnoreCase));
                    if (item == null)
                    {
                        continue;
                    }

                    item.Status = "queued";
                    item.Err = error;
                }
            }

            public int GetQueueCount()
            {
                return QueueItems.Count(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase));
            }

            public int RequeueFailedOrUnmappedUnderPath(string pathPrefix)
            {
                throw new NotImplementedException();
            }

            public int RequeueFailedPaths(IEnumerable<string> paths)
            {
                throw new NotImplementedException();
            }

            public int PurgeUnderPath(string pathPrefix)
            {
                throw new NotImplementedException();
            }

            public int PurgePaths(IEnumerable<string> paths)
            {
                throw new NotImplementedException();
            }

            public void PurgeOldCompleted(int daysToKeep = 14)
            {
                throw new NotImplementedException();
            }

            public void RecordImportResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null)
            {
                ImportResults.Add((queueItemId, outcome, errorMessage));
            }

            public void CompleteItemWithResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null, string statusError = null)
            {
                RecordImportResult(queueItemId, path, outcome, bookId, authorId, quality, errorMessage);
                UpdateStatus(queueItemId, "done", statusError);
            }

            public List<NzbDrone.Core.Datastore.ImportResult> GetImportResults(int? commandId = null)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class RecordingFileMatchingService : IFileMatchingService
        {
            public readonly List<MatchingContext> Contexts = new();
            public readonly List<DiscoveredFileWithMetadata[]> FilesByCall = new();
            public Func<DiscoveredFileWithMetadata[], int?, MatchingContext, FileMatchResult> ResultFactory { get; set; }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, null, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context)
            {
                Contexts.Add(context ?? new MatchingContext());
                FilesByCall.Add(filesWithMetadata?.ToArray() ?? Array.Empty<DiscoveredFileWithMetadata>());

                if (ResultFactory != null)
                {
                    return Task.FromResult(ResultFactory(filesWithMetadata ?? Array.Empty<DiscoveredFileWithMetadata>(), restrictToAuthorId, context));
                }

                var file = filesWithMetadata?.FirstOrDefault();
                if (context?.AllowV5Identification == true)
                {
                    return Task.FromResult(new FileMatchResult
                    {
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = file,
                                Reason = "NO_MATCH",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:frank-herbert",
                                        AuthorName = "Frank Herbert"
                                    }
                                }
                            }
                        }
                    });
                }

                return Task.FromResult(new FileMatchResult
                {
                    UnmatchedFiles = new[]
                    {
                        new UnmatchedFile
                        {
                            File = file,
                            Reason = "NO_MATCH"
                        }
                    }
                });
            }

            public EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, BookMediaType mediaType)
            {
                throw new NotImplementedException();
            }

            public FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, BookMediaType mediaType, int? restrictToAuthorId = null)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class RecordingBookImportService : IBookImportService
        {
            public BookImportFileResult Result { get; set; }

            public Task ImportFileAsync(string path, int bookId, string quality) => throw new NotImplementedException();
            public Task ImportFileAsync(string path, int bookId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task ImportFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags) => Task.FromResult(Result);
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds) => Task.FromResult(Result);
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds, MatchProvenance provenance) => Task.FromResult(Result);
            public Task<BookImportFileResult> ImportExistingFileAsync(DiscoveredFileWithMetadata file, int bookId, int? editionId, string quality, MatchProvenance provenance) => Task.FromResult(Result);
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)> files, int bookId) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)> files, int bookId) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags)> files, int bookId, int? editionId, string quality) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId, int? editionId, string quality) => throw new NotImplementedException();
        }

        private sealed class RecordingBookUnitDestinationService : IBookUnitDestinationService
        {
            public string BuildRootUnitKeyWithExtension(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType) => anyFilePathInUnit;
            public (int BookId, int EditionId) ResolveDestinationForUnit(Book canonicalBook, Edition canonicalEdition, string unitKey) => (canonicalBook.Id, canonicalEdition.Id);
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Edition Edition { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return Edition;
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingAuthorLibraryService : IAuthorLibraryService
        {
            public readonly List<(string ProviderId, MonitoringConfig Config)> AddCalls = new();
            public Author Result { get; set; }

            public Task<Author> AddAuthorAsync(string providerId, MonitoringConfig config = null)
            {
                AddCalls.Add((providerId, config));
                return Task.FromResult(Result);
            }

            public Task<Author> AddAuthorMonitoringBookAsync(string authorProviderId, string bookProviderId) => throw new NotImplementedException();
            public Task<List<Author>> AddAuthorsMonitoringSeriesAsync(string[] authorProviderIds, string seriesProviderId) => throw new NotImplementedException();
            public Task<Author> RefreshAuthorAsync(int authorId) => throw new NotImplementedException();
            public Task RemoveAuthorAsync(int authorId) => throw new NotImplementedException();
        }

        private class RecordingMediaFileRepositoryProxy : DispatchProxy
        {
            public int InsertCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileRepository.InsertManyIgnoreDuplicatePaths) &&
                    args?.Length == 1 &&
                    args[0] is List<BookFile> files)
                {
                    InsertCalls++;
                    return files.Count;
                }

                if (targetMethod?.Name == nameof(IMediaFileRepository.GetFileWithPath) &&
                    args?.Length == 1 &&
                    args[0] is string)
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IMediaFileRepository).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubDiskProvider : IDiskProvider
        {
            private readonly string _scannedPath;
            private readonly IFileInfo _fileInfo;

            public StubDiskProvider(string scannedPath, IFileInfo fileInfo = null)
            {
                _scannedPath = scannedPath;
                _fileInfo = fileInfo;
            }

            public bool FileExists(string path) => string.Equals(path, _scannedPath, StringComparison.OrdinalIgnoreCase);
            public bool FileExistsCanonical(string path) => FileExists(path);
            public bool FolderExists(string path) => false;
            public bool FileExists(string path, StringComparison stringComparison) => FileExists(path);
            public IFileInfo GetFileInfo(string path) => _fileInfo ?? throw new NotImplementedException();
            public long GetFileSize(string path) => throw new NotImplementedException();
            public DateTime FileGetLastWrite(string path) => throw new NotImplementedException();
            public bool IsFileLocked(string path) => false;
            public long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public long? GetTotalSize(string path) => throw new NotImplementedException();
            public DateTime FolderGetCreationTime(string path) => throw new NotImplementedException();
            public DateTime FolderGetLastWrite(string path) => throw new NotImplementedException();
            public void EnsureFolder(string path) => throw new NotImplementedException();
            public bool FolderWritable(string path) => throw new NotImplementedException();
            public bool FolderEmpty(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetDirectories(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetFiles(string path, bool recursive) => throw new NotImplementedException();
            public long GetFolderSize(string path) => throw new NotImplementedException();
            public void CreateFolder(string path) => throw new NotImplementedException();
            public void DeleteFile(string path) => throw new NotImplementedException();
            public void CloneFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void CopyFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void MoveFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void MoveFolder(string source, string destination) => throw new NotImplementedException();
            public bool TryRenameFile(string source, string destination) => throw new NotImplementedException();
            public bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
            public int? GetFileLinkCount(string path) => 1;
            public bool TryCreateRefLink(string source, string destination) => throw new NotImplementedException();
            public void DeleteFolder(string path, bool recursive) => throw new NotImplementedException();
            public string ReadAllText(string filePath) => throw new NotImplementedException();
            public void WriteAllText(string filename, string contents) => throw new NotImplementedException();
            public void FolderSetLastWriteTime(string path, DateTime dateTime) => throw new NotImplementedException();
            public void FileSetLastWriteTime(string path, DateTime dateTime) => throw new NotImplementedException();
            public string GetPathRoot(string path) => throw new NotImplementedException();
            public string GetParentFolder(string path) => throw new NotImplementedException();
            public FileAttributes GetFileAttributes(string path) => throw new NotImplementedException();
            public void EmptyFolder(string path) => throw new NotImplementedException();
            public string GetVolumeLabel(string path) => throw new NotImplementedException();
            public FileStream OpenReadStream(string path) => throw new NotImplementedException();
            public FileStream OpenWriteStream(string path) => throw new NotImplementedException();
            public List<IMount> GetMounts() => throw new NotImplementedException();
            public IMount GetMount(string path) => throw new NotImplementedException();
            public IDirectoryInfo GetDirectoryInfo(string path) => throw new NotImplementedException();
            public List<IDirectoryInfo> GetDirectoryInfos(string path) => throw new NotImplementedException();
            public List<IFileInfo> GetFileInfos(string path, bool recursive = false) => throw new NotImplementedException();
            public void RemoveEmptySubfolders(string path) => throw new NotImplementedException();
            public void SaveStream(Stream stream, string path) => throw new NotImplementedException();
            public bool IsValidFolderPermissionMask(string mask) => throw new NotImplementedException();
        }

        private sealed class ThrowingMetadataTagService : IMetadataTagService
        {
            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => throw Failure(file);
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(IFileInfo file) => throw Failure(file);
            public string ReadAllTagsAsJson(IFileInfo file) => throw Failure(file);
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void Execute(RetagFilesCommand message) => throw new NotImplementedException();
            public void Execute(RetagAuthorCommand message) => throw new NotImplementedException();

            private static TagExtractionException Failure(IFileInfo file)
            {
                return new TagExtractionException(file?.FullName ?? "<unknown>", new IOException("transient read failure"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
#pragma warning disable SYSLIB0050
        public async Task drain_should_terminalize_pending_author_import_as_unmapped_when_author_add_is_pending(bool returnPendingAuthorRow)
        {
            const string scannedPath = "/staging/Frank Herbert/Whipping Star/Whipping Star.m4b";

            var queue = new RecordingIngestQueueRepository();
            queue.QueueItems.Add(new IngestQueueItem
            {
                Id = 101,
                Path = scannedPath,
                Status = "queued",
                TagsJson = "{\"TITLE\":[\"Whipping Star\"],\"ARTIST\":[\"Frank Herbert\"]}",
                DurationSeconds = 600
            });

            var matching = new RecordingFileMatchingService();
            var authorLibrary = new RecordingAuthorLibraryService
            {
                Result = returnPendingAuthorRow
                    ? new Author { Id = -42, Name = "Frank Herbert" }
                    : null
            };
            var mediaFiles = DispatchProxy.Create<IMediaFileRepository, RecordingMediaFileRepositoryProxy>();
            var mediaFilesProxy = (RecordingMediaFileRepositoryProxy)(object)mediaFiles;

            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_ingestQueue", queue);
            SetField(sut, "_fileMatching", matching);
            SetField(sut, "_authorLibraryService", authorLibrary);
            SetField(sut, "_diskProvider", new StubDiskProvider(scannedPath));
            SetField(sut, "_mediaFileRepository", mediaFiles);
            SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

            var method = typeof(ImportOrchestratorV2).GetMethod("DrainRemainingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Unable to locate private DrainRemainingAsync");

            await ((Task)method.Invoke(sut, new object[] { new IngestQueueScanScope(scannedPath), null }));

            Assert.That(matching.Contexts, Has.Count.EqualTo(2), "Drain should make one local pass and one V5 recovery pass");
            Assert.That(matching.Contexts[0].AllowV5Identification, Is.False);
            Assert.That(matching.Contexts[1].AllowV5Identification, Is.True);
            Assert.That(authorLibrary.AddCalls, Has.Count.EqualTo(1), "Drain should attempt author import for the V5 suggestion");
            Assert.That(authorLibrary.AddCalls[0].ProviderId, Is.EqualTo("hc:frank-herbert"));

            Assert.That(queue.StatusUpdates.Any(update => update.Status == "done" && update.Error == "PENDING_AUTHOR_IMPORT"), Is.True,
                "Pending author imports should be terminalized immediately so they do not remain hidden in staging");
            Assert.That(queue.ImportResults.Any(result => result.Outcome == ImportOutcome.Unmapped && result.ErrorMessage == "PENDING_AUTHOR_IMPORT"), Is.True,
                "Pending author imports should be surfaced as unmapped during drain");
            Assert.That(mediaFilesProxy.InsertCalls, Is.EqualTo(1),
                "Drain should create an unmapped BookFile row for pending author imports");
        }

        [Test]
        public async Task drain_should_apply_consensus_unit_tags_when_staging_tags_are_missing_in_large_units()
        {
            const string scannedPath = "/staging/Frank Herbert/Dune";

            var queue = new RecordingIngestQueueRepository();
            queue.QueueItems.Add(new IngestQueueItem
            {
                Id = 201,
                Path = "/staging/Frank Herbert/Dune/01.mp3",
                Status = "queued",
                TagsJson = "{}",
                DurationSeconds = 600
            });

            for (var index = 2; index <= 6; index++)
            {
                queue.QueueItems.Add(new IngestQueueItem
                {
                    Id = 200 + index,
                    Path = $"/staging/Frank Herbert/Dune/{index:00}.mp3",
                    Status = "queued",
                    TagsJson = $"{{\"ALBUM\":[\"Dune\"],\"ALBUMARTIST\":[\"Frank Herbert\"],\"TITLE\":[\"Track {index}\"]}}",
                    DurationSeconds = 600
                });
            }

            var matching = new RecordingFileMatchingService();
            var authorLibrary = new RecordingAuthorLibraryService
            {
                Result = null
            };
            var mediaFiles = DispatchProxy.Create<IMediaFileRepository, RecordingMediaFileRepositoryProxy>();

            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_ingestQueue", queue);
            SetField(sut, "_fileMatching", matching);
            SetField(sut, "_authorLibraryService", authorLibrary);
            SetField(sut, "_diskProvider", new StubDiskProvider("/does/not/exist"));
            SetField(sut, "_mediaFileRepository", mediaFiles);
            SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

            var method = typeof(ImportOrchestratorV2).GetMethod("DrainRemainingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Unable to locate private DrainRemainingAsync");

            await ((Task)method.Invoke(sut, new object[] { new IngestQueueScanScope(scannedPath), null }));

            Assert.That(matching.FilesByCall.Count, Is.GreaterThan(0), "Expected at least one matching call");

            var firstPassFiles = matching.FilesByCall.First();
            var tagLightFile = firstPassFiles.FirstOrDefault(file => file.Path.EndsWith("/01.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.That(tagLightFile, Is.Not.Null, "Expected the tag-light file to be included in the unit");
            Assert.That(tagLightFile.AllTags.ContainsKey("ALBUM"), Is.True);
            Assert.That(tagLightFile.AllTags["ALBUM"], Is.EquivalentTo(new[] { "Dune" }));
            Assert.That(tagLightFile.AllTags.ContainsKey("ALBUMARTIST"), Is.True);
            Assert.That(tagLightFile.AllTags["ALBUMARTIST"], Is.EquivalentTo(new[] { "Frank Herbert" }));
            Assert.That(tagLightFile.AllTags.ContainsKey("TITLE"), Is.False,
                "Consensus unit tags should not smear a per-track singleton title into missing-tag files");
        }

        [Test]
        public async Task drain_should_record_the_typed_apply_failure_instead_of_imported()
        {
            const string scannedPath = "/staging/Frank Herbert/Dune/Dune.epub";
            var queue = new RecordingIngestQueueRepository();
            queue.QueueItems.Add(new IngestQueueItem
            {
                Id = 240,
                Path = scannedPath,
                Status = "queued",
                TagsJson = "{\"TITLE\":[\"Dune\"],\"ARTIST\":[\"Frank Herbert\"]}",
                DurationSeconds = 100
            });

            var matching = new RecordingFileMatchingService
            {
                ResultFactory = (files, _, _) => new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                    },
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                }
            };
            var import = new RecordingBookImportService
            {
                Result = BookImportFileResult.Failed(scannedPath, "FILE_MISSING_AT_APPLY")
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = new Book
            {
                Id = 10,
                AuthorId = 5,
                Title = "Dune",
                MediaType = BookMediaType.Ebook
            };
            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 20, BookId = 10, Title = "Dune" };

            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_ingestQueue", queue);
            SetField(sut, "_fileMatching", matching);
            SetField(sut, "_bookImport", import);
            SetField(sut, "_bookService", bookService);
            SetField(sut, "_editionService", editionService);
            SetField(sut, "_unitDestination", new RecordingBookUnitDestinationService());
            SetField(sut, "_diskProvider", new StubDiskProvider(scannedPath));
            SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

            var method = typeof(ImportOrchestratorV2).GetMethod("DrainRemainingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Unable to locate private DrainRemainingAsync");

            await ((Task)method.Invoke(sut, new object[] { new IngestQueueScanScope(scannedPath), null }));

            Assert.That(queue.ImportResults, Has.Count.EqualTo(1));
            Assert.That(queue.ImportResults[0].Outcome, Is.EqualTo(ImportOutcome.Failed));
            Assert.That(queue.ImportResults[0].ErrorMessage, Is.EqualTo("FILE_MISSING_AT_APPLY"));
            Assert.That(queue.ImportResults.Any(result => result.Outcome == ImportOutcome.Imported), Is.False);
        }

        [Test]
        public async Task drain_should_record_already_linked_without_reporting_a_new_import()
        {
            const string scannedPath = "/staging/Frank Herbert/Dune/Dune.epub";
            var queue = new RecordingIngestQueueRepository();
            queue.QueueItems.Add(new IngestQueueItem
            {
                Id = 241,
                Path = scannedPath,
                Status = "queued",
                TagsJson = "{\"TITLE\":[\"Dune\"],\"ARTIST\":[\"Frank Herbert\"]}",
                DurationSeconds = 100
            });

            var matching = new RecordingFileMatchingService
            {
                ResultFactory = (files, _, _) => new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                    },
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                }
            };
            var import = new RecordingBookImportService
            {
                Result = BookImportFileResult.AlreadyLinked(scannedPath, 1001)
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = new Book
            {
                Id = 10,
                AuthorId = 5,
                Title = "Dune",
                MediaType = BookMediaType.Ebook
            };
            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 20, BookId = 10, Title = "Dune" };

            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_ingestQueue", queue);
            SetField(sut, "_fileMatching", matching);
            SetField(sut, "_bookImport", import);
            SetField(sut, "_bookService", bookService);
            SetField(sut, "_editionService", editionService);
            SetField(sut, "_unitDestination", new RecordingBookUnitDestinationService());
            SetField(sut, "_diskProvider", new StubDiskProvider(scannedPath));
            SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

            var method = typeof(ImportOrchestratorV2).GetMethod("DrainRemainingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Unable to locate private DrainRemainingAsync");

            await ((Task)method.Invoke(sut, new object[] { new IngestQueueScanScope(scannedPath), null }));

            Assert.That(queue.ImportResults, Has.Count.EqualTo(1));
            Assert.That(queue.ImportResults[0].Outcome, Is.EqualTo(ImportOutcome.AlreadyLinked));
            Assert.That(queue.ImportResults[0].ErrorMessage, Is.Null);
            Assert.That(queue.ImportResults.Any(result => result.Outcome == ImportOutcome.Imported), Is.False);
        }

        [Test]
        public void drain_should_requeue_claimed_items_when_a_batch_exception_escapes()
        {
            const string scannedPath = "/staging/Frank Herbert/Dune/01.mp3";

            var queue = new RecordingIngestQueueRepository();
            queue.QueueItems.Add(new IngestQueueItem
            {
                Id = 250,
                Path = scannedPath,
                Status = "queued",
                TagsJson = "{}",
                DurationSeconds = null
            });

            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_ingestQueue", queue);
            SetField(sut, "_diskProvider", new StubDiskProvider(scannedPath));
            SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

            var method = typeof(ImportOrchestratorV2).GetMethod("DrainRemainingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Unable to locate private DrainRemainingAsync");

            var task = (Task)method.Invoke(sut, new object[] { new IngestQueueScanScope(scannedPath), null });

            Assert.That(async () => await task, Throws.TypeOf<NullReferenceException>());
            Assert.That(queue.RequeueCalls, Has.Count.EqualTo(1));
            Assert.That(queue.RequeueCalls[0].Ids, Is.EquivalentTo(new[] { 250 }));
            Assert.That(queue.RequeueCalls[0].Error, Is.EqualTo("DRAIN_BATCH_ABORTED"));
            Assert.That(queue.QueueItems.Single().Status, Is.EqualTo("queued"));
            Assert.That(queue.QueueItems.Single().Err, Is.EqualTo("DRAIN_BATCH_ABORTED"));
        }

        [Test]
        public async Task drain_should_keep_failed_extraction_visible_and_record_a_retryable_failed_result()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"drain_tag_failure_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var scannedPath = Path.Combine(root, "failed.m4b");
            File.WriteAllText(scannedPath, "not really audio");

            try
            {
                var fileInfo = new FileSystem().FileInfo.FromFileName(scannedPath);
                var queue = new RecordingIngestQueueRepository();
                queue.QueueItems.Add(new IngestQueueItem
                {
                    Id = 260,
                    Path = scannedPath,
                    Status = "queued",
                    TagsJson = "{}",
                    MtimeNs = 0,
                    SizeBytes = fileInfo.Length
                });

                var matching = new RecordingFileMatchingService
                {
                    ResultFactory = (_, _, _) => new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };
                var mediaFiles = DispatchProxy.Create<IMediaFileRepository, RecordingMediaFileRepositoryProxy>();
                var mediaFilesProxy = (RecordingMediaFileRepositoryProxy)(object)mediaFiles;

                var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
                SetField(sut, "_ingestQueue", queue);
                SetField(sut, "_fileMatching", matching);
                SetField(sut, "_metadataTagService", new ThrowingMetadataTagService());
                SetField(sut, "_diskProvider", new StubDiskProvider(scannedPath, fileInfo));
                SetField(sut, "_mediaFileRepository", mediaFiles);
                SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

                var method = typeof(ImportOrchestratorV2).GetMethod("DrainRemainingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null);
                await ((Task)method.Invoke(sut, new object[] { new IngestQueueScanScope(scannedPath), null }));

                Assert.That(queue.ImportResults, Has.Count.EqualTo(1));
                Assert.That(queue.ImportResults[0].Outcome, Is.EqualTo(ImportOutcome.Failed));
                Assert.That(queue.ImportResults[0].ErrorMessage, Is.EqualTo(TagExtractionResult.FailureReason));
                Assert.That(mediaFilesProxy.InsertCalls, Is.EqualTo(1), "the still-present path must remain visible as an unmapped BookFile row");
                Assert.That(matching.FilesByCall.All(files => files.Length == 0), Is.True, "a failed read must not be matched as if it were tagless");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Unable to locate private field {fieldName}");
            field.SetValue(target, value);
        }
    }
}
