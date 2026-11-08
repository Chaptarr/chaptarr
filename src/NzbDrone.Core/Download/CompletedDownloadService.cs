using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
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
        bool CanRemoveDownload(TrackedDownload trackedDownload, DownloadCleanupContext cleanup = null);
    }

	    public class CompletedDownloadService : ICompletedDownloadService
	    {
            private static readonly TimeSpan SlowCleanupProbeThreshold = TimeSpan.FromSeconds(5);

	        private readonly IEventAggregator _eventAggregator;
	        private readonly IHistoryService _historyService;
	        private readonly IProvideImportItemService _provideImportItemService;
	        private readonly IDownloadedBooksImportService _downloadedTracksImportService;
	        private readonly IDownloadImportModeResolver _downloadImportModeResolver;
	        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
	        private readonly IDownloadClientFileSnapshotService _downloadClientFileSnapshotService;
	        private readonly Logger _logger;

	        public CompletedDownloadService(IEventAggregator eventAggregator,
	                                        IHistoryService historyService,
	                                        IProvideImportItemService provideImportItemService,
	                                        IDownloadedBooksImportService downloadedTracksImportService,
	                                        IDownloadImportModeResolver downloadImportModeResolver,
	                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
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
            trackedDownload.AwaitingImportConfirmation = false;

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

                var awaitingConfirmation = importResults.Where(result => result.Result == ImportResultType.WaitingForConfirmation).ToList();
                if (awaitingConfirmation.Count > 0)
                {
                    trackedDownload.AwaitingImportConfirmation = true;
                    BlockImport(trackedDownload, awaitingConfirmation.Select(result =>
                        new TrackedDownloadStatusMessage(GetImportFileTitle(trackedDownload, result.ImportDecision.Item.Path), result.Errors)).ToArray());
                    return;
                }

                if (importResults.Any(result => result.Result == ImportResultType.Pending))
                {
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    trackedDownload.Wait(BuildImportStatusMessages(trackedDownload,
                        importResults.Where(result => result.Result == ImportResultType.Pending).ToList()));
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

                if (_trackedDownloadAlreadyImported.IsImported(trackedDownload, history, importResults))
                {
                    var included = _downloadedTracksImportService.ProcessIncludedMedia(
                        trackedDownload.ImportItem ?? trackedDownload.DownloadItem, trackedDownload.RemoteBook, history);
                    if (included.Any(result => result.Result == ImportResultType.Pending))
                    {
                        trackedDownload.State = TrackedDownloadState.ImportPending;
                        trackedDownload.Wait(BuildImportStatusMessages(trackedDownload, included.Where(result => result.Result == ImportResultType.Pending).ToList()));
                        PublishTrackedDownloadUpdated(trackedDownload);
                    }
                    else
                    {
                        CompleteImport(trackedDownload, importResults.Concat(included).ToList(), history);
                    }
                    return;
                }

                if (importResults.Empty())
                {
                    var statusMessages = new[]
                    {
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, "NO_ELIGIBLE_FILES"),
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, string.Format("No files found are eligible for import in {0}", outputPath))
                    };

                    trackedDownload.Warn(BuildImportStatusMessages(trackedDownload, importResults, statusMessages));
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                    PublishTrackedDownloadUpdated(trackedDownload);
                    return;
                }

                var mismatch = importResults.FirstOrDefault(result => result.ImportDecision.Rejections
                    .Any(rejection => rejection.Category == GrabbedFormatMismatch.RejectionCategory))?.Errors.FirstOrDefault();
                if (mismatch == null && importResults.All(result => result.Result == ImportResultType.Rejected &&
                    result.ImportDecision.Rejections.Any(rejection => rejection.IsQualityFilter)))
                {
                    var grabbedQuality = history?.Where(row => row.EventType == EntityHistoryEventType.Grabbed)
                        .OrderByDescending(row => row.Date).FirstOrDefault()?.Quality?.Quality;
                    var media = trackedDownload.RemoteBook?.GetMediaType();
                    var profile = media.HasValue ? trackedDownload.RemoteBook?.Author?.GetQualityProfileForMediaType(media.Value) : null;
                    mismatch = GrabbedFormatMismatch.Message(trackedDownload.DownloadItem.Title,
                        importResults.Select(result => result.ImportDecision.Item.Path), media, grabbedQuality, profile);
                }

                if (mismatch != null)
                {
                    _logger.Warn("Download {0} ({1}) requires Manual Import: {2}",
                        trackedDownload.DownloadItem.Title, trackedDownload.DownloadItem.DownloadId, mismatch);
                    BlockImport(trackedDownload, BuildImportStatusMessages(trackedDownload, importResults,
                        new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, mismatch)));
                    return;
                }

                if (AreAllImportRejectionsTemporary(importResults))
                {
                    trackedDownload.Warn(BuildImportStatusMessages(trackedDownload, importResults));
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

                    BlockImport(trackedDownload, BuildImportStatusMessages(trackedDownload, importResults, statusMessages));
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
                    BlockImport(trackedDownload, BuildImportStatusMessages(trackedDownload, importResults));
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

        private static TrackedDownloadStatusMessage[] BuildImportStatusMessages(TrackedDownload trackedDownload,
            IEnumerable<ImportResult> results, params TrackedDownloadStatusMessage[] headlines)
        {
            var messages = headlines.Concat(trackedDownload.StatusMessages
                .Where(message => headlines.Length == 0 || !message.IsCompletionFallback)).ToList();
            var reportedPaths = messages.Select(message => message.SourcePath ?? message.Title)
                .Where(title => title.IsPathValid(PathValidationType.CurrentOs)).ToHashSet(PathEqualityComparer.Instance);
            var headlineReasons = headlines.SelectMany(message => message.Messages).ToHashSet();
            foreach (var result in results.Where(result => result.Result != ImportResultType.Imported))
            {
                var reasons = result.Errors.Where(reason => !headlineReasons.Contains(reason)).ToList();
                if (reasons.Count == 0) continue;
                foreach (var path in TrackedDownloadAlreadyImported.GetSourcePaths(result.ImportDecision?.Item))
                {
                    if (reportedPaths.Add(path)) messages.Add(new TrackedDownloadStatusMessage(path, reasons) { SourcePath = path });
                }
            }
            foreach (var message in messages)
            {
                if (message.Title.IsPathValid(PathValidationType.CurrentOs) && Path.IsPathRooted(message.Title))
                    message.Title = GetImportFileTitle(trackedDownload, message.Title);
            }
            return messages.ToArray();
        }

        internal static string GetImportFileTitle(TrackedDownload trackedDownload, string path)
        {
            var outputFolder = (trackedDownload.ImportItem ?? trackedDownload.DownloadItem).OutputPath.FullPath;
            return !string.IsNullOrWhiteSpace(outputFolder) && outputFolder.IsParentPath(path)
                ? outputFolder.GetRelativePath(path)
                : Path.GetFileName(path);
        }

        public bool CanRemoveDownload(TrackedDownload trackedDownload, DownloadCleanupContext cleanup = null)
        {
            if (trackedDownload.State != TrackedDownloadState.Imported)
            {
                return false;
            }

            const string additionalFilesMessage = "Additional book files have not been imported. Use Manual Import to review them and Combine with the existing files.";
            var alreadyReported = trackedDownload.StatusMessages.Any(message => message.Messages.Contains(additionalFilesMessage));
            var item = trackedDownload.ImportItem ?? trackedDownload.DownloadItem;
            bool complete;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    _downloadClientFileSnapshotService.CaptureCompletedOutput(item, throwOnError: true, cleanup: cleanup);
                }
                finally
                {
                    if (stopwatch.Elapsed > SlowCleanupProbeThreshold && !trackedDownload.SlowCleanupProbeReported)
                    {
                        trackedDownload.SlowCleanupProbeReported = true;
                        _logger.Warn("Download cleanup file check for {0} at {1} took {2:F1}s (warning threshold {3:F0}s). Check the storage or remote mount if cleanup is slow.",
                            item.DownloadId, item.OutputPath, stopwatch.Elapsed.TotalSeconds, SlowCleanupProbeThreshold.TotalSeconds);
                    }
                }
                trackedDownload.DownloadItem.FilePaths = item.FilePaths;
                trackedDownload.DownloadItem.FileListConfidence = item.FileListConfidence;

                // A disk snapshot may have been saved before a failed check or restart.
                // Verify its contents, not just paths added during this attempt.
                complete = item.FileListConfidence != DownloadClientFileListConfidence.Disk ||
                    _trackedDownloadAlreadyImported.IsImported(trackedDownload,
                        _historyService.FindByDownloadId(item.DownloadId));
            }
            catch (Exception ex)
            {
                var reason = ex is DownloadOutputReadException
                    ? "Unable to read download files before cleanup"
                    : "Unable to verify download import evidence before cleanup";
                _logger.Log(trackedDownload.CleanupCheckFailed ? LogLevel.Debug : LogLevel.Warn, ex,
                    "{0} for {1}: {2}. Keeping files and retrying on the next cleanup pass.", reason, item.DownloadId, item.OutputPath);
                var firstFailure = !trackedDownload.CleanupCheckFailed;
                trackedDownload.CleanupCheckFailed = true;
                trackedDownload.IsTrackable = true;
                trackedDownload.Warn("{0}: {1}. Files are kept; Chaptarr will retry automatically.", reason, ex.Message);
                if (firstFailure)
                {
                    _eventAggregator.PublishEvent(new DownloadCleanupStatusChangedEvent());
                    PublishTrackedDownloadUpdated(trackedDownload);
                }
                return false;
            }

            if (trackedDownload.CleanupCheckFailed)
            {
                _logger.Info("Cleanup verification recovered for download {0}: {1}", item.DownloadId, item.OutputPath);
                trackedDownload.CleanupCheckFailed = false;
                _eventAggregator.PublishEvent(new DownloadCleanupStatusChangedEvent());
                if (complete)
                {
                    trackedDownload.ClearStatus();
                    PublishTrackedDownloadUpdated(trackedDownload);
                }
            }

            if (complete)
            {
                return true;
            }

            if (!trackedDownload.StatusMessages.Any(message => message.Messages.Contains(additionalFilesMessage)))
            {
                trackedDownload.Warn(trackedDownload.StatusMessages.Append(new TrackedDownloadStatusMessage(item.Title, additionalFilesMessage)).ToArray());
            }
            _logger.Log(alreadyReported ? LogLevel.Debug : LogLevel.Warn, "Keeping download {0} for Manual Import: {1}", item.DownloadId,
                string.Join("; ", trackedDownload.StatusMessages.SelectMany(message => message.Messages.Prepend(message.Title))));
            trackedDownload.IsTrackable = true;
            BlockImportWithCurrentStatus(trackedDownload);
            return false;
        }

        private static bool AreAllImportRejectionsTemporary(List<ImportResult> importResults)
        {
            return importResults.All(result =>
                result.Result != ImportResultType.Imported &&
                result.ImportDecision?.Rejections?.Any() == true &&
                result.ImportDecision.Rejections.All(rejection => rejection.Type == RejectionType.Temporary));
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

            CompleteImport(trackedDownload, importResults, history);
            return true;
        }

        private void CompleteImport(TrackedDownload trackedDownload, List<ImportResult> importResults, List<EntityHistory> history)
        {
            _logger.Debug("All required source files were imported for {0}", trackedDownload.DownloadItem.Title);
            trackedDownload.State = TrackedDownloadState.Imported;
            trackedDownload.ClearStatus();
            var skipped = importResults.Where(result => result.IsIncludedMediaSkip).ToList();
            if (skipped.Count > 0)
            {
                trackedDownload.Warn(skipped.Select(result => new TrackedDownloadStatusMessage(
                    GetImportFileTitle(trackedDownload, result.ImportDecision.Item.Path), result.Errors)).ToArray());
                var message = string.Join(Environment.NewLine, skipped.Select(result =>
                    $"Skipped included file '{GetImportFileTitle(trackedDownload, result.ImportDecision.Item.Path)}': {string.Join("; ", result.Errors)}").Distinct());
                var importedHistory = (history ?? new List<EntityHistory>())
                    .Where(row => row.EventType == EntityHistoryEventType.BookFileImported).ToList();
                foreach (var row in importedHistory)
                {
                    var previous = row.GetDataValue("Message");
                    row.Data ??= new Dictionary<string, string>();
                    var key = row.Data.Keys.FirstOrDefault(key => string.Equals(key, "Message", StringComparison.OrdinalIgnoreCase)) ?? "Message";
                    var lines = (previous ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    foreach (var line in message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                        if (!lines.Contains(line, StringComparer.Ordinal)) lines.Add(line);
                    row.Data[key] = string.Join(Environment.NewLine, lines);
                }

                if (importedHistory.Count > 0)
                {
                    try
                    {
                        _historyService.UpdateMany(importedHistory);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Could not save the included-file skip note for download {0}; the requested import is complete", trackedDownload.DownloadItem.DownloadId);
                    }
                }
            }

            PublishTrackedDownloadUpdated(trackedDownload);
            var authorIds = importResults.Where(result => result.Result == ImportResultType.Imported)
                .Select(result => result.ImportDecision.Item?.Author?.Id ?? result.ImportDecision.Item?.Book?.AuthorId ?? 0);
            _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload,
                ResolveCompletedAuthorId(trackedDownload, authorIds,
                    (history ?? new List<EntityHistory>()).Where(row => row.EventType == EntityHistoryEventType.BookFileImported).Select(row => row.AuthorId))));
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
