using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadService
    {
        TrackedDownload Find(string downloadId);
        void StopTracking(string downloadId);
        void StopTracking(List<string> downloadIds);
        TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
        List<TrackedDownload> GetTrackedDownloads();
        void UpdateTrackable(List<TrackedDownload> trackedDownloads);
    }

    public class TrackedDownloadService : ITrackedDownloadService,
                                          IHandle<BookInfoRefreshedEvent>,
                                          IHandle<LibraryIdentityChangedEvent>,
                                          IHandle<BookUpdatedEvent>,
                                          IHandle<AuthorDeletedEvent>
    {
        private readonly IParsingService _parsingService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IDownloadClientFileSnapshotService _downloadClientFileSnapshotService;
        private readonly Logger _logger;
        private readonly ICached<TrackedDownload> _cache;

        public TrackedDownloadService(IParsingService parsingService,
                                      ICacheManager cacheManager,
                                      IHistoryService historyService,
                                      IEventAggregator eventAggregator,
                                      IDownloadHistoryService downloadHistoryService,
                                      ICustomFormatCalculationService formatCalculator,
                                      Logger logger,
                                      IDownloadClientFileSnapshotService downloadClientFileSnapshotService)
        {
            _parsingService = parsingService;
            _historyService = historyService;
            _cache = cacheManager.GetCache<TrackedDownload>(GetType());
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _downloadHistoryService = downloadHistoryService;
            _downloadClientFileSnapshotService = downloadClientFileSnapshotService;
            _logger = logger;
        }

        public TrackedDownload Find(string downloadId)
        {
            return _cache.Find(downloadId);
        }

        public void StopTracking(string downloadId)
        {
            var trackedDownload = _cache.Find(downloadId);

            _cache.Remove(downloadId);
            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { trackedDownload }));
        }

        public void StopTracking(List<string> downloadIds)
        {
            var trackedDownloads = new List<TrackedDownload>();

            foreach (var downloadId in downloadIds)
            {
                var trackedDownload = _cache.Find(downloadId);
                _cache.Remove(downloadId);
                trackedDownloads.Add(trackedDownload);
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(trackedDownloads));
        }

        public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            var existingItem = Find(downloadItem.DownloadId);
            var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(downloadItem.DownloadId);

            // Imported downloads may still be seeding. Keep their original source inventory
            // instead of replacing it with the files left after import.
            var effectiveState = existingItem?.State
                                 ?? (downloadHistory != null ? GetStateFromHistory(downloadHistory.EventType) : TrackedDownloadState.Downloading);

            if (effectiveState != TrackedDownloadState.Imported && effectiveState != TrackedDownloadState.Ignored)
            {
                _downloadClientFileSnapshotService.CaptureClientList(downloadItem);
            }

            _downloadClientFileSnapshotService.ApplySnapshot(downloadItem);

            if (ShouldKeepExistingTrackedDownload(existingItem, downloadHistory))
            {
                LogItemChange(existingItem, existingItem.DownloadItem, downloadItem);

                var downloadForced = existingItem.DownloadItem?.DownloadForced == true;

                // A download-client refresh can race the synchronous grab-history write by a
                // few milliseconds. Retry hydration only for that still-unhydrated cache shape;
                // normal tracked downloads do not incur another history query on every poll.
                if (!downloadForced && !existingItem.Added.HasValue &&
                    (downloadHistory == null || downloadHistory.EventType == DownloadHistoryEventType.DownloadGrabbed))
                {
                    try
                    {
                        var grabbedEvent = _historyService.FindByDownloadId(downloadItem.DownloadId)
                            .OrderByDescending(history => history.Date)
                            .FirstOrDefault(history => history.EventType == EntityHistoryEventType.Grabbed);

                        if (grabbedEvent != null)
                        {
                            downloadForced = IsForcedDownload(grabbedEvent);
                            existingItem.Added = grabbedEvent.Date;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to hydrate delayed grab history for {0}; will retry on the next refresh", downloadItem.DownloadId);
                    }
                }

                downloadItem.DownloadForced = downloadForced;
                existingItem.DownloadItem = downloadItem;
                existingItem.IsTrackable = true;

                return existingItem;
            }

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = downloadClient.Id,
                DownloadItem = downloadItem,
                Protocol = downloadClient.Protocol,
                IsTrackable = true
            };

            try
            {
                var parsedBookInfo = Parser.Parser.ParseBookTitle(trackedDownload.DownloadItem.Title);
                var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId)
                    .OrderByDescending(h => h.Date)
                    .ToList();
                var capturedGrabs = RemoteBook.GetCapturedGrabHistory(historyItems);
                var grabbedEvent = capturedGrabs.FirstOrDefault();
                var historyContextItem = grabbedEvent ?? historyItems.FirstOrDefault();
                var grabbedBookIds = capturedGrabs
                    .Select(h => h.BookId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                // Grab history identifies the targets independently of the client's title.
                // Only missing targets need the ordinary title-matching fallback.
                if (grabbedBookIds.Any())
                {
                    trackedDownload.RemoteBook = _parsingService.Map(
                        parsedBookInfo ?? BuildHistoryParsedBookInfo(historyContextItem, historyContextItem.Author, new List<Book>()),
                        historyContextItem.AuthorId,
                        grabbedBookIds);
                }

                if (grabbedBookIds.Count == 0 && parsedBookInfo != null && NeedsHistoryTargetRecovery(trackedDownload.RemoteBook))
                {
                    trackedDownload.RemoteBook = _parsingService.Map(parsedBookInfo);
                }

                var downloadGrabHistory = _downloadHistoryService.GetLatestGrab(downloadItem.DownloadId);

                if (downloadHistory != null)
                {
                    var state = GetStateFromHistory(downloadHistory.EventType);
                    trackedDownload.State = state;

                    if (downloadHistory.EventType == DownloadHistoryEventType.DownloadImportIncomplete)
                    {
                        var messages = Json.Deserialize<List<TrackedDownloadStatusMessage>>(downloadHistory.Data["statusMessages"]).ToArray();
                        trackedDownload.Warn(messages);
                        trackedDownload.AwaitingImportConfirmation = downloadHistory.Data.TryGetValue("awaitingImportConfirmation", out var awaiting) &&
                            bool.TryParse(awaiting, out var wait) && wait;
                    }
                }

                if (historyItems.Any())
                {
                    trackedDownload.DownloadItem.DownloadForced = IsForcedDownload(grabbedEvent);

                    trackedDownload.Indexer = GetHistoryData(grabbedEvent, EntityHistory.INDEXER) ??
                                              downloadGrabHistory?.Release?.Indexer ??
                                              GetDownloadHistoryData(downloadGrabHistory, EntityHistory.INDEXER);
                    trackedDownload.Added = grabbedEvent?.Date ?? downloadGrabHistory?.Date;

                    if (parsedBookInfo == null || NeedsHistoryTargetRecovery(trackedDownload.RemoteBook))
                    {
                        var historyAuthor = historyContextItem.Author;
                        var historyBooks = capturedGrabs
                            .Select(v => v.Book)
                            .Where(v => v != null)
                            .GroupBy(v => v.Id)
                            .Select(v => v.First())
                            .ToList();

                        if (historyBooks.Empty() && historyContextItem.Book != null)
                        {
                            historyBooks.Add(historyContextItem.Book);
                        }

                        parsedBookInfo = Parser.Parser.ParseBookTitle(historyContextItem.SourceTitle);

                        if (parsedBookInfo == null && historyAuthor != null && historyBooks.Any())
                        {
                            parsedBookInfo =
                                Parser.Parser.ParseBookTitleWithSearchCriteria(historyContextItem.SourceTitle,
                                    historyAuthor,
                                    historyBooks,
                                    historyBooks);
                        }

                        if (!NeedsHistoryTargetRecovery(trackedDownload.RemoteBook))
                        {
                            trackedDownload.RemoteBook.ParsedBookInfo = parsedBookInfo ??
                                BuildHistoryParsedBookInfo(historyContextItem, historyAuthor, historyBooks);
                        }
                        else if (parsedBookInfo != null || historyContextItem.AuthorId > 0 || grabbedBookIds.Any())
                        {
                            trackedDownload.RemoteBook = _parsingService.Map(
                                parsedBookInfo ?? BuildHistoryParsedBookInfo(historyContextItem, historyAuthor, historyBooks),
                                historyContextItem.AuthorId, grabbedBookIds);
                        }
                    }

                    HydrateReleaseFromGrabHistory(trackedDownload.RemoteBook, grabbedEvent, downloadGrabHistory, trackedDownload.Indexer);
                    trackedDownload.RemoteBook?.RestoreGrabbedTargets(capturedGrabs);

                    if (trackedDownload.RemoteBook != null &&
                        Enum.TryParse(GetHistoryData(grabbedEvent, "indexerFlags"), true, out IndexerFlags flags))
                    {
                        trackedDownload.RemoteBook.Release ??= new ReleaseInfo();
                        trackedDownload.RemoteBook.Release.IndexerFlags = flags;
                    }

                    // Restore narrator information from grab history to ensure it flows through import pipeline
                    if (trackedDownload.RemoteBook != null && grabbedEvent?.Data != null)
                    {
                        var historicalQuality = grabbedEvent.Quality;
                        if (NeedsQualityFallback(trackedDownload.RemoteBook.ParsedBookInfo?.Quality) &&
                            IsKnownQuality(historicalQuality))
                        {
                            trackedDownload.RemoteBook.ParsedBookInfo ??= parsedBookInfo ?? new ParsedBookInfo();
                            trackedDownload.RemoteBook.ParsedBookInfo.Quality = historicalQuality;
                            _logger.Debug("Restored quality '{0}' from grab history for download: {1}", historicalQuality, downloadItem.Title);
                        }

                        var historicalNarrator = GetHistoryData(grabbedEvent, "Narrator");
                        if (!string.IsNullOrWhiteSpace(historicalNarrator))
                        {
                            if (trackedDownload.RemoteBook.ParsedBookInfo == null)
                            {
                                trackedDownload.RemoteBook.ParsedBookInfo = parsedBookInfo ?? new ParsedBookInfo();
                            }

                            trackedDownload.RemoteBook.ParsedBookInfo.Narrator = historicalNarrator;
                            _logger.Debug("Restored narrator '{0}' from grab history for download: {1}", historicalNarrator, downloadItem.Title);
                        }

                        // Also restore other historical metadata for completeness
                        var historicalDuration = GetHistoryData(grabbedEvent, "Duration");
                        if (!string.IsNullOrWhiteSpace(historicalDuration))
                        {
                            if (trackedDownload.RemoteBook.ParsedBookInfo.ExtraInfo == null)
                            {
                                trackedDownload.RemoteBook.ParsedBookInfo.ExtraInfo = new Dictionary<string, object>();
                            }

                            trackedDownload.RemoteBook.ParsedBookInfo.ExtraInfo["Duration"] = historicalDuration;
                        }

                        var historicalIsGraphicAudio = GetHistoryData(grabbedEvent, "IsGraphicAudio");
                        if (!string.IsNullOrWhiteSpace(historicalIsGraphicAudio) && bool.TryParse(historicalIsGraphicAudio, out var isGraphicAudio))
                        {
                            trackedDownload.RemoteBook.ParsedBookInfo.IsGraphicAudio = isGraphicAudio;
                        }
                    }
                }

                // Calculate custom formats
                if (trackedDownload.RemoteBook != null)
                {
                    trackedDownload.RemoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(trackedDownload.RemoteBook, downloadItem.TotalSize);
                    var qualityProfile = trackedDownload.RemoteBook.GetQualityProfile();
                    trackedDownload.RemoteBook.CustomFormatScore = qualityProfile?.CalculateCustomFormatScore(trackedDownload.RemoteBook.CustomFormats) ?? 0;
                }

                // A previously unknown blocked download can resolve after its author is added.
                // Replay its persisted failure once, retaining the original event time.
                if (downloadHistory?.EventType == DownloadHistoryEventType.DownloadImportIncomplete &&
                    downloadHistory.BookId == 0 &&
                    trackedDownload.RemoteBook?.GetBooksMatchingReleaseMediaType().Any(book => book.Id > 0) == true)
                {
                    _eventAggregator.PublishEvent(new BookImportIncompleteEvent(trackedDownload, downloadHistory, historyItems));
                }

                // Track it so it can be displayed in the queue even though we can't determine which artist it is for
                if (trackedDownload.RemoteBook == null)
                {
                    _logger.Trace("No Book found for download '{0}'", trackedDownload.DownloadItem.Title);
                }
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find book for " + downloadItem.Title);
                return null;
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);

            _cache.Set(trackedDownload.DownloadItem.DownloadId, trackedDownload);
            return trackedDownload;
        }

        private static bool ShouldKeepExistingTrackedDownload(TrackedDownload existingItem, DownloadHistory downloadHistory)
        {
            if (existingItem == null || existingItem.State == TrackedDownloadState.Downloading)
            {
                return false;
            }

            if (existingItem.State == TrackedDownloadState.ImportPending ||
                existingItem.State == TrackedDownloadState.Importing ||
                existingItem.State == TrackedDownloadState.ImportBlocked ||
                existingItem.State == TrackedDownloadState.DownloadFailedPending)
            {
                return true;
            }

            return downloadHistory?.EventType != DownloadHistoryEventType.DownloadGrabbed;
        }

        private static bool NeedsHistoryTargetRecovery(RemoteBook remoteBook)
        {
            return remoteBook == null ||
                   remoteBook.Author == null ||
                   remoteBook.Books == null ||
                   remoteBook.Books.Empty();
        }

        private static ParsedBookInfo BuildHistoryParsedBookInfo(EntityHistory historyItem, Author historyAuthor, List<Book> historyBooks)
        {
            var firstHistoryBook = historyBooks?.FirstOrDefault(book => book != null);

            return new ParsedBookInfo
            {
                ReleaseTitle = historyItem?.SourceTitle,
                AuthorName = historyAuthor?.Name,
                BookTitle = firstHistoryBook?.Title,
                Quality = IsKnownQuality(historyItem?.Quality) ? historyItem.Quality : null
            };
        }

        private static bool IsKnownQuality(QualityModel quality)
        {
            return quality?.Quality != null &&
                   quality.Quality != Quality.Unknown &&
                   quality.Quality != Quality.UnknownAudio;
        }

        private static bool NeedsQualityFallback(QualityModel quality)
        {
            return quality?.Quality == null ||
                   quality.Quality == Quality.Unknown ||
                   quality.Quality == Quality.UnknownAudio;
        }

        private static string GetHistoryData(EntityHistory history, string key)
        {
            return history?.GetDataValue(key);
        }

        private static bool IsForcedDownload(EntityHistory grabbedEvent)
        {
            if (bool.TryParse(GetHistoryData(grabbedEvent, "DownloadForced"), out var forced) && forced)
            {
                return true;
            }

            return string.Equals(
                GetHistoryData(grabbedEvent, "ReleaseSource"),
                ReleaseSourceType.InteractiveSearch.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDownloadHistoryData(DownloadHistory history, string key)
        {
            if (history?.Data == null || key.IsNullOrWhiteSpace())
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

        private static string FirstNotBlank(params string[] values)
        {
            return values?.FirstOrDefault(value => value.IsNotNullOrWhiteSpace());
        }

        private static void HydrateReleaseFromGrabHistory(RemoteBook remoteBook, EntityHistory grabbedEvent, DownloadHistory downloadGrabHistory, string indexer)
        {
            if (remoteBook == null)
            {
                return;
            }

            var storedRelease = downloadGrabHistory?.Release;
            remoteBook.Release ??= new ReleaseInfo();
            var release = remoteBook.Release;

            release.Indexer = FirstNotBlank(
                release.Indexer,
                indexer,
                GetHistoryData(grabbedEvent, EntityHistory.INDEXER),
                storedRelease?.Indexer,
                GetDownloadHistoryData(downloadGrabHistory, "indexer"));

            release.Title = FirstNotBlank(
                release.Title,
                storedRelease?.Title,
                grabbedEvent?.SourceTitle,
                remoteBook.ParsedBookInfo?.ReleaseTitle);

            if (release.NarratorEvidenceFields?.Count > 0 != true)
            {
                var rawEvidence = GetHistoryData(grabbedEvent, nameof(ReleaseInfo.NarratorEvidenceFields));
                release.NarratorEvidenceFields = storedRelease?.NarratorEvidenceFields?.Count > 0 == true
                    ? storedRelease.NarratorEvidenceFields.ToList()
                    : rawEvidence.IsNotNullOrWhiteSpace() ? Json.Deserialize<List<string>>(rawEvidence) : null;
            }

            release.Narrator = FirstNotBlank(release.Narrator, storedRelease?.Narrator, GetHistoryData(grabbedEvent, "Narrator"));
            release.Guid = FirstNotBlank(release.Guid, storedRelease?.Guid, GetHistoryData(grabbedEvent, "guid"));
            release.DownloadUrl = FirstNotBlank(release.DownloadUrl, storedRelease?.DownloadUrl, GetHistoryData(grabbedEvent, "downloadUrl"));
            release.InfoUrl = FirstNotBlank(release.InfoUrl, storedRelease?.InfoUrl, GetHistoryData(grabbedEvent, "nzbInfoUrl"));

            if (release.IndexerId <= 0 && int.TryParse(GetHistoryData(grabbedEvent, "IndexerId"), out var capturedIndexerId))
            {
                release.IndexerId = capturedIndexerId;
            }

            if (release.IndexerId <= 0)
            {
                if (downloadGrabHistory?.IndexerId > 0)
                {
                    release.IndexerId = downloadGrabHistory.IndexerId;
                }
                else if (storedRelease?.IndexerId > 0)
                {
                    release.IndexerId = storedRelease.IndexerId;
            }
            }

            if (release.Size <= 0)
            {
                if (storedRelease?.Size > 0)
                {
                    release.Size = storedRelease.Size;
                }
                else if (long.TryParse(GetHistoryData(grabbedEvent, EntityHistory.SIZE), out var historySize))
                {
                    release.Size = historySize;
            }
            }

            if (release.DownloadProtocol == DownloadProtocol.Unknown)
            {
                if (storedRelease != null && storedRelease.DownloadProtocol != DownloadProtocol.Unknown)
                {
                    release.DownloadProtocol = storedRelease.DownloadProtocol;
                }
                else if (downloadGrabHistory != null && downloadGrabHistory.Protocol != DownloadProtocol.Unknown)
                {
                    release.DownloadProtocol = downloadGrabHistory.Protocol;
            }
            }

            if (release.PublishDate == default && storedRelease != null)
            {
                release.PublishDate = storedRelease.PublishDate;
        }
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var untrackable = GetTrackedDownloads().ExceptBy(t => t.DownloadItem.DownloadId, trackedDownloads, t => t.DownloadItem.DownloadId, StringComparer.CurrentCulture).ToList();

            foreach (var trackedDownload in untrackable)
            {
                if (ShouldKeepMissingImportPipelineTrackedDownload(trackedDownload))
                {
                    trackedDownload.IsTrackable = true;
                    trackedDownloads.Add(trackedDownload);

                    _logger.Debug("Keeping '{0}' trackable with ChaptarrStage={1}, although it is no longer reported by the download client.",
                        trackedDownload.DownloadItem.Title,
                        trackedDownload.State);

                    continue;
                }

                trackedDownload.IsTrackable = false;
            }
        }

        private static bool ShouldKeepMissingImportPipelineTrackedDownload(TrackedDownload trackedDownload)
        {
            return trackedDownload?.DownloadItem != null &&
                   trackedDownload.DownloadItem.DownloadId.IsNotNullOrWhiteSpace() &&
                   (trackedDownload.State == TrackedDownloadState.ImportPending ||
                    trackedDownload.State == TrackedDownloadState.Importing);
        }

        private void LogItemChange(TrackedDownload trackedDownload, DownloadClientItem existingItem, DownloadClientItem downloadItem)
        {
            if (existingItem == null ||
                existingItem.Status != downloadItem.Status ||
                existingItem.CanBeRemoved != downloadItem.CanBeRemoved ||
                existingItem.CanMoveFiles != downloadItem.CanMoveFiles)
            {
                _logger.Debug("Tracking '{0}:{1}': ClientState={2}{3} ChaptarrStage={4} Book='{5}' OutputPath={6}.",
                    downloadItem.DownloadClientInfo.Name,
                    downloadItem.Title,
                    downloadItem.Status,
                    downloadItem.CanBeRemoved ? "" : downloadItem.CanMoveFiles ? " (busy)" : " (readonly)",
                    trackedDownload.State,
                    trackedDownload.RemoteBook?.ParsedBookInfo,
                    downloadItem.OutputPath);
            }
        }

        private void UpdateCachedItems(List<TrackedDownload> trackedDownloads, HashSet<int> removedBookIds, bool evictOnFailure = false)
        {
            if (trackedDownloads.Count == 0)
            {
                return;
            }

            var historyByDownloadId = _historyService.FindByDownloadIds(
                trackedDownloads.Select(item => item.DownloadItem?.DownloadId), EntityHistoryEventType.Grabbed)
                .Where(history => !string.IsNullOrWhiteSpace(history.DownloadId))
                .ToLookup(history => history.DownloadId, StringComparer.OrdinalIgnoreCase);

            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    var remoteBook = trackedDownload.RemoteBook;
                    var originalAuthorId = remoteBook?.Author?.Id;
                    var hadCapturedTargets = remoteBook?.Books?.Any(book => book?.Id > 0) == true;
                    var mediaTypes = (remoteBook?.Books ?? new List<Book>()).Where(book => book != null)
                        .Select(book => book.MediaType).Distinct().ToList();
                    var historyItems = RemoteBook.GetCapturedGrabHistory(historyByDownloadId[trackedDownload.DownloadItem.DownloadId]);
                    if (historyItems.Count > 0)
                    {
                        var context = historyItems[0];
                        remoteBook ??= new RemoteBook();
                        remoteBook.Author = context.Author;
                        remoteBook.ParsedBookInfo ??= BuildHistoryParsedBookInfo(context, context.Author, new List<Book>());
                        remoteBook.Books = historyItems.Select(history => history.Book)
                            .Where(book => book != null && book.Id > 0 && book.AuthorId == context.Author?.Id &&
                                (mediaTypes.Count != 1 || book.MediaType == mediaTypes[0]))
                            .GroupBy(book => book.Id).Select(group => group.First()).ToList();
                        remoteBook.RestoreGrabbedTargets(historyItems);
                    }
                    else if (remoteBook != null)
                    {
                        remoteBook.Books = (remoteBook.Books ?? new List<Book>())
                            .Where(book => book != null && book.Id > 0 && !removedBookIds.Contains(book.Id)).ToList();
                    }

                    // Title recovery is only for legacy downloads without captured targets.
                    if (!hadCapturedTargets && historyItems.Count == 0 && NeedsHistoryTargetRecovery(remoteBook) && remoteBook?.Author != null)
                    {
                        var parsed = remoteBook.ParsedBookInfo ?? Parser.Parser.ParseBookTitle(trackedDownload.DownloadItem.Title);
                        var mapped = parsed == null ? null : _parsingService.Map(parsed);
                        remoteBook.Books = mapped?.Author?.Id == originalAuthorId
                            ? (mapped.Books ?? new List<Book>()).Where(book => book != null && book.Id > 0 &&
                                (mediaTypes.Count != 1 || book.MediaType == mediaTypes[0])).ToList()
                            : new List<Book>();
                    }

                    if (remoteBook != null)
                    {
                        remoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(remoteBook, trackedDownload.DownloadItem.TotalSize);
                        remoteBook.CustomFormatScore = remoteBook.GetQualityProfile()?.CalculateCustomFormatScore(remoteBook.CustomFormats) ?? 0;
                    }

                    trackedDownload.RemoteBook = remoteBook;
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to refresh tracked download {0}", trackedDownload.DownloadItem?.DownloadId);
                    if (evictOnFailure) _cache.Remove(trackedDownload.DownloadItem.DownloadId);
                }
            }
        }

        private static TrackedDownloadState GetStateFromHistory(DownloadHistoryEventType eventType)
        {
            switch (eventType)
            {
                case DownloadHistoryEventType.DownloadImportIncomplete:
                    return TrackedDownloadState.ImportBlocked;
                case DownloadHistoryEventType.DownloadImported:
                    return TrackedDownloadState.Imported;
                case DownloadHistoryEventType.DownloadFailed:
                    return TrackedDownloadState.DownloadFailed;
                case DownloadHistoryEventType.DownloadIgnored:
                    return TrackedDownloadState.Ignored;
                default:
                    return TrackedDownloadState.Downloading;
            }
        }

        public void Handle(BookInfoRefreshedEvent message)
        {
            var removedBookIds = message.Removed.Where(book => book != null && book.Id > 0).Select(book => book.Id).ToHashSet();
            var cachedItems = _cache.Values.Where(item => item.RemoteBook?.Books?.Any(book => book != null && removedBookIds.Contains(book.Id)) == true).ToList();
            if (cachedItems.Count > 0)
            {
                UpdateCachedItems(cachedItems, removedBookIds);
                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(LibraryIdentityChangedEvent message)
        {
            var items = _cache.Values.Where(item => message.AuthorIds.Contains(item.RemoteBook?.Author?.Id ?? 0) ||
                item.RemoteBook?.Books?.Any(book => message.BookIds.Contains(book.Id)) == true).ToList();
            if (items.Count == 0) return;
            try { UpdateCachedItems(items, message.RemovedBookIds, evictOnFailure: true); }
            catch (Exception ex)
            {
                // The committed history is authoritative. Normal polling reconstructs these
                // targets and their terminal state; never retain a deleted in-memory identity.
                foreach (var item in items) _cache.Remove(item.DownloadItem.DownloadId);
                _logger.Warn(ex, "Failed to reload merged/split download targets; next poll will restore them from history");
            }
            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
        }

        public void Handle(BookUpdatedEvent message)
        {
            Handle(new LibraryIdentityChangedEvent(Array.Empty<int>(), new[] { message.Book.Id }));
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var cachedItems = _cache.Values.Where(t =>
                                        t.RemoteBook?.Author != null &&
                                        t.RemoteBook.Author.Id == message.Author.Id)
                                    .ToList();

            if (cachedItems.Any())
            {
                foreach (var item in cachedItems)
                {
                    item.RemoteBook.Author = null;
                    item.RemoteBook.Books = new List<Book>();
                }

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
