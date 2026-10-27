using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
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

                var parsedBookInfo = trackedDownload.RemoteBook?.ParsedBookInfo;
                _logger.Debug("[IMPORT-TRIGGER] CompletedDownloadService.Import calling ProcessPath for completed download: {0}, path: {1}", trackedDownload.DownloadItem.Title, outputPath);
                var importMode = _downloadImportModeResolver.Resolve(ImportMode.Auto, trackedDownload.DownloadItem);
                var importResults = _downloadedTracksImportService.ProcessPath(outputPath, importMode, trackedDownload.RemoteBook?.Author, trackedDownload.DownloadItem, trackedDownload.RemoteBook);
                ApplyActualFileQuality(trackedDownload, importResults);

                if (importResults.Any(result => result.Result == ImportResultType.Pending))
                {
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

                var history = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId);
                var hasGrabHistory = history?.Any(row => row.EventType == EntityHistoryEventType.Grabbed) == true;
                if (hasGrabHistory && GetExpectedTrackedBookIds(trackedDownload).Count == 0)
                {
                    _logger.Debug("[STRICT-IMPORT] Grab history exists for {0}, but target book context is missing; falling back to best-effort import matching.",
                        trackedDownload.DownloadItem.Title);
                }

                if (VerifyImport(trackedDownload, importResults, history))
                {
                    return;
                }

                if (importResults.Empty())
                {
                    var statusMessages = new[]
                    {
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "NO_ELIGIBLE_FILES"),
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, string.Format("No files found are eligible for import in {0}", outputPath))
                    };

                    trackedDownload.Warn(trackedDownload.StatusMessages.Concat(statusMessages).ToArray());
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    PublishTrackedDownloadUpdated(trackedDownload);
                    return;
                }

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

                var strictVerificationFailure = GetStrictVerificationFailure(trackedDownload, importResults, hasGrabHistory);
                if (strictVerificationFailure != null)
                {
                    var statusMessages = new[]
                    {
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "STRICT_TARGET_VERIFICATION_FAILED"),
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, strictVerificationFailure)
                    };

                    BlockImport(trackedDownload, trackedDownload.StatusMessages.Concat(statusMessages).ToArray());
                    return;
                }

                // Matching can add library identities during import. Retain them for failure
                // reporting after verification without changing the original grabbed targets.
                if (trackedDownload.RemoteBook?.Books?.Any(book => book?.Id > 0) != true)
                {
                    var matchedBooks = importResults.Select(result => result.ImportDecision?.Item?.Book)
                        .Where(book => book?.Id > 0).DistinctBy(book => book.Id).ToList();
                    if (matchedBooks.Any())
                    {
                        trackedDownload.RemoteBook ??= new RemoteBook();
                        trackedDownload.RemoteBook.Books = matchedBooks;
                        trackedDownload.RemoteBook.Author ??= matchedBooks.First().Author;
                        ApplyActualFileQuality(trackedDownload, importResults);
                    }
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

                    BlockImport(trackedDownload, trackedDownload.StatusMessages.Concat(statusMessages).ToArray());
                    return;
                }

                BlockImportWithCurrentStatus(trackedDownload);
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
            PublishTrackedDownloadUpdated(trackedDownload);
            _eventAggregator.PublishEvent(new BookImportIncompleteEvent(trackedDownload));
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
            return VerifyImport(trackedDownload, importResults, _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId));
        }

        private bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults, List<EntityHistory> history)
        {
            if (!_trackedDownloadAlreadyImported.IsImported(trackedDownload, history, importResults))
            {
                return false;
            }

            _logger.Debug("All required source files were imported for {0}", trackedDownload.DownloadItem.Title);
            trackedDownload.State = TrackedDownloadState.Imported;
            trackedDownload.ClearStatus();
            PublishTrackedDownloadUpdated(trackedDownload);
            var authorIds = importResults.Where(result => result.Result == ImportResultType.Imported)
                .Select(result => result.ImportDecision.Item?.Author?.Id ?? result.ImportDecision.Item?.Book?.AuthorId ?? 0);
            _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload,
                ResolveCompletedAuthorId(trackedDownload, authorIds,
                    (history ?? new List<EntityHistory>()).Where(row => row.EventType == EntityHistoryEventType.BookFileImported).Select(row => row.AuthorId))));
            return true;
        }

        private string GetStrictVerificationFailure(TrackedDownload trackedDownload, List<ImportResult> importResults, bool hasGrabHistory)
        {
            if (!hasGrabHistory)
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

            if (trackedDownload.RemoteBook?.AlternativeCopies == true
                    ? importedBookIds.Count > 0 && importedBookIds.All(expectedBookIds.Contains)
                    : expectedBookIds.SequenceEqual(importedBookIds))
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

        private static List<int> GetExpectedTrackedBookIds(TrackedDownload trackedDownload)
        {
            return TrackedDownloadAlreadyImported.GetExpectedBooks(trackedDownload)
                .Select(book => book.Id)
                .Distinct()
                .ToList();
        }

        private static List<Book> GetStrictExpectedTrackedBooks(TrackedDownload trackedDownload)
        {
            return GetStrictExpectedTrackedBooks(TrackedDownloadAlreadyImported.GetExpectedBooks(trackedDownload));
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
            // item.
            if (trackedDownload.State == TrackedDownloadState.Imported ||
                trackedDownload.State == TrackedDownloadState.ImportBlocked ||
                trackedDownload.State == TrackedDownloadState.Ignored)
            {
                return;
            }

            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
            _downloadClientFileSnapshotService.CaptureClientList(trackedDownload.ImportItem);
            _downloadClientFileSnapshotService.ApplySnapshot(trackedDownload.ImportItem);
            trackedDownload.DownloadItem.FilePaths = trackedDownload.ImportItem.FilePaths;
            trackedDownload.DownloadItem.FileListConfidence = trackedDownload.ImportItem.FileListConfidence;
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
