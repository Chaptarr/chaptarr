using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);
    }

	    public class CompletedDownloadService : ICompletedDownloadService
	    {
	        // Restore the pre-d60 three-cycle grace for files arriving through a remote
	        // mount, but never keep a completed download retrying for missing files forever.
	        private const int MissingAuthoritativeMediaFileBlockThreshold = 3;
	        private readonly IEventAggregator _eventAggregator;
	        private readonly IHistoryService _historyService;
	        private readonly IProvideImportItemService _provideImportItemService;
	        private readonly IDownloadedBooksImportService _downloadedTracksImportService;
	        private readonly IDownloadImportModeResolver _downloadImportModeResolver;
	        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
	        private readonly IDownloadClientFileSnapshotService _downloadClientFileSnapshotService;
	        private readonly IFailedDownloadService _failedDownloadService;
	        private readonly Logger _logger;

	        public CompletedDownloadService(IEventAggregator eventAggregator,
	                                        IHistoryService historyService,
	                                        IProvideImportItemService provideImportItemService,
	                                        IDownloadedBooksImportService downloadedTracksImportService,
	                                        IDownloadImportModeResolver downloadImportModeResolver,
	                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
	                                        IFailedDownloadService failedDownloadService,
	                                        Logger logger,
	                                        IDownloadClientFileSnapshotService downloadClientFileSnapshotService)
	        {
	            _eventAggregator = eventAggregator;
	            _historyService = historyService;
	            _provideImportItemService = provideImportItemService;
	            _downloadedTracksImportService = downloadedTracksImportService;
	            _downloadImportModeResolver = downloadImportModeResolver;
	            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
	            _downloadClientFileSnapshotService = downloadClientFileSnapshotService;
	            _failedDownloadService = failedDownloadService;
	            _logger = logger;
	        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading
            if (trackedDownload.State != TrackedDownloadState.Downloading)
            {
                return;
            }

            var historyItem = _historyService.MostRecentForDownloadId(trackedDownload.DownloadItem.DownloadId);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Chaptarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;
            PublishTrackedDownloadUpdated(trackedDownload);
        }

        public void Import(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);
            trackedDownload.ClearStatus();

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            try
            {
                trackedDownload.State = TrackedDownloadState.Importing;
                PublishTrackedDownloadUpdated(trackedDownload);

                var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
                _downloadClientFileSnapshotService.CaptureCompletedOutput(trackedDownload.ImportItem);
                _downloadClientFileSnapshotService.ApplySnapshot(trackedDownload.DownloadItem);

                var expectedBookIds = GetExpectedTrackedBookIds(trackedDownload);
                if (HasGrabHistory(trackedDownload) && expectedBookIds.Count == 0)
                {
                    _logger.Debug("[STRICT-IMPORT] Grab history exists for {0}, but target book context is missing; falling back to best-effort import matching.",
                        trackedDownload.DownloadItem.Title);
                }

                var parsedBookInfo = trackedDownload.RemoteBook?.ParsedBookInfo;
                _logger.Debug("[IMPORT-TRIGGER] CompletedDownloadService.Import calling ProcessPath for completed download: {0}, path: {1}", trackedDownload.DownloadItem.Title, outputPath);
                var importMode = _downloadImportModeResolver.Resolve(ImportMode.Auto, trackedDownload.DownloadItem);
                var importResults = _downloadedTracksImportService.ProcessPath(outputPath, importMode, trackedDownload.RemoteBook?.Author, trackedDownload.DownloadItem, trackedDownload.RemoteBook);
                ApplyActualFileQuality(trackedDownload, importResults);

                if (importResults.Any(result => result.Result == ImportResultType.Pending))
                {
                    trackedDownload.MissingAuthoritativeMediaFileAttempts = 0;
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    trackedDownload.ClearStatus();
                    PublishTrackedDownloadUpdated(trackedDownload);
                    _logger.Debug("Import deferred while detached conversion runs for {0}.", trackedDownload.DownloadItem.Title);
                    return;
                }

                // Log if narrator information is being passed through
                if (parsedBookInfo?.Narrator != null)
                {
                    _logger.Debug("Passing narrator information to import service: {0} for download: {1}", parsedBookInfo.Narrator, trackedDownload.DownloadItem.Title);
                }

                if (importResults.Empty())
                {
                    var statusMessages = new[]
                    {
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "NO_ELIGIBLE_FILES"),
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, string.Format("No files found are eligible for import in {0}", outputPath))
                    };

                    BlockImport(trackedDownload, statusMessages);
                    return;
                }

                if (AreAllImportRejectionsMissingAuthoritativeMediaFiles(importResults))
                {
                    trackedDownload.MissingAuthoritativeMediaFileAttempts++;
                    var statusMessages = importResults
                        .Where(result => result.ImportDecision.Item != null)
                        .Select(result => new TrackedDownloadStatusMessage(Path.GetFileName(result.ImportDecision.Item.Path), result.Errors))
                        .ToArray();

                    trackedDownload.Warn(statusMessages);

                    if (trackedDownload.MissingAuthoritativeMediaFileAttempts >= MissingAuthoritativeMediaFileBlockThreshold)
                    {
                        _logger.Warn("Declared download files did not become readable for {0} after {1} attempts; blocking automatic retries until user action.",
                            trackedDownload.DownloadItem.Title,
                            trackedDownload.MissingAuthoritativeMediaFileAttempts);
                        BlockImportWithCurrentStatus(trackedDownload);
                        return;
                    }

                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    PublishTrackedDownloadUpdated(trackedDownload);
                    return;
                }

                trackedDownload.MissingAuthoritativeMediaFileAttempts = 0;

                if (AreAllImportRejectionsTemporary(importResults))
                {
                    var statusMessages = importResults
                        .Where(result => result.ImportDecision.Item != null)
                        .Select(result => new TrackedDownloadStatusMessage(Path.GetFileName(result.ImportDecision.Item.Path), result.Errors))
                        .ToArray();

                    trackedDownload.Warn(statusMessages);
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    PublishTrackedDownloadUpdated(trackedDownload);
                    return;
                }

                if (VerifyImport(trackedDownload, importResults))
                {
                    return;
                }

                var strictVerificationFailure = GetStrictVerificationFailure(trackedDownload, importResults);
                if (strictVerificationFailure != null)
                {
                    var statusMessages = new[]
                    {
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "STRICT_TARGET_VERIFICATION_FAILED"),
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, strictVerificationFailure)
                    };

                    BlockImport(trackedDownload, statusMessages);
                    return;
                }

                if (importResults.Any(c => c.Result != ImportResultType.Imported))
                {
                    // Nothing importable in the payload because every file is a format the profile
                    // does not allow: the release simply is not what its title advertised. That is
                    // deterministic — retrying imports the same files and gets the same answer — so
                    // fail it like any other bad grab (blocklist + configured replacement search)
                    // instead of parking it in the queue forever. Explicit user grabs are exempt.
                    if (IsProfileDisallowedPayload(importResults) && !IsUserForced(trackedDownload))
                    {
                        var reason = string.Format("No file in this download is in a format allowed by the quality profile ({0})",
                            string.Join(", ", GetDisallowedFormatSummary(importResults)));

                        _logger.Warn("{0} for {1}; marking failed so it can be replaced.", reason, trackedDownload.DownloadItem.Title);

                        _failedDownloadService.MarkAsFailed(trackedDownload, reason);
                        return;
                    }

                    var statusMessages = importResults
                        .Where(v => v.Result != ImportResultType.Imported && v.ImportDecision.Item != null)
                        .Select(v => new TrackedDownloadStatusMessage(Path.GetFileName(v.ImportDecision.Item.Path), v.Errors))
                        .ToArray();

                    BlockImport(trackedDownload, statusMessages);
                    return;
                }

                BlockImport(trackedDownload,
                    new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "IMPORT_VERIFICATION_INCOMPLETE"),
                    new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "Imported files did not satisfy every expected book in the download. Use Retry Import after correcting the match or files."));
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to import download: {0}", trackedDownload.DownloadItem.Title);

                var statusMessages = new[]
                {
                    new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "IMPORT_EXCEPTION"),
                    new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, string.Format("{0}: {1}", e.GetType().Name, e.Message))
                };

                BlockImport(trackedDownload, statusMessages);
            }
        }

        /// <summary>
        /// True when the import produced nothing and every file it did consider was refused by the
        /// quality-profile gate. Categorised rejections (Rejection.IsQualityFilter) are the signal —
        /// never the message text — so a transient or unknown failure can never be mistaken for a
        /// payload mismatch and blocklisted.
        /// </summary>
        internal static bool IsProfileDisallowedPayload(List<ImportResult> importResults)
        {
            if (importResults == null || importResults.Any(result => result.Result == ImportResultType.Imported))
            {
                return false;
            }

            var consideredFiles = importResults
                .Where(result => result.ImportDecision?.Item != null)
                .ToList();

            if (!consideredFiles.Any())
            {
                return false;
            }

            return consideredFiles.All(result =>
                result.ImportDecision.Rejections?.Any(rejection => rejection.IsQualityFilter) == true);
        }

        private static bool AreAllImportRejectionsTemporary(List<ImportResult> importResults)
        {
            return importResults.All(result =>
                result.Result != ImportResultType.Imported &&
                result.ImportDecision?.Rejections?.Any() == true &&
                result.ImportDecision.Rejections.All(rejection => rejection.Type == RejectionType.Temporary));
        }

        private static bool AreAllImportRejectionsMissingAuthoritativeMediaFiles(List<ImportResult> importResults)
        {
            return AreAllImportRejectionsTemporary(importResults) &&
                   importResults.All(result => result.ImportDecision.Rejections.All(rejection =>
                       string.Equals(
                           rejection.Category,
                           DownloadedBooksImportService.MissingAuthoritativeMediaFilesRejectionCategory,
                           StringComparison.Ordinal)));
        }

        private static IEnumerable<string> GetDisallowedFormatSummary(List<ImportResult> importResults)
        {
            return importResults
                .Where(result => result.ImportDecision?.Item != null)
                .Select(result => result.ImportDecision.Item.Quality?.Quality?.Name)
                .Where(name => !name.IsNullOrWhiteSpace())
                .Distinct()
                .DefaultIfEmpty("unknown format");
        }

        private static bool IsUserForced(TrackedDownload trackedDownload)
        {
            return trackedDownload?.DownloadItem?.DownloadForced == true;
        }

        private void BlockImport(TrackedDownload trackedDownload, params TrackedDownloadStatusMessage[] statusMessages)
        {
            trackedDownload.Warn(statusMessages);
            BlockImportWithCurrentStatus(trackedDownload);
        }

        private void BlockImportWithCurrentStatus(TrackedDownload trackedDownload)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;
            PublishImportIncompleteIfChanged(trackedDownload);
        }

        private void PublishImportIncompleteIfChanged(TrackedDownload trackedDownload)
        {
            PublishTrackedDownloadUpdated(trackedDownload);

            var downloadId = trackedDownload?.DownloadItem?.DownloadId;
            if (!downloadId.IsNullOrWhiteSpace())
            {
                var statusMessages = trackedDownload.StatusMessages.ToJson();
                var latestIncomplete = _historyService.FindByDownloadId(downloadId)
                    .Where(h => h.EventType == EntityHistoryEventType.BookImportIncomplete)
                    .OrderByDescending(h => h.Date)
                    .FirstOrDefault();

                if (latestIncomplete != null &&
                    string.Equals(latestIncomplete.SourceTitle, trackedDownload.DownloadItem.Title, StringComparison.Ordinal) &&
                    string.Equals(GetStatusMessages(latestIncomplete), statusMessages, StringComparison.Ordinal))
                {
                    _logger.Debug("Skipping unchanged import incomplete event for download {0}: {1}", downloadId, trackedDownload.DownloadItem.Title);
                    return;
                }
            }

            _eventAggregator.PublishEvent(new BookImportIncompleteEvent(trackedDownload));
        }

        private static string GetStatusMessages(EntityHistory history)
        {
            return history.Data.GetValueOrDefault("statusMessages") ??
                   history.Data.GetValueOrDefault("StatusMessages");
        }

        internal static void ApplyActualFileQuality(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var fileQuality = importResults?
                .Where(result => result.Result == ImportResultType.Imported)
                .Select(result => result.ImportDecision?.Item?.Quality)
                .FirstOrDefault(IsSpecificQuality)
                ?? importResults?
                    .Select(result => result.ImportDecision?.Item?.Quality)
                    .FirstOrDefault(IsSpecificQuality);

            if (fileQuality == null || trackedDownload?.RemoteBook == null)
            {
                return;
            }

            trackedDownload.RemoteBook.ParsedBookInfo ??= new ParsedBookInfo();
            trackedDownload.RemoteBook.ParsedBookInfo.Quality = fileQuality;
        }

        private static bool IsSpecificQuality(QualityModel quality)
        {
            return quality?.Quality != null &&
                   quality.Quality != Quality.Unknown &&
                   quality.Quality != Quality.UnknownAudio;
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var expectedBooks = GetExpectedTrackedBooks(trackedDownload);
            var strictExpectedBooks = GetStrictExpectedTrackedBooks(expectedBooks);
            var strictExpectedBookIds = strictExpectedBooks.Select(book => book.Id).Distinct().ToList();
            var importedBookIds = importResults.Where(c => c.Result == ImportResultType.Imported)
                                               .Select(c => c.ImportDecision.Item?.Book?.Id ?? 0)
                                               .Where(id => id > 0)
                                               .Distinct()
                                               .ToList();

            var allItemsImported = strictExpectedBookIds.Count > 0
                ? strictExpectedBookIds.All(importedBookIds.Contains) && importedBookIds.All(strictExpectedBookIds.Contains)
                : importedBookIds.Count >= Math.Max(1, expectedBooks.Count);

            if (allItemsImported)
            {
                _logger.Debug("All books were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;
                _downloadClientFileSnapshotService.Delete(trackedDownload.DownloadItem);
                _downloadClientFileSnapshotService.Delete(trackedDownload.ImportItem);
                PublishTrackedDownloadUpdated(trackedDownload);

                var importedAuthorIds = importResults.Where(x => x.Result == ImportResultType.Imported)
                    .Select(c => c.ImportDecision.Item?.Author?.Id ?? c.ImportDecision.Item?.Book?.AuthorId ?? 0);

                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, ResolveCompletedAuthorId(trackedDownload, importedAuthorIds)));
                return true;
            }

            // Double check if all episodes were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.

            // EDGE CASE: This process relies on EpisodeIds being consistent between executions, if a series is updated
            // and an episode is removed, but later comes back with a different ID then Sonarr will treat it as incomplete.
            // Since imports should be relatively fast and these types of data changes are infrequent this should be quite
            // safe, but commenting for future benefit.
            var atLeastOneEpisodeImported = importResults.Any(c => c.Result == ImportResultType.Imported);

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                                              .OrderByDescending(h => h.Date)
                                              .ToList();

            var allEpisodesImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);

            if (allEpisodesImportedInHistory)
            {
                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.
                if (atLeastOneEpisodeImported)
                {
                    _logger.Debug("All books were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    var historyAuthorId = ResolveCompletedAuthorId(
                        trackedDownload,
                        historyItems.Where(x => x.EventType == EntityHistoryEventType.BookFileImported).Select(x => x.AuthorId),
                        historyItems.Select(x => x.AuthorId));

                    _logger.ForDebugEvent()
                           .Message("No books were just imported, but all books were previously imported, possible issue with download history.")
                           .Property("AuthorId", historyAuthorId)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.DownloadItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                trackedDownload.State = TrackedDownloadState.Imported;
                _downloadClientFileSnapshotService.Delete(trackedDownload.DownloadItem);
                _downloadClientFileSnapshotService.Delete(trackedDownload.ImportItem);
                PublishTrackedDownloadUpdated(trackedDownload);

                _eventAggregator.PublishEvent(new DownloadCompletedEvent(
                    trackedDownload,
                    ResolveCompletedAuthorId(
                        trackedDownload,
                        historyItems.Where(x => x.EventType == EntityHistoryEventType.BookFileImported).Select(x => x.AuthorId),
                        historyItems.Select(x => x.AuthorId))));

                return true;
            }

            _logger.Debug("Not all books have been imported for {0}", trackedDownload.DownloadItem.Title);
            return false;
        }

        private string GetStrictVerificationFailure(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            if (!HasGrabHistory(trackedDownload))
            {
                return null;
            }

            var importedBooks = importResults?
                .Where(c => c.Result == ImportResultType.Imported)
                .Select(c => c.ImportDecision.Item?.Book)
                .Where(book => book != null)
                .ToList();

            if (importedBooks == null || importedBooks.Count == 0)
            {
                return null;
            }

            var expectedBooks = GetStrictExpectedTrackedBooks(trackedDownload);

            if (expectedBooks.Count == 0)
            {
                return null;
            }

            var expectedBookIds = expectedBooks.Select(book => book.Id).OrderBy(id => id).ToList();
            var importedBookIds = importedBooks.Select(book => book.Id).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();

            if (expectedBookIds.SequenceEqual(importedBookIds))
            {
                return null;
            }

            var expectedLabel = string.Join(", ", expectedBooks.Select(FormatBookLabel).Distinct());
            var importedLabel = string.Join(", ", importedBooks.Select(FormatBookLabel).Distinct());

            return $"Grabbed target was '{expectedLabel}', but imported files matched '{importedLabel}'.";
        }

        private static string FormatBookLabel(Book book)
        {
            if (book == null)
            {
                return "unknown book";
            }

            if (!book.Title.IsNullOrWhiteSpace())
            {
                return book.Title;
            }

            return book.Id > 0 ? $"BookId {book.Id}" : "unknown book";
        }

        private bool HasGrabHistory(TrackedDownload trackedDownload)
        {
            var downloadId = trackedDownload?.DownloadItem?.DownloadId;
            if (downloadId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var historyItems = _historyService.FindByDownloadId(downloadId);
            return historyItems?.Any(h => h.EventType == EntityHistoryEventType.Grabbed) == true;
        }

        private static List<int> GetExpectedTrackedBookIds(TrackedDownload trackedDownload)
        {
            return GetExpectedTrackedBooks(trackedDownload)
                .Select(book => book.Id)
                .Distinct()
                .ToList();
        }

        private static List<Book> GetStrictExpectedTrackedBooks(TrackedDownload trackedDownload)
        {
            return GetStrictExpectedTrackedBooks(GetExpectedTrackedBooks(trackedDownload));
        }

        private static List<Book> GetStrictExpectedTrackedBooks(IEnumerable<Book> expectedBooks)
        {
            return expectedBooks?
                .Where(IsStrictExpectedTrackedBook)
                .ToList() ?? new List<Book>();
        }

        private static bool IsStrictExpectedTrackedBook(Book book)
        {
            return book != null &&
                   book.Id > 0 &&
                   (!book.AnyEditionOk || book.Editions?.Any(edition => edition?.ManualAdd == true) == true);
        }

        private static List<Book> GetExpectedTrackedBooks(TrackedDownload trackedDownload)
        {
            return trackedDownload?.RemoteBook?
                .GetBooksMatchingReleaseMediaType()
                .Where(book => book != null && book.Id > 0)
                .ToList() ?? new List<Book>();
        }

        private static int ResolveCompletedAuthorId(TrackedDownload trackedDownload, params IEnumerable<int>[] candidateAuthorIds)
        {
            var trackedAuthorId = trackedDownload?.RemoteBook?.Author?.Id ?? 0;
            if (trackedAuthorId > 0)
            {
                return trackedAuthorId;
            }

            var trackedBookAuthorId = GetMostCommonPositiveId(
                trackedDownload?.RemoteBook?.Books?.Select(book => book?.AuthorId ?? 0));

            if (trackedBookAuthorId > 0)
            {
                return trackedBookAuthorId;
            }

            foreach (var ids in candidateAuthorIds)
            {
                var resolvedAuthorId = GetMostCommonPositiveId(ids);
                if (resolvedAuthorId > 0)
                {
                    return resolvedAuthorId;
                }
            }

            return 0;
        }

        private static int GetMostCommonPositiveId(IEnumerable<int> ids)
        {
            return ids?
                .Where(id => id > 0)
                .GroupBy(id => id)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? 0;
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            // Imported items are only seeding, ignored items will never import, and blocked items
            // wait for the user's Retry (which re-resolves its own import item) - refreshing their
            // file lists every monitoring cycle costs a client API call plus a snapshot write per
            // item, and resurrects snapshots that were deliberately deleted when the import completed.
            if (trackedDownload.State == TrackedDownloadState.Imported ||
                trackedDownload.State == TrackedDownloadState.ImportBlocked ||
                trackedDownload.State == TrackedDownloadState.Ignored)
            {
                return;
            }

            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
            _downloadClientFileSnapshotService.CaptureClientList(trackedDownload.ImportItem);
            _downloadClientFileSnapshotService.ApplySnapshot(trackedDownload.ImportItem);
            _downloadClientFileSnapshotService.ApplySnapshot(trackedDownload.DownloadItem);
        }

        private void PublishTrackedDownloadUpdated(TrackedDownload trackedDownload)
        {
            _eventAggregator.PublishEvent(new TrackedDownloadUpdatedEvent(trackedDownload));
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping.", downloadItemOutputPath);
                return false;
            }

            return true;
        }
    }
}
