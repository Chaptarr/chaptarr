using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Newtonsoft.Json;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Parser.Model;
using static NzbDrone.Core.MediaFiles.BookImport.BookImportSerializationHelper;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public interface IDiscoveryWorker
    {
        Task<int> DiscoverAndImportAuthorsStreamingAsync(RootFolder rootFolder, IngestQueueScanScope scanScope, int? commandId = null);
    }

        /// <summary>
        /// Streaming discovery worker for author folders:
        /// - Pick a small number of high-signal candidate files per folder
        /// - Try the existing local catalog first
        /// - Backfill a uniquely proven local author by stored provider ID when this media catalog is missing
        /// - Use V5 only after local routes, with guarded path evidence when configured
        /// - Leave unresolved rows queued for local-first Drain to decide and terminalize
        ///
        /// Uses bounded parallelism across folders so a slow/no-match folder doesn't delay
        /// the first successful author import.
        /// </summary>
        public class DiscoveryWorker : IDiscoveryWorker
        {
        private readonly IIngestQueueRepository _ingestQueue;
        private readonly IFileMatchingService _fileMatchingService;
        private readonly IV5MatchingService _v5MatchingService;
        private readonly IContainmentValidator _containmentValidator;
        private readonly IAuthorFolderMatchingService _authorFolderMatchingService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IConfigService _configService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
            private readonly NzbDrone.Core.Messaging.Events.IEventAggregator _eventAggregator;
            private readonly IAuthorLibraryService _authorLibraryService;
            private readonly IMediaFileService _mediaFileService;
            private readonly IDiskProvider _diskProvider;
            private readonly IManageCommandQueue _commandQueueManager;
        private readonly IMatchingUploadLogger _matchingLogger;
        private readonly Logger _logger;

        private sealed class HydratedDiscoveryUnit
        {
            public IngestQueueItem Candidate { get; init; }
            public Dictionary<string, List<string>> CandidateTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public List<DiscoveredFileWithMetadata> Files { get; init; } = new();
            public bool ExtractionFailed { get; init; }
        }

                // Prevent duplicate in-flight imports by provider ID (e.g., "hc:12345"), while
                // accumulating all discovered folder prefixes so queued items under each prefix
                // can be processed once the author import completes.
                private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _inFlightImports =
                    new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);

                // Cap parallel author imports to avoid resource exhaustion on very large libraries.
                private static readonly SemaphoreSlim _authorImportGate = new SemaphoreSlim(2, 2);

            public DiscoveryWorker(
                IIngestQueueRepository ingestQueue,
                IFileMatchingService fileMatchingService,
                IV5MatchingService v5MatchingService,
                IContainmentValidator containmentValidator,
                IAuthorFolderMatchingService authorFolderMatchingService,
                IMetadataTagService metadataTagService,
                IConfigService configService,
                IAuthorLibraryService authorLibraryService,
                IMediaFileService mediaFileService,
                IDiskProvider diskProvider,
                IAuthorService authorService,
                IBookService bookService,
                IManageCommandQueue commandQueueManager,
                NzbDrone.Core.Messaging.Events.IEventAggregator eventAggregator,
                IMatchingUploadLogger matchingLogger,
                Logger logger)
        {
            _ingestQueue = ingestQueue;
            _fileMatchingService = fileMatchingService;
            _v5MatchingService = v5MatchingService;
            _containmentValidator = containmentValidator;
            _authorFolderMatchingService = authorFolderMatchingService;
            _metadataTagService = metadataTagService;
            _configService = configService;
            _authorLibraryService = authorLibraryService;
                _mediaFileService = mediaFileService;
                _diskProvider = diskProvider;
                _authorService = authorService;
                _bookService = bookService;
                _commandQueueManager = commandQueueManager;
                _eventAggregator = eventAggregator;
                _matchingLogger = matchingLogger;
                _logger = logger;
        }

            public async Task<int> DiscoverAndImportAuthorsStreamingAsync(RootFolder rootFolder, IngestQueueScanScope scanScope, int? commandId = null)
            {
                if (rootFolder == null || string.IsNullOrWhiteSpace(rootFolder.Path) || scanScope == null || string.IsNullOrWhiteSpace(scanScope.PathPrefix)) return 0;

                // Streaming requires a command-scoped session to know when staging is complete.
                if (!commandId.HasValue)
                {
                    _logger.Warn("[DISCOVERY] DiscoverAndImportAuthorsStreamingAsync called without commandId; returning 0.");
                    return 0;
                }

                // Yield so staging can run in parallel even when called from a synchronous scan command.
                await Task.Yield();

                    var rootPath = BookImportSerializationHelper.NormalizeDirectory(rootFolder.Path);
                    if (string.IsNullOrWhiteSpace(rootPath)) return 0;

                    var matchingStrictness = BookMatchingStrictness.Balanced;
                    var usePathAsTagsFallback = true;
                    try
                    {
                        if (_configService != null)
                        {
                            matchingStrictness = _configService.BookMatchingStrictness;
                            usePathAsTagsFallback = _configService.UsePathAsTagsFallback;
                        }
                    }
                    catch
                    {
                        matchingStrictness = BookMatchingStrictness.Balanced;
                        usePathAsTagsFallback = true;
                    }

                    if (matchingStrictness == BookMatchingStrictness.Strict)
                    {
                        usePathAsTagsFallback = false;
                    }

                    _logger.Debug("[DISCOVERY] Matching strictness={0}, usePathAsTagsFallback={1}", matchingStrictness, usePathAsTagsFallback);

                    if (commandId.HasValue)
                    {
                        ImportSessionProgressTracker.Activate(commandId.Value);
                        }

                    const int discoveryQueuePageSize = 2500;
                    var resolvedAuthorFolders = 0;
                    var completedAuthorFolders = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                    var lastAttemptHighWaterByFolder = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var discoveryAfterId = 0;
                    // Keep discovery conservative during full-library scans. Author-ready ingest and
                    // SQLite writes run nearby, so wide discovery fan-out multiplies tag and match memory.
                    var discoveryGate = new SemaphoreSlim(1, 1);
                    var discoveryLoop = 0;
                    LogMemorySnapshot("[DISCOVERY] streaming start root='{0}' pageSize={1}", rootPath, discoveryQueuePageSize);

                    // Streaming loop: keep trying new/expanded folders while staging runs; finalize unmapped only after staging completes.
                    while (true)
                    {
                        discoveryLoop++;
                        CheckForPauseAndWait(commandId);

                    var staged = scanScope.GetQueuedItems(_ingestQueue, discoveryQueuePageSize, discoveryAfterId);
                    LogMemorySnapshot("[DISCOVERY] after queued fetch root='{0}' loop={1} ({2} staged)", rootPath, discoveryLoop, staged.Count);
                    if (staged.Count == 0)
                    {
                        if (commandId.HasValue && ImportSessionProgressTracker.IsStagingComplete(commandId.Value))
                        {
                            break;
                        }

                        // We reached the current tail while staging is still producing work. Start
                        // another fair pass; per-folder high-water marks suppress unchanged repeats.
                        discoveryAfterId = 0;
                        await Task.Delay(300).ConfigureAwait(false);
                        continue;
                    }

                    var reachedQueuedTail = staged.Count < discoveryQueuePageSize;
                    discoveryAfterId = staged[staged.Count - 1].Id;

                    // Group queued items by perceived author folder (parent of book folder), with disc-folder stripping.
                    var itemsByAuthorFolder = staged
                        .Select(i => new { Item = i, AuthorFolder = GetAuthorFolder(rootPath, i.Path) })
                        .Where(x => !string.IsNullOrWhiteSpace(x.AuthorFolder))
                        .GroupBy(x => x.AuthorFolder, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Select(v => v.Item).ToList(), StringComparer.OrdinalIgnoreCase);
                    LogMemorySnapshot("[DISCOVERY] after author-folder grouping root='{0}' loop={1} ({2} staged, {3} author folders)",
                        rootPath,
                        discoveryLoop,
                        staged.Count,
                        itemsByAuthorFolder.Count);

                    if (commandId.HasValue && itemsByAuthorFolder.Count > 0)
                    {
                        try
                        {
                            ImportSessionProgressTracker.Activate(commandId.Value);
                            ImportSessionProgressTracker.AddDiscoveredAuthorFolders(commandId.Value, itemsByAuthorFolder.Keys);
                        }
                        catch
                        {
                            // best-effort only
                        }
                    }

                        var progressMade = false;
                        var anyEligibleFolder = false;
                        var eligibleFolders = itemsByAuthorFolder
                            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                            .Where(kvp => !completedAuthorFolders.ContainsKey(kvp.Key))
                            .Where(kvp =>
                            {
                                var items = kvp.Value ?? new List<IngestQueueItem>();
                                if (items.Count == 0) return false;
                                var highWater = items.Max(item => item.Id);
                                if (lastAttemptHighWaterByFolder.TryGetValue(kvp.Key, out var lastHighWater) && highWater <= lastHighWater) return false;
                                return true;
                            })
                            .ToList();
                        LogMemorySnapshot("[DISCOVERY] after eligible-folder filter root='{0}' loop={1} ({2} eligible)",
                            rootPath,
                            discoveryLoop,
                            eligibleFolders.Count);

                        anyEligibleFolder = eligibleFolders.Count > 0;
                        var progressMadeFlag = 0;

                        async Task ProcessAuthorFolderAsync(KeyValuePair<string, List<IngestQueueItem>> kvp)
                        {
                            await discoveryGate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                CheckForPauseAndWait(commandId);

                                var authorFolder = kvp.Key;
                                if (string.IsNullOrWhiteSpace(authorFolder))
                                {
                                    return;
                                }

                                if (completedAuthorFolders.ContainsKey(authorFolder))
                                {
                                    return;
                                }

                                var items = kvp.Value ?? new List<IngestQueueItem>();
                                if (items.Count == 0)
                                {
                                    return;
                                }

                                // Only retry a folder when new items have been staged for it.
                                var folderHighWater = items.Max(item => item.Id);
                                if (lastAttemptHighWaterByFolder.TryGetValue(authorFolder, out var lastHighWater) && folderHighWater <= lastHighWater)
                                {
                                    return;
                                }

                        var discoveredAudiobookFiles = items.Any(i => MediaFromExtension(Path.GetExtension(i.Path)?.ToLowerInvariant() ?? string.Empty) == BookMediaType.Audiobook);
                        var discoveredEbookFiles = items.Any(i => MediaFromExtension(Path.GetExtension(i.Path)?.ToLowerInvariant() ?? string.Empty) == BookMediaType.Ebook);
                        var matchedAuthor = false;
                        var candidates = SelectMatchCandidates(items, maxCandidates: 3);
                        LogMemorySnapshot("[DISCOVERY] after candidate selection folder='{0}' ({1} items, {2} candidates)",
                            GetSafeFolderDisplayName(authorFolder),
                            items.Count,
                            candidates.Count);
                        foreach (var candidate in candidates)
                        {
                            CheckForPauseAndWait(commandId);

                            var unit = HydrateDiscoveryUnit(candidate, items, rootFolder);
                            if (unit.ExtractionFailed || unit.Files.Count == 0)
                            {
                                continue;
                            }

                            var media = MediaFromExtension(Path.GetExtension(candidate.Path).ToLowerInvariant());

                            if (await TryResolveAuthorUnitCandidateAsync(
                                    candidate,
                                    unit.CandidateTags,
                                    unit.Files,
                                    media,
                                    rootFolder,
                                    authorFolder,
                                    commandId,
                                    rootPath,
                                    usePathAsTagsFallback,
                                    discoveredAudiobookFiles,
                                    discoveredEbookFiles).ConfigureAwait(false))
                            {
                                matchedAuthor = true;
                                break;
                            }
                        }
                        LogMemorySnapshot("[DISCOVERY] after candidate attempts folder='{0}' matched={1}",
                            GetSafeFolderDisplayName(authorFolder),
                            matchedAuthor);

                                if (matchedAuthor)
                                {
                                    Interlocked.Increment(ref resolvedAuthorFolders);
                                    completedAuthorFolders.TryAdd(authorFolder, 0);
                                    lastAttemptHighWaterByFolder.TryRemove(authorFolder, out _);
                                    Interlocked.Exchange(ref progressMadeFlag, 1);

                                    try
                                    {
                                        var (processedAuthors, totalAuthors, matchedAuthors, unmatchedAuthors) = ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId.Value, authorFolder, matched: true);
                                        var (processedUnits, totalUnits) = ImportSessionProgressTracker.GetBookUnitProgress(commandId.Value);

                                        var evt = new MediaFiles.Events.ImportStageProgressEvent(
                                            MediaFiles.Events.ImportStage.DiscoveringAuthors,
                                            $"Processed {processedAuthors} of {totalAuthors} author folders",
                                            currentProgress: processedAuthors,
                                            totalProgress: totalAuthors)
                                        {
                                            CommandId = commandId.Value,
                                            CommandStatus = "started",
                                            TotalAuthorFolders = totalAuthors,
                                            ProcessedAuthorFolders = processedAuthors,
                                            MatchedAuthors = matchedAuthors,
                                            UnmatchedAuthors = unmatchedAuthors,
                                            TotalBookFolders = totalUnits,
                                            ProcessedBookFolders = processedUnits,
                                            CurrentItemName = GetSafeFolderDisplayName(authorFolder),
                                            CurrentItemType = "author-folder"
                                        };

                                        _eventAggregator.PublishEvent(evt);
                                    }
                                    catch
                                    {
                                        // best-effort only
                                    }
                                }
                                else
                                {
                                    lastAttemptHighWaterByFolder[authorFolder] = folderHighWater;
                                }
                            }
                            finally
                            {
                                discoveryGate.Release();
                            }
                        }

                        var tasks = eligibleFolders.Select(ProcessAuthorFolderAsync).ToList();
                        if (tasks.Count > 0)
                        {
                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }

                        progressMade = progressMadeFlag == 1;

                        // If staging is complete, keep looping until there are no more eligible folders to attempt.
                        // (Staging can complete before the first discovery snapshot sees all folders due to batch inserts.)
                        if (commandId.HasValue &&
                            ImportSessionProgressTracker.IsStagingComplete(commandId.Value) &&
                            reachedQueuedTail &&
                            !anyEligibleFolder)
                    {
                        break;
                    }

                    if (!progressMade)
                    {
                        // Once staging is complete, poll faster to drain remaining folders without adding latency.
                        var delay = (commandId.HasValue && ImportSessionProgressTracker.IsStagingComplete(commandId.Value)) ? 100 : 350;
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }

                // Final pass after staging complete is deliberately read-only. Remaining rows must reach
                // Drain, which retries the full local matcher before V5 and owns the final visible outcome.
                // Discovery must never terminalize a row merely because author discovery failed.
                if (commandId.HasValue && ImportSessionProgressTracker.IsStagingComplete(commandId.Value))
                {
                    try
                    {
                        const int finalSweepPageSize = 2500;
                        var afterId = 0;
                        var deferredCountsByFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        var reportedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var unitKeysToMark = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        while (true)
                        {
                            CheckForPauseAndWait(commandId);

                            var batch = scanScope.GetQueuedItems(_ingestQueue, finalSweepPageSize, afterId);
                            if (batch.Count == 0)
                            {
                                break;
                            }

                            afterId = batch[batch.Count - 1].Id;

                            foreach (var i in batch)
                            {
                                CheckForPauseAndWait(commandId);

                                var authorFolder = GetAuthorFolder(rootPath, i.Path);
                                if (string.IsNullOrWhiteSpace(authorFolder))
                                {
                                    continue;
                                }

                                // Skip folders already matched/imported; queued items under those folders will be handled
                                // by author-ready ingest.
                                if (completedAuthorFolders.ContainsKey(authorFolder))
                                {
                                    continue;
                                }

                                if (deferredCountsByFolder.TryGetValue(authorFolder, out var existingCount))
                                {
                                    deferredCountsByFolder[authorFolder] = existingCount + 1;
                                }
                                else
                                {
                                    deferredCountsByFolder[authorFolder] = 1;
                                }

                                var unitKey = BuildUnitKey(i.Path);
                                if (!string.IsNullOrWhiteSpace(unitKey))
                                {
                                    unitKeysToMark.Add(unitKey);
                                }

                                // Publish folder progress once per folder (counts are tracked separately).
                                if (reportedFolders.Add(authorFolder))
                                {
                                    try
                                    {
                                        var (processedAuthors, totalAuthors, matchedAuthors, unmatchedAuthors) = ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId.Value, authorFolder, matched: false);
                                        var (processedUnits, totalUnits) = ImportSessionProgressTracker.GetBookUnitProgress(commandId.Value);

                                        var evt = new MediaFiles.Events.ImportStageProgressEvent(
                                            MediaFiles.Events.ImportStage.DiscoveringAuthors,
                                            $"Processed {processedAuthors} of {totalAuthors} author folders",
                                            currentProgress: processedAuthors,
                                            totalProgress: totalAuthors)
                                        {
                                            CommandId = commandId.Value,
                                            CommandStatus = "started",
                                            TotalAuthorFolders = totalAuthors,
                                            ProcessedAuthorFolders = processedAuthors,
                                            MatchedAuthors = matchedAuthors,
                                            UnmatchedAuthors = unmatchedAuthors,
                                            TotalBookFolders = totalUnits,
                                            ProcessedBookFolders = processedUnits,
                                            CurrentItemName = GetSafeFolderDisplayName(authorFolder),
                                            CurrentItemType = "author-folder"
                                        };

                                        _eventAggregator.PublishEvent(evt);
                                    }
                                    catch
                                    {
                                        // best-effort only
                                    }
                                }

                                // Flush book-unit progress in bounded batches to avoid huge per-sweep allocations.
                                if (unitKeysToMark.Count >= 2000)
                                {
                                    try
                                    {
                                        ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, unitKeysToMark);
                                    }
                                    catch
                                    {
                                        // best-effort only
                                    }
                                    unitKeysToMark.Clear();
                                }
                            }

                            if (batch.Count < finalSweepPageSize)
                            {
                                break;
                            }
                        }

                        if (unitKeysToMark.Count > 0)
                        {
                            try
                            {
                                ImportSessionProgressTracker.MarkBookUnitsProcessed(commandId.Value, unitKeysToMark);
                            }
                            catch
                            {
                                // best-effort only
                            }
                        }

                        // Log summary per folder (stable, avoids holding full item lists in memory).
                        foreach (var kvp in deferredCountsByFolder.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            try
                            {
                                _logger.Debug("[DISCOVERY] No author match after candidates; deferred to local-first drain: '{0}' ({1} files)", kvp.Key, kvp.Value);
                            }
                            catch
                            {
                                // best-effort only
                            }
                        }
                    }
                    catch
                    {
                        // best-effort only
                    }
                }

                return resolvedAuthorFolders;
            }

            private async Task<bool> TryResolveAuthorUnitCandidateAsync(
                IngestQueueItem candidate,
                Dictionary<string, List<string>> tags,
                IReadOnlyList<DiscoveredFileWithMetadata> unitFiles,
                BookMediaType mediaType,
                RootFolder rootFolder,
                string authorFolder,
                int? commandId,
                string rootPath,
                bool usePathAsTagsFallback,
                bool discoveredAudiobookFiles,
                bool discoveredEbookFiles)
            {
                // Existing-library matching is authoritative whenever the local catalog can explain
                // the file. Discovery only needs V5 when local evidence cannot identify a book.
                if (await MatchesExistingLibraryAsync(candidate, tags, unitFiles).ConfigureAwait(false))
                {
                    return true;
                }

                // A known author can legitimately have no local catalog for this media type yet
                // (for example, an audiobook author encountered later in an ebook root). When
                // embedded evidence uniquely proves that local author, hydrate by the author's
                // stored provider identity instead of asking V5 to rediscover who they are.
                if (await TryBackfillKnownLocalAuthorAsync(
                        tags,
                        unitFiles,
                        mediaType,
                        rootFolder,
                        authorFolder,
                        discoveredAudiobookFiles,
                        discoveredEbookFiles).ConfigureAwait(false))
                {
                    return true;
                }

                return TryImportAuthorUnitWithOptionalPathFallback(
                    tags,
                    unitFiles,
                    candidate?.Path,
                    mediaType,
                    rootFolder,
                    authorFolder,
                    commandId,
                    rootPath,
                    usePathAsTagsFallback,
                    discoveredAudiobookFiles,
                    discoveredEbookFiles);
            }

            private async Task<bool> MatchesExistingLibraryAsync(
                IngestQueueItem candidate,
                Dictionary<string, List<string>> tags,
                IReadOnlyList<DiscoveredFileWithMetadata> unitFiles)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path) || _fileMatchingService == null)
                {
                    return false;
                }

                try
                {
                    var discovered = unitFiles?.Where(file => file != null).ToArray();
                    if (discovered == null || discovered.Length == 0)
                    {
                        discovered = new[]
                        {
                            new DiscoveredFileWithMetadata
                            {
                                Path = candidate.Path,
                                Size = candidate.SizeBytes,
                                Modified = DateTime.UtcNow,
                                AllTags = CloneTags(tags),
                                DurationSeconds = candidate.DurationSeconds
                            }
                        };
                    }

                    var result = await _fileMatchingService
                        .MatchFilesToLibraryAsync(
                            discovered,
                            restrictToAuthorId: null,
                            MatchingContextPresets.ForScanLocal())
                        .ConfigureAwait(false);

                    var matches = result?.MatchedFiles ?? Array.Empty<FileMatch>();
                    var match = matches.FirstOrDefault();
                    if (match == null || matches.Length != discovered.Length)
                    {
                        return false;
                    }

                    if (discovered.Length > 1)
                    {
                        var oneBook = matches.Select(item => item.BookId).Distinct().Count() == 1;
                        var oneAuthor = matches.Select(item => item.AuthorId).Distinct().Count() == 1;
                        var commonProof = MatchIdentityProofMembership.CommonProof(matches.Select(item => item.IdentityProof));
                        if (!oneBook || !oneAuthor || !MatchIdentityProofMembership.HasRequiredIdentity(commonProof))
                        {
                            _logger.Debug(
                                "[DISCOVERY] Local matches for unit '{0}' did not share an exact author+title field/value proof; trying another unit",
                                candidate.Path);
                            return false;
                        }
                    }

                    _logger.Debug(
                        "[DISCOVERY] Local-first match found for '{0}': '{1}' by '{2}' (edition {3}); deferring folder to drain",
                        candidate.Path,
                        match.BookTitle,
                        match.AuthorName,
                        match.EditionId);
                    return true;
                }
                catch (Exception ex)
                {
                    // A local matcher failure must not prevent server discovery. Drain will also retry
                    // local matching before it assigns a terminal outcome.
                    _logger.Debug(ex, "[DISCOVERY] Local-first match failed for '{0}'; continuing to V5", candidate.Path);
                    return false;
                }
            }

            private async Task<bool> TryBackfillKnownLocalAuthorAsync(
                Dictionary<string, List<string>> tags,
                IReadOnlyList<DiscoveredFileWithMetadata> unitFiles,
                BookMediaType mediaType,
                RootFolder rootFolder,
                string authorFolder,
                bool discoveredAudiobookFiles,
                bool discoveredEbookFiles)
            {
                if (_authorService == null || _bookService == null || _authorLibraryService == null)
                {
                    return false;
                }

                var author = FindUniqueLocalAuthorFromEmbeddedEvidence(tags, authorFolder);
                if (author == null)
                {
                    return false;
                }

                var files = unitFiles?.Where(file => file != null).ToList() ?? new List<DiscoveredFileWithMetadata>();
                if (files.Count > 1)
                {
                    var commonAuthorProof = MatchIdentityProofMembership.CommonProof(files.Select(file =>
                        MatchIdentityProofFactory.FromExpectedIdentity(
                            author.Name,
                            Array.Empty<string>(),
                            file.AllTags,
                            _containmentValidator)));
                    if (!commonAuthorProof.Has(MatchIdentityRole.Author))
                    {
                        _logger.Debug(
                            "[DISCOVERY] Known author '{0}' was not proved by the same exact field/value across unit '{1}'",
                            author.Name,
                            files[0].Path);
                        return false;
                    }
                }

                try
                {
                    var localBooks = _bookService.GetBooksByAuthor(author.Id) ?? new List<Book>();
                    if (localBooks.Any(book => book != null && book.MediaType == mediaType))
                    {
                        // This is an unknown book within an already-present media catalog. V5 may still
                        // identify it; do not turn author containment into a book match.
                        return false;
                    }

                    var providerId = AuthorIdentity.GetPreferredProviderId(author);
                    if (string.IsNullOrWhiteSpace(providerId))
                    {
                        return false;
                    }

                    string discoveredFolder = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(authorFolder) &&
                            _authorFolderMatchingService.ValidateFolderMatchesAuthor(authorFolder, author.Name))
                        {
                            discoveredFolder = NormalizeDirectory(authorFolder);
                        }
                    }
                    catch
                    {
                        // Folder assignment is optional; provider identity remains authoritative.
                    }

                    var config = CreateMonitoringConfig(
                        author.Name,
                        rootFolder,
                        discoveredAudiobookFiles,
                        discoveredEbookFiles,
                        discoveredFolder,
                        "discovery-local-backfill");

                    var hydrated = await _authorLibraryService.AddAuthorAsync(providerId, config).ConfigureAwait(false);
                    if (hydrated == null || hydrated.Id <= 0)
                    {
                        return false;
                    }

                    _logger.Debug(
                        "[DISCOVERY] Backfilled missing {0} catalog for existing author '{1}' via stored provider ID {2}; deferring files to drain",
                        mediaType,
                        hydrated.Name,
                        providerId);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[DISCOVERY] Stored-provider backfill failed for existing author '{0}'; continuing to V5", author.Name);
                    return false;
                }
            }

            private Author FindUniqueLocalAuthorFromEmbeddedEvidence(
                Dictionary<string, List<string>> tags,
                string authorFolder)
            {
                var evidence = FilterMatchableTags(tags);
                if (evidence.Count == 0 || string.IsNullOrWhiteSpace(authorFolder))
                {
                    return null;
                }

                List<Author> candidates;
                try
                {
                    candidates = _authorService.GetCandidates(GetSafeFolderDisplayName(authorFolder)) ?? new List<Author>();
                }
                catch
                {
                    return null;
                }

                var matches = candidates
                    .Where(author => author != null && author.Id > 0)
                    .Where(author => EnumerateAuthorEvidenceNames(author)
                        .Any(name => _containmentValidator.ValidateAuthorInTags(name, evidence)))
                    .Where(author =>
                    {
                        try
                        {
                            return _authorFolderMatchingService.ValidateFolderMatchesAuthor(authorFolder, author.Name);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .Take(2)
                    .ToList();

                return matches.Count == 1 ? matches[0] : null;
            }

            private static IEnumerable<string> EnumerateAuthorEvidenceNames(Author author)
            {
                if (author == null)
                {
                    yield break;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { author.Name, author.NameLastFirst }
                             .Concat(author.Aliases ?? Enumerable.Empty<string>())
                             .Concat(author.Pseudonyms ?? Enumerable.Empty<string>()))
                {
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
                    {
                        yield return name.Trim();
                    }
                }
            }

            private static string GetSafeFolderDisplayName(string folderPath)
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    return folderPath;
                }

                try
                {
                    var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var leaf = Path.GetFileName(trimmed);
                    return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
                }
                catch
                {
                    return folderPath;
                }
            }

            private void CheckForPauseAndWait(int? commandId)
            {
                if (!commandId.HasValue) return;
                try
                {
                    var cmd = _commandQueueManager.Get(commandId.Value);
                    if (cmd != null && cmd.Status == CommandStatus.Paused)
                    {
                        _logger.Debug("[DISCOVERY] Import paused, waiting to resume...");
                        while (cmd.Status == CommandStatus.Paused)
                        {
                            Thread.Sleep(500);
                            cmd = _commandQueueManager.Get(commandId.Value);
                        }
                        _logger.Debug("[DISCOVERY] Import resumed, continuing...");
                    }
                }
                catch
                {
                    // Ignore errors fetching command status
                }
            }

            private static int ExtensionRank(string filePath)
            {
                var ext = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant();
                return ext switch
                {
                    ".m4b" => 5,
                    ".m4a" => 4,
                    ".mp3" => 3,
                    ".aac" => 2,
                    ".flac" => 1,
                    _ => 0
                };
            }

            private static int TagsSignal(string tagsJson)
            {
                if (string.IsNullOrWhiteSpace(tagsJson)) return 0;
                var trimmed = tagsJson.Trim();
                if (trimmed == "{}" || trimmed == "[]") return 0;
                return trimmed.Length;
            }

            private List<IngestQueueItem> SelectMatchCandidates(List<IngestQueueItem> items, int maxCandidates)
            {
                if (items == null || items.Count == 0) return new List<IngestQueueItem>();
                if (maxCandidates <= 0) maxCandidates = 1;

                // Prefer one candidate per unit (book folder + extension) to avoid wasting attempts on tracks from the same book.
                var seenUnitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = new List<IngestQueueItem>();

                foreach (var item in items
                             .OrderByDescending(i => ExtensionRank(i.Path))
                             .ThenByDescending(i => TagsSignal(i.TagsJson))
                             .ThenByDescending(i => i.SizeBytes))
                {
                    var unitKey = BuildUnitKey(item.Path);
                    if (string.IsNullOrWhiteSpace(unitKey) || !seenUnitKeys.Add(unitKey)) continue;

                    candidates.Add(item);
                    if (candidates.Count >= maxCandidates) break;
                }

                if (candidates.Count == 0)
                {
                    candidates.Add(items[0]);
                }

            return candidates;
        }

        private HydratedDiscoveryUnit HydrateDiscoveryUnit(
            IngestQueueItem candidate,
            IReadOnlyList<IngestQueueItem> stagedItems,
            RootFolder rootFolder)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
            {
                return new HydratedDiscoveryUnit { Candidate = candidate };
            }

            var unitKey = BuildUnitKey(candidate.Path);
            var extension = Path.GetExtension(candidate.Path) ?? string.Empty;
            var paths = new List<string>();
            if (!BookCoalescingHelper.IsStandaloneUnitExtension(extension))
            {
                try
                {
                    var directory = Path.GetDirectoryName(candidate.Path);
                    if (_diskProvider != null && !string.IsNullOrWhiteSpace(directory))
                    {
                        paths.AddRange(_diskProvider.GetFiles(directory, recursive: false)
                            .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[DISCOVERY] Could not enumerate physical unit for '{0}'; using staged membership", candidate.Path);
                }
            }

            if (paths.Count == 0)
            {
                paths.AddRange((stagedItems ?? Array.Empty<IngestQueueItem>())
                    .Where(item => item != null && string.Equals(BuildUnitKey(item.Path), unitKey, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Path));
            }

            if (!paths.Any(path => string.Equals(path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(candidate.Path);
            }

            var stagedByPath = (stagedItems ?? Array.Empty<IngestQueueItem>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var files = new List<DiscoveredFileWithMetadata>();
            var candidateTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!stagedByPath.TryGetValue(path, out var item))
                {
                    item = new IngestQueueItem
                    {
                        Id = 0,
                        Path = path,
                        TagsJson = "{}"
                    };
                }

                var tags = SafeDeserializeTags(item.TagsJson);
                if (tags == null || tags.Count == 0)
                {
                    tags = TryHydrateCandidateMetadata(item, rootFolder, out var extractionFailed);
                    if (extractionFailed)
                    {
                        return new HydratedDiscoveryUnit
                        {
                            Candidate = candidate,
                            ExtractionFailed = true
                        };
                    }
                }

                long size = item.SizeBytes;
                try
                {
                    var info = _diskProvider?.GetFileInfo(path);
                    if (info != null && info.Exists)
                    {
                        size = info.Length;
                    }
                }
                catch
                {
                    // The staged size is sufficient for discovery.
                }

                var discovered = new DiscoveredFileWithMetadata
                {
                    Path = path,
                    Size = size,
                    Modified = DateTime.UtcNow,
                    AllTags = CloneTags(tags),
                    DurationSeconds = item.DurationSeconds
                };
                files.Add(discovered);
                if (string.Equals(path, candidate.Path, StringComparison.OrdinalIgnoreCase))
                {
                    candidateTags = CloneTags(tags);
                }
            }

            return new HydratedDiscoveryUnit
            {
                Candidate = candidate,
                CandidateTags = candidateTags,
                Files = files
            };
        }

        private Dictionary<string, List<string>> TryHydrateCandidateMetadata(
            IngestQueueItem candidate,
            RootFolder rootFolder,
            out bool extractionFailed)
        {
            extractionFailed = false;
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path) || _metadataTagService == null)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var fi = _diskProvider.GetFileInfo(candidate.Path);
                if (fi == null || !fi.Exists)
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var (tags, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fi);
                var json = tags == null ? "{}" : JsonConvert.SerializeObject(tags, Formatting.None);
                if (string.IsNullOrWhiteSpace(json))
                {
                    json = "{}";
                }

                if (json.Trim() == "{}" && !MediaDuration.HasDuration(durationSeconds))
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                candidate.TagsJson = json;
                candidate.DurationSeconds = durationSeconds;

                try
                {
                    if (candidate.Id > 0)
                    {
                        _ingestQueue.UpdateBatchTagsAndDuration(new List<(int Id, string TagsJson, int? DurationSeconds)>
                        {
                            (candidate.Id, json, durationSeconds)
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[DISCOVERY] Failed to persist candidate tags for '{0}'", candidate.Path);
                }

                return tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch (TagExtractionException ex)
            {
                extractionFailed = true;
                _logger.Warn(ex, "[DISCOVERY] {0} for '{1}'", TagExtractionResult.FailureReason, candidate.Path);
                try
                {
                    var (dispositionOutcome, dispositionReason) = StagingQueueFileDispositionHelper.EnsureVisibleOrIgnored(
                        candidate.Path,
                        SafeDeserializeTags(candidate.TagsJson),
                        candidate.DurationSeconds,
                        _mediaFileService,
                        _diskProvider,
                        _ => rootFolder,
                        _logger,
                        "[DISCOVERY]");
                    var finalOutcome = dispositionOutcome == ImportOutcome.Unmapped
                        ? ImportOutcome.Failed
                        : dispositionOutcome;
                    var finalReason = finalOutcome == ImportOutcome.Failed
                        ? TagExtractionResult.FailureReason
                        : dispositionReason;

                    if (candidate.Id > 0)
                    {
                        _ingestQueue.CompleteItemWithResult(
                            candidate.Id,
                            candidate.Path,
                            finalOutcome,
                            errorMessage: finalReason,
                            statusError: finalReason);
                    }
                }
                catch (Exception persistException)
                {
                    _logger.Warn(persistException, "[DISCOVERY] Failed to persist extraction failure for '{0}'", candidate.Path);
                }

                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
                // Diagnostics must never affect discovery.
            }
        }

            private string BuildUnitKey(string filePath)
            {
                try
                {
                    return BookCoalescingHelper.BuildGroupingUnitKey(filePath);
                }
                catch
                {
                    return null;
                }
            }

                private bool TryImportAuthor(
                    Dictionary<string, List<string>> tags,
                    IReadOnlyList<DiscoveredFileWithMetadata> unitFiles,
                    string samplePath,
                    BookMediaType mediaType,
                    RootFolder rootFolder,
                    string authorFolder,
                    int? commandId,
                    bool discoveredAudiobookFiles,
                    bool discoveredEbookFiles,
                    bool usedPathEvidence,
                    bool includeFileNameEvidence,
                    out bool contradictoryAuthorEvidence)
                {
            contradictoryAuthorEvidence = false;
            try
            {
                var q = CanonicalMatchInputBuilder.BuildEmbeddedQuery(tags);
                var media = mediaType == BookMediaType.Audiobook ? "audio" : "ebook";
                var matches = _v5MatchingService.SearchV5Matching(q, tags, media, includeFileNameEvidence ? samplePath : null);
                if (matches != null)
                {
                    var preview = string.Join(" | ", matches.Take(3).Select(m => $"{m.id}:{m.name}"));
                    _logger.Debug("[DISCOVERY] V5 candidates ({0}): {1}", matches.Count, preview);
                }
                var top = matches?.FirstOrDefault();
                if (top == null || string.IsNullOrWhiteSpace(top.id)) return false;

                if (!_containmentValidator.ValidateAuthorInTags(top.name, tags))
                {
                    var contradictionTags = FilterMatchableTags(tags);
                    contradictoryAuthorEvidence = matches
                        .Where(match => match != null && !string.IsNullOrWhiteSpace(match.name))
                        .Any(match => _containmentValidator.ValidateAuthorInTags(match.name, contradictionTags));
                    try
                    {
                        _matchingLogger.LogFinalDecision(samplePath,
                            "UNMATCHED",
                            $"V5_AUTHOR_NOT_IN_TAGS author='{top.name}'",
                            tags);
                    }
                    catch
                    {
                        // best-effort only
                    }
                    return false;
                }

                if (!HasHomogeneousDiscoveryIdentity(top, unitFiles, rootFolder?.Path, usedPathEvidence))
                {
                    _logger.Debug(
                        "[DISCOVERY] V5 candidate '{0}' / '{1}' was not proved by one exact author field/value and one exact title field/value across unit '{2}'",
                        top.name,
                        top.edition_title ?? top.work_title,
                        samplePath);
                    return false;
                }

                // Resolve discovered folder: walk UP from the sample file toward root, then fallback to fuzzy root-level match
                var discoveredFolder = authorFolder;
                try
                {
                    var candidate = new Author { Name = top.name };
                    var walked = _authorFolderMatchingService.FindAuthorFolderByWalkingUp(samplePath, rootFolder.Path, candidate);
                    if (!string.IsNullOrWhiteSpace(walked))
                    {
                        discoveredFolder = walked;
                    }
                    else
                    {
                        var matchesFolders = _authorFolderMatchingService.FindAuthorFolders(rootFolder.Path, candidate);
                        var best = matchesFolders?.FirstOrDefault();
                        if (best != null) discoveredFolder = best.Path;
                    }


                }
                catch { }

                // Ensure the folder we use for downstream import + ingest actually contains the scanned units.
                // Some fuzzy matchers can return a different (canonical) author folder that is NOT a parent of the
                // current folder (e.g. "Frank Herbert" vs "Frank Herbert, Bill Ransom"), which would leave items stuck in queue.
                var effectiveFolder = authorFolder;
                try
                {
                    var normAuthorFolder = NormalizeDirectory(authorFolder);
                    var normDiscovered = NormalizeDirectory(discoveredFolder);

                    if (!string.IsNullOrWhiteSpace(normDiscovered) &&
                        !string.IsNullOrWhiteSpace(normAuthorFolder) &&
                        (string.Equals(normDiscovered, normAuthorFolder, StringComparison.OrdinalIgnoreCase) ||
                         normDiscovered.IsParentPath(normAuthorFolder) ||
                         normAuthorFolder.IsParentPath(normDiscovered)))
                    {
                        effectiveFolder = normDiscovered;
                    }
                }
                catch
                {
                    effectiveFolder = authorFolder;
                }

                // Determine whether the current discovery "folder" is actually a book folder (no author folder in path).
                // This happens when a user drops a book folder directly under the root (or drops files directly under the author folder).
                var bookFolder = GetBookFolder(samplePath);
                var isBookFolderGroup = false;
                try
                {
                    var normAuthorFolder = NormalizeDirectory(authorFolder);
                    var normBookFolder = NormalizeDirectory(bookFolder);
                    isBookFolderGroup = !string.IsNullOrWhiteSpace(normAuthorFolder) &&
                                        !string.IsNullOrWhiteSpace(normBookFolder) &&
                                        string.Equals(normAuthorFolder, normBookFolder, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    isBookFolderGroup = false;
                }

                // CRITICAL: Validate that the resolved author folder name matches the V5-returned author name.
                // For true author folders, this prevents assigning "/Frank Herbert/" folder to "Brian Herbert" author.
                // For book-folder discovery (no author folder in path), folder validation is expected to fail and must not block import.
                var (folderValid, folderScore, folderReason) = _authorFolderMatchingService.ValidateFolderMatchesAuthorWithDetails(effectiveFolder, top.name);
                string discoveredAuthorFolderToPreserve = null;
                var importPrefix = effectiveFolder;

                if (folderValid)
                {
                    discoveredAuthorFolderToPreserve = effectiveFolder;
                    _logger.Debug("[DISCOVERY] Folder-author validated: '{0}' matches '{1}' ({2})", effectiveFolder, top.name, folderReason);
                }
                else
                {
                    if (!isBookFolderGroup)
                    {
                        discoveredAuthorFolderToPreserve = null;
                        importPrefix = null;

                        var folderName = string.Empty;
                        try
                        {
                            folderName = Path.GetFileName(effectiveFolder) ?? string.Empty;
                        }
                        catch
                        {
                            folderName = string.Empty;
                        }

                        _logger.Debug("[DISCOVERY] Folder-author mismatch (non-blocking): folder='{0}' vs author='{1}' (score: {2:F3}). " +
                                     "Import proceeding without folder assignment.",
                                     folderName, top.name, folderScore);

                        try
                        {
                            _matchingLogger.LogFinalDecision(samplePath,
                                "UNMATCHED",
                                $"FOLDER_AUTHOR_MISMATCH (non-blocking; path assignment skipped) score={folderScore:F3} threshold=0.900 folder='{folderName}' author='{top.name}'",
                                tags);
                        }
                        catch
                        {
                            // best-effort only
                        }
                    }
                    else
                    {
                        // Readarr-like fallback: allow imports when discovery is operating on a book folder (no author folder in path).
                        // Do NOT assign the book folder as the author's folder; only use it as the scope for matching/import-in-place.
                        discoveredAuthorFolderToPreserve = null;

                        // Default scope: the book folder itself.
                        importPrefix = NormalizeDirectory(bookFolder) ?? effectiveFolder;

                        // If the file lives directly in the root folder (bookFolder == root), scoping to the root would pull in
                        // unrelated queued files under nested author folders. In that case, scope to the single file path.
                        try
                        {
                            var normRoot = NormalizeDirectory(rootFolder?.Path);
                            var normBook = NormalizeDirectory(bookFolder);
                            if (!string.IsNullOrWhiteSpace(normRoot) &&
                                !string.IsNullOrWhiteSpace(normBook) &&
                                string.Equals(normRoot, normBook, StringComparison.OrdinalIgnoreCase))
                            {
                                importPrefix = samplePath;
                            }
                        }
                        catch
                        {
                            // best-effort only
                        }

                        _logger.Debug("[DISCOVERY] Book-folder discovery: folder '{0}' does not match author '{1}' (score: {2:F3}). " +
                                     "Importing author without folder assignment; files will be matched/tracked in place under '{3}'.",
                                     effectiveFolder, top.name, folderScore, importPrefix);
                    }
                }

                var cfg = CreateMonitoringConfig(
                    top.name,
                    rootFolder,
                    discoveredAudiobookFiles,
                    discoveredEbookFiles,
                    discoveredAuthorFolderToPreserve,
                    "discovery-worker");
                // If author already exists locally by provider ID, augment settings and publish ready event
                try
                {
                    var (prefix, rawId) = SplitProviderId(top.id);
                    var existing = _authorService.FindByProviderId(prefix, rawId);
                    if (existing != null)
                    {
                        // Apply progressive settings for the detected media types
                        var updated = _authorService.UpdateAuthorProgressiveSettings(
                            existing,
                            cfg.CreateAudiobook ? cfg.AudiobookQualityProfileId : null,
                            cfg.CreateAudiobook ? cfg.AudiobookMetadataProfileId : null,
                            cfg.CreateAudiobook ? cfg.AudiobookMonitorExisting : null,
                            cfg.CreateAudiobook ? cfg.AudiobookMonitorFuture : null,
                            cfg.CreateEbook ? cfg.EbookQualityProfileId : null,
                            cfg.CreateEbook ? cfg.EbookMetadataProfileId : null,
                            cfg.CreateEbook ? cfg.EbookMonitorExisting : null,
                            cfg.CreateEbook ? cfg.EbookMonitorFuture : null,
                            rootFolder.Path);

                        // Ensure author folder path is set for the relevant media type(s).
                        // When a canonical/generated path is stale (doesn't exist on disk), prefer the discovered on-disk folder
                        // so prefix-based queue queries can see the staged files.
                        var changed = false;
                        if (!string.IsNullOrWhiteSpace(discoveredAuthorFolderToPreserve))
                        {
                            if (cfg.CreateAudiobook)
                            {
                                var shouldUpdate = string.IsNullOrWhiteSpace(updated.AudiobookPath) ||
                                                   (!string.Equals(updated.AudiobookPath, discoveredAuthorFolderToPreserve, StringComparison.OrdinalIgnoreCase) &&
                                                    !_diskProvider.FolderExists(updated.AudiobookPath));
                                if (shouldUpdate)
                                {
                                    _logger.Debug("[DISCOVERY] Updating AudiobookPath for '{0}': '{1}' -> '{2}'",
                                        updated.Name, updated.AudiobookPath ?? "(empty)", discoveredAuthorFolderToPreserve);
                                    updated.AudiobookPath = discoveredAuthorFolderToPreserve;
                                    changed = true;
                                }
                            }

                            if (cfg.CreateEbook)
                            {
                                var shouldUpdate = string.IsNullOrWhiteSpace(updated.EbookPath) ||
                                                   (!string.Equals(updated.EbookPath, discoveredAuthorFolderToPreserve, StringComparison.OrdinalIgnoreCase) &&
                                                    !_diskProvider.FolderExists(updated.EbookPath));
                                if (shouldUpdate)
                                {
                                    _logger.Debug("[DISCOVERY] Updating EbookPath for '{0}': '{1}' -> '{2}'",
                                        updated.Name, updated.EbookPath ?? "(empty)", discoveredAuthorFolderToPreserve);
                                    updated.EbookPath = discoveredAuthorFolderToPreserve;
                                    changed = true;
                                }
                            }
                        }

                        if (changed)
                        {
                            updated = _authorService.UpdateAuthor(updated);
                        }

                            // If we're missing the media type(s) required for this root folder, backfill via AddAuthorAsync
                            // so author-restricted matching has the correct per-media local catalog.
                            var needsBackfill = false;
                            try
                            {
                                var existingBooks = _bookService.GetBooksByAuthor(updated.Id) ?? new List<Book>();
                                if (cfg.CreateAudiobook && !existingBooks.Any(b => b.MediaType == BookMediaType.Audiobook))
                                {
                                    needsBackfill = true;
                                }
                                if (cfg.CreateEbook && !existingBooks.Any(b => b.MediaType == BookMediaType.Ebook))
                                {
                                    needsBackfill = true;
                                }
                            }
                            catch
                            {
                                // best-effort only
                            }

                            if (!needsBackfill)
                            {
                                // Publish ready event so author-restricted matching can proceed
                                _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(updated));
                                _logger.Debug("[DISCOVERY] Existing author '{0}' ready → '{1}'", updated.Name, effectiveFolder);

                                // If the effective folder differs from the author's configured paths, explicitly tell ingest to process it.
                                // This prevents stuck queued items for folders like "Frank Herbert, Bill Ransom/" or other non-canonical author folders.
                                try
                                {
                                    var norm = NormalizeDirectory(importPrefix);
                                    var knownPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    if (!string.IsNullOrWhiteSpace(updated.Path)) knownPrefixes.Add(NormalizeDirectory(updated.Path));
                                    if (!string.IsNullOrWhiteSpace(updated.AudiobookPath)) knownPrefixes.Add(NormalizeDirectory(updated.AudiobookPath));
                                    if (!string.IsNullOrWhiteSpace(updated.EbookPath)) knownPrefixes.Add(NormalizeDirectory(updated.EbookPath));

                                    if (!string.IsNullOrWhiteSpace(norm) && !knownPrefixes.Contains(norm))
                                    {
                                        _eventAggregator.PublishEvent(new AuthorFolderImportReadyEvent(updated, norm));
                                    }
                                }
                                catch
                                {
                                    // best-effort only
                                }

                                return true;
                            }

                            _logger.Debug("[DISCOVERY] Existing author '{0}' missing requested media types; scheduling backfill import", updated.Name);
                        }
                    }
                catch (Exception exExist)
                {
                    _logger.Debug(exExist, "[DISCOVERY] Existing-author augmentation failed, will try import");
                }

                    // Import author if not existing — queue bounded-parallel import and return immediately.
                    // IMPORTANT: multiple on-disk author folder spellings can resolve to the same providerId.
                    // Accumulate all prefixes while the import is in-flight so no folder gets dropped on the floor.
                    var provId = top.id;
                    var normPrefix = NormalizeDirectory(importPrefix);
                    if (string.IsNullOrWhiteSpace(normPrefix))
                    {
                        normPrefix = importPrefix;
                    }

                    var prefixSet = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(normPrefix))
                    {
                        prefixSet.TryAdd(normPrefix, 0);
                    }

                        if (_inFlightImports.TryAdd(provId, prefixSet))
                        {
                            var normPrefixCopy = normPrefix;
                            var authorNameCopy = top.name;
                            var trackedCommandId = commandId;
                            var task = Task.Run(async () =>
                            {
                                await _authorImportGate.WaitAsync().ConfigureAwait(false);
                                try
                                {
                                    var added = await _authorLibraryService.AddAuthorAsync(provId, cfg).ConfigureAwait(false);
                                    if (added != null && added.Id > 0)
                                    {
                                        _logger.Debug("[DISCOVERY] Imported author '{0}' (parallel)", authorNameCopy);

                                    // Capture accumulated prefixes and remove in-flight marker.
                                    // Removing first ensures we don't miss concurrent additions while iterating.
                                    if (!_inFlightImports.TryRemove(provId, out var allPrefixes) || allPrefixes == null)
                                    {
                                        allPrefixes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                                    }

                                    // Defensive: always fire at least the original prefix if we have it.
                                    if (!string.IsNullOrWhiteSpace(normPrefixCopy))
                                    {
                                        allPrefixes.TryAdd(normPrefixCopy, 0);
                                    }

                                    foreach (var prefix in allPrefixes.Keys)
                                    {
                                        var norm = NormalizeDirectory(prefix);
                                        if (string.IsNullOrWhiteSpace(norm))
                                        {
                                            continue;
                                        }

                                        _eventAggregator.PublishEvent(new AuthorFolderImportReadyEvent(added, norm));
                                        _logger.Debug("[DISCOVERY] Fired AuthorFolderImportReadyEvent for '{0}' prefix '{1}'",
                                            added.Name, norm);
                                    }
                                }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(ex, "[DISCOVERY] Parallel import failed for {0}", provId);
                                }
                                finally
                                {
                                    _authorImportGate.Release();
                                    _inFlightImports.TryRemove(provId, out _);
                                }
                            });

                            if (trackedCommandId.HasValue)
                            {
                                ImportCommandWorkTracker.Track(trackedCommandId.Value, task);
                            }
                        }
                    else
                    {
                        // Import already in-flight — accumulate this discovered prefix for later event firing.
                        if (!string.IsNullOrWhiteSpace(normPrefix) && _inFlightImports.TryGetValue(provId, out var existing))
                        {
                            existing.TryAdd(normPrefix, 0);
                            _logger.Debug("[DISCOVERY] Import in-flight for {0}, accumulated prefix '{1}'", provId, normPrefix);
                        }
                        else
                        {
                            _logger.Warn("[DISCOVERY] Import in-flight for {0} but prefix set missing; prefix '{1}' may require rescan",
                                provId, normPrefix);
                        }
                    }

                    // Consider this author handled; matching will start on AuthorReady event
                    return true;
            }
            catch (Exception ex)
            {
                contradictoryAuthorEvidence = false;
                _logger.Debug(ex, "[DISCOVERY] TryImportAuthor failed for {0}", samplePath);
                return false;
            }
        }

	            private bool TryImportAuthorUnitWithOptionalPathFallback(
	                Dictionary<string, List<string>> tags,
	                IReadOnlyList<DiscoveredFileWithMetadata> unitFiles,
	                string samplePath,
	                BookMediaType mediaType,
	                RootFolder rootFolder,
	                string authorFolder,
	                int? commandId,
	                string rootPath,
	                bool usePathAsTagsFallback,
	                bool discoveredAudiobookFiles,
	                bool discoveredEbookFiles)
	        {
	            var effectiveTags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
	            var contradictoryAuthorEvidence = false;
	            if (effectiveTags.Count > 0 &&
	                TryImportAuthor(
                        effectiveTags,
                        unitFiles,
                        samplePath,
                        mediaType,
                        rootFolder,
                        authorFolder,
                        commandId,
                        discoveredAudiobookFiles,
                        discoveredEbookFiles,
                        usedPathEvidence: false,
                        includeFileNameEvidence: usePathAsTagsFallback,
                        out contradictoryAuthorEvidence))
	            {
	                return true;
	            }

            if (!usePathAsTagsFallback || contradictoryAuthorEvidence)
            {
                return false;
            }

            var pathTags = CanonicalMatchInputBuilder.BuildPathDerivedTags(
	                samplePath,
	                GetBookFolder(samplePath),
	                GetAuthorFolder(rootPath, samplePath));

	            var combinedTags = CloneTags(effectiveTags);
	            foreach (var pair in pathTags)
	            {
	                if (!combinedTags.TryGetValue(pair.Key, out var values))
	                {
	                    combinedTags[pair.Key] = new List<string>(pair.Value ?? new List<string>());
	                    continue;
	                }

	                foreach (var value in pair.Value ?? new List<string>())
	                {
	                    if (!values.Contains(value, StringComparer.Ordinal))
	                    {
	                        values.Add(value);
	                    }
	                }
	            }

	            return TryImportAuthor(
	                combinedTags,
	                unitFiles,
	                samplePath,
	                mediaType,
	                rootFolder,
	                authorFolder,
	                commandId,
	                discoveredAudiobookFiles,
	                discoveredEbookFiles,
	                usedPathEvidence: true,
	                includeFileNameEvidence: true,
	                out _);
	        }

        private bool HasHomogeneousDiscoveryIdentity(
            V5MatchedAuthor match,
            IReadOnlyList<DiscoveredFileWithMetadata> unitFiles,
            string rootPath,
            bool includePathEvidence)
        {
            var files = unitFiles?.Where(file => file != null).ToList() ?? new List<DiscoveredFileWithMetadata>();
            if (files.Count <= 1)
            {
                return true;
            }

            var proofs = new List<MatchIdentityProof>();
            foreach (var file in files)
            {
                var evidence = CloneTags(file.AllTags);
                if (includePathEvidence)
                {
                    var pathTags = CanonicalMatchInputBuilder.BuildPathDerivedTags(
                        file.Path,
                        GetBookFolder(file.Path),
                        GetAuthorFolder(rootPath, file.Path));
                    foreach (var pair in pathTags)
                    {
                        if (!evidence.TryGetValue(pair.Key, out var values))
                        {
                            evidence[pair.Key] = new List<string>(pair.Value ?? new List<string>());
                            continue;
                        }

                        foreach (var value in pair.Value ?? new List<string>())
                        {
                            if (!values.Contains(value, StringComparer.Ordinal))
                            {
                                values.Add(value);
                            }
                        }
                    }
                }

                proofs.Add(MatchIdentityProofFactory.FromExpectedIdentity(
                    match?.name,
                    new[] { match?.edition_title, match?.work_title },
                    evidence,
                    _containmentValidator));
            }

            return MatchIdentityProofMembership.HasRequiredIdentity(
                MatchIdentityProofMembership.CommonProof(proofs));
        }

        private static Dictionary<string, List<string>> FilterMatchableTags(Dictionary<string, List<string>> tags)
        {
            return (tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
                .Where(pair => !TagExclusionPolicy.IsExcludedFromMatching(pair.Key) && pair.Value != null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static (bool CreateAudiobook, bool CreateEbook) ResolveCreateMediaTypes(RootFolder rootFolder, bool discoveredAudiobookFiles, bool discoveredEbookFiles)
        {
            if (rootFolder?.FolderType == FolderType.Audiobook)
            {
                return (true, false);
            }

            if (rootFolder?.FolderType == FolderType.Ebook)
            {
                return (false, true);
            }

            return (discoveredAudiobookFiles, discoveredEbookFiles);
        }

        private static MonitoringConfig CreateMonitoringConfig(
            string authorName,
            RootFolder rootFolder,
            bool discoveredAudiobookFiles,
            bool discoveredEbookFiles,
            string discoveredAuthorFolder,
            string requestedBy)
        {
            var (createAudiobook, createEbook) = ResolveCreateMediaTypes(
                rootFolder,
                discoveredAudiobookFiles,
                discoveredEbookFiles);

            var config = new MonitoringConfig
            {
                AuthorName = authorName,
                QueueIfUnavailable = false,
                CreateAudiobook = createAudiobook,
                CreateEbook = createEbook,
                RequestedBy = requestedBy,
                MonitorExisting = true,
                MonitorFuture = true,
                DiscoveredAuthorFolderPath = discoveredAuthorFolder
            };

            if (rootFolder == null)
            {
                return config;
            }

            if (config.CreateAudiobook)
            {
                config.AudiobookRootFolderPath = rootFolder.Path;
                var settings = rootFolder.GetAudiobookSettings();
                if (settings != null)
                {
                    config.AudiobookQualityProfileId = settings.QualityProfileId;
                    config.AudiobookMetadataProfileId = settings.MetadataProfileId;
                    config.AudiobookMonitorExisting = settings.MonitorExisting;
                    config.AudiobookMonitorFuture = settings.MonitorFuture;
                    AddTags(config, settings.Tags);
                }
            }

            if (config.CreateEbook)
            {
                config.EbookRootFolderPath = rootFolder.Path;
                var settings = rootFolder.GetEbookSettings();
                if (settings != null)
                {
                    config.EbookQualityProfileId = settings.QualityProfileId;
                    config.EbookMetadataProfileId = settings.MetadataProfileId;
                    config.EbookMonitorExisting = settings.MonitorExisting;
                    config.EbookMonitorFuture = settings.MonitorFuture;
                    AddTags(config, settings.Tags);
                }
            }

            return config;
        }

        private static void AddTags(MonitoringConfig config, IEnumerable<int> tags)
        {
            var values = tags?.ToList();
            if (config == null || values == null || values.Count == 0)
            {
                return;
            }

            config.Tags ??= new HashSet<int>();
            foreach (var tag in values)
            {
                config.Tags.Add(tag);
            }
        }

        private static Dictionary<string, List<string>> CloneTags(Dictionary<string, List<string>> tags)
        {
            var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null)
            {
                return clone;
            }

            foreach (var kv in tags)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    continue;
                }

                clone[kv.Key] = kv.Value != null ? new List<string>(kv.Value) : new List<string>();
            }

            return clone;
        }

        private (string prefix, string id) SplitProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return ("", "");
            var idx = providerId.IndexOf(':');
            if (idx > 0)
            {
                return (providerId.Substring(0, idx), providerId.Substring(idx + 1));
            }
            // default to hardcover if no prefix provided
            return ("hc", providerId);
        }

            private string GetAuthorFolder(string filePath)
            {
                try
                {
                    var dir = Path.GetDirectoryName(filePath);
                    var parent = Directory.GetParent(dir);
                    return NormalizeDirectory(parent?.FullName);
                }
                catch { return null; }
            }

                private string GetAuthorFolder(string rootPath, string filePath)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(filePath)) return null;

                        var normRoot = NormalizeDirectory(rootPath);
                        var bookFolder = GetBookFolder(filePath);
                        if (string.IsNullOrWhiteSpace(bookFolder))
                        {
                            return GetAuthorFolder(filePath);
                        }

                        // If a file is directly under the root folder, treat the root as the "author folder" group
                        // (it will be handled by book-folder discovery fallbacks and/or explicit prefix events).
                        if (!string.IsNullOrWhiteSpace(normRoot) &&
                            string.Equals(bookFolder, normRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            return bookFolder;
                        }

                        var authorFolder = NormalizeDirectory(Path.GetDirectoryName(bookFolder) ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(authorFolder))
                        {
                            return GetAuthorFolder(filePath);
                        }

                        // If files are directly under the author folder, the computed parent will be the root.
                        if (!string.IsNullOrWhiteSpace(normRoot) &&
                            string.Equals(authorFolder, normRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            return bookFolder;
                        }

                        if (!string.IsNullOrWhiteSpace(normRoot))
                        {
                            var rootPrefix = normRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (!authorFolder.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(authorFolder, normRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                // Outside this root; fall back to simple heuristic
                                return GetAuthorFolder(filePath);
                            }
                        }

                        return authorFolder;
                    }
                    catch
                    {
                        return GetAuthorFolder(filePath);
                    }
                }

            private static bool IsDiscOnlyFolderName(string folderName)
            {
                if (string.IsNullOrWhiteSpace(folderName)) return false;
                var name = folderName.Trim();

                // Common disc folder names: CD1, CD01, Disc 2, Disk_03, etc.
                // Deliberately excludes "Part" because it appears frequently in real book titles.
                var lowered = name.ToLowerInvariant()
                    .Replace('_', ' ')
                    .Replace('-', ' ')
                    .Replace('.', ' ')
                    .Trim();

                var pieces = lowered.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length == 1)
                {
                    // e.g., "cd1", "disc02"
                    if ((lowered.StartsWith("cd") || lowered.StartsWith("disc") || lowered.StartsWith("disk")) && lowered.Length > 2)
                    {
                        var digits = new string(lowered.SkipWhile(c => !char.IsDigit(c)).ToArray());
                        return digits.Length > 0 && digits.All(char.IsDigit);
                    }
                    return false;
                }

                if (pieces.Length == 2)
                {
                    var head = pieces[0];
                    var tail = pieces[1];
                    if ((head == "cd" || head == "disc" || head == "disk") && tail.All(char.IsDigit))
                    {
                        return true;
                    }
                }

                return false;
            }

            private string GetBookFolder(string filePath)
            {
                try
                {
                    var dir = Path.GetDirectoryName(filePath);
                    var norm = NormalizeDirectory(dir);
                    if (string.IsNullOrWhiteSpace(norm)) return norm;

                    try
                    {
                        var leaf = new DirectoryInfo(norm).Name;
                        if (IsDiscOnlyFolderName(leaf))
                        {
                            var parent = NormalizeDirectory(Path.GetDirectoryName(norm) ?? string.Empty);
                            if (!string.IsNullOrWhiteSpace(parent))
                            {
                                return parent;
                            }
                        }
                    }
                    catch
                    {
                        // ignore and return normalized dir
                    }

                    return norm;
                }
                catch
                {
                    return null;
                }
            }

        private BookMediaType MediaFromExtension(string ext)
        {
            return MediaFileExtensions.AudioExtensions.Contains(ext) ? BookMediaType.Audiobook : BookMediaType.Ebook;
        }

    }
}
