using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Instrumentation;

using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.RootFolders;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using System.IO;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Thin coordinator for the import process - delegates to specialized services
    /// Following the architecture from /claudehelper/bibles/import_files_9-7-2025.md
    /// </summary>
    public class ImportOrchestratorV2 : IImportOrchestrator
    {
        private readonly IIngestQueueRepository _ingestQueue;
        private readonly IFileMatchingService _fileMatching;
        private readonly NzbDrone.Core.MediaFiles.BookImport.Services.IDiscoveryWorker _discoveryWorker;
        private readonly IAuthorLibraryService _authorLibraryService;
            private readonly IBookImportService _bookImport;
            private readonly IBookService _bookService;
            private readonly IEditionService _editionService;
            private readonly IAuthorService _authorService;
            private readonly IBookUnitDestinationService _unitDestination;
            private readonly IManageCommandQueue _commandQueueManager;
            private readonly IDiskProvider _diskProvider;
            private readonly IMetadataTagService _metadataTagService;
            private readonly IMediaInfoExtractor _mediaInfoExtractor;
            private readonly IMediaFileRepository _mediaFileRepository;
        private readonly IAuthorFolderMatchingService _authorFolderMatchingService;
        private readonly Logger _logger;
        private readonly IEventAggregator _eventAggregator;
        private int? _commandId;
        private int _stagingTotal;
        private int _stagingProcessed;
        private int _queueProcessed;
        private int _authorFoldersTotal;
        private int _bookFoldersTotal;
        private int _booksMatched;
        private int _filesImported;
            private static DateTime GetQueuedFileModifiedUtc(IngestQueueItem item)
            {
                return item == null ? default : MediaFileFreshness.FromUnixNanoseconds(item.MtimeNs);
            }

            private sealed class StageFilesResult
            {
                public int StagedCount { get; set; }
                public int SkippedKnownUnchangedCount { get; set; }
                public List<string> SeenFilePaths { get; } = new List<string>();
                public bool CleanupSafe { get; set; }
            }

            public ImportOrchestratorV2(
                IIngestQueueRepository ingestQueue,
                IFileMatchingService fileMatching,
                NzbDrone.Core.MediaFiles.BookImport.Services.IDiscoveryWorker discoveryWorker,
                IAuthorLibraryService authorLibraryService,
                IBookImportService bookImport,
                IBookService bookService,
                IEditionService editionService,
                IAuthorService authorService,
                IBookUnitDestinationService unitDestination,
                IManageCommandQueue commandQueueManager,
                IDiskProvider diskProvider,
                IMetadataTagService metadataTagService,
                IMediaInfoExtractor mediaInfoExtractor,
                IMediaFileRepository mediaFileRepository,
            IAuthorFolderMatchingService authorFolderMatchingService,
            IEventAggregator eventAggregator,
            Logger logger)
            {
                _ingestQueue = ingestQueue;
                _fileMatching = fileMatching;
                _discoveryWorker = discoveryWorker;
                _authorLibraryService = authorLibraryService;
                _bookImport = bookImport;
                _bookService = bookService;
                _editionService = editionService;
                _authorService = authorService;
                _unitDestination = unitDestination;
                _commandQueueManager = commandQueueManager;
                _diskProvider = diskProvider;
                _metadataTagService = metadataTagService;
                _mediaInfoExtractor = mediaInfoExtractor;
                _mediaFileRepository = mediaFileRepository;
            _authorFolderMatchingService = authorFolderMatchingService;
            _logger = logger;
            _eventAggregator = eventAggregator;
        }

        public async Task<OrchestratorImportResult> ProcessFilesAsync(
            string path,
            RootFolder rootFolder = null, 
            int? commandId = null, 
            IReadOnlyCollection<string> forceStagePaths = null,
            FilterFilesType filter = FilterFilesType.Known)
        {
            var stopwatch = Stopwatch.StartNew();
            var commandStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _commandId = commandId;
            _stagingTotal = 0;
            _stagingProcessed = 0;
            _queueProcessed = 0;
            _authorFoldersTotal = 0;
            _bookFoldersTotal = 0;
            _booksMatched = 0;
            _filesImported = 0;
            var result = new OrchestratorImportResult();
            var manualRootScan = commandId.HasValue &&
                                 IsManualCommand(commandId.Value);
            var scanScope = new IngestQueueScanScope(path, forceStagePaths);

            try
            {
                if (commandId.HasValue)
                {
                    // Ensure per-command result reporting doesn't reuse stale import_results rows.
                    _ingestQueue.BeginSession(commandId.Value);

                    // Manual rescans should retry items previously marked as unmapped/failed.
                    if (manualRootScan && !scanScope.IsExact)
                    {
                        _ingestQueue.RequeueFailedOrUnmappedUnderPath(path);
                    }
                }

                _logger.Debug("Starting thin root-folder import orchestration for path: {0}", path);
                LogMemorySnapshot("[ORCHESTRATOR] start path='{0}'", path);

                if (commandId.HasValue)
                {
                    // One command can invoke the orchestrator for several folders. Staging-complete
                    // belongs to this pass, not to the lifetime of the command-level progress totals.
                    ImportSessionProgressTracker.BeginStagingPass(commandId.Value);
                }

                // Stage 1 + 2 (overlapped): stream staging while discovery/import runs.
                // This reduces time-to-first-author by not waiting for the full file list to be built.
                //
                // Streaming mode: stage files and discover authors concurrently.
                var stageTask = StageFilesAsync(path, rootFolder, commandId, forceStagePaths, filter);
                var discoveryTask = _discoveryWorker.DiscoverAndImportAuthorsStreamingAsync(rootFolder, scanScope, commandId);
                LogMemorySnapshot("[ORCHESTRATOR] after starting stage+discovery path='{0}'", path);

                var stageResult = await stageTask;
                result.ScannedFilePaths = stageResult.SeenFilePaths;
                result.CleanupSafe = stageResult.CleanupSafe;
                _logger.Debug("Staged {0} files for processing ({1} known unchanged files skipped)", stageResult.StagedCount, stageResult.SkippedKnownUnchangedCount);
                LogMemorySnapshot("[ORCHESTRATOR] after staging path='{0}' staged={1} seen={2} skipped={3}",
                    path,
                    stageResult.StagedCount,
                    stageResult.SeenFilePaths.Count,
                    stageResult.SkippedKnownUnchangedCount);

                // Scheduled scans retry only previously-Failed files that were actually observed on disk now.
                // Unmapped rows remain manual-retry-only, and missing Failed paths are not resurrected.
                var requeuedFailed = RequeueObservedFailuresForScheduledRootScan(
                    manualRootScan,
                    rootFolder,
                    stageResult.SeenFilePaths);
                if (requeuedFailed > 0)
                {
                    _logger.Debug("[ORCHESTRATOR] Re-queued {0} previously-Failed files observed during scheduled scan", requeuedFailed);
                }

                    await discoveryTask;
                    LogMemorySnapshot("[ORCHESTRATOR] after discovery path='{0}'", path);

                    await DrainRemainingAsync(scanScope, rootFolder);
                    LogMemorySnapshot("[ORCHESTRATOR] after drain path='{0}'", path);

                    // Phase 3: Event-driven ingest tasks (author-ready matching/import) must complete before the command ends.
                    if (commandId.HasValue)
                    {
                        await AwaitIngestToCompleteAsync(scanScope, rootFolder, commandId.Value, commandStartedAt);
                        LogMemorySnapshot("[ORCHESTRATOR] after author-ready ingest wait path='{0}'", path);
                    }

                    // Stage 4: Collect results from database. A specific-file scan invokes the
                    // orchestrator once per folder while reusing the same command id, so the raw
                    // command-scoped result set is cumulative. Keep true root scans command-wide,
                    // but scope forced-path retries to the exact files processed by this call.
                    var importResults = _ingestQueue.GetImportResults(commandId);
                    var rawImportResultCount = importResults.Count;
                    var resultScopePaths = BuildResultScopePathSet(forceStagePaths);
                    if (resultScopePaths != null)
                    {
                        importResults = importResults
                            .Where(r => !string.IsNullOrWhiteSpace(r?.Path) && resultScopePaths.Contains(r.Path))
                            .ToList();
                    }

                    LogMemorySnapshot("[ORCHESTRATOR] after loading import results path='{0}' results={1} rawResults={2} scoped={3}",
                        path,
                        importResults.Count,
                        rawImportResultCount,
                        resultScopePaths != null);
                
                // Convert to OrchestratorImportResult format
                foreach (var importResult in importResults)
                {
                    switch (importResult.Outcome)
                    {
                        case ImportOutcome.Imported:
                            if (importResult.BookId.HasValue)
                            {
                                // Get book details for imported file
                                var book = _bookService.GetBook(importResult.BookId.Value);
                                if (book != null)
                                {
                                    result.ImportedFiles.Add(new ImportedFile
                                    {
                                        FilePath = importResult.Path,
                                        BookId = book.Id,
                                        BookTitle = book.Title,
                                        AuthorName = book.Author?.Name ?? "Unknown"
                                    });
                                }
                            }
                            break;
                            
                        case ImportOutcome.Unmapped:
                            result.UnmappedFiles.Add(new UnmappedFile
                            {
                                FilePath = importResult.Path,
                                Reason = importResult.ErrorMessage ?? "No matching book found"
                            });
                            break;
                            
                        case ImportOutcome.Failed:
                        {
                            var failureReason = importResult.ErrorMessage ?? "IMPORT_APPLY_FAILED";
                            result.Errors.Add($"{importResult.Path}: {failureReason}");
                            result.FailedFiles.Add(new FailedFile
                            {
                                FilePath = importResult.Path,
                                Reason = failureReason
                            });
                            break;
                        }

                        case ImportOutcome.AlreadyLinked:
                            // Identity was already satisfied; do not count it as a new import.
                            break;

                        case ImportOutcome.Ignored:
                            // Intentionally ignored items (e.g., out-of-scope for current root folder type)
                            // do not appear as imported/unmapped/errors.
                            break;
                    }
                    
                    // Track newly added authors
                    if (importResult.AuthorId.HasValue && importResult.Outcome == ImportOutcome.Imported)
                    {
                        var author = _authorService.GetAuthor(importResult.AuthorId.Value);
                        if (author != null && !result.AddedAuthors.Contains(author.Name))
                        {
                            result.AddedAuthors.Add(author.Name);
                        }
                    }
                }
                
                _logger.Debug("Import orchestration completed in {0}ms - Imported: {1}, Unmapped: {2}, Errors: {3}",
                    stopwatch.ElapsedMilliseconds, 
                    result.ImportedFiles.Count, 
                    result.UnmappedFiles.Count, 
                    result.Errors.Count);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Import orchestration failed");
                result.Errors.Add($"Import failed: {ex.Message}");
                return result;
            }
        }

            private async Task DrainRemainingAsync(IngestQueueScanScope scanScope, RootFolder rootFolder)
            {
                var scannedPath = scanScope?.PathPrefix;
                if (string.IsNullOrWhiteSpace(scannedPath))
                {
                    return;
                }

            var normalizedScanned = NormalizeDirectory(scannedPath) ?? scannedPath;
            var scannedIsFile = false;
            try
            {
                scannedIsFile = _diskProvider.FileExists(scannedPath);
                if (!scannedIsFile)
                {
                    var isFolder = _diskProvider.FolderExists(scannedPath);
                    if (!isFolder && Path.HasExtension(scannedPath))
                    {
                        scannedIsFile = true;
                    }
                }
            }
            catch
            {
                scannedIsFile = false;
            }

                var totalClaimed = 0;
                var totalMatched = 0;
                var totalUnmapped = 0;
                var totalFailed = 0;
                var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unmatchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pendingUnmappedBookFiles = new List<BookFile>();
                var pendingUnmappedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void FlushPendingUnmappedBookFiles()
                {
                    if (pendingUnmappedBookFiles.Count == 0)
                    {
                        return;
                    }

                    try
                    {
                        var inserted = _mediaFileRepository.InsertManyIgnoreDuplicatePaths(pendingUnmappedBookFiles);
                        if (inserted > 0)
                        {
                            _logger.Debug("[DRAIN] Created {0} unmapped BookFile rows ({1} already existed)", inserted, pendingUnmappedBookFiles.Count - inserted);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[DRAIN] Failed to create unmapped BookFile rows for {0} attempted files", pendingUnmappedBookFiles.Count);
                    }
                    finally
                    {
                        pendingUnmappedBookFiles.Clear();
                    }
                }

            while (true)
            {
                var remaining = scanScope.GetQueuedItems(_ingestQueue, limit: 10000);
                if (remaining.Count == 0)
                {
                    if (totalClaimed > 0)
                    {
                        _logger.Debug("[DRAIN] Complete under '{0}': claimed={1}, matched={2}, unmapped={3}, failed={4}", normalizedScanned, totalClaimed, totalMatched, totalUnmapped, totalFailed);
                    }
                    return;
                }

                var claimableRemaining = remaining.ToList();

                _logger.Debug("[DRAIN] {0} items still queued under '{1}' — draining via global match", remaining.Count, normalizedScanned);

                // Claim atomically (folder units preferred) so nothing stays queued forever.
                var claimed = new List<IngestQueueItem>();

                if (scannedIsFile || scanScope.IsExact)
                {
                    foreach (var item in claimableRemaining)
                    {
                        if (_ingestQueue.TryClaimItem(item.Id, out var claimedItem))
                        {
                            claimed.Add(claimedItem);
                        }
                    }
                }
                else
                {
                    // Avoid claiming the entire scanned folder tree due to a file directly under scannedPath.
                    var unitFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in claimableRemaining)
                    {
                        var dir = string.Empty;
                        try { dir = NormalizeDirectory(Path.GetDirectoryName(item.Path) ?? string.Empty) ?? string.Empty; } catch { dir = string.Empty; }
                        if (string.IsNullOrWhiteSpace(dir))
                        {
                            continue;
                        }

                        if (string.Equals(dir, normalizedScanned, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_ingestQueue.TryClaimItem(item.Id, out var claimedItem))
                            {
                                claimed.Add(claimedItem);
                            }
                            continue;
                        }

                        unitFolders.Add(dir);
                    }

                    foreach (var folder in unitFolders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var unitItems = _ingestQueue.TryClaimUnit(folder);
                        if (unitItems != null && unitItems.Count > 0)
                        {
                            claimed.AddRange(unitItems);
                        }
                    }
                }

                if (claimed.Count == 0)
                {
                    _logger.Debug("[DRAIN] All remaining queued items are already claimed by event handlers");
                    return;
                }

                totalClaimed += claimed.Count;

                try
                {
                string GetUnitKey(string filePath)
                {
                    try
                    {
                        var dir = NormalizeDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
                        var ext = (Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(dir)) return null;
                        return (dir + "|" + ext).ToLowerInvariant();
                    }
                    catch
                    {
                        return null;
                    }
                }

                // Read tags once per (folder+extension) unit and apply to all files in that unit.
                // For files directly under a top-level folder (likely an author folder), read per-file tags
                // to avoid cross-book contamination when multiple books live in the same folder.
                var directChildFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var item in claimed)
                    {
                        var dir = NormalizeDirectory(Path.GetDirectoryName(item.Path) ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        var parent = NormalizeDirectory(Path.GetDirectoryName(dir) ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(parent) &&
                            string.Equals(parent, normalizedScanned, StringComparison.OrdinalIgnoreCase))
                        {
                            directChildFolders.Add(dir);
                        }
                    }
                }
                catch
                {
                    // best-effort only
                }

                var unitTagsByKey = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
                var extractionFailedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var unitGroups = claimed.GroupBy(i => GetUnitKey(i.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                                             .Where(g => !string.IsNullOrWhiteSpace(g.Key));

                    foreach (var g in unitGroups)
                    {
                        var any = g.FirstOrDefault();
                        if (any == null) continue;

                        var dir = NormalizeDirectory(Path.GetDirectoryName(any.Path) ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(dir) && directChildFolders.Contains(dir))
                        {
                            continue;
                        }

                        var ext = (Path.GetExtension(any.Path) ?? string.Empty).ToLowerInvariant();
                        var fileCount = g.Count();

                        var shouldReadPerFile =
                            fileCount <= 5 ||
                            MediaFileExtensions.IsSingleFileBookContainer(ext);

                        if (shouldReadPerFile)
                        {
                            foreach (var item in g)
                            {
                                if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;

                                var (tags, durationSeconds, extractionFailed) = TryReadTagsFromDisk(item.Path);
                                if (extractionFailed)
                                {
                                    extractionFailedPaths.Add(item.Path);
                                    continue;
                                }

                                if (tags != null && tags.Count > 0)
                                {
                                    unitTagsByKey[item.Path] = tags;
                                }
                                if (MediaDuration.HasDuration(durationSeconds) && !item.DurationSeconds.HasValue)
                                {
                                    item.DurationSeconds = durationSeconds;
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

                            var stagedTags = SafeDeserializeTags(item.TagsJson);
                            if (stagedTags != null && stagedTags.Count > 0)
                            {
                                unitTagCandidates.Add(stagedTags);
                                continue;
                            }

                            var (diskTags, diskDurationSeconds, extractionFailed) = TryReadTagsFromDisk(item.Path);
                            if (extractionFailed)
                            {
                                extractionFailedPaths.Add(item.Path);
                                continue;
                            }

                            if (diskTags != null && diskTags.Count > 0)
                            {
                                unitTagCandidates.Add(diskTags);
                            }

                            if (MediaDuration.HasDuration(diskDurationSeconds) && !item.DurationSeconds.HasValue)
                            {
                                item.DurationSeconds = diskDurationSeconds;
                            }
                        }

                        var consensusTags = UnitTagConsensusBuilder.BuildConsensus(unitTagCandidates, fileCount);
                        if (consensusTags.Count > 0)
                        {
                            unitTagsByKey[g.Key] = consensusTags;
                        }
                    }
                }
                catch
                {
                    // best-effort only
                }

                    var discoveredFiles = new List<DiscoveredFileWithMetadata>(claimed.Count);
                    var discoveredByPath = new Dictionary<string, DiscoveredFileWithMetadata>(StringComparer.OrdinalIgnoreCase);
                    var byPath = new Dictionary<string, IngestQueueItem>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in claimed)
                    {
                        var tags = SafeDeserializeTags(item.TagsJson);
                        int? durationSecondsOverride = null;
                        var extractionFailed = extractionFailedPaths.Contains(item.Path);

                    // Prefer disk tags (either per-file for risky folders, or per-unit otherwise).
                    try
                    {
                        if (!extractionFailed)
                        {
                            var dir = NormalizeDirectory(Path.GetDirectoryName(item.Path) ?? string.Empty);
                            if (!string.IsNullOrWhiteSpace(dir) && directChildFolders.Contains(dir))
                            {
                                var (perFile, perFileDurationSeconds, perFileExtractionFailed) = TryReadTagsFromDisk(item.Path);
                                extractionFailed = perFileExtractionFailed;
                                if (perFile != null && perFile.Count > 0)
                                {
                                    tags = perFile;
                                    durationSecondsOverride = perFileDurationSeconds;
                                }
                            }
                            else if (tags == null || tags.Count == 0)
                            {
                                if (!string.IsNullOrWhiteSpace(item.Path) &&
                                    unitTagsByKey.TryGetValue(item.Path, out var perFileTags) &&
                                    perFileTags != null &&
                                    perFileTags.Count > 0)
                                {
                                    tags = perFileTags;
                                }
                                else
                                {
                                    var unitKey = GetUnitKey(item.Path) ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(unitKey) &&
                                        unitTagsByKey.TryGetValue(unitKey, out var unitTags) &&
                                        unitTags != null &&
                                        unitTags.Count > 0)
                                    {
                                        tags = unitTags;
                                    }
                                    else
                                    {
                                        var (diskTags, diskDurationSeconds, diskExtractionFailed) = TryReadTagsFromDisk(item.Path);
                                        extractionFailed = diskExtractionFailed;
                                        if (diskTags != null && diskTags.Count > 0)
                                        {
                                            tags = diskTags;
                                            durationSecondsOverride = diskDurationSeconds;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // best-effort only
                    }

                        var effectiveTags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        var durationSeconds = durationSecondsOverride ?? item.DurationSeconds;
                        if (!extractionFailed && !durationSeconds.HasValue)
                        {
                            durationSeconds = MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(item.Path));
                        }
                        var discovered = new DiscoveredFileWithMetadata
                        {
                            Path = item.Path,
                            Size = item.SizeBytes,
                            Modified = GetQueuedFileModifiedUtc(item),
                            AllTags = effectiveTags,
                            DurationSeconds = durationSeconds
                        };
                        discoveredByPath[item.Path] = discovered;
                        byPath[item.Path] = item;
                        if (extractionFailed)
                        {
                            extractionFailedPaths.Add(item.Path);
                            continue;
                        }

                        discoveredFiles.Add(discovered);
                    }

                _logger.Debug("[DRAIN] Built {0} files for matching ({1} unit tagsets from disk, {2} direct-child folders read per-file)",
                    discoveredFiles.Count, unitTagsByKey.Count, directChildFolders.Count);

                    void QueueUnmappedBookFilePath(string filePath, DiscoveredFileWithMetadata meta)
                    {
                        if (string.IsNullOrWhiteSpace(filePath))
                        {
                            return;
                        }

                        if (pendingUnmappedPaths.Contains(filePath))
                        {
                            return;
                        }

                        if (!byPath.TryGetValue(filePath, out var itemRef))
                        {
                            return;
                        }

                        var ext = Path.GetExtension(filePath);
                        var quality = MediaFileExtensions.GetQualityForExtension(ext);
                        var qualityModel = new NzbDrone.Core.Qualities.QualityModel { Quality = quality };
                        pendingUnmappedBookFiles.Add(new BookFile
                        {
                            Path = filePath,
                            Size = itemRef.SizeBytes,
                            Modified = MediaFileFreshness.FromUnixNanoseconds(itemRef.MtimeNs, DateTime.UnixEpoch),
                            DateAdded = DateTime.UtcNow,
                            EditionId = 0,
                            Quality = qualityModel,
                            MediaInfo = new MediaInfoModel(),
                            MediaType = BookFile.DetermineMediaType(qualityModel),
                            ReplicaPaths = new List<string>(),
                            AllTags = meta?.AllTags,
                            DurationSeconds = meta?.DurationSeconds
                        });

                        pendingUnmappedPaths.Add(filePath);

                        if (pendingUnmappedBookFiles.Count >= 250)
                        {
                            FlushPendingUnmappedBookFiles();
                        }
                    }

                void QueueUnmappedBookFile(DiscoveredFileWithMetadata meta)
                {
                    if (meta?.Path == null)
                    {
                        return;
                        }

                    QueueUnmappedBookFilePath(meta.Path, meta);
                }

                foreach (var failedPath in extractionFailedPaths)
                {
                    if (!byPath.TryGetValue(failedPath, out var failedItem))
                    {
                        continue;
                    }

                    discoveredByPath.TryGetValue(failedPath, out var failedMeta);
                    QueueUnmappedBookFilePath(failedPath, failedMeta);
                    _ingestQueue.CompleteItemWithResult(
                        failedItem.Id,
                        failedPath,
                        ImportOutcome.Failed,
                        errorMessage: TagExtractionResult.FailureReason,
                        statusError: TagExtractionResult.FailureReason);
                    unmatchedPaths.Add(failedPath);
                    totalFailed++;
                }

                var localCtx = MatchingContextPresets.ForScanLocal();
                var v5Ctx = MatchingContextPresets.ForScanV5();
                var strictScopedRematchCtx = MatchingContextPresets.ForScanScopedRematch();

                async Task ProcessMatchesAsync(IEnumerable<FileMatch> matches)
                {
                    foreach (var m in matches ?? Array.Empty<FileMatch>())
                    {
                        if (m?.File?.Path == null) continue;
                        if (matchedPaths.Contains(m.File.Path) || unmatchedPaths.Contains(m.File.Path)) continue;
                        if (!byPath.TryGetValue(m.File.Path, out var itemRef)) continue;

                        var destBookId = m.BookId;
                        var destEditionId = m.EditionId;

                        try
                        {
                            var canonicalEdition = _editionService.GetEdition(m.EditionId);
                            if (canonicalEdition == null)
                            {
                                throw new InvalidOperationException($"Matched edition not found: {m.EditionId}");
                            }

                            var canonicalBook = _bookService.GetBook(canonicalEdition.BookId);
                            if (canonicalBook == null)
                            {
                                throw new InvalidOperationException($"Matched book not found: {canonicalEdition.BookId}");
                            }

                            var destKey = _unitDestination.BuildRootUnitKeyWithExtension(m.File.Path, canonicalEdition.Title, canonicalBook.MediaType);
                            var dest = _unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, destKey);
                            destBookId = dest.BookId;
                            destEditionId = dest.EditionId;

                            var applyResult = await _bookImport.ImportExistingFileAsync(
                                m.File,
                                destBookId,
                                destEditionId,
                                "Unknown",
                                m.Provenance);
                            if (applyResult == null || !m.File.Path.PathEquals(applyResult.Path))
                            {
                                applyResult = BookImportFileResult.Failed(m.File.Path, "NO_APPLY_RESULT");
                            }

                            if (applyResult.Outcome == ImportOutcome.Unmapped)
                            {
                                QueueUnmappedBookFile(m.File);
                            }

                            _ingestQueue.CompleteItemWithResult(
                                itemRef.Id,
                                m.File.Path,
                                applyResult.Outcome,
                                bookId: destBookId,
                                authorId: m.AuthorId,
                                quality: "Unknown",
                                errorMessage: applyResult.ReasonCode,
                                statusError: applyResult.ReasonCode);
                            matchedPaths.Add(m.File.Path);

                            if (applyResult.IsApplied)
                            {
                                totalMatched++;
                            }
                            else if (applyResult.Outcome == ImportOutcome.Unmapped)
                            {
                                totalUnmapped++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "[DRAIN] Import failed for '{0}'", m.File.Path);
                            _ingestQueue.CompleteItemWithResult(itemRef.Id, m.File.Path, ImportOutcome.Failed, bookId: destBookId, authorId: m.AuthorId, quality: "Unknown", errorMessage: "APPLY_EXCEPTION", statusError: "APPLY_EXCEPTION");
                            matchedPaths.Add(m.File.Path);
                        }
                    }
                }

                void TerminalizeUnmatched(UnmatchedFile u, string overrideReason = null)
                {
                    if (u?.File?.Path == null) return;
                    if (matchedPaths.Contains(u.File.Path) || unmatchedPaths.Contains(u.File.Path)) return;
                    if (!byPath.TryGetValue(u.File.Path, out var itemRef)) return;

                    var reason = overrideReason ?? u.Reason ?? "No match after drain";
                    QueueUnmappedBookFile(u.File);
                    _ingestQueue.CompleteItemWithResult(itemRef.Id, u.File.Path, ImportOutcome.Unmapped, errorMessage: reason, statusError: reason);
                    unmatchedPaths.Add(u.File.Path);
                    totalUnmapped++;
                }

                FileMatchResult localResult;
                try
                {
                    localResult = await _fileMatching.MatchFilesToLibraryAsync(discoveredFiles.ToArray(), restrictToAuthorId: null, localCtx);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[DRAIN] Local matching failed; terminalizing {0} claimed items as unmapped", claimed.Count);
                    foreach (var item in claimed)
                    {
                        if (matchedPaths.Contains(item.Path) || unmatchedPaths.Contains(item.Path))
                        {
                            continue;
                        }

                        try
                        {
                            discoveredByPath.TryGetValue(item.Path, out var meta);
                            QueueUnmappedBookFilePath(item.Path, meta);
                            _ingestQueue.CompleteItemWithResult(item.Id, item.Path, ImportOutcome.Unmapped, errorMessage: "Drain local match failed", statusError: "Drain local match failed");
                            totalUnmapped++;
                        }
                        catch
                        {
                            // best-effort only
                        }
                    }

                    FlushPendingUnmappedBookFiles();
                    continue;
                }

                await ProcessMatchesAsync(localResult.MatchedFiles);

                var remainingUnmatched = (localResult.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                    .Where(u => u?.File?.Path != null && byPath.ContainsKey(u.File.Path) && !matchedPaths.Contains(u.File.Path))
                    .Select(u => u.File)
                    .ToArray();

                if (remainingUnmatched.Length > 0)
                {
                    FileMatchResult v5Result;
                    try
                    {
                        v5Result = await _fileMatching.MatchFilesToLibraryAsync(remainingUnmatched, restrictToAuthorId: null, v5Ctx);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[DRAIN] V5 identification failed; terminalizing {0} remaining items as unmapped", remainingUnmatched.Length);
                        foreach (var f in remainingUnmatched)
                        {
                            TerminalizeUnmatched(new UnmatchedFile { File = f, Reason = "DRAIN_V5_IDENT_FAILED" }, "DRAIN_V5_IDENT_FAILED");
                        }
                        v5Result = new FileMatchResult { MatchedFiles = Array.Empty<FileMatch>(), UnmatchedFiles = Array.Empty<UnmatchedFile>() };
                    }

                    await ProcessMatchesAsync(v5Result.MatchedFiles);

                    var v5Unmatched = (v5Result.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                        .Where(u => u?.File?.Path != null && byPath.ContainsKey(u.File.Path) && !matchedPaths.Contains(u.File.Path))
                        .ToArray();

                    var groups = v5Unmatched
                        .Select(u => new { Unmatched = u, Suggestion = u.PotentialAuthors?.FirstOrDefault() })
                        .Where(x => x.Suggestion != null && !string.IsNullOrWhiteSpace(x.Suggestion.ProviderId))
                        .GroupBy(x => x.Suggestion.ProviderId, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var group in groups)
                    {
                        var suggestion = group.First().Suggestion;
                        var providerId = suggestion.ProviderId;

                        var filesForAuthor = group
                            .Select(x => x.Unmatched.File)
                            .Where(f => f?.Path != null && byPath.ContainsKey(f.Path) && !matchedPaths.Contains(f.Path))
                            .ToArray();

                        if (filesForAuthor.Length == 0)
                        {
                            continue;
                        }

                        Author author;
                        try
                        {
                            if (!TryBuildAuthorImportMonitoringConfig(
                                    suggestion.AuthorName,
                                    filesForAuthor.Select(f => f.Path),
                                    rootFolder,
                                    requestedBy: "drain",
                                    out var config,
                                    out var configError))
                            {
                                _logger.Warn("[DRAIN] Cannot build author import config for '{0}' ({1}): {2}",
                                    suggestion.AuthorName ?? "<unknown>", providerId ?? "<unknown>", configError ?? "unknown");

                                foreach (var f in filesForAuthor)
                                {
                                    TerminalizeUnmatched(new UnmatchedFile { File = f, Reason = "AUTHOR_IMPORT_CONFIG_FAILED" }, "AUTHOR_IMPORT_CONFIG_FAILED");
                                }
                                continue;
                            }

                            author = await _authorLibraryService.AddAuthorAsync(providerId, config);

                            // Pending imports (negative IDs) can't be imported immediately.
                            if (author == null || author.Id <= 0)
                            {
                                _logger.Warn("[DRAIN] Author add returned no immediate author for '{0}' ({1}), id={2}",
                                    suggestion.AuthorName ?? "<unknown>", providerId ?? "<unknown>", author?.Id ?? 0);

                                foreach (var f in filesForAuthor)
                                {
                                    if (f?.Path == null || !byPath.TryGetValue(f.Path, out var itemRef))
                                    {
                                        continue;
                                    }

                                    discoveredByPath.TryGetValue(f.Path, out var pendingMeta);
                                    var existing = _mediaFileRepository.GetFileWithPath(f.Path);
                                    var finalReason = "PENDING_AUTHOR_IMPORT";
                                    var outcome = ImportOutcome.Unmapped;

                                    if (existing != null && existing.EditionId != 0)
                                    {
                                        outcome = ImportOutcome.Ignored;
                                        finalReason = "ALREADY_TRACKED";
                                    }
                                    else if (!_diskProvider.FileExists(f.Path))
                                    {
                                        outcome = ImportOutcome.Ignored;
                                        finalReason = "FILE_MISSING";
                                    }
                                    else if (rootFolder != null && !StagingQueueFileDispositionHelper.IsFileAllowedForRootFolderType(f.Path, rootFolder))
                                    {
                                        outcome = ImportOutcome.Unmapped;
                                        finalReason = $"ROOT_FOLDER_TYPE_{rootFolder.FolderType}";
                                        if (existing == null)
                                        {
                                            QueueUnmappedBookFilePath(f.Path, pendingMeta);
                                        }
                                    }
                                    else if (existing == null)
                                    {
                                        QueueUnmappedBookFilePath(f.Path, pendingMeta);
                                    }

                                    _ingestQueue.CompleteItemWithResult(itemRef.Id, f.Path, outcome, errorMessage: finalReason, statusError: finalReason);
                                    unmatchedPaths.Add(f.Path);
                                    if (outcome == ImportOutcome.Unmapped)
                                    {
                                        totalUnmapped++;
                                    }
                                }

                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "[DRAIN] Failed to add author '{0}' ({1})", suggestion.AuthorName ?? "<unknown>", providerId ?? "<unknown>");
                            foreach (var f in filesForAuthor)
                            {
                                TerminalizeUnmatched(new UnmatchedFile { File = f, Reason = "AUTHOR_IMPORT_FAILED" }, "AUTHOR_IMPORT_FAILED");
                            }
                            continue;
                        }

                        try
                        {
                            var scopedResult = await _fileMatching.MatchFilesToLibraryAsync(filesForAuthor, restrictToAuthorId: author.Id, strictScopedRematchCtx);
                            await ProcessMatchesAsync(scopedResult.MatchedFiles);

                            foreach (var u in scopedResult.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                            {
                                TerminalizeUnmatched(u, u?.Reason ?? $"No matching edition for author '{author.Name}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "[DRAIN] Scoped rematch failed for author '{0}' (ID={1})", author?.Name ?? "<unknown>", author?.Id ?? 0);
                            foreach (var f in filesForAuthor)
                            {
                                TerminalizeUnmatched(new UnmatchedFile { File = f, Reason = "SCOPED_REMATCH_FAILED" }, "SCOPED_REMATCH_FAILED");
                            }
                        }
                    }

                    // Anything still unmatched without a suggestion
                    foreach (var u in v5Unmatched)
                    {
                        if (matchedPaths.Contains(u.File.Path) || unmatchedPaths.Contains(u.File.Path))
                        {
                            continue;
                        }

                        var hasSuggestion = u.PotentialAuthors != null &&
                                            u.PotentialAuthors.Length > 0 &&
                                            !string.IsNullOrWhiteSpace(u.PotentialAuthors[0]?.ProviderId);
                        if (!hasSuggestion)
                        {
                            TerminalizeUnmatched(u, u.Reason ?? "DRAIN_V5_NO_SUGGESTION");
                        }
                    }
                }

                    foreach (var item in claimed)
                    {
                        if (matchedPaths.Contains(item.Path) || unmatchedPaths.Contains(item.Path))
                        {
                            continue;
                        }

                        discoveredByPath.TryGetValue(item.Path, out var meta);
                        QueueUnmappedBookFilePath(item.Path, meta);
                        _ingestQueue.CompleteItemWithResult(item.Id, item.Path, ImportOutcome.Unmapped, errorMessage: "DRAIN_NO_RESULT", statusError: "DRAIN_NO_RESULT");
                        totalUnmapped++;
                    }

                    FlushPendingUnmappedBookFiles();
                }
                finally
                {
                    var claimedIds = claimed
                        .Where(item => item != null)
                        .Select(item => item.Id)
                        .Distinct()
                        .ToList();

                    if (claimedIds.Count > 0)
                    {
                        _ingestQueue.RequeueInProgress(claimedIds, "DRAIN_BATCH_ABORTED");
                    }
                }
            }
        }

        private (Dictionary<string, List<string>> Tags, int? DurationSeconds, bool ExtractionFailed) TryReadTagsFromDisk(string filePath)
        {
            try
            {
                if (_metadataTagService == null || string.IsNullOrWhiteSpace(filePath))
                {
                    return (null, null, false);
                }

                var fi = _diskProvider.GetFileInfo(filePath);
                if (fi == null || !fi.Exists)
                {
                    return (null, null, false);
                }

                var (raw, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fi);
                if (raw == null || raw.Count == 0)
                {
                    return (null, durationSeconds, false);
                }

                var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in raw)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                    {
                        continue;
                    }

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

        private static HashSet<string> BuildResultScopePathSet(IReadOnlyCollection<string> forceStagePaths)
        {
            if (forceStagePaths == null || forceStagePaths.Count == 0)
            {
                return null;
            }

            var paths = forceStagePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            return paths.Count == 0
                ? null
                : new HashSet<string>(paths, PathEqualityComparer.Instance);
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
                // Diagnostics must never affect import orchestration.
            }
        }

        private bool IsManualCommand(int commandId)
        {
            if (commandId <= 0)
            {
                return false;
            }

            try
            {
                var cmd = _commandQueueManager?.Get(commandId);
                return cmd?.Trigger == CommandTrigger.Manual;
            }
            catch
            {
                return false;
            }
        }

        private int RequeueObservedFailuresForScheduledRootScan(
            bool manualRootScan,
            RootFolder rootFolder,
            IEnumerable<string> observedPaths)
        {
            if (manualRootScan)
            {
                return 0;
            }

            var observedEligiblePaths = (observedPaths ?? Enumerable.Empty<string>())
                .Where(filePath => !string.IsNullOrWhiteSpace(filePath))
                .Where(filePath => rootFolder == null ||
                                   rootFolder.FolderType == FolderType.Mixed ||
                                   IsFileValidForRootFolderType(filePath, rootFolder))
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            return _ingestQueue.RequeueFailedPaths(observedEligiblePaths);
        }

        private async Task<StageFilesResult> StageFilesAsync(
            string path,
            RootFolder rootFolder,
            int? commandId,
            IReadOnlyCollection<string> forceStagePaths = null,
            FilterFilesType filter = FilterFilesType.Known)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new StageFilesResult();
            LogMemorySnapshot("[STAGE] start path='{0}'", path);

            // Yield so discovery can run concurrently with staging when invoked from a synchronous disk scan.
            await Task.Yield();

            if (string.IsNullOrWhiteSpace(path) || !_diskProvider.FolderExists(path))
            {
                _logger.Warn("[STAGE] Path does not exist: {0}", path);
                if (commandId.HasValue)
                {
                    ImportSessionProgressTracker.Activate(commandId.Value);
                    ImportSessionProgressTracker.MarkStagingComplete(commandId.Value);
                }
                return result;
            }

            if (commandId.HasValue)
            {
                ImportSessionProgressTracker.Activate(commandId.Value);
            }

            // Managed ebook replica paths (created for mixed audiobook+ebook colocation) should never be staged/imported.
            // Load once per stage run and skip during enumeration to avoid duplicate book files/unmapped noise.
            var replicaPathsToSkip = new HashSet<string>(PathEqualityComparer.Instance);
            try
            {
                if (rootFolder != null &&
                    rootFolder.FolderType == FolderType.Mixed)
                {
                    LogMemorySnapshot("[STAGE] before replica path load path='{0}' root='{1}'", path, rootFolder.Path);
                    replicaPathsToSkip = _mediaFileRepository.GetReplicaPathsWithBasePath(rootFolder.Path);
                    LogMemorySnapshot("[STAGE] after replica path load path='{0}' replicas={1}", path, replicaPathsToSkip.Count);
                }
            }
            catch
            {
                // Best-effort only.
            }

            // Ensure the UI import chip activates immediately when a scan begins.
            // (If SignalR is connected, this guarantees the pill appears even before the first author match.)
            try
            {
                if (commandId.HasValue)
                {
                    var displayRoot = GetSafeProgressRootName(path, rootFolder);
                    _eventAggregator.PublishEvent(new ImportStageProgressEvent(
                        ImportStage.ScanningFolders,
                        string.IsNullOrWhiteSpace(displayRoot) ? "Scanning folders" : $"Scanning '{displayRoot}'",
                        currentProgress: 0,
                        totalProgress: 0)
                    {
                        CommandId = commandId.Value,
                        CommandStatus = "started",
                        TotalAuthorFolders = _authorFoldersTotal,
                        TotalBookFolders = _bookFoldersTotal
                    });
                }
            }
            catch
            {
                // best-effort only
            }

            LogMemorySnapshot("[STAGE] before known file stat load path='{0}'", path);
            var knownFilesByPath = LoadKnownFilesByPath(path);
            LogMemorySnapshot("[STAGE] after known file stat load path='{0}' known={1}", path, knownFilesByPath.Count);
            var forceStagePathSet = forceStagePaths == null || forceStagePaths.Count == 0
                ? new HashSet<string>(PathEqualityComparer.Instance)
                : new HashSet<string>(forceStagePaths.Where(p => !string.IsNullOrWhiteSpace(p)), PathEqualityComparer.Instance);
            var observedForceStagePathSet = new HashSet<string>(PathEqualityComparer.Instance);

            // Stream file enumeration so discovery/import can begin before the full list is built.
            // Keep inserts batched for DB efficiency.
            const int initialBatchSize = 250;
            const int steadyBatchSize = 1250;
            var batchSize = initialBatchSize;

            var stagedCount = 0;
            var stagingItems = new List<IngestQueueItem>(batchSize);
            var batchUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batchAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextStagingMemoryCheckpoint = 5000;

            string BuildUnitKey(string filePath)
            {
                try
                {
                    var dir = NormalizeDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
                    var ext = (Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(dir)) return null;
                    return (dir + "|" + ext).ToLowerInvariant();
                }
                catch
                {
                    return null;
                }
            }

            bool IsBookFile(string filePath)
            {
                var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
                return MediaFileExtensions.AudioExtensions.Contains(ext) || MediaFileExtensions.TextExtensions.Contains(ext);
            }

                void FlushBatch()
                {
                    if (stagingItems.Count == 0) return;
                    var batchCount = stagingItems.Count;

                    // Only stage to ingest_queue here. Create BookFiles rows later when a file is actually attempted
                    // (imported/matched or terminalized as EditionId=0) to avoid hammering the main DB during discovery.
                    _ingestQueue.InsertBatch(stagingItems);

                    if (stagedCount <= batchCount || stagedCount >= nextStagingMemoryCheckpoint)
                    {
                        LogMemorySnapshot("[STAGE] after batch flush path='{0}' staged={1} seen={2} batch={3}",
                            path,
                            stagedCount,
                            result.SeenFilePaths.Count,
                            batchCount);

                        while (nextStagingMemoryCheckpoint <= stagedCount)
                        {
                            nextStagingMemoryCheckpoint += 5000;
                        }
                    }

                    if (commandId.HasValue)
                    {
                        try
                    {
                        ImportSessionProgressTracker.Activate(commandId.Value);
                        var (_, totalUnits) = ImportSessionProgressTracker.AddDiscoveredBookUnits(commandId.Value, batchUnitKeys);
                        var (_, totalAuthors) = ImportSessionProgressTracker.AddDiscoveredAuthorFolders(commandId.Value, batchAuthorFolders);
                        _bookFoldersTotal = totalUnits;
                        _authorFoldersTotal = totalAuthors;
                    }
                    catch
                    {
                        // best-effort progress only
                    }
                }

                stagingItems.Clear();
                batchUnitKeys.Clear();
                batchAuthorFolders.Clear();

                // After the first flush, switch to a larger batch size for throughput.
                if (batchSize != steadyBatchSize)
                {
                    batchSize = steadyBatchSize;
                    stagingItems.Capacity = Math.Max(stagingItems.Capacity, batchSize);
                }
            }

            try
            {
                var enumerationHadErrors = false;
                foreach (var filePath in EnumerateFilesSkippingExcludedFolders(path, ex =>
                         {
                             enumerationHadErrors = true;
                             _logger.Warn(ex, "[STAGE] File enumeration under '{0}' was incomplete; database cleanup will be skipped", path);
                         }))
                {
                    CheckForPauseAndWait(commandId, ImportStage.ScanningFolders);

                    if (!IsBookFile(filePath))
                    {
                        continue;
                    }

                    // Explicit file rescans are exact inventory requests, not "scan the containing
                    // folder too" requests. The folder is only the efficient enumeration boundary.
                    if (forceStagePathSet.Count > 0 && !forceStagePathSet.Contains(filePath))
                    {
                        continue;
                    }

                    if (replicaPathsToSkip.Count > 0 && replicaPathsToSkip.Contains(filePath))
                    {
                        continue;
                    }

                    // File info for size/mtime (used for import decisions and UI).
                    System.IO.Abstractions.IFileInfo fi;
                    try
                    {
                        fi = _diskProvider.GetFileInfo(filePath);
                    }
                    catch (Exception ex)
                    {
                        enumerationHadErrors = true;
                        _logger.Warn(ex, "[STAGE] Failed to stat file '{0}'; database cleanup will be skipped", filePath);
                        continue;
                    }
                    if (fi == null || !fi.Exists)
                    {
                        enumerationHadErrors = true;
                        _logger.Warn("[STAGE] File disappeared or could not be statted during scan: {0}; database cleanup will be skipped", filePath);
                        continue;
                    }

                    result.SeenFilePaths.Add(fi.FullName);

                    knownFilesByPath.TryGetValue(fi.FullName, out var knownFile);
                    var forceStage = forceStagePathSet.Contains(fi.FullName);
                    if (forceStage)
                    {
                        observedForceStagePathSet.Add(fi.FullName);
                    }

                    // Root-type mismatch suppresses matching/import, not inventory visibility. Keep the path in
                    // SeenFilePaths so DiskScanService can persist it as EditionId=0 and the user never loses it.
                    if (rootFolder != null &&
                        rootFolder.FolderType != FolderType.Mixed &&
                        !IsFileValidForRootFolderType(fi.FullName, rootFolder))
                    {
                        continue;
                    }

                    if (!ShouldStageFile(fi, knownFile, forceStage, filter))
                    {
                        result.SkippedKnownUnchangedCount++;
                        continue;
                    }

                    stagingItems.Add(new IngestQueueItem
                    {
                        Path = fi.FullName,
                        MtimeNs = MediaFileFreshness.ToUnixNanoseconds(fi.LastWriteTimeUtc),
                        SizeBytes = fi.Length,
                        TagsJson = "{}",
                        Status = "queued",
                        ForceRequeue = forceStage || filter != FilterFilesType.Known
                    });

                    stagedCount++;
                    result.StagedCount = stagedCount;
                    _stagingProcessed = stagedCount;

                    var unitKey = BuildUnitKey(fi.FullName);
                    if (!string.IsNullOrWhiteSpace(unitKey)) batchUnitKeys.Add(unitKey);

                    var authorFolder = GetAuthorFolder(fi.FullName);
                    if (!string.IsNullOrWhiteSpace(authorFolder)) batchAuthorFolders.Add(authorFolder);

                    if (stagingItems.Count >= batchSize)
                    {
                        FlushBatch();
                    }
                }

                FlushBatch();

                if (forceStagePathSet.Count > 0 && observedForceStagePathSet.Count != forceStagePathSet.Count)
                {
                    var missingForcedPaths = forceStagePathSet
                        .Except(observedForceStagePathSet, PathEqualityComparer.Instance)
                        .Take(10)
                        .ToList();

                    _logger.Warn("[STAGE] {0}/{1} force-stage paths were not observed during enumeration under '{2}'. Examples: {3}",
                        forceStagePathSet.Count - observedForceStagePathSet.Count,
                        forceStagePathSet.Count,
                        path,
                        string.Join(", ", missingForcedPaths));
                }

                _logger.Debug("[STAGE] Staged {0} files to ingest queue (streaming) in {1}ms; saw {2} files, skipped {3} known unchanged (totals so far: authors={4}, units={5})",
                    stagedCount, stopwatch.ElapsedMilliseconds, result.SeenFilePaths.Count, result.SkippedKnownUnchangedCount, _authorFoldersTotal, _bookFoldersTotal);

                result.CleanupSafe = !enumerationHadErrors;
                return result;
            }
                finally
                {
                    // If enumeration fails mid-batch, flush any staged items we already discovered so they
                    // still get queued for matching/import (and can be retried later).
                    try
                    {
                        FlushBatch();
                    }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[STAGE] Final batch flush failed during cleanup for '{0}'", path);
                }

                if (commandId.HasValue)
                {
                    try
                    {
                        ImportSessionProgressTracker.Activate(commandId.Value);
                        ImportSessionProgressTracker.MarkStagingComplete(commandId.Value);
                    }
                    catch
                    {
                        // best-effort only
                    }
                }
                }
        }

        private Dictionary<string, BookFile> LoadKnownFilesByPath(string path)
        {
            try
            {
                var knownFiles = _mediaFileRepository is MediaFileRepository concreteRepository
                    ? concreteRepository.GetFileStatsWithBasePath(path)
                    : _mediaFileRepository.GetFilesWithBasePath(path);

                knownFiles ??= new List<BookFile>();
                if (knownFiles.Count == 0)
                {
                    return new Dictionary<string, BookFile>(PathEqualityComparer.Instance);
                }

                return knownFiles
                    .Where(file => !string.IsNullOrWhiteSpace(file?.Path))
                    .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                    .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[STAGE] Failed to load known BookFiles under '{0}'; staging will process all discovered files", path);
                return new Dictionary<string, BookFile>(PathEqualityComparer.Instance);
            }
        }

        private static bool ShouldStageFile(
            System.IO.Abstractions.IFileInfo diskFile,
            BookFile knownFile,
            bool forceStage,
            FilterFilesType filter)
        {
            if (forceStage || knownFile == null)
            {
                return true;
            }

            return filter switch
            {
                FilterFilesType.None => true,
                FilterFilesType.Matched => knownFile.EditionId == 0,
                FilterFilesType.Known => !IsKnownFileUnchanged(diskFile, knownFile),
                _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unrecognized file filter")
            };
        }

        private static bool IsKnownFileUnchanged(System.IO.Abstractions.IFileInfo diskFile, BookFile knownFile)
        {
            return MediaFileFreshness.IsUnchanged(knownFile, diskFile);
        }

        private static string GetSafeProgressRootName(string path, RootFolder rootFolder)
        {
            // Prefer an explicit root-folder name (does not leak filesystem structure).
            var name = rootFolder?.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            // Fall back to leaf folder name only (never send an absolute path to UI/SignalR).
            var candidate = (rootFolder?.Path ?? path) ?? string.Empty;
            try
            {
                candidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var leaf = Path.GetFileName(candidate);
                return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
            }
            catch
            {
                return null;
            }
        }

        private IEnumerable<string> EnumerateFilesSkippingExcludedFolders(string rootPath, Action<Exception> onEnumerationError = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                yield break;
            }

            var normalizedRoot = NormalizeDirectory(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
            {
                yield break;
            }

            var stack = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stack.Push(normalizedRoot);

            bool ShouldSkipDirectory(string candidateDir)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(candidateDir))
                    {
                        return true;
                    }

                    var name = Path.GetFileName(candidateDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(name) && name.StartsWith(".", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    string relative;
                    try
                    {
                        relative = normalizedRoot.GetRelativePath(candidateDir);
                    }
                    catch
                    {
                        // Defensive: avoid following symlinks/junctions outside the configured root folder.
                        return true;
                    }

                    if (DiskScanService.ExcludedSubFoldersRegex.IsMatch(relative + Path.DirectorySeparatorChar))
                    {
                        return true;
                    }
                }
                catch
                {
                    // best-effort only
                }

                return false;
            }

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var normalizedCurrent = NormalizeDirectory(current) ?? current;
                if (!visited.Add(normalizedCurrent))
                {
                    continue;
                }

                IEnumerable<string> subdirs;
                try
                {
                    subdirs = _diskProvider.GetDirectories(current);
                }
                catch (Exception ex)
                {
                    onEnumerationError?.Invoke(ex);
                    subdirs = Enumerable.Empty<string>();
                }

                foreach (var subdir in subdirs)
                {
                    if (ShouldSkipDirectory(subdir))
                    {
                        continue;
                    }

                    stack.Push(subdir);
                }

                IEnumerable<string> files;
                try
                {
                    files = _diskProvider.GetFiles(current, recursive: false);
                }
                catch (Exception ex)
                {
                    onEnumerationError?.Invoke(ex);
                    files = Enumerable.Empty<string>();
                }

                foreach (var file in files)
                {
                    yield return file;
                }
            }
        }

        private void CheckForPauseAndWait(int? commandId, ImportStage stage)
        {
            if (!commandId.HasValue) return;
            try
            {
                var cmd = _commandQueueManager.Get(commandId.Value);
                if (cmd != null && cmd.Status == CommandStatus.Paused)
                {
                    _logger.Debug("[ORCH] Import paused, waiting to resume...");
                    PublishProgress(stage, "Import paused", commandId.Value, "paused");
                    while (cmd.Status == CommandStatus.Paused)
                    {
                        Thread.Sleep(500);
                        cmd = _commandQueueManager.Get(commandId.Value);
                    }
                    _logger.Debug("[ORCH] Import resumed, continuing...");
                    PublishProgress(stage, "Resumed", commandId.Value, "started");
                }
            }
            catch
            {
                // Ignore errors fetching command status
            }
        }

        private void PublishProgress(ImportStage stage, string message, int commandId, string status)
        {
            int current = 0, total = 0;
            switch (stage)
            {
                case ImportStage.ScanningFolders:
                    current = _stagingProcessed;
                    total = _stagingTotal;
                    break;
                case ImportStage.MatchingBooks:
                    current = _queueProcessed;
                    total = _queueProcessed + _ingestQueue.GetQueueCount();
                    break;
                default:
                    break;
            }

            var evt = new ImportStageProgressEvent(stage, message, current, total)
            {
                CommandId = commandId,
                CommandStatus = status,
                TotalAuthorFolders = _authorFoldersTotal,
                TotalBookFolders = _bookFoldersTotal,
                MatchedBooks = _booksMatched,
                FilesImported = _filesImported,
                ProcessedBookFolders = _queueProcessed
            };
            _eventAggregator.PublishEvent(evt);
        }

            private Dictionary<string, List<string>> SafeDeserializeTags(string json)
            {
                return BookImportSerializationHelper.SafeDeserializeTags(json);
            }

        private int FlushResidualStagingUnderPath(IngestQueueScanScope scanScope, RootFolder rootFolder, string logPrefix = "[ORCH-FLUSH]")
        {
            var scannedPath = scanScope?.PathPrefix;
            if (string.IsNullOrWhiteSpace(scannedPath))
            {
                return 0;
            }

            var total = 0;
            var afterId = 0;

            while (true)
            {
                var items = scanScope.GetActiveItemsForSweep(_ingestQueue, 1000, afterId);
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var item in items)
                {
                    try
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Path))
                        {
                            continue;
                        }

                        var outcome = ImportOutcome.Ignored;
                        var finalReason = "EMPTY_PATH";
                        var existing = _mediaFileRepository.GetFileWithPath(item.Path);

                        if (existing != null)
                        {
                            outcome = existing.EditionId == 0 ? ImportOutcome.Unmapped : ImportOutcome.Ignored;
                            finalReason = outcome == ImportOutcome.Unmapped
                                ? (string.IsNullOrWhiteSpace(item.Err) ? "ALREADY_UNMAPPED" : item.Err)
                                : "ALREADY_TRACKED";
                        }
                        else
                        {
                            var fileInfo = _diskProvider.GetFileInfo(item.Path);
                            if (fileInfo == null || !fileInfo.Exists)
                            {
                                outcome = ImportOutcome.Ignored;
                                finalReason = "FILE_MISSING";
                            }
                            else if (rootFolder != null && !StagingQueueFileDispositionHelper.IsFileAllowedForRootFolderType(item.Path, rootFolder))
                            {
                                finalReason = $"ROOT_FOLDER_TYPE_{rootFolder.FolderType}";
                                var ext = Path.GetExtension(item.Path);
                                var quality = MediaFileExtensions.GetQualityForExtension(ext);
                                var qualityModel = new NzbDrone.Core.Qualities.QualityModel { Quality = quality };

                                _mediaFileRepository.InsertManyIgnoreDuplicatePaths(new List<BookFile>
                                {
                                    new BookFile
                                    {
                                        Path = item.Path,
                                        Size = fileInfo.Length,
                                        Modified = fileInfo.LastWriteTime,
                                        DateAdded = DateTime.UtcNow,
                                        EditionId = 0,
                                        Quality = qualityModel,
                                        MediaInfo = new MediaInfoModel(),
                                        MediaType = BookFile.DetermineMediaType(qualityModel),
                                        AllTags = SafeDeserializeTags(item.TagsJson),
                                        DurationSeconds = item.DurationSeconds
                                    }
                                });

                                existing = _mediaFileRepository.GetFileWithPath(item.Path);
                                if (existing != null && existing.EditionId == 0)
                                {
                                    outcome = ImportOutcome.Unmapped;
                                }
                                else if (existing != null)
                                {
                                    outcome = ImportOutcome.Ignored;
                                    finalReason = "ALREADY_TRACKED";
                                }
                                else
                                {
                                    outcome = ImportOutcome.Ignored;
                                    finalReason = "UNMAPPED_CREATE_FAILED";
                                }
                            }
                            else
                            {
                                var ext = Path.GetExtension(item.Path);
                                var quality = MediaFileExtensions.GetQualityForExtension(ext);
                                var qualityModel = new NzbDrone.Core.Qualities.QualityModel { Quality = quality };

                                _mediaFileRepository.InsertManyIgnoreDuplicatePaths(new List<BookFile>
                                {
                                    new BookFile
                                    {
                                        Path = item.Path,
                                        Size = fileInfo.Length,
                                        Modified = fileInfo.LastWriteTime,
                                        DateAdded = DateTime.UtcNow,
                                        EditionId = 0,
                                        Quality = qualityModel,
                                        MediaInfo = new MediaInfoModel(),
                                        MediaType = BookFile.DetermineMediaType(qualityModel),
                                        AllTags = SafeDeserializeTags(item.TagsJson),
                                        DurationSeconds = item.DurationSeconds
                                    }
                                });

                                existing = _mediaFileRepository.GetFileWithPath(item.Path);
                                if (existing != null && existing.EditionId == 0)
                                {
                                    outcome = ImportOutcome.Unmapped;
                                    finalReason = string.IsNullOrWhiteSpace(item.Err) ? "UNMAPPED" : item.Err;
                                }
                                else if (existing != null)
                                {
                                    outcome = ImportOutcome.Ignored;
                                    finalReason = "ALREADY_TRACKED";
                                }
                                else
                                {
                                    outcome = ImportOutcome.Ignored;
                                    finalReason = "UNMAPPED_CREATE_FAILED";
                                }
                            }
                        }

                        _ingestQueue.CompleteItemWithResult(
                            item.Id,
                            item.Path,
                            outcome,
                            errorMessage: finalReason,
                            statusError: finalReason);

                        total++;
                        afterId = Math.Max(afterId, item.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "{0} Failed to flush staging item id={1} path='{2}'", logPrefix, item?.Id ?? 0, item?.Path ?? "<null>");
                    }
                }

                if (items.Count < 1000)
                {
                    break;
                }
            }

            if (total > 0)
            {
                _logger.Debug("{0} Flushed {1} residual staging items under '{2}'", logPrefix, total, NormalizeDirectory(scannedPath) ?? scannedPath);
            }

            return total;
        }

        private async Task AwaitIngestToCompleteAsync(IngestQueueScanScope scanScope, RootFolder rootFolder, int commandId, long commandStartedAt)
        {
            var scannedPath = scanScope?.PathPrefix;
            if (string.IsNullOrWhiteSpace(scannedPath))
            {
                return;
            }

            ImportCommandWorkTracker.Activate(commandId);

            var normalizedScanned = NormalizeDirectory(scannedPath) ?? scannedPath;

            var idleDelayMs = 250;
            const int maxIdleDelayMs = 2000;
            var lastActiveDiagnosticLog = DateTime.MinValue;

            while (true)
            {
                CheckForPauseAndWait(commandId, ImportStage.MatchingBooks);

                // Wait for any tracked background work (author imports + author-ready ingest) to complete.
                await ImportCommandWorkTracker.WaitForIdleAsync(commandId).ConfigureAwait(false);

                var recovered = scanScope.IsExact
                    ? 0
                    : _ingestQueue.RecoverInProgressUpdatedBefore(scannedPath, commandStartedAt, "RECOVERED_PREVIOUS_COMMAND");
                if (recovered > 0)
                {
                    _logger.Warn("[ORCH] Recovered {0} abandoned in_progress staging items from before the current command under '{1}'", recovered, normalizedScanned);
                }

                // Also ensure the staging queue for this scan is drained (no queued/in_progress items remain).
                var remainingActive = scanScope.IsExact
                    ? scanScope.GetActiveItems(_ingestQueue).Count
                    : _ingestQueue.GetActiveCountUnderPath(scannedPath);
                if (remainingActive <= 0)
                {
                    _logger.Debug("[ORCH] Ingest complete under '{0}'", normalizedScanned);
                    return;
                }

                var queuedSample = scanScope.GetQueuedItems(_ingestQueue, limit: 1);
                if ((queuedSample == null || queuedSample.Count == 0) &&
                    DateTime.UtcNow - lastActiveDiagnosticLog > TimeSpan.FromSeconds(30))
                {
                    LogActiveStagingDiagnostics(scanScope, normalizedScanned, remainingActive);
                    lastActiveDiagnosticLog = DateTime.UtcNow;
                }

                _logger.Debug("[ORCH] {0} staging items still active under '{1}' — draining", remainingActive, normalizedScanned);
                await DrainRemainingAsync(scanScope, rootFolder).ConfigureAwait(false);

                var residualAfterDrain = scanScope.IsExact
                    ? scanScope.GetActiveItems(_ingestQueue).Count
                    : _ingestQueue.GetActiveCountUnderPath(scannedPath);
                if (residualAfterDrain > 0)
                {
                    FlushResidualStagingUnderPath(scanScope, rootFolder);
                }

                await Task.Delay(idleDelayMs).ConfigureAwait(false);
                idleDelayMs = Math.Min(idleDelayMs * 2, maxIdleDelayMs);
            }
        }

        private void LogActiveStagingDiagnostics(IngestQueueScanScope scanScope, string normalizedScanned, int remainingActive)
        {
            try
            {
                var samples = scanScope.GetActiveItems(_ingestQueue, limit: 10);
                var counts = scanScope.IsExact
                    ? samples.GroupBy(item => item.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .Select(group => new IngestQueueStatusCount { Status = group.Key, Count = group.Count() })
                        .ToList()
                    : _ingestQueue.GetActiveStatusCountsUnderPath(scanScope.PathPrefix) ?? new List<IngestQueueStatusCount>();
                var countText = counts.Count == 0
                    ? "none"
                    : string.Join(", ", counts.Select(c => $"{c.Status}={c.Count}"));
                var sampleText = samples.Count == 0
                    ? "none"
                    : string.Join(" | ", samples.Select(item =>
                    {
                        var updated = item.UpdatedAt > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt).UtcDateTime.ToString("O")
                            : "unknown";
                        return $"id={item.Id} status={item.Status} err={item.Err ?? "<null>"} updated={updated} path={item.Path}";
                    }));

                _logger.Debug("[ORCH] Active staging diagnostics under '{0}': active={1}, statusCounts=[{2}], samples=[{3}]",
                    normalizedScanned, remainingActive, countText, sampleText);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[ORCH] Failed to gather active staging diagnostics under '{0}'", normalizedScanned);
            }
        }

        private string GetAuthorFolder(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(dir)) return null;
                var parent = Directory.GetParent(dir);
                return NormalizeDirectory(parent?.FullName);
            }
            catch
            {
                return null;
            }
        }

        private string NormalizeDirectory(string path)
        {
            return BookImportSerializationHelper.NormalizeDirectory(path);
        }

        private bool TryBuildAuthorImportMonitoringConfig(string authorName, IEnumerable<string> filePaths, RootFolder rootFolder, string requestedBy, out MonitoringConfig config, out string error)
        {
            var request = new SuggestedAuthorImportConfigRequest
            {
                AuthorName = authorName,
                FilePaths = filePaths,
                FixedRootFolder = rootFolder,
                QueueIfUnavailable = false,
                RequestedBy = requestedBy,
                AllowMissingRootFolder = true,
                AllowMissingMediaSettings = true,
                IncludeRootDefaultTags = true,
                PreserveDiscoveredAuthorFolder = true
            };

            return SuggestedAuthorImportCoordinator.TryBuildMonitoringConfig(
                request,
                null,
                null,
                _authorFolderMatchingService,
                out config,
                out error);
        }

        private DiscoveredFile[] FilterFilesByRootFolderType(DiscoveredFile[] files, RootFolder rootFolder)
        {
            if (rootFolder.FolderType == FolderType.Mixed)
                return files;

            return files.Where(file => IsFileValidForRootFolderType(file.Path, rootFolder)).ToArray();
        }

        private bool IsFileValidForRootFolderType(string filePath, RootFolder rootFolder)
        {
            return StagingQueueFileDispositionHelper.IsFileAllowedForRootFolderType(filePath, rootFolder);
        }

    }

    // Service interfaces that the thin orchestrator depends on
    // DTOs are now in Models/OrchestratorDtos.cs



    public interface IBookImportService
    {
        Task ImportFileAsync(string path, int bookId, string quality);
        Task ImportFileAsync(string path, int bookId, string quality, Dictionary<string, List<string>> tags);
        Task ImportFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags);
        // Import a file that already resides in the library (from root-folder scanning)
        Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags);
        Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds);
        Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds, MatchProvenance provenance);
        Task<BookImportFileResult> ImportExistingFileAsync(DiscoveredFileWithMetadata file, int bookId, int? editionId, string quality, MatchProvenance provenance);

        /// <summary>
        /// Batch inventory apply for files already in a library root. No disk mutation or transfer.
        /// </summary>
        Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId);
        Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)> files, int bookId);
        Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)> files, int bookId);

        // Legacy overload - defaults to TrackInPlace for safety (no accidental file movement)
        Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags)> files, int bookId, int? editionId, string quality);
        Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId, int? editionId, string quality);
    }
}
