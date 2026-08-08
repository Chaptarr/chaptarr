using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NLog;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Queue
{
    public interface IQueueService
    {
        List<Queue> GetQueue();
        Queue Find(int id);
        void Remove(int id);
    }

    public class QueueService : IQueueService,
                                IHandle<TrackedDownloadRefreshedEvent>,
                                IHandle<TrackedDownloadUpdatedEvent>
    {
        private readonly IEventAggregator _eventAggregator;
        private static List<Queue> _queue = new();
        private readonly IHistoryService _historyService;
        private readonly IConversionTrackingService _conversionTrackingService;
        private readonly IConversionJobService _conversionJobService;
        private readonly IDiskProvider _diskProvider;
        private readonly Dictionary<string, QualityModel> _inferredQualityCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public QueueService(IEventAggregator eventAggregator,
                            IHistoryService historyService,
                            IConversionTrackingService conversionTrackingService = null,
                            IDiskProvider diskProvider = null,
                            IConversionJobService conversionJobService = null)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _conversionTrackingService = conversionTrackingService;
            _conversionJobService = conversionJobService;
            _diskProvider = diskProvider;
        }

        public List<Queue> GetQueue()
        {
            var queue = _queue
                .Select(CloneQueueItem)
                .ToList();

            ApplyConversionStatuses(queue);
            return queue;
        }

        public Queue Find(int id)
        {
            return CloneQueueItem(_queue.SingleOrDefault(q => q.Id == id));
        }

        public void Remove(int id)
        {
            _queue = _queue
                .Where(q => q.Id != id)
                .ToList();
        }

        private IEnumerable<Queue> MapQueue(TrackedDownload trackedDownload, IReadOnlyDictionary<string, List<EntityHistory>> grabbedHistoryByDownloadId)
        {
            var books = trackedDownload.RemoteBook.GetBooksMatchingReleaseMediaType();

            if (books.Any())
            {
                foreach (var book in books)
                {
                    yield return MapQueueItem(trackedDownload, book, grabbedHistoryByDownloadId);
                }
            }
            else
            {
                yield return MapQueueItem(trackedDownload, null, grabbedHistoryByDownloadId);
            }
        }

        private Queue MapQueueItem(TrackedDownload trackedDownload, Book book, IReadOnlyDictionary<string, List<EntityHistory>> grabbedHistoryByDownloadId)
        {
            var downloadForced = trackedDownload.DownloadItem?.DownloadForced == true;
            var grabHistory = GetGrabHistory(trackedDownload.DownloadItem.DownloadId, grabbedHistoryByDownloadId);
            var history = grabHistory.FirstOrDefault();
            if (history != null)
            {
                var forcedValue = GetHistoryData(history, "DownloadForced");
                if (bool.TryParse(forcedValue, out var persistedForced))
                {
                    downloadForced = persistedForced;
                }

                // Older grab rows recorded an approved interactive choice as DownloadForced=false.
                // ReleaseSource has always captured the explicit user selection and repairs that state.
                if (string.Equals(
                    GetHistoryData(history, "ReleaseSource"),
                    ReleaseSourceType.InteractiveSearch.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    downloadForced = true;
                }
            }

            var quality = trackedDownload.RemoteBook?.ParsedBookInfo?.Quality;

            // Grab history is the persisted copy of what was known at grab time.
            if (NeedsQualityFallback(quality) && IsSpecificQuality(history?.Quality))
            {
                quality = history.Quality;
            }

            // Parse from the title when the grab did not already resolve a specific quality.
            if (NeedsQualityFallback(quality))
            {
                try
                {
                    var parsed = Parser.Parser.ParseBookTitle(trackedDownload.DownloadItem.Title);
                    if (parsed?.Quality != null && parsed.Quality.Quality.Id != 0)
                    {
                        quality = parsed.Quality;
                    }
                }
                catch
                {
                    // ignore parsing errors; continue to next fallback
                }
            }

            // After completion, a single-file output path can tell us the actual file format.
            if (NeedsQualityFallback(quality) && trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed)
            {
                try
                {
                    if (!trackedDownload.DownloadItem.OutputPath.IsEmpty)
                    {
                        var outputPath = trackedDownload.DownloadItem.OutputPath.ToString();
                        var ext = Path.GetExtension(outputPath);
                        if (!string.IsNullOrWhiteSpace(ext))
                        {
                            var inferred = NzbDrone.Core.MediaFiles.MediaFileExtensions.GetQualityForExtension(ext);
                            if (inferred != Quality.Unknown)
                            {
                                quality = new QualityModel(inferred);
                            }
                        }
                    }
                }
                catch
                {
                    // ignore path/IO errors; final fallback below
                }
            }

            if (NeedsQualityFallback(quality) && trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed)
            {
                quality = InferCompletedDownloadQuality(trackedDownload, book, grabHistory) ?? quality;
            }

            if (quality == null)
            {
                quality = new QualityModel(Quality.Unknown);
            }

            var author = trackedDownload.RemoteBook?.Author ?? history?.Author;
            var conversionTarget = QualityConversionHelper.GetPlannedConversionTarget(author, quality);
            var activeConversion = _conversionTrackingService?.Get(trackedDownload.DownloadItem.DownloadId);

            var queue = new Queue
            {
                Author = author,
                Book = book ?? history?.Book,
                Quality = quality,
                Title = Parser.Parser.RemoveFileExtension(trackedDownload.DownloadItem.Title),
                Size = trackedDownload.DownloadItem.TotalSize,
                Sizeleft = trackedDownload.DownloadItem.RemainingSize,
                Timeleft = trackedDownload.DownloadItem.RemainingTime,
                Added = trackedDownload.Added ?? history?.Date,
                Status = trackedDownload.DownloadItem.Status.ToString(),
                TrackedDownloadStatus = trackedDownload.Status,
                TrackedDownloadState = trackedDownload.State,
                StatusMessages = trackedDownload.StatusMessages.ToList(),
                ErrorMessage = GetQueueErrorMessage(trackedDownload),
                RemoteBook = trackedDownload.RemoteBook,
                DownloadId = trackedDownload.DownloadItem.DownloadId,
                TargetBookIds = GetTargetBookIds(grabHistory, trackedDownload, book),
                ConversionStatus = activeConversion?.Status,
                ConvertToQualityId = activeConversion?.TargetQualityId ?? conversionTarget?.Id,
                ConvertToQuality = activeConversion?.TargetQualityName ?? conversionTarget?.Name,
                ConversionProgress = activeConversion?.Progress,
                ConversionMessage = activeConversion?.Message ?? GetConversionMessage(quality, conversionTarget),
                CanCancelConversion = IsCancellableConversion(activeConversion),
                CanRetryImport = CanRetryImport(trackedDownload),
                Protocol = trackedDownload.Protocol,
                DownloadClient = trackedDownload.DownloadItem.DownloadClientInfo.Name,
                Indexer = trackedDownload.Indexer ?? GetHistoryData(history, EntityHistory.INDEXER),
                OutputPath = trackedDownload.DownloadItem.OutputPath.ToString(),
                DownloadForced = downloadForced,
                DownloadClientHasPostImportCategory = trackedDownload.DownloadItem.DownloadClientInfo.HasPostImportCategory
            };

            queue.Id = HashConverter.GetHashInt31($"trackedDownload-{trackedDownload.DownloadClient}-{trackedDownload.DownloadItem.DownloadId}-book{queue.Book?.Id ?? 0}");

            if (queue.Timeleft.HasValue)
            {
                queue.EstimatedCompletionTime = DateTime.UtcNow.Add(queue.Timeleft.Value);
            }

            return queue;
        }

        private static Queue CloneQueueItem(Queue queue)
        {
            if (queue == null)
            {
                return null;
            }

            // Shallow clone: protects queue-owned fields that are overlaid per read
            // while keeping the existing shared Author/Book/RemoteBook references.
            return new Queue
            {
                Id = queue.Id,
                Author = queue.Author,
                Book = queue.Book,
                Quality = queue.Quality,
                Size = queue.Size,
                Title = queue.Title,
                Sizeleft = queue.Sizeleft,
                Timeleft = queue.Timeleft,
                EstimatedCompletionTime = queue.EstimatedCompletionTime,
                Added = queue.Added,
                Status = queue.Status,
                TrackedDownloadStatus = queue.TrackedDownloadStatus,
                TrackedDownloadState = queue.TrackedDownloadState,
                StatusMessages = queue.StatusMessages?.ToList(),
                DownloadId = queue.DownloadId,
                TargetBookIds = queue.TargetBookIds?.ToList() ?? new List<int>(),
                ConversionStatus = queue.ConversionStatus,
                ConvertToQualityId = queue.ConvertToQualityId,
                ConvertToQuality = queue.ConvertToQuality,
                ConversionProgress = queue.ConversionProgress,
                ConversionMessage = queue.ConversionMessage,
                CanCancelConversion = queue.CanCancelConversion,
                CanRetryImport = queue.CanRetryImport,
                RemoteBook = queue.RemoteBook,
                Protocol = queue.Protocol,
                DownloadClient = queue.DownloadClient,
                DownloadClientHasPostImportCategory = queue.DownloadClientHasPostImportCategory,
                Indexer = queue.Indexer,
                OutputPath = queue.OutputPath,
                ErrorMessage = queue.ErrorMessage,
                DownloadForced = queue.DownloadForced
            };
        }

        private static bool NeedsQualityFallback(QualityModel quality)
        {
            return quality?.Quality == null ||
                   quality.Quality == Quality.Unknown ||
                   quality.Quality == Quality.UnknownAudio;
        }

        private static bool IsSpecificQuality(QualityModel quality)
        {
            return quality?.Quality != null &&
                   quality.Quality != Quality.Unknown &&
                   quality.Quality != Quality.UnknownAudio;
        }

        private QualityModel InferCompletedDownloadQuality(TrackedDownload trackedDownload, Book book, List<EntityHistory> grabHistory)
        {
            var downloadId = trackedDownload?.DownloadItem?.DownloadId;
            if (!string.IsNullOrWhiteSpace(downloadId) &&
                _inferredQualityCache.TryGetValue(downloadId, out var cached) &&
                IsSpecificQuality(cached))
            {
                return cached;
            }

            var targetMediaType = GetTargetMediaType(book, trackedDownload?.RemoteBook, grabHistory);
            var fileCandidates = new List<string>();

            fileCandidates.AddRange(trackedDownload?.DownloadItem?.FilePaths ?? Enumerable.Empty<string>());
            fileCandidates.AddRange(trackedDownload?.StatusMessages?
                .Select(message => message?.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title)) ?? Enumerable.Empty<string>());

            var inferred = InferQualityFromFileNames(fileCandidates, targetMediaType);
            if (!IsSpecificQuality(inferred))
            {
                inferred = InferQualityFromOutputFolder(trackedDownload, targetMediaType);
            }

            if (!string.IsNullOrWhiteSpace(downloadId) && IsSpecificQuality(inferred))
            {
                _inferredQualityCache[downloadId] = inferred;
            }

            return inferred;
        }

        private QualityModel InferQualityFromOutputFolder(TrackedDownload trackedDownload, BookMediaType? targetMediaType)
        {
            if (_diskProvider == null || trackedDownload?.DownloadItem == null || trackedDownload.DownloadItem.OutputPath.IsEmpty)
            {
                return null;
            }

            try
            {
                var outputPath = trackedDownload.DownloadItem.OutputPath.ToString();
                if (!_diskProvider.FolderExists(outputPath))
                {
                    return null;
                }

                return InferQualityFromFileNames(_diskProvider.GetFiles(outputPath, true), targetMediaType);
            }
            catch
            {
                return null;
            }
        }

        private static QualityModel InferQualityFromFileNames(IEnumerable<string> paths, BookMediaType? targetMediaType)
        {
            var qualities = (paths ?? Enumerable.Empty<string>())
                .Where(path => !ShouldSkipQualityInferencePath(path))
                .Select(path => Path.GetExtension(path))
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Where(extension => MediaFileExtensions.AllExtensions.Contains(extension))
                .Where(extension => ExtensionMatchesTargetMediaType(extension, targetMediaType))
                .Select(MediaFileExtensions.GetQualityForExtension)
                .Where(quality => quality != Quality.Unknown)
                .Distinct()
                .ToList();

            if (qualities.Count == 1)
            {
                return new QualityModel(qualities[0]);
            }

            if (qualities.Count > 1 && targetMediaType == BookMediaType.Audiobook && qualities.All(QualityMediaTypeHelper.IsAudiobookQuality))
            {
                return new QualityModel(Quality.UnknownAudio);
            }

            return null;
        }

        private static bool ExtensionMatchesTargetMediaType(string extension, BookMediaType? targetMediaType)
        {
            if (!targetMediaType.HasValue)
            {
                return true;
            }

            return targetMediaType.Value == BookMediaType.Audiobook
                ? MediaFileExtensions.AudioExtensions.Contains(extension)
                : MediaFileExtensions.TextExtensions.Contains(extension);
        }

        private static bool ShouldSkipQualityInferencePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return true;
            }

            if (fileName.StartsWith(".", StringComparison.Ordinal) ||
                fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var extension = Path.GetExtension(fileName);
            if (extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".!ut", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fileName.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("preview", StringComparison.OrdinalIgnoreCase);
        }

        private static BookMediaType? GetTargetMediaType(Book book, RemoteBook remoteBook, List<EntityHistory> grabHistory)
        {
            if (book != null)
            {
                return book.MediaType;
            }

            var historyBook = grabHistory?
                .Select(history => history.Book)
                .FirstOrDefault(historyBook => historyBook != null);
            if (historyBook != null)
            {
                return historyBook.MediaType;
            }

            return remoteBook?.GetBooksMatchingReleaseMediaType()
                .Select(remoteBookItem => (BookMediaType?)remoteBookItem.MediaType)
                .FirstOrDefault();
        }

        private static List<int> GetTargetBookIds(List<EntityHistory> grabHistory, TrackedDownload trackedDownload, Book book)
        {
            var historyIds = grabHistory?
                .Where(history => history?.BookId > 0)
                .Select(history => history.BookId)
                .Distinct()
                .ToList() ?? new List<int>();

            if (historyIds.Any())
            {
                return historyIds;
            }

            historyIds = grabHistory?
                .Select(history => history?.Book?.Id ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            if (historyIds.Any())
            {
                return historyIds;
            }

            if (book?.Id > 0)
            {
                return new List<int> { book.Id };
            }

            var matchingMediaTypeBooks = trackedDownload?.RemoteBook?.GetBooksMatchingReleaseMediaType();
            if (matchingMediaTypeBooks?.Any() == true)
            {
                return GetBookIds(matchingMediaTypeBooks);
            }

            return GetBookIds(trackedDownload?.RemoteBook?.Books);
        }

        private static List<int> GetBookIds(IEnumerable<Book> books)
        {
            return books?
                .Where(book => book?.Id > 0)
                .Select(book => book.Id)
                .Distinct()
                .ToList() ?? new List<int>();
        }

        private Dictionary<string, List<EntityHistory>> BuildGrabHistoryByDownloadId(IEnumerable<TrackedDownload> trackedDownloads)
        {
            var downloadIds = (trackedDownloads ?? Enumerable.Empty<TrackedDownload>())
                .Select(trackedDownload => trackedDownload?.DownloadItem?.DownloadId)
                .Where(downloadId => !string.IsNullOrWhiteSpace(downloadId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var grabbedHistoryByDownloadId = downloadIds.ToDictionary(
                downloadId => downloadId,
                _ => new List<EntityHistory>(),
                StringComparer.OrdinalIgnoreCase);

            var grabbedHistory = _historyService.FindByDownloadIds(downloadIds, EntityHistoryEventType.Grabbed);
            foreach (var group in grabbedHistory
                .Where(history => !string.IsNullOrWhiteSpace(history.DownloadId))
                .GroupBy(history => history.DownloadId, StringComparer.OrdinalIgnoreCase))
            {
                grabbedHistoryByDownloadId[group.Key] = group
                    .OrderByDescending(h => h.Date)
                    .ToList();
            }

            return grabbedHistoryByDownloadId;
        }

        private List<EntityHistory> GetGrabHistory(string downloadId, IReadOnlyDictionary<string, List<EntityHistory>> grabbedHistoryByDownloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return new List<EntityHistory>();
            }

            if (grabbedHistoryByDownloadId != null && grabbedHistoryByDownloadId.TryGetValue(downloadId, out var grabHistory))
            {
                return grabHistory ?? new List<EntityHistory>();
            }

            return _historyService.Find(downloadId, EntityHistoryEventType.Grabbed)
                .OrderByDescending(h => h.Date)
                .ToList();
        }

        private void ApplyConversionStatuses(List<Queue> queue)
        {
            if (_conversionTrackingService == null)
            {
                return;
            }

            foreach (var item in queue)
            {
                var durableJob = _conversionJobService?.Get(item.DownloadId);
                if (durableJob != null && durableJob.Status != ConversionJobStatus.Completed)
                {
                    item.ConversionStatus = GetConversionJobStatus(durableJob.Status);
                    item.ConvertToQualityId = durableJob.TargetQualityId > 0 ? durableJob.TargetQualityId : item.ConvertToQualityId;
                    item.ConvertToQuality = durableJob.TargetQualityName ?? item.ConvertToQuality;
                    item.ConversionProgress = durableJob.Progress;
                    item.ConversionMessage = durableJob.Message;
                    item.CanCancelConversion = durableJob.Status == ConversionJobStatus.Queued ||
                                               durableJob.Status == ConversionJobStatus.Converting;
                    continue;
                }

                var conversion = _conversionTrackingService.Get(item.DownloadId);
                if (conversion == null)
                {
                    if (item.ConversionStatus == "converting" || item.ConversionStatus == "cancelling" || item.ConversionStatus == "cancelled" || item.ConversionStatus == "failed")
                    {
                        item.ConversionStatus = null;
                        item.ConversionProgress = null;
                        item.ConversionMessage = GetConversionMessage(item.Quality, QualityConversionHelper.GetPlannedConversionTarget(item.Author, item.Quality));
                        item.CanCancelConversion = false;
                    }

                    continue;
                }

                item.ConversionStatus = conversion.Status;
                item.ConvertToQualityId = conversion.TargetQualityId ?? item.ConvertToQualityId;
                item.ConvertToQuality = conversion.TargetQualityName ?? item.ConvertToQuality;
                item.ConversionProgress = conversion.Progress;
                item.ConversionMessage = conversion.Message;
                item.CanCancelConversion = IsCancellableConversion(conversion);
            }
        }

        private static string GetConversionJobStatus(ConversionJobStatus status)
        {
            return status switch
            {
                ConversionJobStatus.Queued => "queued",
                ConversionJobStatus.Converting => "converting",
                ConversionJobStatus.ReadyToImport => "ready_to_import",
                ConversionJobStatus.Completed => "completed",
                ConversionJobStatus.Failed => "failed",
                ConversionJobStatus.Cancelling => "cancelling",
                ConversionJobStatus.Cancelled => "cancelled",
                _ => null
            };
        }

        private static bool IsCancellableConversion(ConversionQueueStatus conversion)
        {
            return conversion?.CanCancel == true &&
                   string.Equals(conversion.Status, "converting", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetConversionMessage(QualityModel sourceQuality, Quality targetQuality)
        {
            if (targetQuality == null)
            {
                return null;
            }

            if (sourceQuality?.Quality == Quality.UnknownAudio)
            {
                return "Detecting source format after download completes";
            }

            return null;
        }

        private static bool CanRetryImport(TrackedDownload trackedDownload)
        {
            return trackedDownload?.DownloadItem?.Status == DownloadItemStatus.Completed &&
                   trackedDownload.State == TrackedDownloadState.ImportBlocked &&
                   !string.IsNullOrWhiteSpace(trackedDownload.DownloadItem.DownloadId);
        }

        private static string GetHistoryData(EntityHistory history, string key)
        {
            if (history?.Data == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (history.Data.TryGetValue(key, out var value))
            {
                return value;
            }

            return history.Data
                .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        private static string GetQueueErrorMessage(TrackedDownload trackedDownload)
        {
            if (trackedDownload == null)
            {
                return null;
            }

            if (trackedDownload.State == TrackedDownloadState.ImportBlocked ||
                trackedDownload.Status == TrackedDownloadStatus.Warning ||
                trackedDownload.Status == TrackedDownloadStatus.Error)
            {
                var statusMessage = trackedDownload.StatusMessages?
                    .SelectMany(message => message?.Messages ?? Enumerable.Empty<string>())
                    .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message) && !LooksLikeInternalStatusCode(message));

                if (!string.IsNullOrWhiteSpace(statusMessage))
                {
                    return statusMessage;
                }
            }

            return trackedDownload.DownloadItem?.Message;
        }

        private static bool LooksLikeInternalStatusCode(string message)
        {
            return message.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            var trackedCount = message.TrackedDownloads?.Count ?? 0;
            if (Logger.IsDebugEnabled)
            {
                Logger.Debug("[MEMORY] Queue refresh start ({0} tracked downloads): {1}", trackedCount, MemorySnapshot.CaptureDetailed());
            }
            PruneInferredQualityCache(message.TrackedDownloads);

            var trackedDownloads = (message.TrackedDownloads ?? new List<TrackedDownload>())
                .Where(ShouldIncludeTrackedDownload)
                .OrderBy(c => c.DownloadItem.RemainingTime)
                .ToList();
            var grabbedHistoryByDownloadId = BuildGrabHistoryByDownloadId(trackedDownloads);

            var queue = trackedDownloads
                .SelectMany(trackedDownload => MapQueue(trackedDownload, grabbedHistoryByDownloadId))
                .ToList();

            ApplyConversionStatuses(queue);
            _queue = queue;
            if (Logger.IsDebugEnabled)
            {
                Logger.Debug("[MEMORY] Queue refresh complete ({0} queue items): {1}", queue.Count, MemorySnapshot.CaptureDetailed());
            }

            _eventAggregator.PublishEvent(new QueueUpdatedEvent());
        }

        public void Handle(TrackedDownloadUpdatedEvent message)
        {
            var trackedDownload = message.TrackedDownload;
            var downloadId = trackedDownload?.DownloadItem?.DownloadId;

            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            var queue = _queue
                .Where(q => !string.Equals(q.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ShouldIncludeTrackedDownload(trackedDownload))
            {
                var grabbedHistoryByDownloadId = BuildGrabHistoryByDownloadId(new[] { trackedDownload });
                queue.AddRange(MapQueue(trackedDownload, grabbedHistoryByDownloadId));
                queue = queue
                    .OrderBy(c => c.Timeleft)
                    .ToList();
            }

            ApplyConversionStatuses(queue);
            _queue = queue;

            _eventAggregator.PublishEvent(new QueueUpdatedEvent());
        }

        private static bool ShouldIncludeTrackedDownload(TrackedDownload trackedDownload)
        {
            return trackedDownload?.IsTrackable == true &&
                   trackedDownload.State != TrackedDownloadState.Imported &&
                   trackedDownload.State != TrackedDownloadState.DownloadFailed &&
                   trackedDownload.State != TrackedDownloadState.Ignored;
        }

        private void PruneInferredQualityCache(IEnumerable<TrackedDownload> trackedDownloads)
        {
            if (_inferredQualityCache.Count == 0)
            {
                return;
            }

            var activeDownloadIds = (trackedDownloads ?? Enumerable.Empty<TrackedDownload>())
                .Select(trackedDownload => trackedDownload?.DownloadItem?.DownloadId)
                .Where(downloadId => !string.IsNullOrWhiteSpace(downloadId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var cachedDownloadId in _inferredQualityCache.Keys.ToList())
            {
                if (!activeDownloadIds.Contains(cachedDownloadId))
                {
                    _inferredQualityCache.Remove(cachedDownloadId);
                }
            }
        }
    }
}
