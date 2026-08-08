using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Common.Extensions;
using System.Text.RegularExpressions;
using static NzbDrone.Core.MediaFiles.BookImport.BookImportSerializationHelper;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    /// <summary>
    /// Listens for author import completion and immediately processes queued staging items
    /// under the author’s folder(s), matching only to that author’s editions.
    /// </summary>
        public class IngestQueueOnAuthorReadyHandler : IHandle<AuthorRefreshCompleteEvent>, IHandle<PendingAuthorImportSucceededEvent>, IHandle<PendingAuthorImportFailedEvent>, IHandle<PendingAuthorImportCancelledEvent>, IHandle<AuthorFolderImportReadyEvent>
        {
        private readonly IIngestQueueRepository _ingestQueue;
            private readonly IFileMatchingService _fileMatching;
            private readonly IBookImportService _bookImport;
            private readonly IMediaFileService _mediaFileService;
            private readonly IMetadataTagService _metadataTagService;
            private readonly IMediaInfoExtractor _mediaInfoExtractor;
            private readonly IContainmentValidator _containmentValidator;
            private readonly IBookService _bookService;
            private readonly IEditionService _editionService;
            private readonly IBookUnitDestinationService _unitDestination;
            private readonly IRootFolderService _rootFolderService;
            private readonly NzbDrone.Common.Disk.IDiskProvider _diskProvider;
            private readonly IEventAggregator _eventAggregator;
            private readonly IManageCommandQueue _commandQueueManager;
            private readonly StagingResidualQueueSweeper _stagingResidualQueueSweeper;
            private readonly Logger _logger;
            private sealed class AuthorIngestGate : IDisposable
            {
                public SemaphoreSlim Semaphore { get; } = new SemaphoreSlim(1, 1);
                private int _refCount;

                public void AddRef()
                {
                    Interlocked.Increment(ref _refCount);
                }

                public int ReleaseRef()
                {
                    return Interlocked.Decrement(ref _refCount);
                }

                public void Dispose()
                {
                    Semaphore.Dispose();
                }
            }

            private readonly ConcurrentDictionary<int, AuthorIngestGate> _authorLocks = new ConcurrentDictionary<int, AuthorIngestGate>();
            // This path does matching, media-file writes, staging updates, and SQLite work.
            // Keep it serial during scans so author-ready bursts do not multiply heap and DB pressure.
            private static readonly SemaphoreSlim _globalIngestGate = new SemaphoreSlim(1, 1);
            private readonly ConcurrentDictionary<string, DateTime> _negativeCacheByUnit = new ConcurrentDictionary<string, DateTime>();
            private readonly ConcurrentDictionary<string, DateTime> _authorLastProcessed = new ConcurrentDictionary<string, DateTime>();
            private const int NegativeCacheMinutes = 30; // TTL for negative result caching
            private const int LargeAuthorReadyCompactionFileThreshold = 100;
            private static readonly TimeSpan LargeAuthorReadyCompactionDurationThreshold = TimeSpan.FromMinutes(2);

            public IngestQueueOnAuthorReadyHandler(
                IIngestQueueRepository ingestQueue,
                IFileMatchingService fileMatching,
                IBookImportService bookImport,
                IMediaFileService mediaFileService,
                IMetadataTagService metadataTagService,
                IMediaInfoExtractor mediaInfoExtractor,
                IContainmentValidator containmentValidator,
                IBookService bookService,
                IEditionService editionService,
                IBookUnitDestinationService unitDestination,
                IRootFolderService rootFolderService,
                NzbDrone.Common.Disk.IDiskProvider diskProvider,
                IEventAggregator eventAggregator,
                IManageCommandQueue commandQueueManager,
                StagingResidualQueueSweeper stagingResidualQueueSweeper,
                Logger logger)
            {
                _ingestQueue = ingestQueue;
                _fileMatching = fileMatching;
                _bookImport = bookImport;
                _mediaFileService = mediaFileService;
                _metadataTagService = metadataTagService;
                _mediaInfoExtractor = mediaInfoExtractor;
                _containmentValidator = containmentValidator;
                _bookService = bookService;
                _editionService = editionService;
                _unitDestination = unitDestination;
                _rootFolderService = rootFolderService;
                _diskProvider = diskProvider;
                _eventAggregator = eventAggregator;
                _commandQueueManager = commandQueueManager;
                _stagingResidualQueueSweeper = stagingResidualQueueSweeper;
                _logger = logger;
            }

            public void Handle(AuthorRefreshCompleteEvent message)
            {
            var author = message.Author;
            if (author == null || string.IsNullOrWhiteSpace(author.Name))
            {
                return;
            }

                try
                {
                    var commandId = ProgressMessageContext.CommandModel?.Id;
                    var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(author.Path)) prefixes.Add(NormalizeDirectory(author.Path));
                    if (!string.IsNullOrWhiteSpace(author.AudiobookPath)) prefixes.Add(NormalizeDirectory(author.AudiobookPath));
                    if (!string.IsNullOrWhiteSpace(author.EbookPath)) prefixes.Add(NormalizeDirectory(author.EbookPath));

                if (!prefixes.Any())
                {
                    _logger.Debug("[INGEST-AUTHOR-READY] No author folder paths to process for '{0}'", author.Name);
                    return;
                }

                foreach (var prefix in prefixes)
                {
                    // Throttle per author+prefix to allow different root folders to process independently
                    if (!ShouldProcessAuthorEvent(author.Id, prefix))
                    {
                        _logger.Debug("[INGEST-AUTHOR-READY][SKIP-DUP-EVENT] Recent event suppressed for '{0}' (ID: {1}), prefix: '{2}'", author.Name, author.Id, prefix);
                        continue;
                    }

                        if (!PrefixBelongsToAuthor(author, prefix))
                        {
                            _logger.Debug("[INGEST-AUTHOR-READY][SKIP] Prefix '{0}' does not belong to author '{1}'", prefix, author.Name);
                            continue;
                        }
                        // Schedule background processing with per-author lock to avoid overlap
                        var gate = _authorLocks.GetOrAdd(author.Id, _ => new AuthorIngestGate());
                        gate.AddRef();
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await gate.Semaphore.WaitAsync();
                                await _globalIngestGate.WaitAsync().ConfigureAwait(false);
                                try
                                {
                                    await ProcessQueuedUnderPrefix(author, prefix, commandId).ConfigureAwait(false);
                                }
                                finally
                                {
                                    _globalIngestGate.Release();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "[INGEST-AUTHOR-READY] Exception while processing prefix '{0}'", LeafName(prefix));
                            }
                            finally
                            {
                                gate.Semaphore.Release();
                                if (gate.ReleaseRef() == 0)
                                {
                                    if (_authorLocks.TryRemove(new KeyValuePair<int, AuthorIngestGate>(author.Id, gate)))
                                    {
                                        gate.Dispose();
                                    }
                                }
                            }
                        });

                        if (commandId.HasValue)
                        {
                            ImportCommandWorkTracker.Track(commandId.Value, task);
                        }
                    }
                }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[INGEST-AUTHOR-READY] Error processing queued items for author '{0}'", message.Author?.Name ?? "<null>");
            }
            }

            public void Handle(AuthorFolderImportReadyEvent message)
            {
                var author = message?.Author;
                var prefix = message?.Prefix;
                if (author == null || string.IsNullOrWhiteSpace(author.Name) || string.IsNullOrWhiteSpace(prefix))
                {
                    return;
                }

                    try
                    {
                        var commandId = ProgressMessageContext.CommandModel?.Id;
                        prefix = NormalizeDirectory(prefix);
                        _logger.Debug("[INGEST-AUTHOR-READY] Received AuthorFolderImportReadyEvent for '{0}' prefix '{1}'", author.Name, prefix);

                    // Throttle per author+prefix (this event is explicitly scoped to a discovered folder).
                    if (!ShouldProcessAuthorEvent(author.Id, prefix))
                    {
                        _logger.Debug("[INGEST-AUTHOR-READY][SKIP-DUP-EVENT] Recent event suppressed for '{0}' (ID: {1}), prefix: '{2}' (ExplicitPrefix)", author.Name, author.Id, prefix);
                        return;
                    }

                    if (!PrefixBelongsToAuthor(author, prefix))
                    {
                        _logger.Debug("[INGEST-AUTHOR-READY][SKIP] Prefix '{0}' does not belong to author '{1}' (ExplicitPrefix)", prefix, author.Name);
                        return;
                        }

                        var gate = _authorLocks.GetOrAdd(author.Id, _ => new AuthorIngestGate());
                        gate.AddRef();
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await gate.Semaphore.WaitAsync();
                                await _globalIngestGate.WaitAsync().ConfigureAwait(false);
                                try
                                {
                                    await ProcessQueuedUnderPrefix(author, prefix, commandId).ConfigureAwait(false);
                                }
                                finally
                                {
                                    _globalIngestGate.Release();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "[INGEST-AUTHOR-READY] (ExplicitPrefix) Exception while processing prefix '{0}'", LeafName(prefix));
                            }
                            finally
                            {
                                gate.Semaphore.Release();
                                if (gate.ReleaseRef() == 0)
                                {
                                    if (_authorLocks.TryRemove(new KeyValuePair<int, AuthorIngestGate>(author.Id, gate)))
                                    {
                                        gate.Dispose();
                                    }
                                }
                            }
                        });

                        if (commandId.HasValue)
                        {
                            ImportCommandWorkTracker.Track(commandId.Value, task);
                        }
                    }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[INGEST-AUTHOR-READY] (ExplicitPrefix) Error processing queued items for author '{0}'", author.Name);
                }
            }

            public void Handle(PendingAuthorImportSucceededEvent message)
            {
            var author = message.ImportedAuthor;
            if (author == null || string.IsNullOrWhiteSpace(author.Name))
            {
                return;
            }

                try
                {
                    var commandId = ProgressMessageContext.CommandModel?.Id;
                    var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(author.Path)) prefixes.Add(NormalizeDirectory(author.Path));
                    if (!string.IsNullOrWhiteSpace(author.AudiobookPath)) prefixes.Add(NormalizeDirectory(author.AudiobookPath));
                    if (!string.IsNullOrWhiteSpace(author.EbookPath)) prefixes.Add(NormalizeDirectory(author.EbookPath));
                if (!prefixes.Any())
                {
                    _logger.Debug("[INGEST-AUTHOR-READY] (PendingImport) No author folder paths to process for '{0}'", author.Name);
                    return;
                }

                foreach (var prefix in prefixes)
                {
                    // Throttle per author+prefix to allow different root folders to process independently
                    if (!ShouldProcessAuthorEvent(author.Id, prefix))
                    {
                        _logger.Debug("[INGEST-AUTHOR-READY][SKIP-DUP-EVENT] Recent event suppressed for '{0}' (ID: {1}), prefix: '{2}' (PendingImport)", author.Name, author.Id, prefix);
                        continue;
                    }

                        if (!PrefixBelongsToAuthor(author, prefix))
                        {
                            _logger.Debug("[INGEST-AUTHOR-READY][SKIP] Prefix '{0}' does not belong to author '{1}' (PendingImport)", prefix, author.Name);
                            continue;
                        }
                        var gate = _authorLocks.GetOrAdd(author.Id, _ => new AuthorIngestGate());
                        gate.AddRef();
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await gate.Semaphore.WaitAsync();
                                await _globalIngestGate.WaitAsync().ConfigureAwait(false);
                                try
                                {
                                    await ProcessQueuedUnderPrefix(author, prefix, commandId).ConfigureAwait(false);
                                }
                                finally
                                {
                                    _globalIngestGate.Release();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "[INGEST-AUTHOR-READY] (PendingImport) Exception while processing prefix '{0}'", LeafName(prefix));
                            }
                            finally
                            {
                                gate.Semaphore.Release();
                                if (gate.ReleaseRef() == 0)
                                {
                                    if (_authorLocks.TryRemove(new KeyValuePair<int, AuthorIngestGate>(author.Id, gate)))
                                    {
                                        gate.Dispose();
                                    }
                                }
                            }
                        });

                        if (commandId.HasValue)
                        {
                            ImportCommandWorkTracker.Track(commandId.Value, task);
                        }
                    }

                    QueueRescanForPendingImportSuccess(author, message.PendingImport);
                }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[INGEST-AUTHOR-READY] (PendingImport) Error processing queued items for author '{0}'", author.Name);
            }
        }

            public void Handle(PendingAuthorImportFailedEvent message)
            {
                SweepPendingImportResidualItems(message?.PendingImport, "[INGEST-PENDING-FAILED]");
            }

            public void Handle(PendingAuthorImportCancelledEvent message)
            {
                SweepPendingImportResidualItems(message?.PendingImport, "[INGEST-PENDING-CANCELLED]");
            }

            private void QueueRescanForPendingImportSuccess(Author author, PendingAuthorImport pendingImport)
            {
                try
                {
                    var foldersToRescan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    void AddIfHasUnmapped(string folder)
                    {
                        if (string.IsNullOrWhiteSpace(folder))
                        {
                            return;
                        }

                        var normalized = NormalizeDirectory(folder);
                        var files = _mediaFileService.GetFilesWithBasePath(normalized);
                        if (files.Any(file => file.EditionId == 0))
                        {
                            foldersToRescan.Add(normalized);
                        }
                    }

                    AddIfHasUnmapped(author.Path);
                    AddIfHasUnmapped(author.AudiobookPath);
                    AddIfHasUnmapped(author.EbookPath);
                    AddIfHasUnmapped(pendingImport?.DiscoveredAuthorFolderPath);

                    if (!foldersToRescan.Any())
                    {
                        return;
                    }

                    _commandQueueManager.Push(new RescanFoldersCommand(foldersToRescan.ToList(), FilterFilesType.None, new List<int> { author.Id }));
                    _logger.Debug("[INGEST-AUTHOR-READY] Queued targeted rescan for pending import success on author '{0}' across {1} folder(s)", author.Name, foldersToRescan.Count);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[INGEST-AUTHOR-READY] Failed to queue targeted rescan for author '{0}'", author?.Name ?? "<unknown>");
                }
            }

            private void SweepPendingImportResidualItems(PendingAuthorImport pendingImport, string logPrefix)
            {
                var prefix = NormalizeDirectory(pendingImport?.DiscoveredAuthorFolderPath);
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    return;
                }

                try
                {
                    _stagingResidualQueueSweeper.SweepUnderPath(prefix, logPrefix);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "{0} Failed sweeping residual staging items under '{1}'", logPrefix, prefix);
                }
            }

        private bool PrefixBelongsToAuthor(Author author, string prefix)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prefix)) return false;
                var np = NormalizeDirectory(prefix);
                return (!string.IsNullOrWhiteSpace(author.Path) && string.Equals(NormalizeDirectory(author.Path), np, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(author.AudiobookPath) && string.Equals(NormalizeDirectory(author.AudiobookPath), np, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(author.EbookPath) && string.Equals(NormalizeDirectory(author.EbookPath), np, StringComparison.OrdinalIgnoreCase));
            }
            catch { return true; }
        }

        private static MatchingContext CreateAuthorReadyMatchingContext()
        {
            return MatchingContextPresets.ForAuthorReady();
        }

        private async Task ProcessQueuedUnderPrefix(Author author, string prefix, int? commandId)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return;

            var processStopwatch = Stopwatch.StartNew();
            var iterations = 0;
            var totalDiscovered = 0;
            var totalImported = 0;
            var totalUnmappedOrIgnored = 0;

            _logger.Debug("[INGEST-AUTHOR-READY] Processing queued items for author '{0}'", author.Name);
            _logger.Debug("[INGEST-AUTHOR-READY] Prefix='{0}'", prefix);
            LogMemorySnapshot("[INGEST-AUTHOR-READY] prefix start for author '{0}' prefix '{1}'", author.Name, LeafName(prefix));

            // Some discovery fallbacks may enqueue an explicit FILE path as the prefix to scope processing safely.
            // In that case, unit-claiming by folder would incorrectly claim the entire folder tree (e.g., the whole root folder).
            var prefixIsFile = false;
            try
            {
                prefixIsFile = _diskProvider.FileExists(prefix);
                if (!prefixIsFile)
                {
                    var isFolder = _diskProvider.FolderExists(prefix);
                    if (!isFolder && Path.HasExtension(prefix))
                    {
                        prefixIsFile = true;
                    }
                }
            }
            catch
            {
                prefixIsFile = false;
            }

            // Command-scoped progress: only report progress for the command that triggered the scan/import.
            // Do not fall back to a global "active command" (it corrupts progress under concurrency).

            // Author folder roots for this author across media types. When files are dropped directly into an author folder
            // (instead of a per-book folder), we must not apply unit-level tags from a single representative file across the
            // entire folder, otherwise we can "steal" identity (wrong title/filename) and mis-match multiple books.
            var authorFolderRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedPrefix = string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(author.Path)) authorFolderRoots.Add(NormalizeDirectory(author.Path));
                if (!string.IsNullOrWhiteSpace(author.AudiobookPath)) authorFolderRoots.Add(NormalizeDirectory(author.AudiobookPath));
                if (!string.IsNullOrWhiteSpace(author.EbookPath)) authorFolderRoots.Add(NormalizeDirectory(author.EbookPath));
                normalizedPrefix = NormalizeDirectory(prefix) ?? string.Empty;
            }
            catch
            {
                // best-effort only
                normalizedPrefix = string.Empty;
            }

            // Clean expired negative cache entries
            var expiredTime = DateTime.UtcNow.AddMinutes(-NegativeCacheMinutes);
            var expiredKeys = _negativeCacheByUnit.Where(kvp => kvp.Value < expiredTime).Select(kvp => kvp.Key).ToList();
            foreach (var key in expiredKeys)
            {
                _negativeCacheByUnit.TryRemove(key, out _);
            }

            var idleDelayMs = 250;
            const int maxIdleDelayMs = 2000;

            // Establish a starting estimate of total book units so the progress bar has a denominator.
            // We count distinct unit keys (book folder + media type) under this author prefix.
            var initialItems = _ingestQueue.GetQueuedItemsUnderPath(prefix, 10000) ?? new List<IngestQueueItem>();
            var initialUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var qi in initialItems)
            {
                var uk = GetUnitKey(qi.Path);
                if (!string.IsNullOrWhiteSpace(uk)) initialUnitKeys.Add(uk);
            }
            var totalUnitsEstimate = initialUnitKeys.Count;
            LogMemorySnapshot("[INGEST-AUTHOR-READY] after initial queue estimate for author '{0}' prefix '{1}' ({2} items, {3} units)",
                author.Name,
                LeafName(prefix),
                initialItems.Count,
                totalUnitsEstimate);

            while (true)
            {
                iterations++;
                // Recompute expired time at each iteration start
                expiredTime = DateTime.UtcNow.AddMinutes(-NegativeCacheMinutes);

                var items = _ingestQueue.GetQueuedItemsUnderPath(prefix, 10000);
                _logger.Trace("[INGEST-AUTHOR-READY] Queued items fetched: {0} under '{1}' for author '{2}'", items?.Count ?? 0, prefix, author.Name);
                LogMemorySnapshot("[INGEST-AUTHOR-READY] after queued fetch for author '{0}' prefix '{1}' iteration {2} ({3} items)",
                    author.Name,
                    LeafName(prefix),
                    iterations,
                    items?.Count ?? 0);
                if (items == null || items.Count == 0)
                {
                    // If staging is still ongoing for this import session, keep polling so we don't miss
                    // late-staged items that arrive after the initial author-ready event.
                    if (commandId.HasValue && !ImportSessionProgressTracker.IsStagingComplete(commandId.Value))
                    {
                        await Task.Delay(idleDelayMs).ConfigureAwait(false);
                        idleDelayMs = Math.Min(idleDelayMs * 2, maxIdleDelayMs);
                        continue;
                    }

                    break;
                }

                idleDelayMs = 250;

                // Group items into units first (directory + extension) and claim per-unit to avoid per-file churn
                var discovered = new List<DiscoveredFileWithMetadata>();
                var byPath = new Dictionary<string, IngestQueueItem>(StringComparer.OrdinalIgnoreCase);
                var skippedCount = 0;
                var skippedItems = new List<IngestQueueItem>();
                var ignoredCount = 0;
                var ignoredUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var extractionFailureCount = 0;
                var completedExtractionFailurePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var extractionFailureUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void CompleteTagExtractionFailure(IngestQueueItem item)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Path) || !completedExtractionFailurePaths.Add(item.Path))
                    {
                        return;
                    }

                    _logger.Warn("[INGEST-AUTHOR-READY] {0} for '{1}'", TagExtractionResult.FailureReason, item.Path);
                    var (dispositionOutcome, dispositionReason) = StagingQueueFileDispositionHelper.EnsureVisibleOrIgnored(
                        item.Path,
                        SafeDeserializeTags(item.TagsJson),
                        item.DurationSeconds,
                        _mediaFileService,
                        _diskProvider,
                        _rootFolderService.GetBestRootFolder,
                        _logger,
                        "[INGEST-AUTHOR-READY]");
                    var finalOutcome = dispositionOutcome == ImportOutcome.Unmapped
                        ? ImportOutcome.Failed
                        : dispositionOutcome;
                    var finalReason = finalOutcome == ImportOutcome.Failed
                        ? TagExtractionResult.FailureReason
                        : dispositionReason;

                    _ingestQueue.CompleteItemWithResult(
                        item.Id,
                        item.Path,
                        finalOutcome,
                        authorId: author.Id,
                        errorMessage: finalReason,
                        statusError: finalReason);
                    if (finalOutcome == ImportOutcome.Failed)
                    {
                        extractionFailureCount++;
                        var unitKey = GetUnitKey(item.Path);
                        if (!string.IsNullOrWhiteSpace(unitKey))
                        {
                            extractionFailureUnitKeys.Add(unitKey);
                        }
                    }
                }

                var audioExtensions = MediaFileExtensions.AudioExtensions;
                var textExtensions = MediaFileExtensions.TextExtensions;

                var groups = items.GroupBy(i => GetUnitKey(i.Path) ?? string.Empty)
                                   .ToList();
                LogMemorySnapshot("[INGEST-AUTHOR-READY] after grouping for author '{0}' prefix '{1}' iteration {2} ({3} items, {4} groups)",
                    author.Name,
                    LeafName(prefix),
                    iterations,
                    items.Count,
                    groups.Count);
                foreach (var g in groups)
                {
                    var any = g.First();
                    var unitKey = g.Key;
                    var cacheKey = $"{author.Id}|{unitKey}";

                    // Skip entire unit if negative-cached
                    if (_negativeCacheByUnit.TryGetValue(cacheKey, out var cachedTime) && cachedTime > expiredTime)
                    {
                        foreach (var gi in g)
                        {
                            _logger.Debug("[READY-SKIP-CACHED] file='{0}' unitKey='{1}' cached until {2}", gi.Path, unitKey, cachedTime.AddMinutes(NegativeCacheMinutes));
                            skippedCount++;
                            skippedItems.Add(gi);
                        }
                        continue;
                    }

                    // Claim the full unit by folder
                    string folder;
                    try { folder = System.IO.Path.GetDirectoryName(any.Path) ?? string.Empty; }
                    catch { folder = string.Empty; }
                    List<IngestQueueItem> claimedSet;
                    if (prefixIsFile)
                    {
                        // Claim only the specific queued items we are processing (avoid folder recursion).
                        claimedSet = new List<IngestQueueItem>();
                        foreach (var gi in g)
                        {
                            if (_ingestQueue.TryClaimItem(gi.Id, out var claimedItem) && claimedItem != null)
                            {
                                claimedSet.Add(claimedItem);
                            }
                        }
                    }
                    else
                    {
                        claimedSet = _ingestQueue.TryClaimUnit(folder) ?? new List<IngestQueueItem>();
                    }
                    if (claimedSet.Count == 0)
                    {
                        // Nothing to do (claimed elsewhere)
                        continue;
                    }

                    // Validate that the unit still belongs to a configured root folder. Media-type mismatches are kept
                    // visible as unmapped rows so users can match them in place and organize them into the right root later.
                    var unitRootFolder = _rootFolderService.GetBestRootFolder(folder);
                    if (unitRootFolder == null)
                    {
                        _logger.Debug("[READY-IGNORE-UNIT] folder='{0}' unitKey='{1}' files={2} authorId={3} reason={4}",
                            LeafName(folder), LeafUnitKey(unitKey), claimedSet.Count, author.Id, "NO_ROOT_FOLDER");

                        ignoredUnitKeys.Add(unitKey);

                        foreach (var claimed in claimedSet)
                        {
                            try
                            {
                                _ingestQueue.CompleteItemWithResult(claimed.Id, claimed.Path, ImportOutcome.Ignored, authorId: author.Id, errorMessage: "NO_ROOT_FOLDER", statusError: "NO_ROOT_FOLDER");
                                ignoredCount++;
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "[READY-IGNORE-UNIT] Failed marking ignored item done: {0}", LeafName(claimed.Path));
                            }
                        }

                        // Mark ignored units as processed so the global progress can complete.
                        try
                        {
                            if (commandId.HasValue)
                            {
                                ImportSessionProgressTracker.Activate(commandId.Value);
                                ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { unitKey });
                            }
                        }
                        catch
                        {
                            // best-effort progress only
                        }

                        continue;
                    }

                    if (!IsFileAllowedForRootFolderType(any.Path, unitRootFolder, audioExtensions, textExtensions))
                    {
                        var reason = $"ROOT_FOLDER_TYPE_{unitRootFolder.FolderType}";

                        _logger.Debug("[READY-UNMAPPED-UNIT] folder='{0}' unitKey='{1}' files={2} authorId={3} reason={4}",
                            LeafName(folder), LeafUnitKey(unitKey), claimedSet.Count, author.Id, reason);

                        ignoredUnitKeys.Add(unitKey);

                        foreach (var claimed in claimedSet)
                        {
                            try
                            {
                                var (outcome, dispositionReason) = StagingQueueFileDispositionHelper.EnsureVisibleOrIgnored(
                                    claimed.Path,
                                    SafeDeserializeTags(claimed.TagsJson),
                                    claimed.DurationSeconds,
                                    _mediaFileService,
                                    _diskProvider,
                                    _rootFolderService.GetBestRootFolder,
                                    _logger,
                                    "[INGEST-AUTHOR-READY]");

                                var finalReason = outcome == ImportOutcome.Unmapped ? reason : dispositionReason;
                                _ingestQueue.CompleteItemWithResult(claimed.Id, claimed.Path, outcome, authorId: author.Id, errorMessage: finalReason, statusError: finalReason);
                                ignoredCount++;
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "[READY-UNMAPPED-UNIT] Failed marking item done: {0}", LeafName(claimed.Path));
                            }
                        }

                        // Mark mismatched units as processed so the global progress can complete.
                        try
                        {
                            if (commandId.HasValue)
                            {
                                ImportSessionProgressTracker.Activate(commandId.Value);
                                ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { unitKey });
                            }
                        }
                        catch
                        {
                            // best-effort progress only
                        }

                        continue;
                    }

                    _logger.Debug("[READY-TRY-UNIT] folder='{0}' unitKey='{1}' files={2} authorId={3}", LeafName(folder), LeafUnitKey(unitKey), claimedSet.Count, author.Id);

                    // Ensure we have usable tags for matching even when staging was tag-light.
                    // BuildUnitTagsByKey returns either:
                    // - unit-level tags (folder+extension) for likely-homogeneous multi-part releases (fast path), or
                    // - per-file tags for small/multi-book flat folders (correctness path).
                    var extractionFailedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var unitTagsByKey = BuildUnitTagsByKey(claimedSet, extractionFailedPaths);
                    foreach (var claimed in claimedSet)
                    {
                        if (extractionFailedPaths.Contains(claimed.Path))
                        {
                            CompleteTagExtractionFailure(claimed);
                            continue;
                        }

                        var tags = SafeDeserializeTags(claimed.TagsJson);
                        int? durationSecondsOverride = null;
                        var ext = string.Empty;
                        try { ext = Path.GetExtension(claimed.Path) ?? string.Empty; } catch { }
                        var isAudioFile = MediaFileExtensions.AudioExtensions.Contains(ext);

                        // If this file is directly under the author's root folder, do NOT use unit tags from a single
                        // representative. Always read per-file tags to avoid cross-book contamination and to avoid
                        // "duration-only" tagsets blocking downstream matching.
                        var claimedDir = string.Empty;
                        try { claimedDir = NormalizeDirectory(Path.GetDirectoryName(claimed.Path) ?? string.Empty); } catch { }
                        if (!string.IsNullOrWhiteSpace(claimedDir) &&
                            (authorFolderRoots.Contains(claimedDir) ||
                             (!prefixIsFile && !string.IsNullOrWhiteSpace(normalizedPrefix) && claimedDir.PathEquals(normalizedPrefix))))
                        {
                            // Only hit disk when staging metadata is missing. In the common case, DiscoveryWorker has already
                            // populated per-file tags_json + duration_seconds and we should not re-open the file.
                            var stagingHasTags = tags != null && tags.Count > 0;
                            var stagingHasDuration = MediaDuration.HasDuration(claimed.DurationSeconds);
                            if (!stagingHasTags || (isAudioFile && !stagingHasDuration))
                            {
                                var (perFile, perFileDurationSeconds, extractionFailed) = TryReadTagsFromDisk(claimed.Path);
                                if (extractionFailed)
                                {
                                    CompleteTagExtractionFailure(claimed);
                                    continue;
                                }

                                if (perFile != null && perFile.Count > 0)
                                {
                                    tags = perFile;
                                    durationSecondsOverride = perFileDurationSeconds;
                                }
                            }
                        }
                        else if (tags == null || tags.Count == 0)
                        {
                            // Prefer per-file tags when BuildUnitTagsByKey stored them (e.g. multi-.m4b folders),
                            // otherwise fall back to unit-level tags.
                            if (!string.IsNullOrWhiteSpace(claimed.Path) &&
                                unitTagsByKey.TryGetValue(claimed.Path, out var perFileTags) &&
                                perFileTags != null &&
                                perFileTags.Count > 0)
                            {
                                tags = perFileTags;
                            }
                            else if (unitTagsByKey.TryGetValue(GetUnitKey(claimed.Path) ?? string.Empty, out var unitTags) &&
                                     unitTags != null &&
                                     unitTags.Count > 0)
                            {
                                tags = unitTags;
                            }

                            if (tags == null || tags.Count == 0)
                            {
                                var (perFile, perFileDurationSeconds, extractionFailed) = TryReadTagsFromDisk(claimed.Path);
                                if (extractionFailed)
                                {
                                    CompleteTagExtractionFailure(claimed);
                                    continue;
                                }

                                if (perFile != null && perFile.Count > 0)
                                {
                                    tags = perFile;
                                    durationSecondsOverride = perFileDurationSeconds;
                                }
                            }
                        }

                        int? discoveredDurationSeconds = null;
                        if (isAudioFile)
                        {
                            discoveredDurationSeconds = durationSecondsOverride ?? claimed.DurationSeconds;
                            if (!discoveredDurationSeconds.HasValue || discoveredDurationSeconds.Value <= 0)
                            {
                                discoveredDurationSeconds = GetDurationSeconds(claimed.Path);
                            }
                        }

                        discovered.Add(new DiscoveredFileWithMetadata
                        {
                            Path = claimed.Path,
                            Size = claimed.SizeBytes,
                            Modified = GetQueuedFileModifiedUtc(claimed),
                            AllTags = tags,
                            DurationSeconds = discoveredDurationSeconds
                        });
                        byPath[claimed.Path] = claimed;
                    }
                }
                totalDiscovered += discovered.Count;
                LogMemorySnapshot("[INGEST-AUTHOR-READY] after discovery build for author '{0}' prefix '{1}' iteration {2} ({3} discovered, {4} claimed, {5} skipped, {6} ignored)",
                    author.Name,
                    LeafName(prefix),
                    iterations,
                    discovered.Count,
                    byPath.Count,
                    skippedCount,
                    ignoredCount);

                if (discovered.Count == 0)
                {
                    if (ignoredCount > 0)
                    {
                        _logger.Debug("[INGEST-AUTHOR-READY] Completed {0} out-of-scope files for author '{1}'", ignoredCount, author.Name);
                        _logger.Debug("[INGEST-AUTHOR-READY] Prefix='{0}'", prefix);
                    }
                    totalUnmappedOrIgnored += ignoredCount + extractionFailureCount;

                    try
                    {
                        if (commandId.HasValue && extractionFailureUnitKeys.Count > 0)
                        {
                            ImportSessionProgressTracker.Activate(commandId.Value);
                            ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, extractionFailureUnitKeys);
                        }
                    }
                    catch
                    {
                        // best-effort progress only
                    }

                    if (skippedCount > 0)
                    {
                        _logger.Debug("[INGEST-AUTHOR-READY] Skipped {0} cached negative results for author '{1}' (left queued for drain recovery)", skippedCount, author.Name);
                        _logger.Debug("[INGEST-AUTHOR-READY] Prefix='{0}'", prefix);
                        var skippedUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var si in skippedItems)
                        {
                            try
                            {
                                var unitKey = GetUnitKey(si.Path) ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(unitKey)) skippedUnitKeys.Add(unitKey);
                            }
                            catch (Exception umEx)
                            {
                                _logger.Warn(umEx, "[INGEST-AUTHOR-READY] Failed to record skipped cached item: {0}", si.Path);
                            }
                        }

                        // Count skipped-negative-cache units as processed so the global progress can complete.
                        try
                        {
                            if (commandId.HasValue && skippedUnitKeys.Count > 0)
                            {
                                ImportSessionProgressTracker.Activate(commandId.Value);
                                ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, skippedUnitKeys);
                            }
                        }
                        catch
                        {
                            // best-effort progress only
                        }
                    }

                    // If we couldn't claim anything but staging is still running, wait and retry.
                    if (commandId.HasValue && !ImportSessionProgressTracker.IsStagingComplete(commandId.Value))
                    {
                        await Task.Delay(idleDelayMs).ConfigureAwait(false);
                        idleDelayMs = Math.Min(idleDelayMs * 2, maxIdleDelayMs);
                        continue;
                    }

                    break; // Nothing to process
                }

                try
                {
                    var filesForMatching = discovered.ToArray();
                    LogMemorySnapshot("[INGEST-AUTHOR-READY] before matching for author '{0}' prefix '{1}' iteration {2} ({3} files)",
                        author.Name,
                        LeafName(prefix),
                        iterations,
                        filesForMatching.Length);
                    var result = await _fileMatching.MatchFilesToLibraryAsync(filesForMatching, author.Id, CreateAuthorReadyMatchingContext());
                    filesForMatching = null;
                    _logger.Debug("[INGEST-AUTHOR-READY] Matching finished for authorId={0}: matched={1}, unmatched={2}", author.Id, result.MatchedFiles?.Length ?? 0, result.UnmatchedFiles?.Length ?? 0);
                    LogMemorySnapshot("[INGEST-AUTHOR-READY] after matching for author '{0}' prefix '{1}' iteration {2} (matched={3}, unmatched={4})",
                        author.Name,
                        LeafName(prefix),
                        iterations,
                        result.MatchedFiles?.Length ?? 0,
                        result.UnmatchedFiles?.Length ?? 0);

                    // Group matched files by (EditionId, UnitKey) where UnitKey = parent folder + extension
                    var matchedByUnit = result.MatchedFiles
                        .GroupBy(m => new { m.AuthorId, m.EditionId, UnitKey = GetUnitKey(m.File.Path) })
                        .ToList();
                    LogMemorySnapshot("[INGEST-AUTHOR-READY] after matched grouping for author '{0}' prefix '{1}' iteration {2} ({3} matched units)",
                        author.Name,
                        LeafName(prefix),
                        iterations,
                        matchedByUnit.Count);

                    var importedCount = 0;
                    var applyFailureCount = 0;
                    var importedUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var unmatchedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var discoveredByPath = discovered.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);

                    DiscoveredFileWithMetadata BuildApplyFile(
                        string path,
                        Dictionary<string, List<string>> tags,
                        int? durationSeconds)
                    {
                        discoveredByPath.TryGetValue(path, out var observed);
                        byPath.TryGetValue(path, out var queued);

                        return new DiscoveredFileWithMetadata
                        {
                            Path = path,
                            Size = observed?.Size ?? queued?.SizeBytes ?? 0,
                            Modified = observed?.Modified ?? GetQueuedFileModifiedUtc(queued),
                            Quality = observed?.Quality,
                            AllTags = tags ?? observed?.AllTags,
                            DurationSeconds = durationSeconds ?? observed?.DurationSeconds
                        };
                    }

                    foreach (var um in result.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                    {
                        if (um?.File?.Path != null)
                        {
                            unmatchedByPath[um.File.Path] = um.Reason ?? "UNKNOWN";
                        }
                    }
                    foreach (var unit in matchedByUnit)
                    {
                        var list = new List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)>();
                        foreach (var m in unit)
                        {
                            matchedPaths.Add(m.File.Path);
                            _logger.Debug("[READY-DECISION] MATCHED file='{0}' authorId={1} editionId={2} unitKey='{3}'", LeafName(m.File.Path), unit.Key.AuthorId, unit.Key.EditionId, LeafUnitKey(unit.Key.UnitKey));
                            if (byPath.TryGetValue(m.File.Path, out var _))
                            {
                                list.Add((m.File.Path, m.File.AllTags ?? new Dictionary<string, List<string>>(), m.File.DurationSeconds, m.Provenance));
                            }
                        }
                        if (list.Count == 0) continue;
                        // Determine destination Book/Edition per unit key (clone if needed)
                        var sampleMatch = unit.First();
                        var matchedAuthorId = sampleMatch.AuthorId;
                        var canonicalEdition = _editionService.GetEdition(sampleMatch.EditionId);
                        var canonicalBook = _bookService.GetBook(canonicalEdition.BookId);

                        // Special case: multiple matched files directly under the AUTHOR ROOT folder.
                        // These may represent either:
                        //  - a multipart copy (tracks/parts) that should be imported together, OR
                        //  - multiple full copies of the same edition that should be cloned, OR
                        //  - an ambiguous case we must fail-closed on.
                        //
                        // User requirement: ONLY merge multipart when the summed file duration ~= edition duration.
                        var unitDir = NormalizeDirectory(Path.GetDirectoryName(sampleMatch.File.Path) ?? string.Empty) ?? string.Empty;
                        var isUnderAuthorRoot = authorFolderRoots != null &&
                                               !string.IsNullOrWhiteSpace(unitDir) &&
                                               authorFolderRoots.Contains(unitDir);

                        if (isUnderAuthorRoot &&
                            canonicalBook.MediaType == BookMediaType.Audiobook &&
                            list.Count > 1)
                        {
                            var perFileDurations = list.Select(x => x.DurationSeconds).ToList();
                            var decision = DecideAuthorRootDurationAction(
                                canonicalEdition.DurationSeconds,
                                perFileDurations,
                                out var tol,
                                out var sumSeconds,
                                out var reason);

                            if (decision == AuthorRootDurationDecision.MergeMultipart)
                            {
                                _logger.Debug("[AUTHOR-ROOT] MULTIPART_MERGE durationSum={0}s editionDuration={1}s tol={2}s editionId={3}",
                                    sumSeconds, canonicalEdition.DurationSeconds, tol, canonicalEdition.Id);
                                // Fall through: import together as one multipart unit.
                            }
                            else if (decision == AuthorRootDurationDecision.SplitDuplicates)
                            {
                                _logger.Debug("[AUTHOR-ROOT] DUPLICATE_FULL_COPIES editionDuration={0}s tol={1}s editionId={2} copies={3}",
                                    canonicalEdition.DurationSeconds, tol, canonicalEdition.Id, list.Count);

                                // Import each file as its own unit (stable per-path key) so duplicates become clones.
                                var splitUnitAppliedAny = false;
                                foreach (var (p, tags, durationSeconds, provenance) in list)
                                {
                                    var perFileKey = BuildPerFileUnitKey(p, canonicalEdition.Title, canonicalBook.MediaType);
                                    var fileDest = _unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, perFileKey);

                                    var splitApplyResults = await _bookImport.ImportFilesAsync(
                                        new List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)>
                                        {
                                            (BuildApplyFile(p, tags, durationSeconds), fileDest.EditionId, provenance)
                                        },
                                        fileDest.BookId);

                                    if (byPath.TryGetValue(p, out var itemRef))
                                    {
                                        matchedPaths.Add(p);
                                        var applyResult = CompleteApplyResult(
                                            splitApplyResults,
                                            p,
                                            itemRef,
                                            fileDest.BookId,
                                            matchedAuthorId,
                                            ref importedCount,
                                            ref applyFailureCount);
                                        splitUnitAppliedAny |= applyResult.IsApplied;
                                    }
                                }

                                if (splitUnitAppliedAny && !string.IsNullOrWhiteSpace(unit.Key.UnitKey))
                                {
                                    importedUnitKeys.Add(unit.Key.UnitKey);
                                }

                                continue;
                            }
                            else
                            {
                                var errorCode = (reason ?? "AUTHOR_ROOT_AMBIGUOUS_MULTIPART_UNKNOWN").StartsWith("AUTHOR_ROOT_", StringComparison.OrdinalIgnoreCase)
                                    ? reason
                                    : $"AUTHOR_ROOT_AMBIGUOUS_MULTIPART_{reason ?? "UNKNOWN"}";

                                _logger.Debug("[AUTHOR-ROOT] {0}: leaving {1} files unmapped (editionId={2}, editionDuration={3}s, durationSum={4}s, tol={5}s)",
                                    errorCode,
                                    list.Count,
                                    canonicalEdition.Id,
                                    canonicalEdition.DurationSeconds.GetValueOrDefault(),
                                    sumSeconds,
                                    tol);

                                foreach (var (p, _, _, _) in list)
                                {
                                    if (byPath.TryGetValue(p, out var itemRef))
                                    {
                                        matchedPaths.Add(p);
                                        MarkUnmapped(p);
                                        _ingestQueue.CompleteItemWithResult(
                                            itemRef.Id,
                                            p,
                                            ImportOutcome.Unmapped,
                                            authorId: matchedAuthorId,
                                            errorMessage: errorCode,
                                            statusError: errorCode);
                                    }
                                }

                                continue;
                            }
                        }

                        // Attempt to coalesce sibling disc folders under the detected book root and
                        // expand the import list when safe to do so.
                        var originalCount = list.Count;
                        try
                        {
                            var co = BookCoalescingHelper.Coalesce(author,
                                prefix,
                                sampleMatch.File.Path,
                                canonicalEdition,
                                byPath,
                                _ingestQueue,
                                canonicalBook.MediaType,
                                _logger);
                            if (co.Coalesced && co.ExtraFiles != null && co.ExtraFiles.Count > 0)
                            {
                                foreach (var e in co.ExtraFiles)
                                {
                                    if (!matchedPaths.Contains(e.Path))
                                    {
                                        var extraDurationSeconds = discoveredByPath.TryGetValue(e.Path, out var extraMeta) ? extraMeta.DurationSeconds : null;
                                        list.Add((e.Path, e.Tags, extraDurationSeconds, sampleMatch.Provenance));
                                        matchedPaths.Add(e.Path);
                                    }
                                }
                                _logger.Debug("[BOOK-COALESCE] Import expanded from {0} to {1} files (editionId={2})", originalCount, list.Count, canonicalEdition.Id);
                            }

                            // Use helper-provided root-level unit key
                            var coalescedDestKey = _unitDestination.BuildRootUnitKeyWithExtension(sampleMatch.File.Path, canonicalEdition.Title, canonicalBook.MediaType);
                            var coalescedDest = _unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, coalescedDestKey);

                            var coalescedApplyResults = await _bookImport.ImportFilesAsync(
                                list.Select(file => (BuildApplyFile(file.Path, file.Tags, file.DurationSeconds), (int?)coalescedDest.EditionId, file.Provenance)).ToList(),
                                coalescedDest.BookId);

                            // Record results and update statuses
                            var unitAppliedAny = false;
                            foreach (var (p, _, _, _) in list)
                            {
                                if (byPath.TryGetValue(p, out var itemRef))
                                {
                                    var applyResult = CompleteApplyResult(
                                        coalescedApplyResults,
                                        p,
                                        itemRef,
                                        coalescedDest.BookId,
                                        matchedAuthorId,
                                        ref importedCount,
                                        ref applyFailureCount);
                                    unitAppliedAny |= applyResult.IsApplied;
                                }
                            }

                            if (unitAppliedAny && !string.IsNullOrWhiteSpace(unit.Key.UnitKey))
                            {
                                importedUnitKeys.Add(unit.Key.UnitKey);
                            }

                            continue; // handled this unit
                        }
                        catch (Exception cx)
                        {
                            _logger.Debug(cx, "[BOOK-COALESCE] Skipped due to error");
                        }

                        // Use a root-level unit key (book root + media type + extension) to avoid per-disc clones
                        // while ensuring different media containers (m4b vs mp3) never collapse into the same book.
                        var destKey = _unitDestination.BuildRootUnitKeyWithExtension(sampleMatch.File.Path, canonicalEdition.Title, canonicalBook.MediaType);
                        var dest = _unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, destKey);

                        var unitApplyResults = await _bookImport.ImportFilesAsync(
                            list.Select(file => (BuildApplyFile(file.Path, file.Tags, file.DurationSeconds), (int?)dest.EditionId, file.Provenance)).ToList(),
                            dest.BookId);

                        // Record results and update statuses
                        var unitApplied = false;
                        foreach (var (p, _, _, _) in list)
                        {
                            if (byPath.TryGetValue(p, out var itemRef))
                            {
                                var applyResult = CompleteApplyResult(
                                    unitApplyResults,
                                    p,
                                    itemRef,
                                    dest.BookId,
                                    matchedAuthorId,
                                    ref importedCount,
                                    ref applyFailureCount);
                                unitApplied |= applyResult.IsApplied;
                            }
                        }

                        if (unitApplied && !string.IsNullOrWhiteSpace(unit.Key.UnitKey))
                        {
                            importedUnitKeys.Add(unit.Key.UnitKey);
                        }
                    }

                    // Group unmatched files by unit key to handle siblings properly
                    var unmatchedUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var um in result.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                    {
                        if (um?.File?.Path != null)
                        {
                            var uk = GetUnitKey(um.File.Path);
                            if (!string.IsNullOrWhiteSpace(uk))
                            {
                                unmatchedUnits.Add(uk);
                            }
                        }
                    }

                    // Process all files, marking entire units as unmapped when any file in unit is unmatched
                    var processedCount = applyFailureCount + extractionFailureCount;
                    foreach (var kvp in byPath)
                    {
                        var path = kvp.Key;
                        var itemRef = kvp.Value;
                        if (matchedPaths.Contains(path)) continue;

                        var unitKey = GetUnitKey(path) ?? string.Empty;

                        // If ANY file in this unit was marked unmatched, mark ALL files in the unit as unmapped
                        if (unmatchedUnits.Contains(unitKey))
                        {
                            var reason = unmatchedByPath.TryGetValue(path, out var r) ? r : "Unit sibling was unmatched";
                            _logger.Debug("[READY-DECISION] UNMAPPED file='{0}' unitKey='{1}' reason='{2}'", LeafName(path), LeafUnitKey(unitKey), reason);
                            var tags = discoveredByPath.TryGetValue(path, out var df) ? df.AllTags : null;
                            var authorInTags = tags != null &&
                                               tags.Count > 0 &&
                                               _containmentValidator != null &&
                                               _containmentValidator.ValidateAuthorInTags(author.Name, tags);

                            if (!authorInTags)
                            {
                                var err = "AUTHOR_NOT_IN_TAGS";
                                _logger.Debug("[READY-DECISION] UNMAPPED file='{0}' unitKey='{1}' reason='{2}'", LeafName(path), LeafUnitKey(unitKey), err);
                                var (outcome, dispositionReason) = StagingQueueFileDispositionHelper.EnsureVisibleOrIgnored(
                                    path,
                                    tags,
                                    discoveredByPath.TryGetValue(path, out var authorTagsMeta) ? authorTagsMeta.DurationSeconds : itemRef.DurationSeconds,
                                    _mediaFileService,
                                    _diskProvider,
                                    _rootFolderService.GetBestRootFolder,
                                    _logger,
                                    "[INGEST-AUTHOR-READY]");
                                var finalReason = outcome == ImportOutcome.Unmapped ? err : dispositionReason;
                                _ingestQueue.CompleteItemWithResult(itemRef.Id, path, outcome, authorId: author.Id, errorMessage: finalReason, statusError: finalReason);
                                processedCount++;
                                continue;
                            }

                            MarkUnmapped(path);
                            var unmappedReason = $"No matching edition for author '{author.Name}' - {reason}";
                            _ingestQueue.CompleteItemWithResult(itemRef.Id, path, ImportOutcome.Unmapped, authorId: author.Id, errorMessage: unmappedReason, statusError: unmappedReason);
                            processedCount++;
                        }
                            else
                            {
                                // Defer: return to queued for the next pass instead of prematurely flagging as unmapped.
                                // IMPORTANT: do not leave the item in 'in_progress' or it can get stuck indefinitely.
                                var err = "NOT_ATTEMPTED_IN_BATCH";
                                _logger.Debug("[READY-DEFERRED] file='{0}' unitKey='{1}' reason='{2}'",
                                    path, string.IsNullOrEmpty(unitKey) ? "<none>" : unitKey, err);
                                _ingestQueue.UpdateStatus(itemRef.Id, "queued", err);
                            }
                        }

                    _logger.Debug("[INGEST-AUTHOR-READY] Imported {0}, unmapped {1} for author '{2}'", importedCount, processedCount, author.Name);
                    _logger.Debug("[INGEST-AUTHOR-READY] Prefix='{0}'", prefix);
                    totalImported += importedCount;
                    totalUnmappedOrIgnored += processedCount;
                    LogMemorySnapshot("[INGEST-AUTHOR-READY] after batch decisions for author '{0}' prefix '{1}' iteration {2} (imported={3}, processed={4}, matchedUnits={5})",
                        author.Name,
                        LeafName(prefix),
                        iterations,
                        importedCount,
                        processedCount,
                        matchedByUnit.Count);

                    // Update GLOBAL book-unit progress (cumulative across the entire scan), not per-author.
                    // This keeps the header chip stable: processed/total should not reset on each author.
                    try
                    {
                        if (commandId.HasValue)
                        {
                            ImportSessionProgressTracker.Activate(commandId.Value);

                            var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var g in matchedByUnit)
                            {
                                var k = g?.Key?.UnitKey;
                                if (!string.IsNullOrWhiteSpace(k)) processedKeys.Add(k);
                            }
                            foreach (var k in unmatchedUnits) { if (!string.IsNullOrWhiteSpace(k)) processedKeys.Add(k); }
                            foreach (var k in extractionFailureUnitKeys) { processedKeys.Add(k); }

                            var (processedUnits, totalUnits) = ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, processedKeys);
                            ImportSessionProgressTracker.MarkBookUnitsImported(commandId.Value, importedUnitKeys);
                            ImportSessionProgressTracker.AddFilesImported(commandId.Value, importedCount);
                            var (authorsImportedTotal, bookUnitsImportedTotal, filesImportedTotal) = ImportSessionProgressTracker.GetImportedCounts(commandId.Value);
                            var (processedAuthors, totalAuthors, matchedAuthors, unmatchedAuthors) = ImportSessionProgressTracker.GetAuthorFolderOutcomeProgress(commandId.Value);

                            var totalUnitsForBar = totalUnits > 0 ? totalUnits : Math.Max(totalUnitsEstimate, processedUnits);

                            var progEvt = new ImportStageProgressEvent(ImportStage.MatchingBooks,
                                $"Processing '{author.Name}': {processedUnits} of {totalUnitsForBar} book units",
                                currentProgress: processedUnits,
                                totalProgress: totalUnitsForBar)
                            {
                                CommandId = commandId.Value,
                                TotalAuthorFolders = totalAuthors,
                                ProcessedAuthorFolders = processedAuthors,
                                MatchedAuthors = matchedAuthors,
                                UnmatchedAuthors = unmatchedAuthors,
                                TotalBookFolders = totalUnitsForBar,
                                ProcessedBookFolders = processedUnits,
                                AuthorsImported = authorsImportedTotal,
                                MatchedBooks = bookUnitsImportedTotal,
                                FilesImported = filesImportedTotal,
                                CurrentItemName = author.Name,
                                CurrentItemType = "author",
                                MatchedAuthorId = author.Id
                            };

                            _eventAggregator.PublishEvent(progEvt);
                        }
                    }
                    catch
                    {
                        // best-effort progress only
                    }

                    // Break out of loop if no progress was made
                    if (importedCount == 0 && processedCount == 0)
                    {
                        // If staging is still ongoing, release the claims and keep polling for late-staged items.
                        if (commandId.HasValue && !ImportSessionProgressTracker.IsStagingComplete(commandId.Value))
                        {
                            foreach (var itemRef in byPath.Values)
                            {
                                try
                                {
                                    _ingestQueue.UpdateStatus(itemRef.Id, "queued");
                                }
                                catch
                                {
                                    // best-effort only
                                }
                            }

                            await Task.Delay(idleDelayMs).ConfigureAwait(false);
                            idleDelayMs = Math.Min(idleDelayMs * 2, maxIdleDelayMs);
                            continue;
                            }

                            // Ensure we don't leave claimed items stuck in 'in_progress' when breaking.
                            try
                            {
                                var ids = byPath?.Values?.Select(v => v.Id).ToList();
                                if (ids != null && ids.Count > 0)
                                {
                                    _ingestQueue.RequeueInProgress(ids, "AUTHOR_READY_NO_PROGRESS");
                                }
                            }
                            catch
                            {
                                // best-effort only
                            }

                            _logger.Debug("[INGEST-AUTHOR-READY] No progress made (0 imported, 0 processed), breaking loop for author '{0}'", author.Name);
                            break;
                        }
                }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[INGEST-AUTHOR-READY] Failed batch for author '{0}'", author.Name);
                        // Never leave items stuck in 'in_progress' due to an exception; re-queue so the next
                        // author-ready event (or drain) can retry safely.
                        try
                        {
                            var ids = byPath?.Values?.Select(v => v.Id).ToList();
                            if (ids != null && ids.Count > 0)
                            {
                                _ingestQueue.RequeueInProgress(ids, "AUTHOR_READY_BATCH_FAILED");
                            }
                        }
                        catch
                        {
                            // best-effort only
                        }
                    }
            }

            try
            {
                LogMemorySnapshot("[INGEST-AUTHOR-READY] before final active-leftover fetch for author '{0}' prefix '{1}'", author.Name, LeafName(prefix));
                var activeLeftover = _ingestQueue.GetActiveItemsForSweepUnderPath(prefix, 10000, 0) ?? new List<IngestQueueItem>();
                LogMemorySnapshot("[INGEST-AUTHOR-READY] after final active-leftover fetch for author '{0}' prefix '{1}' ({2} items)", author.Name, LeafName(prefix), activeLeftover.Count);
                if (activeLeftover.Count > 0)
                {
                    var leftoverUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in activeLeftover)
                    {
                        var unitKey = GetUnitKey(item.Path) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(unitKey))
                        {
                            leftoverUnitKeys.Add(unitKey);
                        }
                    }
                    LogMemorySnapshot("[INGEST-AUTHOR-READY] after leftover unit-key build for author '{0}' prefix '{1}' ({2} keys)", author.Name, LeafName(prefix), leftoverUnitKeys.Count);

                    _stagingResidualQueueSweeper.SweepUnderPath(prefix, "[INGEST-AUTHOR-READY-FLUSH]");
                    LogMemorySnapshot("[INGEST-AUTHOR-READY] after final sweep for author '{0}' prefix '{1}'", author.Name, LeafName(prefix));

                    try
                    {
                        if (commandId.HasValue && leftoverUnitKeys.Count > 0)
                        {
                            ImportSessionProgressTracker.Activate(commandId.Value);
                            ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, leftoverUnitKeys);
                        }
                    }
                    catch
                    {
                        // best-effort progress only
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[INGEST-AUTHOR-READY] Final sweep failed for author '{0}' prefix '{1}'", author.Name, LeafName(prefix));
            }

            processStopwatch.Stop();
            LogMemorySnapshot("[INGEST-AUTHOR-READY] prefix complete for author '{0}' prefix '{1}' ({2} iterations, {3} discovered, {4} imported, {5} unmappedOrIgnored, {6})",
                author.Name,
                LeafName(prefix),
                iterations,
                totalDiscovered,
                totalImported,
                totalUnmappedOrIgnored,
                processStopwatch.Elapsed);
            CompactAfterLargeAuthorReadyPrefixIfNeeded(author.Name, prefix, totalDiscovered, processStopwatch.Elapsed);
        }

        private void CompactAfterLargeAuthorReadyPrefixIfNeeded(string authorName, string prefix, int discoveredFileCount, TimeSpan elapsed)
        {
            if (discoveredFileCount < LargeAuthorReadyCompactionFileThreshold &&
                elapsed < LargeAuthorReadyCompactionDurationThreshold)
            {
                return;
            }

            try
            {
                _logger.Debug("[MEMORY] Large author-ready prefix complete for '{0}' prefix '{1}' ({2} files, {3}); before compacting GC: {4}",
                    authorName,
                    LeafName(prefix),
                    discoveredFileCount,
                    elapsed,
                    MemorySnapshot.CaptureDetailed());

                MemorySnapshot.CollectFullCompacting();

                _logger.Debug("[MEMORY] Large author-ready prefix complete for '{0}' prefix '{1}' ({2} files, {3}); after compacting GC: {4}",
                    authorName,
                    LeafName(prefix),
                    discoveredFileCount,
                    elapsed,
                    MemorySnapshot.CaptureDetailed());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[MEMORY] Large author-ready prefix compacting GC failed for '{0}' prefix '{1}'", authorName, LeafName(prefix));
            }
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
                // Diagnostics must never affect ingest processing.
            }
        }

        private string BuildRootUnitKey(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType)
        {
            try
            {
                var root = FindBookRootFolder(anyFilePathInUnit, editionTitle);
                if (string.IsNullOrWhiteSpace(root)) root = System.IO.Path.GetDirectoryName(anyFilePathInUnit) ?? string.Empty;
                root = NormalizeDirectory(root) ?? string.Empty;
                return (root + "|" + mediaType.ToString()).ToLowerInvariant();
            }
            catch { return GetUnitKey(anyFilePathInUnit); }
        }

        private (bool Coalesced, List<(string Path, Dictionary<string, List<string>> Tags)> ExtraFiles) TryCoalesceBookLevelImport(
            Author author,
            string authorPrefix,
            string unitFolder,
            Edition matchedEdition,
            Dictionary<string, IngestQueueItem> byPath,
            BookMediaType mediaType)
        {
            var result = new List<(string Path, Dictionary<string, List<string>> Tags)>();
            try
            {
                var bookRoot = FindBookRootFolder(unitFolder, matchedEdition?.Title);
                if (string.IsNullOrWhiteSpace(bookRoot))
                {
                    _logger.Debug("[BOOK-COALESCE][SKIP] No strict root for unitFolder='{0}'", unitFolder);
                    return (false, result);
                }

                // Safety rail: ensure the root belongs to this author
                var normAuthorPrefix = NormalizeDirectory(authorPrefix) ?? string.Empty;
                var normRoot = NormalizeDirectory(bookRoot) ?? string.Empty;
                if (!normRoot.StartsWith(normAuthorPrefix + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normRoot, normAuthorPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Debug("[BOOK-COALESCE][SKIP] Root '{0}' not under author prefix '{1}'", normRoot, normAuthorPrefix);
                    return (false, result);
                }

                _logger.Debug("[BOOK-COALESCE][ROOT] unit='{0}' root='{1}' title='{2}'", unitFolder, normRoot, matchedEdition?.Title);

                // Build sibling folder set (immediate children under root) from queued items in memory
                var siblingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in byPath)
                {
                    var path = kv.Key;
                    if (!path.StartsWith(normRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var parent = System.IO.Path.GetDirectoryName(dir) ?? string.Empty;
                    if (string.Equals(NormalizeDirectory(parent), normRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        siblingFolders.Add(NormalizeDirectory(dir));
                    }
                }

                // If no subfolders detected, treat the root as a single-folder album (nothing to coalesce)
                if (siblingFolders.Count <= 1)
                {
                    return (false, result);
                }

                try
                {
                    var sampleNames = siblingFolders.Select(p => { try { return new System.IO.DirectoryInfo(p).Name; } catch { return p; } }).Take(5);
                    _logger.Debug("[BOOK-COALESCE][SIBLINGS] root='{0}' count={1} samples=[{2}]", normRoot, siblingFolders.Count, string.Join(", ", sampleNames));
                }
                catch { }

                if (!LooksLikeEnumeratedSiblings(siblingFolders))
                {
                    _logger.Debug("[BOOK-COALESCE][SKIP] Siblings not enumerated discs under root '{0}'", normRoot);
                    return (false, result);
                }

                // Claim all sibling units to avoid races; gather their queued items.
                // Prefer already-claimed items present in byPath; only call TryClaimUnit for folders not present.
                var claimedItems = new List<IngestQueueItem>();
                var newlyClaimedIds = new List<int>();
                try
                {
                    foreach (var folder in siblingFolders)
                    {
                        // Collect existing claimed items for this folder from in-memory byPath
                        var existing = byPath.Values.Where(v =>
                        {
                            try { return string.Equals(NormalizeDirectory(System.IO.Path.GetDirectoryName(v.Path) ?? string.Empty), folder, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        }).ToList();

                        if (existing.Any())
                        {
                            claimedItems.AddRange(existing);
                            _logger.Debug("[BOOK-COALESCE][CLAIM] Using existing byPath items for '{0}' count={1}", folder, existing.Count);
                            continue;
                        }

                        // Try to claim any remaining sibling units that weren't already in byPath
                        var claimed = _ingestQueue.TryClaimUnit(folder) ?? new List<IngestQueueItem>();
                        if (claimed.Count == 0)
                        {
                            _logger.Debug("[BOOK-COALESCE] Sibling '{0}' already claimed/processed elsewhere; skipping", folder);
                            continue;
                        }
                        claimedItems.AddRange(claimed);
                        foreach (var ci in claimed)
                        {
                            byPath[ci.Path] = ci; // track in-memory for later status updates
                            newlyClaimedIds.Add(ci.Id);
                        }
                        _logger.Debug("[BOOK-COALESCE][CLAIM] Claimed '{0}' items={1}", folder, claimed.Count);
                    }
                    _logger.Debug("[BOOK-COALESCE][CLAIMS] siblings={0} totalItems={1} newlyClaimed={2}", siblingFolders.Count, claimedItems.Count, newlyClaimedIds.Count);
                }
                catch
                {
                    // Roll back any new claims we acquired in this method
                    foreach (var id in newlyClaimedIds)
                    {
                        try { _ingestQueue.UpdateStatus(id, "queued"); } catch { }
                    }
                    return (false, result);
                }

                // Filter to current mediaType and exclude paths already included in current unit list
                foreach (var ci in claimedItems)
                {
                    try
                    {
                        var ext = System.IO.Path.GetExtension(ci.Path) ?? string.Empty;
                        var q = MediaFileExtensions.GetQualityForExtension(ext);
                        var fileMedia = BookFile.DetermineMediaType(new QualityModel { Quality = q });
                        if ((mediaType == BookMediaType.Audiobook && fileMedia != "audiobook")
                            || (mediaType == BookMediaType.Ebook && fileMedia != "ebook"))
                        {
                            continue;
                        }
                        var tags = SafeDeserializeTags(ci.TagsJson);
                        result.Add((ci.Path, tags));
                    }
                    catch { }
                }

                return (result.Count > 0, result);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[BOOK-COALESCE] Error during coalesce evaluation");
                return (false, result);
            }
        }

        private string FindBookRootFolder(string startPathOrFolder, string editionTitle)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(startPathOrFolder) || string.IsNullOrWhiteSpace(editionTitle)) return null;
                var start = System.IO.Directory.Exists(startPathOrFolder)
                    ? startPathOrFolder
                    : (System.IO.Path.GetDirectoryName(startPathOrFolder) ?? string.Empty);
                var normTitle = NormalizeTokensForCompare(editionTitle);
                if (string.IsNullOrWhiteSpace(normTitle)) return null;

                // Consider only two candidates: the unit folder itself and its parent
                var unit = NormalizeDirectory(start);
                var parent = NormalizeDirectory(System.IO.Path.GetDirectoryName(unit) ?? string.Empty);
                string UnitName(string p)
                {
                    try { return new System.IO.DirectoryInfo(p).Name; } catch { return null; }
                }
                var unitName = NormalizeTokensForCompare(UnitName(unit));
                var parentName = NormalizeTokensForCompare(UnitName(parent));

                if (!string.IsNullOrWhiteSpace(unitName) && unitName.Contains(normTitle, StringComparison.Ordinal)) return unit;
                if (!string.IsNullOrWhiteSpace(parentName) && parentName.Contains(normTitle, StringComparison.Ordinal)) return parent;
                return null;
            }
            catch { return null; }
        }

        private bool LooksLikeEnumeratedSiblings(HashSet<string> siblingFolders)
        {
            try
            {
                // Extract names
                var names = siblingFolders
                    .Select(p =>
                    {
                        try { return new System.IO.DirectoryInfo(p).Name; } catch { return null; }
                    })
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
                if (names.Count <= 1) return false;

                // Tokenize each name
                var tokenized = names.Select(TokenizeName).ToList();
                if (tokenized.Count == 0) return false;

                // Compute common tokens among all siblings
                var common = new HashSet<string>(tokenized[0], StringComparer.Ordinal);
                for (int i = 1; i < tokenized.Count; i++)
                {
                    common.IntersectWith(tokenized[i]);
                    if (common.Count == 0) break;
                }

                // Disallow dangerous common tokens that suggest different versions
                var blacklist = new HashSet<string>(new[] { "version", "alt", "alternate", "remaster", "remastered", "deluxe", "extended" }, StringComparer.Ordinal);
                if (common.Any(c => blacklist.Contains(c))) return false;

                // Check if siblings look like discs: either explicit disc keywords exist OR
                // residual tokens (after removing common) are purely numeric/roman
                var discKeywords = new HashSet<string>(new[] { "disc", "disk", "cd", "part", "tape", "cassette", "side" }, StringComparer.Ordinal);
                var anyDiscKeyword = tokenized.Any(tks => tks.Any(t => discKeywords.Contains(t)));

                bool AllResidualNumeric()
                {
                    foreach (var tks in tokenized)
                    {
                        var residual = tks.Where(t => !common.Contains(t)).ToList();
                        if (residual.Count == 0) return false;
                        foreach (var r in residual)
                        {
                            if (!(IsDigits(r) || IsRoman(r))) return false;
                        }
                    }
                    return true;
                }
                var residualOk = AllResidualNumeric();

                _logger.Debug("[BOOK-COALESCE][ANALYZE] common=[{0}] discKeyword={1} residualNumeric={2}", string.Join(" ", common.Take(6)), anyDiscKeyword, residualOk);

                if (!anyDiscKeyword && !residualOk) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        private static bool IsRoman(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var v = s.ToUpperInvariant();
            return Regex.IsMatch(v, "^(M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3}))$");
        }

        private static string NormalizeTokensForCompare(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.ToLowerInvariant();
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                var ch = arr[i];
                if (!(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))) arr[i] = ' ';
            }
            var norm = Regex.Replace(new string(arr), "\\s+", " ").Trim();
            return norm;
        }

        private static List<string> TokenizeName(string s)
        {
            var norm = NormalizeTokensForCompare(s);
            // Split letters/numbers boundaries: e.g., CD1 -> CD 1
            norm = Regex.Replace(norm, "([a-z]+)([0-9]+)", "$1 $2");
            norm = Regex.Replace(norm, "([0-9]+)([a-z]+)", "$1 $2");
            return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static string GetUnitKey(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                var ext = Path.GetExtension(path) ?? string.Empty;
                return (dir + "|" + ext).ToLowerInvariant();
            }
            catch { return path; }
        }

        private static bool IsFileAllowedForRootFolderType(string filePath, RootFolder rootFolder, IReadOnlySet<string> audioExtensions, IReadOnlySet<string> textExtensions)
        {
            return StagingQueueFileDispositionHelper.IsFileAllowedForRootFolderType(filePath, rootFolder);
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "<empty>";
            }

            try
            {
                return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return path;
            }
        }

        private static string LeafUnitKey(string unitKey)
        {
            if (string.IsNullOrWhiteSpace(unitKey))
            {
                return "<none>";
            }

            try
            {
                var parts = unitKey.Split(new[] { '|' }, 2);
                if (parts.Length == 2)
                {
                    return $"{LeafName(parts[0])}|{parts[1]}";
                }

                return LeafName(unitKey);
            }
            catch
            {
                return unitKey;
            }
        }

        private DateTime GetQueuedFileModifiedUtc(IngestQueueItem item)
        {
            if (item == null)
            {
                return DateTime.UtcNow;
            }

            try
            {
                var queuedModified = MediaFileFreshness.FromUnixNanoseconds(item.MtimeNs);
                if (queuedModified != default)
                {
                    return queuedModified;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(item.Path) && _diskProvider.FileExists(item.Path))
                {
                    return _diskProvider.FileGetLastWrite(item.Path);
                }
            }
            catch
            {
                // ignore
            }

            return DateTime.UtcNow;
        }

            private static int GetDurationMergeToleranceSeconds(int editionDurationSeconds)
            {
                // Strict: only merge when duration is a strong signal, but allow minor container rounding.
                // 0.2% with a floor/ceiling keeps it stable across short/long audiobooks.
            var tol = (int)Math.Round(editionDurationSeconds * 0.002);
            if (tol < 60) tol = 60;
            if (tol > 300) tol = 300;
            return tol;
        }

        private enum AuthorRootDurationDecision
        {
            MergeMultipart,
            SplitDuplicates,
            Unmapped
        }

        private static AuthorRootDurationDecision DecideAuthorRootDurationAction(
            int? editionDurationSeconds,
            IReadOnlyList<int?> perFileDurationSeconds,
            out int toleranceSeconds,
            out int sumSeconds,
            out string reason)
        {
            toleranceSeconds = 0;
            sumSeconds = 0;
            reason = null;

            if (!editionDurationSeconds.HasValue || editionDurationSeconds.Value <= 0)
            {
                reason = "NO_EDITION_DURATION";
                return AuthorRootDurationDecision.Unmapped;
            }

            if (perFileDurationSeconds == null ||
                perFileDurationSeconds.Count == 0 ||
                perFileDurationSeconds.Any(d => !d.HasValue || d.Value <= 0))
            {
                reason = "NO_FILE_DURATION";
                return AuthorRootDurationDecision.Unmapped;
            }

            var edition = editionDurationSeconds.Value;
            toleranceSeconds = GetDurationMergeToleranceSeconds(edition);
            var tol = toleranceSeconds;
            sumSeconds = perFileDurationSeconds.Sum(d => d.Value);

            if (Math.Abs(sumSeconds - edition) <= toleranceSeconds)
            {
                return AuthorRootDurationDecision.MergeMultipart;
            }

            if (perFileDurationSeconds.All(d => Math.Abs(d.Value - edition) <= tol))
            {
                return AuthorRootDurationDecision.SplitDuplicates;
            }

            reason = "DURATION_MISMATCH";
            return AuthorRootDurationDecision.Unmapped;
        }

        private static string BuildPerFileUnitKey(string filePath, string editionTitle, BookMediaType mediaType)
        {
            try
            {
                // Base on the book-root key, but include the full path to keep copies distinct even
                // when they live in the same folder (author-root duplicates).
                var baseKey = BookCoalescingHelper.BuildRootUnitKey(filePath, editionTitle, mediaType);
                return $"{baseKey}|{NormalizeDirectory(filePath)}".ToLowerInvariant();
            }
            catch
            {
                return filePath ?? string.Empty;
            }
        }

        private bool ShouldProcessAuthorEvent(int authorId, string prefix)
        {
            try
            {
                var now = DateTime.UtcNow;
                var key = $"{authorId}|{NormalizeDirectory(prefix)}";
                var last = _authorLastProcessed.GetOrAdd(key, DateTime.MinValue);
                // throttle duplicate events within 60 seconds per author+prefix
                if ((now - last).TotalSeconds < 60)
                {
                    return false;
                }
                _authorLastProcessed[key] = now;
                return true;
            }
            catch
            {
                return true;
            }
        }

            private Dictionary<string, Dictionary<string, List<string>>> BuildUnitTagsByKey(
                List<IngestQueueItem> claimedSet,
                ISet<string> extractionFailedPaths)
            {
                var tagsByUnit = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
                if (claimedSet == null || claimedSet.Count == 0) return tagsByUnit;

                var groups = claimedSet
                    .GroupBy(i => GetUnitKey(i.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                    .ToList();

                foreach (var g in groups)
                {
                    var firstPath = g.FirstOrDefault()?.Path;
                    var ext = (Path.GetExtension(firstPath ?? string.Empty) ?? string.Empty).ToLowerInvariant();
                    var fileCount = g.Count();

                    // Read per-file tags for small groups and "single-file book" containers.
                    // This prevents representative-tag smearing in flat folders (e.g. Collected Works/*.m4b).
                    var shouldReadPerFile =
                        fileCount <= 5 ||
                        MediaFileExtensions.IsSingleFileBookContainer(ext);

                    if (shouldReadPerFile)
                    {
                        foreach (var item in g)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;

                            Dictionary<string, List<string>> perFileTags = null;

                            if (!string.IsNullOrWhiteSpace(item.TagsJson) && item.TagsJson.Trim() != "{}")
                            {
                                perFileTags = SafeDeserializeTags(item.TagsJson);
                            }

                            if (perFileTags == null || perFileTags.Count == 0)
                            {
                                var (diskTags, _, extractionFailed) = TryReadTagsFromDisk(item.Path);
                                if (extractionFailed)
                                {
                                    extractionFailedPaths?.Add(item.Path);
                                    continue;
                                }

                                perFileTags = diskTags;
                            }

                            if (perFileTags != null && perFileTags.Count > 0)
                            {
                                tagsByUnit[item.Path] = perFileTags;
                            }
                        }

                        continue;
                    }

                    var unitTagCandidates = new List<Dictionary<string, List<string>>>(fileCount);
                    foreach (var item in g)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Path))
                        {
                            continue;
                        }

                        Dictionary<string, List<string>> perFileTags = null;
                        if (!string.IsNullOrWhiteSpace(item.TagsJson) && item.TagsJson.Trim() != "{}")
                        {
                            perFileTags = SafeDeserializeTags(item.TagsJson);
                        }

                        if (perFileTags == null || perFileTags.Count == 0)
                        {
                            var (diskTags, _, extractionFailed) = TryReadTagsFromDisk(item.Path);
                            if (extractionFailed)
                            {
                                extractionFailedPaths?.Add(item.Path);
                                continue;
                            }

                            perFileTags = diskTags;
                        }

                        if (perFileTags != null && perFileTags.Count > 0)
                        {
                            unitTagCandidates.Add(perFileTags);
                        }
                    }

                    var consensusTags = UnitTagConsensusBuilder.BuildConsensus(unitTagCandidates, fileCount);
                    if (consensusTags.Count > 0)
                    {
                        tagsByUnit[g.Key] = consensusTags;
                    }
                }

                return tagsByUnit;
            }

            private (Dictionary<string, List<string>> Tags, int? DurationSeconds, bool ExtractionFailed) TryReadTagsFromDisk(string filePath)
            {
                try
                {
                    if (_metadataTagService == null) return (null, null, false);
                    if (string.IsNullOrWhiteSpace(filePath)) return (null, null, false);

                    var fi = _diskProvider.GetFileInfo(filePath);
                    if (fi == null || !fi.Exists) return (null, null, false);

                    var (raw, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fi);
                    if (raw == null || raw.Count == 0) return (null, durationSeconds, false);

                    var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in raw)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        tags[kv.Key] = kv.Value ?? new List<string>();
                    }
                    return (tags, durationSeconds, false);
                }
                catch (TagExtractionException)
                {
                    return (null, null, true);
                }
                catch
                {
                    return (null, null, false);
                }
            }

        private BookImportFileResult CompleteApplyResult(
            IReadOnlyList<BookImportFileResult> applyResults,
            string path,
            IngestQueueItem item,
            int bookId,
            int authorId,
            ref int importedCount,
            ref int applyFailureCount)
        {
            var applyResult = applyResults?
                .FirstOrDefault(result => result?.Path != null && result.Path.PathEquals(path)) ??
                BookImportFileResult.Failed(path, "NO_APPLY_RESULT");

            if (applyResult.Outcome == ImportOutcome.Unmapped)
            {
                MarkUnmapped(path);
            }

            _ingestQueue.CompleteItemWithResult(
                item.Id,
                path,
                applyResult.Outcome,
                bookId: bookId,
                authorId: authorId,
                quality: "Unknown",
                errorMessage: applyResult.ReasonCode,
                statusError: applyResult.ReasonCode);

            if (applyResult.IsApplied)
            {
                importedCount++;
            }
            else if (!applyResult.IsHandled)
            {
                applyFailureCount++;
            }

            return applyResult;
        }

        private void MarkUnmapped(string filePath)
        {
            BookImportUnmappedFileHelper.MarkUnmapped(_mediaFileService, _diskProvider, filePath, _logger, "[INGEST-AUTHOR-READY]");
        }
        private int? GetDurationSeconds(string filePath)
        {
            return MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(filePath));
        }
    }
}
