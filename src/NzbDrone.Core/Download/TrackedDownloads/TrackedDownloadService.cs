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

        public void UpdateBookCache(int bookId)
        {
            var updateCacheItems = _cache.Values.Where(x => x.RemoteBook != null && x.RemoteBook.Books.Any(a => a.Id == bookId)).ToList();

            if (updateCacheItems.Any())
            {
                foreach (var item in updateCacheItems)
                {
                    var parsedBookInfo = Parser.Parser.ParseBookTitle(item.DownloadItem.Title);
                    item.RemoteBook = null;

                    if (parsedBookInfo != null)
                    {
                        item.RemoteBook = _parsingService.Map(parsedBookInfo);
                    }
                }

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
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

            // Once a download has imported (or been ignored) it only sits in the client seeding;
            // re-capturing its file list would re-create the snapshot row that was deliberately
            // deleted when the import completed. The persisted history covers the fresh-cache
            // case after a restart, when no in-memory item exists yet.
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
                if (!downloadForced && !existingItem.Added.HasValue)
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

                if (parsedBookInfo != null)
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
                    }
                }

                if (historyItems.Any())
                {
                    var firstHistoryItem = historyItems.First();
                    var grabbedEvent = historyItems.FirstOrDefault(v => v.EventType == EntityHistoryEventType.Grabbed);
                    var historyContextItem = grabbedEvent ?? firstHistoryItem;
                    trackedDownload.DownloadItem.DownloadForced = IsForcedDownload(grabbedEvent);
                    var grabbedBookIds = historyItems.Where(v => v.EventType == EntityHistoryEventType.Grabbed)
                        .Select(h => h.BookId)
                        .Where(id => id > 0)
                        .Distinct()
                        .ToList();

                    trackedDownload.Indexer = GetHistoryData(grabbedEvent, EntityHistory.INDEXER) ??
                                              downloadGrabHistory?.Release?.Indexer ??
                                              GetDownloadHistoryData(downloadGrabHistory, EntityHistory.INDEXER);
                    trackedDownload.Added = grabbedEvent?.Date ?? downloadGrabHistory?.Date;

                    // For a download Chaptarr grabbed, the DownloadId's grab history states which
                    // books it is for. The client's title is still parsed for metadata (quality,
                    // narrator, release group), but it does not get to replace those targets or add
                    // extra ones. Downloads Chaptarr did not grab have no grabbed ids and keep the
                    // sibling *arr title-matching behaviour, as do grabs whose book rows are gone
                    // (GetExistingBooks drops them — the 2026-05-23 stale-target contract).
                    if (grabbedBookIds.Any())
                    {
                        var grabTargeted = _parsingService.Map(
                            parsedBookInfo ?? BuildHistoryParsedBookInfo(historyContextItem, historyContextItem.Author, new List<Book>()),
                            historyContextItem.AuthorId,
                            grabbedBookIds);

                        if (!NeedsHistoryTargetRecovery(grabTargeted))
                        {
                            trackedDownload.RemoteBook = grabTargeted;
                        }
                    }

                    if (parsedBookInfo == null || NeedsHistoryTargetRecovery(trackedDownload.RemoteBook))
                    {
                        var historyAuthor = historyContextItem.Author;
                        var historyBooks = historyItems
                            .Where(v => v.EventType == EntityHistoryEventType.Grabbed)
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

                        if (parsedBookInfo != null)
                        {
                            trackedDownload.RemoteBook = _parsingService.Map(parsedBookInfo,
                                historyContextItem.AuthorId,
                                grabbedBookIds);
                        }
                        else if (historyAuthor != null && historyBooks.Any())
                        {
                            parsedBookInfo =
                                Parser.Parser.ParseBookTitleWithSearchCriteria(historyContextItem.SourceTitle,
                                    historyAuthor,
                                    historyBooks);

                            if (parsedBookInfo != null)
                            {
                                trackedDownload.RemoteBook = _parsingService.Map(parsedBookInfo,
                                    historyContextItem.AuthorId,
                                    grabbedBookIds);
                            }
                        }

                        if (NeedsHistoryTargetRecovery(trackedDownload.RemoteBook) &&
                            (historyContextItem.AuthorId > 0 || grabbedBookIds.Any()))
                        {
                            var historyParsedBookInfo = parsedBookInfo ?? BuildHistoryParsedBookInfo(historyContextItem, historyAuthor, historyBooks);
                            trackedDownload.RemoteBook = _parsingService.Map(historyParsedBookInfo, historyContextItem.AuthorId, grabbedBookIds);
                        }
                    }

                    HydrateReleaseFromGrabHistory(trackedDownload.RemoteBook, grabbedEvent, downloadGrabHistory, trackedDownload.Indexer);

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
                    var qualityProfile = trackedDownload.RemoteBook.Author?.GetQualityProfileForQuality(trackedDownload.RemoteBook.ParsedBookInfo?.Quality?.Quality ?? Quality.Unknown);
                    trackedDownload.RemoteBook.CustomFormatScore = qualityProfile?.CalculateCustomFormatScore(trackedDownload.RemoteBook.CustomFormats) ?? 0;
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

            release.Guid = FirstNotBlank(release.Guid, storedRelease?.Guid, GetHistoryData(grabbedEvent, "guid"));
            release.Author = FirstNotBlank(release.Author, storedRelease?.Author, GetHistoryData(grabbedEvent, "author"), remoteBook.ParsedBookInfo?.AuthorName);
            release.Book = FirstNotBlank(release.Book, storedRelease?.Book, GetHistoryData(grabbedEvent, "book"), remoteBook.ParsedBookInfo?.BookTitle);
            release.Isbn = FirstNotBlank(release.Isbn, storedRelease?.Isbn, GetHistoryData(grabbedEvent, "isbn"));
            release.DownloadUrl = FirstNotBlank(release.DownloadUrl, storedRelease?.DownloadUrl, GetHistoryData(grabbedEvent, "downloadUrl"));
            release.InfoUrl = FirstNotBlank(release.InfoUrl, storedRelease?.InfoUrl, GetHistoryData(grabbedEvent, "nzbInfoUrl"));
            release.CommentUrl = FirstNotBlank(release.CommentUrl, storedRelease?.CommentUrl, GetHistoryData(grabbedEvent, "commentUrl"));
            release.Container = FirstNotBlank(release.Container, storedRelease?.Container, GetHistoryData(grabbedEvent, "container"));
            release.Origin = FirstNotBlank(release.Origin, storedRelease?.Origin, GetHistoryData(grabbedEvent, "origin"));
            release.Source = FirstNotBlank(release.Source, storedRelease?.Source, GetHistoryData(grabbedEvent, "source"));
            release.Narrator = FirstNotBlank(release.Narrator, storedRelease?.Narrator, GetHistoryData(grabbedEvent, "Narrator"));
            release.Duration = FirstNotBlank(release.Duration, storedRelease?.Duration, GetHistoryData(grabbedEvent, "Duration"));

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

            release.IsGraphicAudio = release.IsGraphicAudio ||
                                     storedRelease?.IsGraphicAudio == true ||
                                     bool.TryParse(GetHistoryData(grabbedEvent, "IsGraphicAudio"), out var historyIsGraphicAudio) && historyIsGraphicAudio;

            HydrateParsedBookInfoFromReleaseMetadata(remoteBook);
        }

        private static void HydrateParsedBookInfoFromReleaseMetadata(RemoteBook remoteBook)
        {
            if (remoteBook?.Release == null)
            {
                return;
            }

            remoteBook.ParsedBookInfo ??= new ParsedBookInfo();
            var parsed = remoteBook.ParsedBookInfo;
            var release = remoteBook.Release;

            parsed.AuthorName = FirstNotBlank(parsed.AuthorName, release.Author);
            parsed.BookTitle = FirstNotBlank(parsed.BookTitle, release.Book);
            parsed.ReleaseTitle = FirstNotBlank(parsed.ReleaseTitle, release.Title);
            parsed.Narrator = FirstNotBlank(parsed.Narrator, release.Narrator);

            if (release.Isbn.IsNotNullOrWhiteSpace() && !parsed.ExtraInfo.ContainsKey("Isbn"))
            {
                parsed.ExtraInfo["Isbn"] = release.Isbn;
            }

            if (release.Container.IsNotNullOrWhiteSpace() && !parsed.ExtraInfo.ContainsKey("Container"))
            {
                parsed.ExtraInfo["Container"] = release.Container;
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

	        private void UpdateCachedItem(TrackedDownload trackedDownload)
	        {
	            if (trackedDownload == null)
	            {
	                return;
	            }

	            if (trackedDownload.DownloadItem == null || trackedDownload.DownloadItem.Title.IsNullOrWhiteSpace())
	            {
	                trackedDownload.RemoteBook = null;
	                return;
	            }

	            var parsedBookInfo = Parser.Parser.ParseBookTitle(trackedDownload.DownloadItem.Title);

	            trackedDownload.RemoteBook = parsedBookInfo == null ? null : _parsingService.Map(parsedBookInfo);
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
            var needsToUpdate = false;

            foreach (var episode in message.Removed)
            {
                var cachedItems = _cache.Values.Where(t =>
                                            t.RemoteBook?.Books != null &&
                                            t.RemoteBook.Books.Any(e => e.Id == episode.Id))
                                        .ToList();

                if (cachedItems.Any())
                {
                    needsToUpdate = true;
                }

                cachedItems.ForEach(UpdateCachedItem);
            }

            if (needsToUpdate)
            {
                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var cachedItems = _cache.Values.Where(t =>
                                        t.RemoteBook?.Author != null &&
                                        t.RemoteBook.Author.Id == message.Author.Id)
                                    .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
