using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.RootFolders;
using static NzbDrone.Core.MediaFiles.BookImport.BookImportSerializationHelper;

namespace NzbDrone.Core.Datastore
{
    public class StagingResidualQueueSweeper
    {
        private readonly IIngestQueueRepository _ingestQueue;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;

        public StagingResidualQueueSweeper(
            IIngestQueueRepository ingestQueue,
            IMediaFileService mediaFileService,
            IDiskProvider diskProvider,
            IRootFolderService rootFolderService,
            Logger logger)
        {
            _ingestQueue = ingestQueue;
            _mediaFileService = mediaFileService;
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        public int SweepAllResidualItems(int batchSize = 1000, bool completedOnly = false)
        {
            var stopwatch = Stopwatch.StartNew();
            var released = 0;
            var total = 0;
            var failedBatches = 0;
            var afterId = 0;
            var logPrefix = completedOnly ? "[STAGING-CLEANUP]" : "[STAGING-STARTUP]";

            while (true)
            {
                List<IngestQueueItem> items;
                try
                {
                    items = _ingestQueue.GetActiveItems(batchSize, afterId, includeCompleted: true);
                }
                catch (Exception ex)
                {
                    failedBatches++;
                    _logger.Warn(ex, "{0} Unable to read staging work after id {1}; remaining work retained", logPrefix, afterId);
                    break;
                }

                if (items == null || items.Count == 0)
                {
                    break;
                }

                // A failed batch stays durable; advance so unrelated work can still finish.
                afterId = items.Max(item => item.Id);
                if (completedOnly)
                {
                    items = items.Where(item => item.Status == "done").ToList();
                }

                if (items.Count == 0) { continue; }

                try
                {
                    var completedResults = _ingestQueue.GetImportResults(items.Select(item => item.Path).ToArray())
                        .ToDictionary(result => result.QueueItemId);
                    var representedPaths = (_mediaFileService.GetFileWithPath(items.Select(item => item.Path).ToList()) ?? new List<BookFile>())
                        .Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
                    var verifiedResults = new List<int>();
                    var verifiedPaths = new List<string>();
                    foreach (var item in items)
                    {
                        if (item.Status == "done" && completedResults.TryGetValue(item.Id, out var result) &&
                            (representedPaths.Contains(item.Path) || !_diskProvider.GetFileInfo(item.Path).Exists))
                        {
                            verifiedResults.Add(result.Id);
                        }
                        else if (!completedOnly && SweepItem(item, logPrefix) > 0)
                        {
                            total++;
                            verifiedPaths.Add(item.Path);
                        }
                    }

                    verifiedResults.AddRange(_ingestQueue.GetImportResults(verifiedPaths).Select(result => result.Id));
                    released += _ingestQueue.PurgeCompletedResults(verifiedResults);
                }
                catch (Exception ex)
                {
                    failedBatches++;
                    _logger.Warn(ex, "{0} Retained batch of {1} staging items through id {2}; continuing with later items", logPrefix, items.Count, afterId);
                }
            }

            _logger.Info("{0} Reconciled {1} abandoned items, released {2} completed items, failed batches: {3}, elapsed: {4}ms",
                logPrefix, total, released, failedBatches, stopwatch.ElapsedMilliseconds);
            return total;
        }

        public int SweepUnderPath(string pathPrefix, string logPrefix = "[STAGING-SWEEP]", int batchSize = 1000)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return 0;
            }

            var total = 0;
            var afterId = 0;
            LogMemorySnapshot("{0} SweepUnderPath start ('{1}', batchSize={2})", logPrefix, pathPrefix, batchSize);

            while (true)
            {
                var items = _ingestQueue.GetActiveItemsForSweepUnderPath(pathPrefix, batchSize, afterId);
                if (items == null || items.Count == 0)
                {
                    break;
                }

                foreach (var item in items)
                {
                    total += SweepItem(item, logPrefix);
                    afterId = Math.Max(afterId, item.Id);
                }
                LogMemorySnapshot("{0} SweepUnderPath batch complete ('{1}', batch={2}, total={3}, afterId={4})", logPrefix, pathPrefix, items.Count, total, afterId);

                if (items.Count < batchSize)
                {
                    break;
                }
            }

            if (total > 0)
            {
                _logger.Info("{0} Swept {1} residual staging items under '{2}'", logPrefix, total, pathPrefix);
            }
            LogMemorySnapshot("{0} SweepUnderPath complete ('{1}', total={2})", logPrefix, pathPrefix, total);

            return total;
        }

        private void LogMemorySnapshot(string message, params object[] args)
        {
            if (!_logger.IsDebugEnabled)
            {
                return;
            }

            try
            {
                var formatted = args == null || args.Length == 0 ? message : string.Format(message, args);
                _logger.Debug("[MEMORY] {0}: {1}", formatted, MemorySnapshot.CaptureDetailed());
            }
            catch
            {
                // Diagnostics must never affect staging cleanup.
            }
        }

        private int SweepItem(IngestQueueItem item, string logPrefix)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                return 0;
            }

            try
            {
                var tags = SafeDeserializeTags(item.TagsJson);
                var (outcome, reason) = StagingQueueFileDispositionHelper.EnsureVisibleOrIgnored(
                    item.Path,
                    tags,
                    item.DurationSeconds,
                    _mediaFileService,
                    _diskProvider,
                    _rootFolderService.GetBestRootFolder,
                    _logger,
                    logPrefix);

                if (reason == "UNMAPPED_CREATE_FAILED")
                {
                    _logger.Warn("{0} Keeping staging item {1}: its file could not be saved in the library", logPrefix, item.Id);
                    return 0;
                }

                var finalReason = outcome == ImportOutcome.Unmapped && !string.IsNullOrWhiteSpace(item.Err)
                    ? item.Err
                    : reason;

                _ingestQueue.CompleteItemWithResult(
                    item.Id,
                    item.Path,
                    outcome,
                    errorMessage: finalReason,
                    statusError: finalReason);

                _logger.Debug("{0} Swept staging item id={1} path='{2}' -> {3} ({4})",
                    logPrefix,
                    item.Id,
                    item.Path,
                    outcome,
                    finalReason);

                return 1;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "{0} Failed to sweep staging item id={1} path='{2}'", logPrefix, item.Id, item.Path);
                return 0;
            }
        }
    }
}
