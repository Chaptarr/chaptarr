using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorReadyCrossAuthorAcceptanceFixture
    {
        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null) { }
            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null) { }
            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null) { }
            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000) => new List<MatchingLogEntry>();
            public void ClearLogs() { }
        }

        private sealed class RecordingMatchingService : IFileMatchingService
        {
            public readonly List<(int? RestrictToAuthorId, MatchingContext Context, int FileCount)> Calls = new();
            public Func<DiscoveredFileWithMetadata[], FileMatchResult> ResultFactory { get; set; }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, null, false);
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, false);
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext
                {
                    PerFileMatching = forDownloads
                });
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context)
            {
                Calls.Add((restrictToAuthorId, context, filesWithMetadata?.Length ?? 0));

                if (ResultFactory != null)
                {
                    return Task.FromResult(ResultFactory(filesWithMetadata ?? Array.Empty<DiscoveredFileWithMetadata>()));
                }

                var file = filesWithMetadata?.FirstOrDefault();
                if (file == null)
                {
                    return Task.FromResult(new FileMatchResult());
                }

                return Task.FromResult(new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch
                        {
                            File = file,
                            AuthorId = 6,
                            AuthorName = "Brian Herbert",
                            BookId = 224,
                            BookTitle = "Whipping Star",
                            EditionId = 675
                        }
                    },
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                });
            }

            public EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, BookMediaType mediaType) => throw new NotImplementedException();
            public FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, BookMediaType mediaType, int? restrictToAuthorId = null) => throw new NotImplementedException();
        }

        private sealed class RecordingIngestQueueRepository : IIngestQueueRepository
        {
            private readonly List<IngestQueueItem> _items = new();

            public readonly List<(int QueueItemId, string Path, ImportOutcome Outcome, int? BookId, int? AuthorId, string ErrorMessage)> Results = new();
            public readonly List<(int Id, string Status, string Error)> StatusUpdates = new();

            private static IEnumerable<IngestQueueItem> QueueItemsOrItems(RecordingIngestQueueRepository repository) => repository._items;
            private static IngestQueueItem CloneItem(IngestQueueItem item) => Clone(item);

            public RecordingIngestQueueRepository(params IngestQueueItem[] items)
            {
                if (items != null)
                {
                    _items.AddRange(items);
                }
            }

            public void BeginSession(int commandId) { }
            public void InsertBatch(List<IngestQueueItem> items) => throw new NotImplementedException();

            public List<IngestQueueItem> GetQueuedItems(int limit = 100)
            {
                return _items
                    .Where(x => string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .Select(Clone)
                    .ToList();
            }

            public List<IngestQueueItem> GetQueuedItemsUnderPath(string pathPrefix, int limit = 100, int afterId = 0)
            {
                if (string.IsNullOrWhiteSpace(pathPrefix))
                {
                    return new List<IngestQueueItem>();
                }

                var normalizedPrefix = NormalizePath(pathPrefix);
                return _items
                    .Where(x => x.Id > afterId)
                    .Where(x => string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    .Where(x => PathMatches(x.Path, normalizedPrefix))
                    .OrderBy(x => x.Id)
                    .Take(limit)
                    .Select(Clone)
                    .ToList();
            }

            public int GetActiveCountUnderPath(string pathPrefix)
            {
                return GetActiveItemsUnderPath(pathPrefix, int.MaxValue).Count;
            }

            public List<IngestQueueStatusCount> GetActiveStatusCountsUnderPath(string pathPrefix)
            {
                return GetActiveItemsUnderPath(pathPrefix, int.MaxValue)
                    .GroupBy(x => x.Status ?? string.Empty)
                    .Select(g => new IngestQueueStatusCount { Status = g.Key, Count = g.Count() })
                    .ToList();
            }

            public List<IngestQueueItem> GetActiveItemsUnderPath(string pathPrefix, int limit = 20)
            {
                if (string.IsNullOrWhiteSpace(pathPrefix))
                {
                    return new List<IngestQueueItem>();
                }

                var normalizedPrefix = NormalizePath(pathPrefix);
                return _items
                    .Where(x => string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                    .Where(x => PathMatches(x.Path, normalizedPrefix))
                    .Take(limit)
                    .Select(Clone)
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

            public int RecoverStaleInProgress(string pathPrefix, int staleMinutes = 10) => 0;
            public int RecoverInProgressUpdatedBefore(string pathPrefix, long updatedBefore, string error = null) => 0;

            public bool TryClaimItem(int id, out IngestQueueItem item)
            {
                var match = _items.FirstOrDefault(x => x.Id == id && string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    item = null;
                    return false;
                }

                match.Status = "in_progress";
                match.Attempts++;
                item = Clone(match);
                return true;
            }

            public List<IngestQueueItem> TryClaimUnit(string folderPath)
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    return new List<IngestQueueItem>();
                }

                var normalizedFolder = NormalizePath(folderPath);
                var claimed = _items
                    .Where(x => string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    .Where(x => PathMatches(x.Path, normalizedFolder) || string.Equals(NormalizePath(Path.GetDirectoryName(x.Path) ?? string.Empty), normalizedFolder, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var item in claimed)
                {
                    item.Status = "in_progress";
                    item.Attempts++;
                }

                return claimed.Select(Clone).ToList();
            }

            public void UpdateStatus(int id, string status, string error = null)
            {
                var item = _items.FirstOrDefault(x => x.Id == id);
                if (item != null)
                {
                    item.Status = status;
                    item.Err = error;
                }

                StatusUpdates.Add((id, status, error));
            }

            public void UpdateBatchTagsJson(IEnumerable<(int Id, string TagsJson)> items) => throw new NotImplementedException();
            public void UpdateBatchTagsAndDuration(IEnumerable<(int Id, string TagsJson, int? DurationSeconds)> items) => throw new NotImplementedException();
            public void UpdateBatchStatus(List<int> ids, string status) => throw new NotImplementedException();
            public void RequeueInProgress(List<int> ids, string error = null)
            {
                if (ids == null)
                {
                    return;
                }

                foreach (var id in ids)
                {
                    var item = _items.FirstOrDefault(x => x.Id == id);
                    if (item != null && string.Equals(item.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = "queued";
                        item.Err = error;
                    }
                }
            }

            public int GetQueueCount()
            {
                return _items.Count(x => string.Equals(x.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(x.Status, "in_progress", StringComparison.OrdinalIgnoreCase));
            }

            public int RequeueFailedOrUnmappedUnderPath(string pathPrefix) => 0;
            public int RequeueFailedPaths(IEnumerable<string> paths) => 0;
            public int PurgeUnderPath(string pathPrefix) => 0;
            public int PurgePaths(IEnumerable<string> paths) => 0;
            public void PurgeOldCompleted(int daysToKeep = 14) { }

            public void RecordImportResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null)
            {
                Results.Add((queueItemId, path, outcome, bookId, authorId, errorMessage));
            }

            public void CompleteItemWithResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null, string statusError = null)
            {
                RecordImportResult(queueItemId, path, outcome, bookId, authorId, quality, errorMessage);
                UpdateStatus(queueItemId, "done", statusError);
            }

            public List<NzbDrone.Core.Datastore.ImportResult> GetImportResults(int? commandId = null)
            {
                return Results
                    .Select((r, index) => new NzbDrone.Core.Datastore.ImportResult
                    {
                        Id = index + 1,
                        QueueItemId = r.QueueItemId,
                        Path = r.Path,
                        Outcome = r.Outcome,
                        BookId = r.BookId,
                        AuthorId = r.AuthorId,
                        ErrorMessage = r.ErrorMessage
                    })
                    .ToList();
            }

            private static bool PathMatches(string path, string prefix)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(prefix))
                {
                    return false;
                }

                var normalizedPath = NormalizePath(path);
                return normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                       normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ||
                       normalizedPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizePath(string path)
            {
                try
                {
                    return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    return (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }

            private static IngestQueueItem Clone(IngestQueueItem item)
            {
                if (item == null)
                {
                    return null;
                }

                return new IngestQueueItem
                {
                    Id = item.Id,
                    Path = item.Path,
                    MtimeNs = item.MtimeNs,
                    SizeBytes = item.SizeBytes,
                    TagsJson = item.TagsJson,
                    DurationSeconds = item.DurationSeconds,
                    Status = item.Status,
                    Attempts = item.Attempts,
                    Err = item.Err,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt
                };
            }
        }

        private sealed class RecordingBookImportService : IBookImportService
        {
            public readonly List<(List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds, long Size, DateTime Modified)> Files, int BookId, int? EditionId, string Quality)> Calls = new();
            public Func<string, BookImportFileResult> ResultFactory { get; set; }

            public Task ImportFileAsync(string path, int bookId, string quality) => throw new NotImplementedException();
            public Task ImportFileAsync(string path, int bookId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task ImportFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds) => throw new NotImplementedException();
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds, MatchProvenance provenance) => throw new NotImplementedException();
            public Task<BookImportFileResult> ImportExistingFileAsync(DiscoveredFileWithMetadata file, int bookId, int? editionId, string quality, MatchProvenance provenance) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId) => throw new NotImplementedException();

            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)> files, int bookId)
            {
                var editionId = files?.Select(file => file.EditionId).FirstOrDefault();
                var legacyFiles = files?
                    .Select(file => (file.Path, file.Tags, file.DurationSeconds))
                    .ToList() ?? new List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)>();
                return ImportFilesAsync(legacyFiles, bookId, editionId, "Unknown");
            }

            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)> files, int bookId)
            {
                var editionId = files?.Select(file => file.EditionId).FirstOrDefault();
                var legacyFiles = files?
                    .Select(file => (file.File?.Path, file.File?.AllTags, file.File?.DurationSeconds, file.File?.Size ?? 0, file.File?.Modified ?? default))
                    .ToList() ?? new List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds, long Size, DateTime Modified)>();
                return Record(legacyFiles, bookId, editionId, "Unknown");
            }

            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags)> files, int bookId, int? editionId, string quality)
            {
                return ImportFilesAsync(files?.Select(x => (x.Path, x.Tags, (int?)null)).ToList() ?? new List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)>(), bookId, editionId, quality);
            }

            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId, int? editionId, string quality)
            {
                var observed = (files ?? new List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)>())
                    .Select(file => (file.Path, file.Tags, file.DurationSeconds, 0L, default(DateTime)))
                    .ToList();
                return Record(observed, bookId, editionId, quality);
            }

            private Task<IReadOnlyList<BookImportFileResult>> Record(
                List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds, long Size, DateTime Modified)> files,
                int bookId,
                int? editionId,
                string quality)
            {
                Calls.Add((files, bookId, editionId, quality));
                IReadOnlyList<BookImportFileResult> results = files
                    .Select(file => ResultFactory?.Invoke(file.Path) ?? BookImportFileResult.Imported(file.Path, 1000 + Calls.Count))
                    .ToList();
                return Task.FromResult(results);
            }
        }

        private sealed class RecordingBookUnitDestinationService : IBookUnitDestinationService
        {
            public readonly List<(Book CanonicalBook, Edition CanonicalEdition, string UnitKey)> Calls = new();

            public string BuildRootUnitKeyWithExtension(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType)
            {
                return $"{Path.GetDirectoryName(anyFilePathInUnit) ?? string.Empty}|{mediaType}|{Path.GetExtension(anyFilePathInUnit) ?? string.Empty}".ToLowerInvariant();
            }

            public (int BookId, int EditionId) ResolveDestinationForUnit(Book canonicalBook, Edition canonicalEdition, string unitKey)
            {
                Calls.Add((canonicalBook, canonicalEdition, unitKey));
                return (canonicalBook.Id, canonicalEdition.Id);
            }
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            public RootFolder RootFolder { get; set; }

            public List<RootFolder> All() => RootFolder == null ? new List<RootFolder>() : new List<RootFolder> { RootFolder };
            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => RootFolder;
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => RootFolder;
            public string GetBestRootFolderPath(string path) => RootFolder?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => RootFolder?.Path;
        }

        private sealed class StubMediaInfoExtractor : IMediaInfoExtractor
        {
            public MediaInfoModel ExtractMediaInfo(string filePath) => new MediaInfoModel();
            public TimeSpan GetDuration(string filePath) => TimeSpan.Zero;
            public bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null) => true;
        }

        private sealed class StubDiskProvider : IDiskProvider
        {
            public bool FolderExists(string path) => false;
            public bool FileExists(string path) => false;
            public bool FileExistsCanonical(string path) => false;
            public bool FileExists(string path, StringComparison stringComparison) => false;
            public IFileInfo GetFileInfo(string path) => new FileSystem().FileInfo.FromFileName(path);

            public long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public long? GetTotalSize(string path) => throw new NotImplementedException();
            public DateTime FolderGetCreationTime(string path) => throw new NotImplementedException();
            public DateTime FolderGetLastWrite(string path) => throw new NotImplementedException();
            public DateTime FileGetLastWrite(string path) => throw new NotImplementedException();
            public void EnsureFolder(string path) => throw new NotImplementedException();
            public bool FolderWritable(string path) => throw new NotImplementedException();
            public bool FolderEmpty(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetDirectories(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetFiles(string path, bool recursive) => throw new NotImplementedException();
            public long GetFolderSize(string path) => throw new NotImplementedException();
            public long GetFileSize(string path) => throw new NotImplementedException();
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
            public bool IsFileLocked(string path) => false;
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

        private sealed class NullEventAggregator : IEventAggregator
        {
            public readonly List<IEvent> Events = new();

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                if (@event != null)
                {
                    Events.Add(@event);
                }
            }
        }

        private sealed class AlwaysTrueContainmentValidator : IContainmentValidator
        {
            public bool Contains(string haystack, string needle) => true;
            public bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags) => true;
            public bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags) => true;
            public IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact = false) => Array.Empty<EditionTitleEvidence>();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
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

        [Test]
        public async Task author_ready_should_accept_recovered_cross_author_match()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var currentAuthor = new Author
            {
                Id = 31,
                Name = "Frank Herbert",
                Path = "/library/audiobooks/Frank Herbert"
            };

            var queueItem = new IngestQueueItem
            {
                Id = 1,
                Path = "/library/audiobooks/Frank Herbert/Whipping Star/Whipping Star.m4b",
                MtimeNs = 1_700_000_000_000_000_000,
                SizeBytes = 1234,
                TagsJson = "{\"TITLE\":[\"Whipping Star\"],\"ARTIST\":[\"Brian Herbert\"],\"ALBUMARTIST\":[\"Brian Herbert\"]}",
                DurationSeconds = 3600,
                Status = "queued",
                Attempts = 0,
                Err = null,
                CreatedAt = 1,
                UpdatedAt = 1
            };

            var ingestQueue = new RecordingIngestQueueRepository(queueItem);
            var matchingService = new RecordingMatchingService();
            var importService = new RecordingBookImportService();
            var destinationService = new RecordingBookUnitDestinationService();
            var eventAggregator = new NullEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = new Book
            {
                Id = 224,
                AuthorId = 6,
                Title = "Whipping Star",
                MediaType = BookMediaType.Audiobook
            };

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = new Edition
            {
                Id = 675,
                BookId = 224,
                Title = "Whipping Star",
                DurationSeconds = 3600,
                ReadingFormatId = 2,
                Monitored = true
            };

            var handler = new IngestQueueOnAuthorReadyHandler(
                ingestQueue,
                matchingService,
                importService,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                null,
                new StubMediaInfoExtractor(),
                new AlwaysTrueContainmentValidator(),
                bookService,
                editionService,
                destinationService,
                new StubRootFolderService
                {
                    RootFolder = new RootFolder
                    {
                        Path = "/library/audiobooks",
                        FolderType = FolderType.Audiobook
                    }
                },
                new StubDiskProvider(),
                eventAggregator,
                DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                null,
                logger);

            var commandId = Math.Abs(Guid.NewGuid().GetHashCode());
            if (commandId == 0)
            {
                commandId = 1;
            }

            var previousCommand = ProgressMessageContext.CommandModel;
            ProgressMessageContext.CommandModel = new CommandModel { Id = commandId };
            ImportSessionProgressTracker.Activate(commandId);
            ImportSessionProgressTracker.MarkStagingComplete(commandId);

            try
            {
                handler.Handle(new AuthorRefreshCompleteEvent(currentAuthor));

                var idleTask = ImportCommandWorkTracker.WaitForIdleAsync(commandId);
                var completed = await Task.WhenAny(idleTask, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.That(completed, Is.EqualTo(idleTask), "Author-ready background work did not finish in time.");
                await idleTask;

                Assert.That(matchingService.Calls, Has.Count.EqualTo(1));
                Assert.That(matchingService.Calls[0].RestrictToAuthorId, Is.EqualTo(currentAuthor.Id));
                Assert.That(matchingService.Calls[0].Context.AllowV5Identification, Is.True);
                Assert.That(matchingService.Calls[0].Context.AllowAuthorImport, Is.True);
                Assert.That(matchingService.Calls[0].Context.AllowUnscopedFallback, Is.False);
                Assert.That(matchingService.Calls[0].Context.DisablePathFallback, Is.True);
                Assert.That(matchingService.Calls[0].Context.PerFileMatching, Is.False);

                Assert.That(importService.Calls, Has.Count.EqualTo(1));
                Assert.That(importService.Calls[0].BookId, Is.EqualTo(224));
                Assert.That(importService.Calls[0].EditionId, Is.EqualTo(675));
                Assert.That(importService.Calls[0].Files.Single().Size, Is.EqualTo(1234));
                Assert.That(importService.Calls[0].Files.Single().Modified, Is.EqualTo(new DateTime(638355968000000000L, DateTimeKind.Utc)));

                Assert.That(ingestQueue.Results, Has.Count.EqualTo(1));
                Assert.That(ingestQueue.Results[0].Outcome, Is.EqualTo(ImportOutcome.Imported));
                Assert.That(ingestQueue.Results[0].AuthorId, Is.EqualTo(6), "author-ready should preserve the recovered cross-author match");
                Assert.That(ingestQueue.Results[0].BookId, Is.EqualTo(224));
                Assert.That(ingestQueue.Results[0].ErrorMessage, Is.Null);

                Assert.That(ingestQueue.StatusUpdates.Any(x => x.Id == 1 && x.Status == "done"), Is.True);
            }
            finally
            {
                ProgressMessageContext.CommandModel = previousCommand;
                ImportCommandWorkTracker.Clear(commandId);
                ImportSessionProgressTracker.Clear(commandId);
            }
        }

        [Test]
        public async Task author_ready_should_terminalize_each_path_from_the_apply_results_in_a_partial_batch()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var currentAuthor = new Author
            {
                Id = 31,
                Name = "Frank Herbert",
                Path = "/library/audiobooks/Frank Herbert"
            };
            var firstPath = "/library/audiobooks/Frank Herbert/Whipping Star/Disc 1.m4b";
            var secondPath = "/library/audiobooks/Frank Herbert/Whipping Star/Disc 2.m4b";
            var thirdPath = "/library/audiobooks/Frank Herbert/Whipping Star/Disc 3.m4b";

            IngestQueueItem CreateItem(int id, string path) => new()
            {
                Id = id,
                Path = path,
                MtimeNs = 1_700_000_000_000_000_000,
                SizeBytes = 1234,
                TagsJson = "{\"TITLE\":[\"Whipping Star\"],\"ARTIST\":[\"Brian Herbert\"]}",
                DurationSeconds = 1800,
                Status = "queued",
                Attempts = 0,
                CreatedAt = 1,
                UpdatedAt = 1
            };

            var ingestQueue = new RecordingIngestQueueRepository(CreateItem(1, firstPath), CreateItem(2, secondPath), CreateItem(3, thirdPath));
            var matchingService = new RecordingMatchingService
            {
                ResultFactory = files => new FileMatchResult
                {
                    MatchedFiles = files.Select(file => new FileMatch
                    {
                        File = file,
                        AuthorId = 6,
                        AuthorName = "Brian Herbert",
                        BookId = 224,
                        BookTitle = "Whipping Star",
                        EditionId = 675
                    }).ToArray(),
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                }
            };
            var importService = new RecordingBookImportService
            {
                ResultFactory = path => string.Equals(path, firstPath, StringComparison.OrdinalIgnoreCase)
                    ? BookImportFileResult.Imported(path, 1001)
                    : string.Equals(path, secondPath, StringComparison.OrdinalIgnoreCase)
                        ? BookImportFileResult.Failed(path, "FILE_MISSING_AT_APPLY")
                        : BookImportFileResult.AlreadyLinked(path, 1003)
            };
            var destinationService = new RecordingBookUnitDestinationService();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = new Book
            {
                Id = 224,
                AuthorId = 6,
                Title = "Whipping Star",
                MediaType = BookMediaType.Audiobook
            };

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = new Edition
            {
                Id = 675,
                BookId = 224,
                Title = "Whipping Star",
                DurationSeconds = 3600,
                ReadingFormatId = 2,
                Monitored = true
            };

            var handler = new IngestQueueOnAuthorReadyHandler(
                ingestQueue,
                matchingService,
                importService,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                null,
                new StubMediaInfoExtractor(),
                new AlwaysTrueContainmentValidator(),
                bookService,
                editionService,
                destinationService,
                new StubRootFolderService
                {
                    RootFolder = new RootFolder { Path = "/library/audiobooks", FolderType = FolderType.Audiobook }
                },
                new StubDiskProvider(),
                new NullEventAggregator(),
                DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                null,
                logger);

            var commandId = Math.Abs(Guid.NewGuid().GetHashCode());
            if (commandId == 0)
            {
                commandId = 1;
            }

            var previousCommand = ProgressMessageContext.CommandModel;
            ProgressMessageContext.CommandModel = new CommandModel { Id = commandId };
            ImportSessionProgressTracker.Activate(commandId);
            ImportSessionProgressTracker.MarkStagingComplete(commandId);

            try
            {
                handler.Handle(new AuthorRefreshCompleteEvent(currentAuthor));
                var idleTask = ImportCommandWorkTracker.WaitForIdleAsync(commandId);
                var completed = await Task.WhenAny(idleTask, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.That(completed, Is.EqualTo(idleTask), "Author-ready background work did not finish in time.");
                await idleTask;

                Assert.That(importService.Calls, Has.Count.EqualTo(1));
                Assert.That(importService.Calls[0].Files.Select(file => file.Path), Is.EquivalentTo(new[] { firstPath, secondPath, thirdPath }));
                Assert.That(ingestQueue.Results, Has.Count.EqualTo(3));
                Assert.That(ingestQueue.Results.Single(result => result.Path == firstPath).Outcome, Is.EqualTo(ImportOutcome.Imported));
                Assert.That(ingestQueue.Results.Single(result => result.Path == secondPath).Outcome, Is.EqualTo(ImportOutcome.Failed));
                Assert.That(ingestQueue.Results.Single(result => result.Path == secondPath).ErrorMessage, Is.EqualTo("FILE_MISSING_AT_APPLY"));
                Assert.That(ingestQueue.Results.Single(result => result.Path == thirdPath).Outcome, Is.EqualTo(ImportOutcome.AlreadyLinked));
                Assert.That(ImportSessionProgressTracker.GetImportedCounts(commandId).FilesImported, Is.EqualTo(1));
            }
            finally
            {
                ProgressMessageContext.CommandModel = previousCommand;
                ImportCommandWorkTracker.Clear(commandId);
                ImportSessionProgressTracker.Clear(commandId);
            }
        }
    }
}
