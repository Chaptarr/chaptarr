using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;

using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Authors;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Parser;
using System.Text.RegularExpressions;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// File matching service that uses batched FTS search to match files to existing library entries.
    /// Uses a two-phase approach: search authors first, then their editions.
    /// </summary>
    public class FileMatchingService : IFileMatchingService
    {
        private readonly IMatchingUploadLogger _matchingLogger;
        private readonly IV5MatchingService _v5MatchingService;
        private readonly IContainmentValidator _containmentValidator;
        private readonly Logger _logger;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IManageCommandQueue _commandQueue;
        private readonly IAuthorFolderMatchingService _authorFolderMatchingService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigService _configService;
        private readonly IAuthorService _authorService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IEditionFtsRepository _editionFtsRepository;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IEditionRepository _editionRepository;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;

        private readonly AsyncLocal<RejectionCaptureContext> _rejectionCapture = new AsyncLocal<RejectionCaptureContext>();
        private readonly AsyncLocal<IMatchingTraceSink> _matchingTraceSink = new AsyncLocal<IMatchingTraceSink>();

        private sealed class RejectionCaptureContext
        {
            public RejectionCaptureContext(string scope, List<CandidateRejection> rejections, int maxRejections)
            {
                Scope = scope;
                Rejections = rejections;
                MaxRejections = maxRejections;
            }

            public string Scope { get; }
            public List<CandidateRejection> Rejections { get; }
            public int MaxRejections { get; }
        }

        private sealed class HolyGrailEvaluation
        {
            public FileMatch Match { get; set; }
            public Dictionary<string, List<string>> WinningTags { get; set; }
            public Dictionary<int, Book> BooksById { get; set; }
            public bool PathFallbackUsed { get; set; }
            public string PathFallbackSuppressedReason { get; set; }
        }

        private enum EmbeddedEvidenceDisposition
        {
            NoUsableEvidence,
            InsufficientEvidence,
            ContradictoryEvidence,
            Matched
        }

        private sealed class MatchDecision
        {
            public FileMatch Match { get; set; }
            public Dictionary<string, List<string>> ProofTags { get; set; }
            public bool PathFallbackUsed { get; set; }
            public string PathFallbackSuppressedReason { get; set; }
            public bool MatchedPinnedFirstCrack { get; set; }
            public string MatchedNarrator { get; set; }
            public string PinnedTargetResult { get; set; }
            public string PinnedTargetFailure { get; set; }
            public bool TriedUnscopedFallback { get; set; }
            public bool SkippedScopedMatch { get; set; }
            public bool MatchedViaV5Recovery { get; set; }
            public AuthorSuggestion PotentialAuthor { get; set; }
            public string UnmatchedReason { get; set; }
            public List<CandidateRejection> Rejections { get; set; }
            public Dictionary<int, Book> BooksById { get; set; }
        }

        private sealed class V5SuggestionInfo
        {
            public string ProviderId { get; set; }
            public string AuthorName { get; set; }
            public double Confidence { get; set; }
            public string BookProviderId { get; set; }
            public string BookTitle { get; set; }
            public string EditionHardcoverId { get; set; }
            public string EditionTitle { get; set; }
            public string Reason { get; set; }
        }

        private sealed class ProofMembership
        {
            public bool Passes { get; set; }
            public Dictionary<string, List<string>> ProofTags { get; set; }
            public MatchIdentityProof IdentityProof { get; set; }
            public string Reason { get; set; }
        }

        private sealed class SuccessfulProbeMatch
        {
            public DiscoveredFileWithMetadata File { get; set; }
            public Dictionary<string, List<string>> Tags { get; set; }
            public MatchDecision Decision { get; set; }
            public MatchIdentityProof Evidence { get; set; }
            public string LogicalWorkKey { get; set; }
        }


        private sealed class HolyGrailAttemptResult
        {
            public FileMatch Match { get; set; }
            public UnmatchedFile UnmatchedFile { get; set; }
            public HolyGrailEvaluation Evaluation { get; set; }
            public List<CandidateRejection> Rejections { get; set; }
        }

        private sealed class FtsSmokeTestResult
        {
            public EditionFtsMatch Match { get; set; }
            public string MatchedVia { get; set; }
            public MatchProvenance Provenance { get; set; }
            public MatchIdentityProof IdentityProof { get; set; }
            public Dictionary<int, Book> BooksById { get; set; }
        }

        private sealed class PinnedFirstCrackEvaluation
        {
            public FileMatch Match { get; set; }
            public string MatchedNarrator { get; set; }
            public string FailureReason { get; set; }
        }

        private T RunWithRejectionCapture<T>(string scope, Func<T> matchFunc, List<CandidateRejection> rejections, int maxRejections = 50)
        {
            if (matchFunc == null)
            {
                return default;
            }

            rejections ??= new List<CandidateRejection>();

            var prior = _rejectionCapture.Value;
            _rejectionCapture.Value = new RejectionCaptureContext(scope, rejections, maxRejections);
            try
            {
                return matchFunc();
            }
            finally
            {
                _rejectionCapture.Value = prior;
            }
        }

        private static List<CandidateRejection> NullIfEmpty(List<CandidateRejection> rejections)
        {
            return rejections != null && rejections.Count > 0 ? rejections : null;
        }

        private static List<CandidateRejection> MergeRejections(params IEnumerable<CandidateRejection>[] rejectionSets)
        {
            if (rejectionSets == null || rejectionSets.Length == 0)
            {
                return null;
            }

            var merged = new List<CandidateRejection>();
            foreach (var set in rejectionSets)
            {
                if (set == null)
                {
                    continue;
                }

                merged.AddRange(set.Where(r => r != null));
            }

            return NullIfEmpty(merged);
        }

        private static List<CandidateRejection> BuildGroupedDurationGateRejections(
            IEnumerable<CandidateRejection> sourceRejections,
            IReadOnlyList<DiscoveredFileWithMetadata> files,
            int? observedSeconds)
        {
            if (!observedSeconds.HasValue || sourceRejections == null)
            {
                return null;
            }

            var fileCount = files?.Count ?? 0;
            var duplicateSuspect = HasDuplicateDurationSize(files);
            var grouped = new List<CandidateRejection>();

            foreach (var rejection in sourceRejections)
            {
                if (!IsDurationGateRejection(rejection))
                {
                    continue;
                }

                grouped.Add(new CandidateRejection
                {
                    Phase = rejection.Phase,
                    EditionId = rejection.EditionId,
                    Score = rejection.Score,
                    TitleSnippet = rejection.TitleSnippet,
                    Reason = "GROUP_DURATION_GATE",
                    FallbackDisposition = rejection.FallbackDisposition,
                    Detail = AppendDiagnosticDetail(
                        rejection.Detail,
                        $"grouped=true files={fileCount.ToString(CultureInfo.InvariantCulture)}{(duplicateSuspect ? " duplicateSuspect=true" : string.Empty)}")
                });
            }

            return NullIfEmpty(grouped);
        }

        private static bool IsDurationGateRejection(CandidateRejection rejection)
        {
            if (rejection == null || string.IsNullOrWhiteSpace(rejection.Reason))
            {
                return false;
            }

            return string.Equals(rejection.Reason, "NEAR_EXACT_DURATION_GATE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rejection.Reason, "NARRATOR_MISSING_DURATION_GATE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDuplicateDurationSize(IReadOnlyList<DiscoveredFileWithMetadata> files)
        {
            return files != null &&
                   files.Where(f => f != null && f.Size > 0 && f.DurationSeconds.GetValueOrDefault() > 0)
                        .GroupBy(f => new { f.Size, DurationSeconds = f.DurationSeconds.Value })
                        .Any(g => g.Count() > 1);
        }

        private static string AppendDiagnosticDetail(string detail, string addition)
        {
            if (string.IsNullOrWhiteSpace(addition))
            {
                return detail;
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                return addition;
            }

            return $"{detail} {addition}";
        }

        private bool IsMatchingTraceEnabled()
        {
            return _matchingTraceSink.Value != null;
        }

        private void RecordTrace(
            string eventType,
            string phase = null,
            EditionFtsMatch candidate = null,
            string reason = null,
            string detail = null,
            string filePath = null,
            Dictionary<string, string> data = null)
        {
            var sink = _matchingTraceSink.Value;
            if (sink == null)
            {
                return;
            }

            try
            {
                sink.Record(new MatchingTraceEvent
                {
                    EventType = eventType,
                    Phase = phase,
                    FilePath = filePath,
                    EditionId = candidate?.EditionId,
                    BookId = candidate?.BookId,
                    AuthorId = candidate?.AuthorId,
                    Score = candidate?.MatchScore,
                    Title = candidate?.EditionTitle ?? candidate?.BookTitle,
                    Reason = reason,
                    Detail = detail,
                    Data = data
                });
            }
            catch
            {
                // Trace sinks are diagnostics only. Matching decisions must never depend on them.
            }
        }

        private void RecordFtsTrace(EditionFtsTraceEvent evt, string phase, string filePath)
        {
            var sink = _matchingTraceSink.Value;
            if (sink == null || evt == null)
            {
                return;
            }

            var eventType = evt.EventType switch
            {
                "input" => "fts_input",
                "query" => $"fts_{evt.Step}_query",
                "candidate" => $"fts_{evt.Step}_candidate",
                "summary" => $"fts_{evt.Step}_summary",
                "completed" => "fts_completed",
                _ => "fts_diagnostic"
            };

            try
            {
                sink.Record(new MatchingTraceEvent
                {
                    EventType = eventType,
                    Phase = phase,
                    FilePath = filePath,
                    EditionId = evt.EditionId,
                    BookId = evt.BookId,
                    AuthorId = evt.AuthorId,
                    Score = evt.Score,
                    Title = evt.EditionTitle ?? evt.BookTitle,
                    Rank = evt.RawRank,
                    DistinctBookRank = evt.DistinctBookRank,
                    ElapsedMilliseconds = evt.ElapsedMilliseconds,
                    TotalElapsedMilliseconds = evt.TotalElapsedMilliseconds,
                    ResultCount = evt.ResultCount,
                    DistinctBookCount = evt.DistinctBookCount,
                    Terms = evt.Terms?.ToList(),
                    Columns = evt.Columns,
                    Query = evt.Query,
                    Data = new Dictionary<string, string>
                    {
                        ["step"] = evt.Step ?? string.Empty,
                        ["resultSource"] = evt.ResultSource ?? string.Empty,
                        ["bookTitle"] = evt.BookTitle ?? string.Empty,
                        ["authorName"] = evt.AuthorName ?? string.Empty,
                        ["narratorNames"] = evt.NarratorNames ?? string.Empty,
                        ["publisher"] = evt.Publisher ?? string.Empty,
                        ["fieldKey"] = evt.FieldKey ?? string.Empty,
                        ["broadRecallScore"] = evt.BroadRecallScore?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                        ["stage2TitleScore"] = evt.Stage2TitleScore?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                        ["stage2DetailScore"] = evt.Stage2DetailScore?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                        ["stage2TitleSourceFields"] = evt.Stage2TitleSourceFields ?? string.Empty,
                        ["stage2DetailSourceFields"] = evt.Stage2DetailSourceFields ?? string.Empty,
                        ["stage2MatchedFieldCount"] = evt.Stage2MatchedFieldCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ["stage2TitleFieldCount"] = evt.Stage2TitleFieldCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ["stage2DetailFieldCount"] = evt.Stage2DetailFieldCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ["durationSeconds"] = evt.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ["releaseDate"] = evt.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                        ["readingFormatId"] = evt.ReadingFormatId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                    }
                });
            }
            catch
            {
                // Trace sinks are diagnostics only. Matching decisions must never depend on them.
            }
        }

        private static string TruncateRejectionDetail(string value, int maxLen)
        {
            if (string.IsNullOrEmpty(value) || maxLen <= 0)
            {
                return value;
            }

            return value.Length > maxLen ? value.Substring(0, maxLen) + "..." : value;
        }

        private void RecordCapturePhaseRejection(string phase, string reason, string detail = null)
        {
            var ctx = _rejectionCapture.Value;
            if (ctx?.Rejections == null)
            {
                return;
            }

            if (ctx.Rejections.Count >= ctx.MaxRejections)
            {
                return;
            }

            var rejectionPhase = phase;
            if (!string.IsNullOrWhiteSpace(ctx.Scope))
            {
                rejectionPhase = $"{ctx.Scope}/{phase}";
            }

            ctx.Rejections.Add(new CandidateRejection
            {
                Phase = rejectionPhase,
                EditionId = null,
                Score = null,
                TitleSnippet = null,
                Reason = reason,
                Detail = TruncateRejectionDetail(detail, 200)
            });
        }

        private static readonly IReadOnlySet<string> HolyGrailLeftoverHardNoiseTokens =
            Services.BookImportUnitGroupingService.HardNoiseTokens;

        private static readonly IReadOnlySet<string> HolyGrailLeftoverStructuralTokens =
            Services.BookImportUnitGroupingService.StructuralTokens;

        // When the only unexplained leftovers are numeric tokens, allow them if the evidence field
        // clearly contains packaging markers (e.g., "Volume 1", "Disc 2", "Track 01").
        // This prevents common audiobook splitting metadata from causing unnecessary V5 fallbacks.
        private static readonly IReadOnlySet<string> HolyGrailLeftoverNumericPackagingTokens =
            Services.BookImportUnitGroupingService.NumericPackagingTokens;

        private static readonly HashSet<string> SeriesEvidenceNonSeriesNumericKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TRACKNUMBER", "DISCNUMBER", "TOTALTRACKS", "TOTALDISCS",
            "TRCK", "TPOS", "TRKN"
        };

        private static readonly IReadOnlySet<string> SeriesPositionDecorationTokens =
            SeriesPositionTokenHelper.PositionDecorationTokens;

        private static readonly HashSet<string> SeriesNameNoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "the", "of", "in", "on", "at", "by", "for", "with", "to", "from", "as",
            "series", "saga", "chronicles", "collection"
        };

        /// <summary>
        /// Single method that checks ALL exclusion lists: trash keys, comment keys, and matching-exclude keys.
        /// Use this everywhere a tag key needs to be checked for exclusion from matching.
        /// </summary>
        internal static bool IsExcludedFromMatching(string key) => TagExclusionPolicy.IsExcludedFromMatching(key);

        private static readonly Regex AsinRegex = new Regex(@"\bB[0-9A-Z]{9}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public FileMatchingService(
            IMatchingUploadLogger matchingLogger,
            IV5MatchingService v5MatchingService,
            IContainmentValidator containmentValidator,
            IPendingAuthorImportService pendingAuthorImportService,
            IManageCommandQueue commandQueue,
            IAuthorFolderMatchingService authorFolderMatchingService,
            IRootFolderService rootFolderService,
            IConfigService configService,
            IAuthorService authorService,
            IEventAggregator eventAggregator,
            IAuthorLibraryService authorLibraryService,
            IEditionFtsRepository editionFtsRepository,
            IBookService bookService,
            IEditionService editionService,
            IEditionRepository editionRepository,
            IMediaInfoExtractor mediaInfoExtractor,
            Logger logger)
        {
            _matchingLogger = matchingLogger;
            _v5MatchingService = v5MatchingService;
            _containmentValidator = containmentValidator;
            _pendingAuthorImportService = pendingAuthorImportService;
            _commandQueue = commandQueue;
            _authorFolderMatchingService = authorFolderMatchingService;
            _rootFolderService = rootFolderService;
            _configService = configService;
            _authorService = authorService;
            _eventAggregator = eventAggregator;
            _authorLibraryService = authorLibraryService;
            _editionFtsRepository = editionFtsRepository;
            _bookService = bookService;
            _editionService = editionService;
            _editionRepository = editionRepository;
            _mediaInfoExtractor = mediaInfoExtractor;
            _logger = logger;
        }

        private BookMatchingStrictness GetConfiguredMatchingStrictness()
        {
            try
            {
                return _configService?.BookMatchingStrictness ?? BookMatchingStrictness.Balanced;
            }
            catch
            {
                return BookMatchingStrictness.Balanced;
            }
        }

        private bool IsConfiguredPathAsTagsFallbackEnabled(BookMatchingStrictness strictness)
        {
            if (strictness == BookMatchingStrictness.Strict)
            {
                return false;
            }

            try
            {
                return _configService?.UsePathAsTagsFallback ?? true;
            }
            catch
            {
                return true;
            }
        }

        private static EmbeddedEvidenceDisposition ClassifyEmbeddedEvidence(
            bool hasUsableEvidence,
            bool matched,
            bool hasContradictoryEvidence,
            IReadOnlyCollection<CandidateRejection> rejections)
        {
            if (matched)
            {
                return EmbeddedEvidenceDisposition.Matched;
            }

            if (!hasUsableEvidence)
            {
                return EmbeddedEvidenceDisposition.NoUsableEvidence;
            }

            if (hasContradictoryEvidence || HasContradictoryFallbackDisposition(rejections))
            {
                return EmbeddedEvidenceDisposition.ContradictoryEvidence;
            }

            return EmbeddedEvidenceDisposition.InsufficientEvidence;
        }

        private static bool HasContradictoryFallbackDisposition(IEnumerable<CandidateRejection> rejections)
        {
            return rejections != null && rejections.Any(rejection =>
                rejection != null &&
                string.Equals(rejection.FallbackDisposition, "contradictory", StringComparison.OrdinalIgnoreCase));
        }

        [Obsolete("Use the 3-parameter overload and explicitly specify forDownloads")]
        public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
        {
            return MatchFilesToLibraryAsync(filesWithMetadata, null, false);
        }

        [Obsolete("Use the 3-parameter overload and explicitly specify forDownloads")]
        public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
        {
            return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, false);
        }

        public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
        {
            var ctx = forDownloads
                ? MatchingContextPresets.ForDownloaded(false, allowPathFallback: true)
                : MatchingContextPresets.ForDirectDefault();

            return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, ctx);
        }

        public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context)
        {
            var totalStopwatch = Stopwatch.StartNew();
            context ??= new MatchingContext();
            var cancellationToken = context.CancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            _matchingTraceSink.Value = context.TraceSink;

            var allowV5Identification = context.AllowV5Identification;
            var allowAuthorImport = context.AllowAuthorImport;
            var deferUnmatchedToAuthorReady = context.DeferUnmatchedToAuthorReady;
            var allowUnscopedFallback = context.AllowUnscopedFallback;
            var disablePathFallback = context.DisablePathFallback;
            var perFileMatching = context.PerFileMatching;
            var allowGroupedV5Suggestions = context.AllowGroupedV5Suggestions;
            var targetBookIds = context.TargetBookIds?
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var hardAllowedBookIds = context.HardAllowedBookIds?
                .Where(id => id > 0)
                .Distinct()
                .ToHashSet();

            var pinnedFirstCrack = targetBookIds?.Count == 1
                ? TryBuildPinnedEditionFirstCrackTarget(targetBookIds[0], restrictToAuthorId)
                : null;

            var matchingStrictness = GetConfiguredMatchingStrictness();
            var usePathAsTagsFallback = !disablePathFallback && IsConfiguredPathAsTagsFallbackEnabled(matchingStrictness);

            if (filesWithMetadata == null || filesWithMetadata.Length == 0)
            {
                _logger.Debug("[FILE-FLOW] No files provided for matching");
                return Task.FromResult(new FileMatchResult
                {
                    MatchedFiles = new FileMatch[0],
                    UnmatchedFiles = new UnmatchedFile[0]
                });
            }

            // Back-compat log label: "forDownloads" historically meant "per-file matching, no author gating".
            var forDownloads = perFileMatching;
            _logger.Debug("[MATCH-START] forDownloads={0}, fileCount={1}, restrictToAuthor={2}, allowV5={3}, allowImport={4}, defer={5}, unscopedFallback={6}",
                forDownloads, filesWithMetadata.Length, restrictToAuthorId.HasValue,
                allowV5Identification, allowAuthorImport, deferUnmatchedToAuthorReady, allowUnscopedFallback);
            _logger.Debug("[MATCH-SETTINGS] disablePathFallback={0}", disablePathFallback);
            _logger.Debug("[MATCH-SETTINGS] strictness={0}, usePathAsTagsFallback={1}", matchingStrictness, usePathAsTagsFallback);
            if (IsMatchingTraceEnabled())
            {
                RecordTrace("match_start", data: new Dictionary<string, string>
                {
                    ["fileCount"] = filesWithMetadata.Length.ToString(CultureInfo.InvariantCulture),
                    ["restrictToAuthorId"] = restrictToAuthorId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ["allowV5"] = allowV5Identification.ToString(),
                    ["allowImport"] = allowAuthorImport.ToString(),
                    ["defer"] = deferUnmatchedToAuthorReady.ToString(),
                    ["unscopedFallback"] = allowUnscopedFallback.ToString(),
                    ["perFile"] = perFileMatching.ToString(),
                    ["groupedV5Suggestions"] = allowGroupedV5Suggestions.ToString(),
                    ["disablePathFallback"] = disablePathFallback.ToString(),
                    ["strictness"] = matchingStrictness.ToString(),
                    ["usePathAsTagsFallback"] = usePathAsTagsFallback.ToString(),
                    ["targetBookIds"] = targetBookIds == null ? string.Empty : string.Join(",", targetBookIds),
                    ["hardAllowedBookIds"] = hardAllowedBookIds == null ? string.Empty : string.Join(",", hardAllowedBookIds.OrderBy(id => id))
                });
            }

            var result = new FileMatchResult();
            var v5QuestionsWithoutSuggestion = new HashSet<string>(StringComparer.Ordinal);

                // Grouped matching: one match per physical unit.
                // Standalone book containers (ebooks) default to one file per unit.
                // Audio containers can still be chapterized sets, so they group by folder + extension.
                // Build tags from spread samples + consensus so we don't trust only the first file.
                var groupedMatched = new List<FileMatch>();
            var groupedUnmatched = new List<UnmatchedFile>();
            var shortCircuitedAny = false;

                // When matching is author-restricted (post-import ingest), an author folder may legally contain multiple
                // distinct single-file books directly under the author root (no per-book folder). In that case, grouping by
                // folder+extension would collapse multiple books into a single unit and "steal" identity (wrong title/filename),
                // causing incorrect matches. Split those author-root files into per-file units.
                HashSet<string> restrictedAuthorFolders = null;
                string restrictedAuthorName = null;
                Dictionary<string, bool> directUnderAuthorFolderCache = null;
                if (restrictToAuthorId.HasValue)
                {
                    try
                    {
                        var a = _authorService?.GetAuthor(restrictToAuthorId.Value);
                        if (a != null)
                        {
                            restrictedAuthorName = a.Name;
                            restrictedAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (!string.IsNullOrWhiteSpace(a.Path)) restrictedAuthorFolders.Add(NormalizeDirectory(a.Path));
                            if (!string.IsNullOrWhiteSpace(a.AudiobookPath)) restrictedAuthorFolders.Add(NormalizeDirectory(a.AudiobookPath));
                            if (!string.IsNullOrWhiteSpace(a.EbookPath)) restrictedAuthorFolders.Add(NormalizeDirectory(a.EbookPath));
                            if (restrictedAuthorFolders.Count == 0) restrictedAuthorFolders = null;
                            directUnderAuthorFolderCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    catch
                    {
                        restrictedAuthorFolders = null;
                        restrictedAuthorName = null;
                        directUnderAuthorFolderCache = null;
                    }
                }

                IReadOnlyDictionary<string, int> directAuthorIdentitySupport = null;
                ISet<string> directAuthorExcludedIdentityValues = null;
                if (restrictToAuthorId.HasValue && !string.IsNullOrWhiteSpace(restrictedAuthorName))
                {
                    var normalizedRestrictedAuthorName = BookImportUnitGroupingService.NormalizeIdentityValue(restrictedAuthorName);
                    if (!string.IsNullOrWhiteSpace(normalizedRestrictedAuthorName))
                    {
                        directAuthorExcludedIdentityValues = new HashSet<string>(StringComparer.Ordinal)
                        {
                            normalizedRestrictedAuthorName
                        };
                    }

                    directAuthorIdentitySupport = BookImportUnitGroupingService.BuildIdentityPairSupport(filesWithMetadata, directAuthorExcludedIdentityValues);
                }

                // Group by unit key plus any extra author-root split key.
                // A "book unit" is usually folder + extension, except standalone book
                // containers which are always one file per unit.
                // Direct-under-author-root files can still split further via SubKey when needed.
                var groups = filesWithMetadata
                    .GroupBy(f =>
                    {
                        var folder = GetBookFolder(f.Path);
                        var ext = Path.GetExtension(f.Path).ToLowerInvariant();
                        var unitKey = BookCoalescingHelper.BuildGroupingUnitKey(f.Path);
                        var subKey = string.Empty;

                        if (restrictToAuthorId.HasValue &&
                            !string.IsNullOrWhiteSpace(folder) &&
                            !string.IsNullOrWhiteSpace(restrictedAuthorName))
                        {
                            // Prefer exact author folder links when available, but also tolerate non-canonical on-disk
                            // spellings (e.g. "George R. R. Martin" vs stored "George R.R. Martin") by resolving
                            // the author folder from the path via AuthorFolderMatchingService.
                            var isDirectUnderAuthorFolder = false;
                            if (restrictedAuthorFolders != null && restrictedAuthorFolders.Contains(folder))
                            {
                                isDirectUnderAuthorFolder = true;
                            }
                            else if (directUnderAuthorFolderCache != null && directUnderAuthorFolderCache.TryGetValue(folder, out var cached))
                            {
                                isDirectUnderAuthorFolder = cached;
                            }
                            else
                            {
                                var resolvedAuthorFolder = GetAuthorFolder(f.Path, restrictedAuthorName);
                                isDirectUnderAuthorFolder = !string.IsNullOrWhiteSpace(resolvedAuthorFolder) && folder.PathEquals(resolvedAuthorFolder);
                                if (directUnderAuthorFolderCache != null)
                                {
                                    directUnderAuthorFolderCache[folder] = isDirectUnderAuthorFolder;
                                }
                            }

                            if (isDirectUnderAuthorFolder)
                            {
                                var identityKey = BookImportUnitGroupingService.BuildIdentityKey(f?.AllTags, directAuthorIdentitySupport, directAuthorExcludedIdentityValues);
                                subKey = !string.IsNullOrWhiteSpace(identityKey) ? identityKey : f.Path; // cluster when tags agree; else unique per file
                            }
                        }

                        return new { UnitKey = unitKey, Folder = folder, Ext = ext, SubKey = subKey };
                    })
                    .ToList();

            _logger.Debug("[FILE-MATCHING] Processing {0} grouped sets (unitized)", groups.Count);
            // Publish discovery of book units (file groups)
            try
            {
                var evt = new MediaFiles.Events.ImportStageProgressEvent(
                    MediaFiles.Events.ImportStage.MatchingAuthorsLocally,
                    $"Discovered {groups.Count} file groups for matching",
                    currentProgress: 0,
                    totalProgress: groups.Count)
                {
                    BookUnitsDiscovered = groups.Count,
                    ProcessedBookFolders = 0,
                };
                evt.CommandId = ProgressMessageContext.CommandModel?.Id;
                _eventAggregator.PublishEvent(evt);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[PROGRESS] Failed to publish book-unit discovery progress event");
            }

            // Track author folders already handled to short-circuit further groups under the same author
            var processedAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processedUnits = 0;
            var authorsQueued = 0;
            var deferToAuthorRacer = !restrictToAuthorId.HasValue;

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var files = group.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                var representative = files.First();
                var folder = Path.GetDirectoryName(representative.Path) ?? string.Empty; // book folder
                var normalizedFolder = group.Key.Folder ?? GetBookFolder(representative.Path) ?? NormalizeDirectory(folder) ?? string.Empty;
                var ext = group.Key.Ext;
                var correlationId = Guid.NewGuid().ToString("N").Substring(0, 8);
                var cid = $"[CID:{correlationId}] ";

                _logger.Debug("{4}[FILE-GROUP-START] path='{0}' ext='{1}' fileCount={2} restrictToAuthor={3}",
                    representative.Path, ext, files.Count, restrictToAuthorId.HasValue, cid);

                // Skip if this file is under any already-processed author folder.
                // Author folders are only added to processedAuthorFolders AFTER V5 match resolves the author.
                if (!restrictToAuthorId.HasValue && processedAuthorFolders.Any(processed =>
                    !string.IsNullOrEmpty(processed) &&
                    representative.Path.StartsWith(processed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.Debug("[FILE-GROUP-SKIP] path='{0}' reason_code=GROUP_SKIPPED_ALREADY_PROCESSED caller={1}",
                        representative.Path, "Scan");
                    continue;
                }

                var isAudiobook = MediaFileExtensions.AudioExtensions.Contains(ext);
                var mediaType = isAudiobook ? BookMediaType.Audiobook : BookMediaType.Ebook;

                var samples = SelectSpreadSamples(files);
                var homogeneousTags = BuildGroupConsensusTags(files);
                try { _logger.Debug("[TAGS] Consensus from spread samples ({0} keys, samples={1})", homogeneousTags.Count, samples.Count); } catch { }

                var groupMatchDurationSeconds = representative.DurationSeconds;
                if (isAudiobook)
                {
                    // In unscoped per-file mode, avoid computing TOTALDURATION across potentially heterogeneous groups.
                    // Per-file mode computes TOTALDURATION per identity subgroup below.
                    var shouldComputeGroupDuration = !(perFileMatching &&
                                                       !restrictToAuthorId.HasValue &&
                                                       files.Count > 1 &&
                                                       !deferUnmatchedToAuthorReady);
                    if (shouldComputeGroupDuration)
                    {
                        var totalGroupDurationSeconds = ResolveTotalDurationSeconds(files);
                        TryEnrichAudiobookTagsWithTotalDuration(homogeneousTags, totalGroupDurationSeconds, files.Count, cid);
                        groupMatchDurationSeconds = ResolveGroupedMatchDurationSeconds(representative, totalGroupDurationSeconds, isAudiobook);
                    }
                }

                var groupFile = new DiscoveredFileWithMetadata
                {
                    Path = representative.Path,
                    Size = representative.Size,
                    Modified = representative.Modified,
                    AllTags = homogeneousTags,
                    GroupMemberTags = files
                        .Select(member => member?.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
                        .ToList(),
                    DurationSeconds = groupMatchDurationSeconds
                };

                _logger.Debug("{3}[FILE-GROUP] Folder='{0}', Ext='{1}', Files={2}", folder, ext, files.Count, cid);
                var tagKeys = homogeneousTags.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                if (tagKeys.Count > 0)
                {
                    var preview = string.Join(", ", tagKeys.Take(10));
                    _logger.Debug("{2}[FILE-GROUP] Embedded tags used ({0} keys): {1}", tagKeys.Count, preview, cid);
                }
                if (restrictToAuthorId.HasValue)
                {
                    // HOLY GRAIL: Author-restricted matching using simple FTS + smoke test
                    var authorId = restrictToAuthorId.Value;
                    try
                    {
                        var scopedAuthorName = restrictedAuthorName;
                        var isRestrictedAuthorRootFolder = restrictedAuthorFolders != null &&
                                                           !string.IsNullOrWhiteSpace(normalizedFolder) &&
                                                           restrictedAuthorFolders.Contains(normalizedFolder);

                            Dictionary<string, List<string>> GetTagsForMatch(DiscoveredFileWithMetadata f)
                            {
                                if (f?.AllTags != null && f.AllTags.Count > 0)
                                {
                                    return CloneTags(f.AllTags);
                                }

                                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            }

                        MatchDecision TryMatchOne(DiscoveredFileWithMetadata f, Dictionary<string, List<string>> tags, Dictionary<string, List<string>> v5Tags)
                        {
                            string pinnedFailure = null;
                            // If this import was grabbed for a specific book with a pinned edition (ManualAdd / AnyEditionOk=false),
                            // give that pinned edition first crack before any identifier short-circuit or FTS matching.
                            if (pinnedFirstCrack != null)
                            {
                                var pinnedEvaluation = EvaluatePinnedEditionFirstCrack(new DiscoveredFileWithMetadata
                                {
                                    Path = f.Path,
                                    Size = f.Size,
                                    Modified = f.Modified,
                                    AllTags = tags,
                                    GroupMemberTags = f.GroupMemberTags,
                                    DurationSeconds = f.DurationSeconds
                                }, tags, mediaType, pinnedFirstCrack, out var matchedNarrator);

                                if (pinnedEvaluation?.Match != null)
                                {
                                    _logger.Debug("{0}[PINNED-FIRST-CRACK] SUCCESS: bk={1} '{2}' ed={3} narratorHit='{4}'",
                                        cid, pinnedEvaluation.Match.BookId, pinnedEvaluation.Match.BookTitle, pinnedEvaluation.Match.EditionId, matchedNarrator ?? "<unknown>");
                                    return new MatchDecision
                                    {
                                        Match = pinnedEvaluation.Match,
                                        ProofTags = tags,
                                        PathFallbackUsed = false,
                                        MatchedPinnedFirstCrack = true,
                                        MatchedNarrator = matchedNarrator,
                                        PinnedTargetResult = "matched"
                                    };
                                }

                                pinnedFailure = pinnedEvaluation?.FailureReason ?? "pinned target did not match embedded tags";
                            }

                            var scopedAuthorPresentInTags = true;
                            if (allowUnscopedFallback && !string.IsNullOrWhiteSpace(scopedAuthorName))
                            {
                                scopedAuthorPresentInTags = IsAuthorPresentInNonCommentNonTrashTags(scopedAuthorName, tags);
                            }

                            var skippedScopedMatch = allowUnscopedFallback &&
                                                     !string.IsNullOrWhiteSpace(scopedAuthorName) &&
                                                     !scopedAuthorPresentInTags;

                            var rejections = new List<CandidateRejection>();

                            FileMatch match = null;
                            HolyGrailEvaluation holyGrailEvaluation = null;
                            if (!skippedScopedMatch)
                            {
                                holyGrailEvaluation = RunWithRejectionCapture(
                                    "scoped",
                            () => EvaluateHolyGrailMatchFileInternal(new DiscoveredFileWithMetadata
                            {
                                Path = f.Path,
                                Size = f.Size,
                                Modified = f.Modified,
                                AllTags = tags,
                                GroupMemberTags = f.GroupMemberTags,
                                DurationSeconds = f.DurationSeconds
                            }, mediaType, authorId, disablePathFallback: disablePathFallback, inferAuthorFromPathDuringPathFallback: true, unscoped: false, hardAllowedBookIds: hardAllowedBookIds),
                            rejections);
                                match = holyGrailEvaluation?.Match;
                            }

                            var triedUnscopedFallback = false;
                            if (match == null && allowUnscopedFallback)
                            {
                                triedUnscopedFallback = true;
                                holyGrailEvaluation = RunWithRejectionCapture(
                                    "unscoped",
                                    () => EvaluateHolyGrailMatchFileInternal(new DiscoveredFileWithMetadata
                                    {
                                        Path = f.Path,
                                        Size = f.Size,
                                        Modified = f.Modified,
                                        AllTags = tags,
                                        GroupMemberTags = f.GroupMemberTags,
                                        DurationSeconds = f.DurationSeconds
                                    }, mediaType, restrictToAuthorId: null, disablePathFallback: isRestrictedAuthorRootFolder || disablePathFallback, inferAuthorFromPathDuringPathFallback: false, unscoped: true, hardAllowedBookIds: hardAllowedBookIds),
                                    rejections);
                                match = holyGrailEvaluation?.Match;
                            }

                            AuthorSuggestion potentialAuthor = null;
                            string unmatchedReason = null;
                            var matchedViaV5Recovery = false;

                            if (match == null && allowV5Identification)
                            {
                                var v5File = new DiscoveredFileWithMetadata
                                {
                                    Path = f.Path,
                                    Size = f.Size,
                                    Modified = f.Modified,
                                    AllTags = tags,
                                    GroupMemberTags = f.GroupMemberTags,
                                    DurationSeconds = f.DurationSeconds
                                };

                                var recovery = TryRecoverRestrictedMissViaV5(
                                    v5File,
                                    v5Tags,
                                    mediaType,
                                    allowAuthorImport,
                                    usePathAsTagsFallback,
                                    v5QuestionsWithoutSuggestion,
                                    hardAllowedBookIds);
                                if (recovery.match != null)
                                {
                                    match = recovery.match;
                                    matchedViaV5Recovery = true;
                                    if (match.Provenance != null)
                                    {
                                        match.Provenance.Route = $"v5_recovery/{match.Provenance.Route ?? "local_match"}";
                                    }
                                    holyGrailEvaluation = new HolyGrailEvaluation
                                    {
                                        Match = recovery.match,
                                        WinningTags = tags,
                                        PathFallbackUsed = false,
                                        PathFallbackSuppressedReason = "recovered_author_scoped"
                                    };
                                }
                                else
                                {
                                    potentialAuthor = recovery.suggestion;
                                    unmatchedReason = recovery.reason;
                                }
                            }

                            return new MatchDecision
                            {
                                Match = match,
                                ProofTags = holyGrailEvaluation?.WinningTags ?? tags,
                                BooksById = holyGrailEvaluation?.BooksById,
                                PathFallbackUsed = holyGrailEvaluation?.PathFallbackUsed ?? false,
                                PathFallbackSuppressedReason = holyGrailEvaluation?.PathFallbackSuppressedReason,
                                MatchedPinnedFirstCrack = false,
                                PinnedTargetResult = pinnedFirstCrack != null ? "failed" : null,
                                PinnedTargetFailure = pinnedFirstCrack != null ? pinnedFailure : null,
                                TriedUnscopedFallback = triedUnscopedFallback,
                                SkippedScopedMatch = skippedScopedMatch,
                                MatchedViaV5Recovery = matchedViaV5Recovery,
                                PotentialAuthor = potentialAuthor,
                                UnmatchedReason = unmatchedReason,
                                Rejections = match != null ? null : NullIfEmpty(rejections)
                            };
                        }

                        // Fast path: single file.
                        if (files.Count == 1)
                        {
                            var tags = GetTagsForMatch(representative);
                            var embeddedV5Tags = representative?.AllTags != null && representative.AllTags.Count > 0
                                ? CloneTags(representative.AllTags)
                                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            var decision = TryMatchOne(representative, tags, embeddedV5Tags);
                            var match = decision.Match;
                            if (match != null)
                            {
                                _logger.Debug("{0}[HOLY-GRAIL] SUCCESS: bk={1} '{2}' ed={3} author='{4}'",
                                    cid, match.BookId, match.BookTitle, match.EditionId, match.AuthorName);

                                var matchReason = decision.MatchedPinnedFirstCrack
                                    ? "Matched pinned edition (first crack smoke test)"
                                    : decision.MatchedViaV5Recovery
                                        ? "Matched via V5 author recovery + Holy Grail FTS (author-restricted)"
                                    : decision.TriedUnscopedFallback || decision.SkippedScopedMatch
                                        ? "Matched via Holy Grail FTS + smoke test (unscoped fallback)"
                                        : "Matched via Holy Grail FTS + smoke test (author-restricted)";

                                groupedMatched.Add(CopyFileMatchForFile(match, representative));
                                LogDecisionWithProvenance(
                                    representative.Path,
                                    "MATCHED",
                                    matchReason,
                                    tags,
                                    mediaType,
                                    match,
                                    decision.ProofTags,
                                    decision.PathFallbackUsed,
                                    decision.PathFallbackSuppressedReason,
                                    decision.PinnedTargetResult,
                                    decision.PinnedTargetFailure,
                                    commandId: ProgressMessageContext.CommandModel?.Id,
                                    correlationId: correlationId);
                                continue;
                            }

                            var reason = !string.IsNullOrWhiteSpace(decision.UnmatchedReason)
                                ? decision.UnmatchedReason
                                : decision.TriedUnscopedFallback || decision.SkippedScopedMatch
                                ? $"NO_MATCH_HOLY_GRAIL (authorId={authorId}; unscopedFallback=true{(decision.SkippedScopedMatch ? "; scopedAuthorNotInTags=true" : string.Empty)})"
                                : $"NO_MATCH_HOLY_GRAIL (authorId={authorId})";

                            groupedUnmatched.Add(new UnmatchedFile
                            {
                                File = representative,
                                Reason = reason,
                                PotentialAuthors = decision.PotentialAuthor != null ? new[] { decision.PotentialAuthor } : new AuthorSuggestion[0]
                            });
                            LogDecisionWithProvenance(
                                representative.Path,
                                "UNMATCHED",
                                reason,
                                tags,
                                mediaType,
                                proofTags: decision.ProofTags,
                                pathFallbackUsed: decision.PathFallbackUsed,
                                pathFallbackSuppressedReason: decision.PathFallbackSuppressedReason,
                                pinnedTargetResult: decision.PinnedTargetResult,
                                pinnedTargetFailure: decision.PinnedTargetFailure,
                                rejections: decision.Rejections,
                                commandId: ProgressMessageContext.CommandModel?.Id,
                                correlationId: correlationId);
                            continue;
                        }

                        // Multi-file group: first let stable unit evidence try the existing grouped match path.
                        // This is what lets a full-book duration prove a chapterized audiobook; every member
                        // still has to pass strict proof membership before we stamp the grouped result.
                        var unitHandledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        List<CandidateRejection> groupedSeedDurationRejections = null;

                        if (homogeneousTags.Count > 0)
                        {
                            var groupedSeedTags = CloneTags(homogeneousTags);
                            var groupedSeedDecision = TryMatchOne(groupFile, groupedSeedTags, CloneTags(groupedSeedTags));
                            var groupedSeedMatch = groupedSeedDecision.Match;
                            groupedSeedDurationRejections = BuildGroupedDurationGateRejections(groupedSeedDecision.Rejections, files, groupMatchDurationSeconds);

                            if (groupedSeedMatch != null)
                            {
                                var proofSourceTags = groupedSeedDecision.ProofTags != null && groupedSeedDecision.ProofTags.Count > 0
                                    ? groupedSeedDecision.ProofTags
                                    : groupedSeedTags;
                                var proofEvidence = groupedSeedMatch.IdentityProof;

                                if (HasRequiredAuthorAndBookProof(proofEvidence))
                                {
                                    var membershipResults = files
                                        .Select(file =>
                                        {
                                            var tags = GetTagsForMatch(file);
                                            return new
                                            {
                                                File = file,
                                                Tags = tags,
                                                Membership = BuildHomogeneousProofMembership(
                                                    groupedSeedMatch,
                                                    file,
                                                    tags,
                                                    proofEvidence)
                                            };
                                        })
                                        .ToList();

                                    var groupedMembers = membershipResults
                                        .Where(x => x.File != null && x.Membership.Passes)
                                        .ToList();

                                    if (groupedMembers.Count > 1)
                                    {
                                        var remainderCount = membershipResults.Count(x => x.File != null && !x.Membership.Passes);
                                        if (remainderCount > 0)
                                        {
                                            _logger.Debug("{0}[HOLY-GRAIL] GROUPED-SEED accepted {1}/{2}; rematching remainder={3}",
                                                cid, groupedMembers.Count, files.Count, remainderCount);
                                        }

                                        var anyFallback = groupedSeedDecision.TriedUnscopedFallback ||
                                                          groupedSeedDecision.SkippedScopedMatch;
                                        var matchReason = groupedSeedDecision.MatchedPinnedFirstCrack
                                            ? "Matched pinned edition (first crack smoke test)"
                                            : groupedSeedDecision.MatchedViaV5Recovery
                                                ? "Matched via V5 author recovery + Holy Grail FTS (author-restricted grouped seed)"
                                                : anyFallback
                                                    ? "Matched via Holy Grail FTS + smoke test (grouped seed fallback)"
                                                    : "Matched via Holy Grail FTS + smoke test (grouped seed)";

                                        foreach (var member in groupedMembers)
                                        {
                                            cancellationToken.ThrowIfCancellationRequested();

                                            unitHandledPaths.Add(member.File.Path);
                                            var memberMatch = CopyFileMatchForFile(
                                                groupedSeedMatch,
                                                member.File,
                                                member.Membership.IdentityProof);
                                            groupedMatched.Add(memberMatch);

                                            LogDecisionWithProvenance(
                                                member.File.Path,
                                                "MATCHED",
                                                matchReason,
                                                member.Tags,
                                                mediaType,
                                                memberMatch,
                                                member.Membership.ProofTags ?? proofSourceTags,
                                                groupedSeedDecision.PathFallbackUsed || anyFallback,
                                                groupedSeedDecision.PathFallbackSuppressedReason,
                                                groupedSeedDecision.PinnedTargetResult,
                                                groupedSeedDecision.PinnedTargetFailure,
                                                commandId: ProgressMessageContext.CommandModel?.Id,
                                                correlationId: correlationId);
                                        }
                                    }
                                }
                            }
                        }

                        // Then probe remaining files and only stamp grouped units when successful probes share
                        // exact proof fields and a unit-level rerun resolves cleanly.
                        var matchCache = new Dictionary<string, (Dictionary<string, List<string>> Tags, MatchDecision Decision)>(StringComparer.OrdinalIgnoreCase);
                        foreach (var file in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (file == null || string.IsNullOrWhiteSpace(file.Path))
                            {
                                continue;
                            }

                            if (unitHandledPaths.Contains(file.Path))
                            {
                                continue;
                            }

                            var tags = GetTagsForMatch(file);
                            var embeddedFileTags = file?.AllTags != null && file.AllTags.Count > 0
                                ? CloneTags(file.AllTags)
                                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            var res = TryMatchOne(file, tags, embeddedFileTags);
                            matchCache[file.Path] = (tags, res);
                        }

                        var booksById = new Dictionary<int, Book>();
                        var successfulProbes = new List<SuccessfulProbeMatch>();
                        foreach (var file in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (file == null || string.IsNullOrWhiteSpace(file.Path) || !matchCache.TryGetValue(file.Path, out var cached))
                            {
                                continue;
                            }

                            if (cached.Decision?.Match == null)
                            {
                                continue;
                            }

                            var evidence = cached.Decision.Match.IdentityProof;
                            var logicalWorkKey = GetLogicalWorkKey(cached.Decision.Match, booksById);
                            if (!HasRequiredAuthorAndBookProof(evidence) || string.IsNullOrWhiteSpace(logicalWorkKey))
                            {
                                continue;
                            }

                            successfulProbes.Add(new SuccessfulProbeMatch
                            {
                                File = file,
                                Tags = cached.Tags,
                                Decision = cached.Decision,
                                Evidence = evidence,
                                LogicalWorkKey = logicalWorkKey
                            });
                        }

                        foreach (var logicalGroup in successfulProbes
                                     .GroupBy(p => p.LogicalWorkKey, StringComparer.OrdinalIgnoreCase)
                                     .OrderByDescending(g => g.Count()))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var seedProbe = logicalGroup
                                .OrderBy(p => p.File.Path, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault(p => HasRequiredAuthorAndBookProof(p.Evidence));

                            if (seedProbe == null)
                            {
                                continue;
                            }

                            var memberProbes = logicalGroup
                                .Select(p => new
                                {
                                    Probe = p,
                                    Membership = BuildHomogeneousProofMembership(
                                        seedProbe.Decision.Match,
                                        p.File,
                                        p.Tags,
                                        seedProbe.Evidence)
                                })
                                .Where(x => !unitHandledPaths.Contains(x.Probe.File.Path) && x.Membership.Passes)
                                .ToList();

                            if (memberProbes.Count <= 1)
                            {
                                continue;
                            }

                            var unitFiles = memberProbes.Select(p => p.Probe.File).ToList();
                            var unitTags = CloneTags(seedProbe.Tags);
                            int? totalUnitDurationSeconds = null;
                            var unitMatchDurationSeconds = memberProbes[0].Probe.File.DurationSeconds;

                            if (isAudiobook)
                            {
                                totalUnitDurationSeconds = ResolveTotalDurationSeconds(unitFiles);
                                TryEnrichAudiobookTagsWithTotalDuration(unitTags, totalUnitDurationSeconds, unitFiles.Count, cid);
                                unitMatchDurationSeconds = ResolveGroupedMatchDurationSeconds(memberProbes[0].Probe.File, totalUnitDurationSeconds, isAudiobook);
                            }

                            var unitFile = new DiscoveredFileWithMetadata
                            {
                                Path = memberProbes[0].Probe.File.Path,
                                Size = memberProbes[0].Probe.File.Size,
                                Modified = memberProbes[0].Probe.File.Modified,
                                AllTags = unitTags,
                                GroupMemberTags = unitFiles
                                    .Select(member => member?.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
                                    .ToList(),
                                DurationSeconds = unitMatchDurationSeconds
                            };

                            var unitDecision = TryMatchOne(unitFile, unitTags, CloneTags(unitTags));
                            var unitMatch = unitDecision.Match;
                            var unitLogicalWorkKey = unitMatch != null ? GetLogicalWorkKey(unitMatch, booksById) : null;

                            if (unitMatch == null || !string.Equals(unitLogicalWorkKey, logicalGroup.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var member in memberProbes)
                                {
                                    var probe = member.Probe;
                                    cancellationToken.ThrowIfCancellationRequested();

                                    unitHandledPaths.Add(probe.File.Path);
                                    var reason = unitMatch == null
                                        ? $"NO_MATCH_HOLY_GRAIL (authorId={authorId}; unitRerunFailed=true)"
                                        : $"NO_MATCH_HOLY_GRAIL (authorId={authorId}; unitRerunChangedWork=true)";

                                    groupedUnmatched.Add(new UnmatchedFile
                                    {
                                        File = probe.File,
                                        Reason = reason,
                                        PotentialAuthors = new AuthorSuggestion[0]
                                    });

                                    LogDecisionWithProvenance(
                                        probe.File.Path,
                                        "UNMATCHED",
                                        reason,
                                        probe.Tags,
                                        mediaType,
                                        proofTags: unitTags,
                                        pathFallbackUsed: unitDecision.PathFallbackUsed,
                                        pathFallbackSuppressedReason: unitDecision.PathFallbackSuppressedReason,
                                        pinnedTargetResult: unitDecision.PinnedTargetResult,
                                        pinnedTargetFailure: unitDecision.PinnedTargetFailure,
                                        rejections: MergeRejections(
                                            BuildGroupedDurationGateRejections(unitDecision.Rejections, unitFiles, unitMatchDurationSeconds),
                                            unitDecision.Rejections),
                                        commandId: ProgressMessageContext.CommandModel?.Id,
                                        correlationId: correlationId);
                                }

                                continue;
                            }

                            var anyFallback = unitDecision.TriedUnscopedFallback ||
                                              unitDecision.SkippedScopedMatch ||
                                              memberProbes.Any(p => p.Probe.Decision.TriedUnscopedFallback || p.Probe.Decision.SkippedScopedMatch);
                            var matchReason = unitDecision.MatchedPinnedFirstCrack
                                ? "Matched pinned edition (first crack smoke test)"
                                : unitDecision.MatchedViaV5Recovery
                                    ? "Matched via V5 author recovery + Holy Grail FTS (author-restricted)"
                                    : anyFallback
                                        ? "Matched via Holy Grail FTS + smoke test (unit rerun fallback)"
                                        : "Matched via Holy Grail FTS + smoke test (unit rerun)";

                            foreach (var member in memberProbes)
                            {
                                var probe = member.Probe;
                                cancellationToken.ThrowIfCancellationRequested();

                                unitHandledPaths.Add(probe.File.Path);
                                var memberMatch = CopyFileMatchForFile(unitMatch, probe.File, member.Membership.IdentityProof);
                                groupedMatched.Add(memberMatch);

                                LogDecisionWithProvenance(
                                    probe.File.Path,
                                    "MATCHED",
                                    matchReason,
                                    probe.Tags,
                                    mediaType,
                                    memberMatch,
                                    unitDecision.ProofTags ?? unitTags,
                                    unitDecision.PathFallbackUsed || anyFallback,
                                    unitDecision.PathFallbackSuppressedReason,
                                    unitDecision.PinnedTargetResult,
                                    unitDecision.PinnedTargetFailure,
                                    commandId: ProgressMessageContext.CommandModel?.Id,
                                    correlationId: correlationId);
                            }
                        }

                        foreach (var file in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (file == null || string.IsNullOrWhiteSpace(file.Path) || unitHandledPaths.Contains(file.Path))
                            {
                                continue;
                            }

                            if (!matchCache.TryGetValue(file.Path, out var cached))
                            {
                                continue;
                            }

                            var tagsToUse = cached.Tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            var fileDecision = cached.Decision;
                            var match = fileDecision.Match;
                            if (match != null)
                            {
                                groupedMatched.Add(CopyFileMatchForFile(match, file));
                                LogDecisionWithProvenance(
                                    file.Path,
                                    "MATCHED",
                                    fileDecision.MatchedPinnedFirstCrack
                                        ? "Matched pinned edition (first crack smoke test)"
                                        : fileDecision.MatchedViaV5Recovery
                                            ? "Matched via V5 author recovery + Holy Grail FTS (author-restricted)"
                                            : "Matched via Holy Grail FTS + smoke test (per-file probe)",
                                    tagsToUse,
                                    mediaType,
                                    match,
                                    fileDecision.ProofTags,
                                    fileDecision.PathFallbackUsed,
                                    fileDecision.PathFallbackSuppressedReason,
                                    fileDecision.PinnedTargetResult,
                                    fileDecision.PinnedTargetFailure,
                                    commandId: ProgressMessageContext.CommandModel?.Id,
                                    correlationId: correlationId);
                            }
                            else
                            {
                                var reason = !string.IsNullOrWhiteSpace(fileDecision.UnmatchedReason)
                                    ? fileDecision.UnmatchedReason
                                    : fileDecision.TriedUnscopedFallback || fileDecision.SkippedScopedMatch
                                        ? $"NO_MATCH_HOLY_GRAIL (authorId={authorId}; unscopedFallback=true{(fileDecision.SkippedScopedMatch ? "; scopedAuthorNotInTags=true" : string.Empty)})"
                                        : $"NO_MATCH_HOLY_GRAIL (authorId={authorId})";

                                groupedUnmatched.Add(new UnmatchedFile
                                {
                                    File = file,
                                    Reason = reason,
                                    PotentialAuthors = fileDecision.PotentialAuthor != null ? new[] { fileDecision.PotentialAuthor } : new AuthorSuggestion[0]
                                });
                                LogDecisionWithProvenance(
                                    file.Path,
                                    "UNMATCHED",
                                    reason,
                                    tagsToUse,
                                    mediaType,
                                    proofTags: fileDecision.ProofTags,
                                    pathFallbackUsed: fileDecision.PathFallbackUsed,
                                    pathFallbackSuppressedReason: fileDecision.PathFallbackSuppressedReason,
                                    pinnedTargetResult: fileDecision.PinnedTargetResult,
                                    pinnedTargetFailure: fileDecision.PinnedTargetFailure,
                                    rejections: MergeRejections(groupedSeedDurationRejections, fileDecision.Rejections),
                                    commandId: ProgressMessageContext.CommandModel?.Id,
                                    correlationId: correlationId);
                            }
                        }
                        continue;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[FILE-GROUP] Author-restricted matching failed for folder '{0}'", folder);
                    }
                }

                // Author gating / event-driven sequencing:
                // When enabled, avoid unrestricted local matching. Instead, identify (and optionally import) the author,
                // then let the author-ready handler match the files.
                if (deferToAuthorRacer && deferUnmatchedToAuthorReady)
                {
                    if (allowV5Identification)
                    {
                        try
                        {
                            var v5Suggested = TryV5SuggestionWithPathFallback(
                                homogeneousTags,
                                mediaType,
                                representative.Path,
                                usePathAsTagsFallback && !disablePathFallback,
                                v5QuestionsWithoutSuggestion);

                            if (v5Suggested != null && allowAuthorImport)
                            {
                                var s = v5Suggested;
                                // Resolve author folder by walking from root toward file, fuzzy matching author name
                                var resolvedAuthorFolder = GetAuthorFolder(representative.Path, s.AuthorName);
                                if (!string.IsNullOrWhiteSpace(resolvedAuthorFolder))
                                {
                                    _logger.Debug("[SHORT-CIRCUIT] Author folder resolved: {0}", resolvedAuthorFolder);
                                }

                                // Build monitoring config based on the root folder
                                var rf = _rootFolderService.GetBestRootFolder(representative.Path);
                                var config = new MonitoringConfig
                                {
                                    AuthorName = s.AuthorName,
                                    DiscoveredAuthorFolderPath = resolvedAuthorFolder,
                                    QueueIfUnavailable = false,
                                    RequestedBy = "FileMatchingService"
                                };
                                switch (rf.FolderType)
                                {
                                    case FolderType.Audiobook:
                                        config.CreateAudiobook = true;
                                        config.CreateEbook = false;
                                        config.AudiobookRootFolderPath = rf.Path;
                                        var a = rf.GetAudiobookSettings();
                                        if (a != null)
                                        {
                                            config.AudiobookQualityProfileId = a.QualityProfileId;
                                            config.AudiobookMetadataProfileId = a.MetadataProfileId;
                                            config.AudiobookMonitored = a.Monitored;
                                            config.AudiobookMonitorNewItems = a.MonitorNewItems;
                                            config.AudiobookMonitorExistingMode = ResolveRootMonitorExistingMode(a);
                                            config.MergeTagsForMediaType(BookMediaType.Audiobook, a.Tags);
                                        }
                                        break;
                                    case FolderType.Ebook:
                                        config.CreateAudiobook = false;
                                        config.CreateEbook = true;
                                        config.EbookRootFolderPath = rf.Path;
                                        var e = rf.GetEbookSettings();
                                        if (e != null)
                                        {
                                            config.EbookQualityProfileId = e.QualityProfileId;
                                            config.EbookMetadataProfileId = e.MetadataProfileId;
                                            config.EbookMonitored = e.Monitored;
                                            config.EbookMonitorNewItems = e.MonitorNewItems;
                                            config.EbookMonitorExistingMode = ResolveRootMonitorExistingMode(e);
                                            config.MergeTagsForMediaType(BookMediaType.Ebook, e.Tags);
                                        }
                                        break;
                                    case FolderType.Mixed:
                                    default:
                                        var mixedTypes = ResolveCreateMediaTypes(rf, mediaType);
                                        config.CreateAudiobook = mixedTypes.CreateAudiobook;
                                        config.CreateEbook = mixedTypes.CreateEbook;
                                        if (config.CreateAudiobook)
                                        {
                                            config.AudiobookRootFolderPath = rf.Path;
                                            var ma = rf.GetAudiobookSettings();
                                            if (ma != null)
                                            {
                                                config.AudiobookQualityProfileId = ma.QualityProfileId;
                                                config.AudiobookMetadataProfileId = ma.MetadataProfileId;
                                                config.AudiobookMonitored = ma.Monitored;
                                                config.AudiobookMonitorNewItems = ma.MonitorNewItems;
                                                config.AudiobookMonitorExistingMode = ResolveRootMonitorExistingMode(ma);
                                                config.MergeTagsForMediaType(BookMediaType.Audiobook, ma.Tags);
                                            }
                                        }
                                        if (config.CreateEbook)
                                        {
                                            config.EbookRootFolderPath = rf.Path;
                                            var me = rf.GetEbookSettings();
                                            if (me != null)
                                            {
                                                config.EbookQualityProfileId = me.QualityProfileId;
                                                config.EbookMetadataProfileId = me.MetadataProfileId;
                                                config.EbookMonitored = me.Monitored;
                                                config.EbookMonitorNewItems = me.MonitorNewItems;
                                                config.EbookMonitorExistingMode = ResolveRootMonitorExistingMode(me);
                                                config.MergeTagsForMediaType(BookMediaType.Ebook, me.Tags);
                                            }
                                        }
                                        break;
                                }

                                // If author exists, augment settings and publish ready event; else import
                                try
                                {
                                    var colon = s.ProviderId?.IndexOf(':') ?? -1;
                                    if (colon > 0)
                                    {
                                        var prefix = s.ProviderId.Substring(0, colon);
                                        var rawId = s.ProviderId.Substring(colon + 1);
                                        var existing = _authorService.FindByProviderId(prefix, rawId);
                                        if (existing != null)
                                        {
                                            var updated = _authorService.UpdateAuthorProgressiveSettings(
                                                existing,
                                                config.CreateAudiobook ? config.AudiobookQualityProfileId : null,
                                                config.CreateAudiobook ? config.AudiobookMetadataProfileId : null,
                                                config.CreateAudiobook ? config.AudiobookMonitored : null,
                                                config.CreateAudiobook ? config.AudiobookMonitorNewItems : null,
                                                config.CreateEbook ? config.EbookQualityProfileId : null,
                                                config.CreateEbook ? config.EbookMetadataProfileId : null,
                                                config.CreateEbook ? config.EbookMonitored : null,
                                                config.CreateEbook ? config.EbookMonitorNewItems : null,
                                                rf.Path);

                                            var changed = false;
                                            if (config.CreateAudiobook && string.IsNullOrWhiteSpace(updated.AudiobookPath) && !string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath))
                                            {
                                                updated.AudiobookPath = config.DiscoveredAuthorFolderPath;
                                                changed = true;
                                            }
                                            if (config.CreateEbook && string.IsNullOrWhiteSpace(updated.EbookPath) && !string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath))
                                            {
                                                updated.EbookPath = config.DiscoveredAuthorFolderPath;
                                                changed = true;
                                            }

                                            if (config.CreateAudiobook && updated.AudiobookTags == null && config.AudiobookTags != null)
                                            {
                                                updated.AudiobookTags = new HashSet<int>(config.AudiobookTags);
                                                changed = true;
                                            }

                                            if (config.CreateEbook && updated.EbookTags == null && config.EbookTags != null)
                                            {
                                                updated.EbookTags = new HashSet<int>(config.EbookTags);
                                                changed = true;
                                            }

                                            if (changed)
                                            {
                                                updated.Tags = (updated.AudiobookTags ?? new HashSet<int>())
                                                    .Concat(updated.EbookTags ?? new HashSet<int>())
                                                    .ToHashSet();
                                                updated = _authorService.UpdateAuthor(updated);
                                            }

                                            _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(updated));
                                            if (!restrictToAuthorId.HasValue && !string.IsNullOrWhiteSpace(resolvedAuthorFolder))
                                            {
                                                processedAuthorFolders.Add(resolvedAuthorFolder);
                                            }
                                            authorsQueued++;
                                            shortCircuitedAny = true;
                                            continue; // move to next group
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(ex, "[SHORT-CIRCUIT] Existing-author readiness publish failed for {0}", s.ProviderId);
                                }

                                try
                                {
                                    var added = _authorLibraryService.AddAuthorAsync(s.ProviderId, config).GetAwaiter().GetResult();
                                    if (added != null && added.Id > 0)
                                    {
                                        _logger.Debug("[SHORT-CIRCUIT] Added author '{0}' ({1}); skipping remaining groups under: {2}",
                                            s.AuthorName, s.ProviderId, resolvedAuthorFolder ?? folder);
                                    }
                                    authorsQueued++;
                                    if (!restrictToAuthorId.HasValue && !string.IsNullOrWhiteSpace(resolvedAuthorFolder))
                                    {
                                        processedAuthorFolders.Add(resolvedAuthorFolder);
                                    }
                                    shortCircuitedAny = true;
                                    continue; // move to next group
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(ex, "[SHORT-CIRCUIT] Failed to add author immediately for {0}", s.ProviderId);
                                    shortCircuitedAny = true;
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "[AUTHOR-GATE] Deferred matching for group due to author gating");
                        }
                    }

                    _logger.Debug("[NON-RESTRICT][SKIP] path='{0}' reason=EVENT_DRIVEN_DEFER to=author-ready", representative.Path);
                    shortCircuitedAny = true;
                    continue; // move to next group
                }

                // Per-file mode: match by metadata and avoid stamping one representative match across multi-file groups.
                // This aligns with "match first, group after" behavior and prevents flat-folder collapse.
                if (perFileMatching && files.Count > 1)
                {
                    (FileMatch match, List<CandidateRejection> rejections) TryMatchUnscopedOne(DiscoveredFileWithMetadata f, Dictionary<string, List<string>> tags)
                    {
                        var rejections = new List<CandidateRejection>();
                        var match = RunWithRejectionCapture(
                            "unrestricted",
                            () => EvaluateHolyGrailMatchFileInternal(new DiscoveredFileWithMetadata
                            {
                                Path = f.Path,
                                Size = f.Size,
                                Modified = f.Modified,
                                AllTags = tags,
                                DurationSeconds = f.DurationSeconds
                            }, mediaType, null, disablePathFallback, inferAuthorFromPathDuringPathFallback: true, unscoped: false, hardAllowedBookIds: hardAllowedBookIds)?.Match,
                            rejections);

                        return (match, match != null ? null : NullIfEmpty(rejections));
                    }

                    Dictionary<string, List<string>> GetTagsForUnscopedMatch(DiscoveredFileWithMetadata f)
                    {
                        if (f?.AllTags != null && f.AllTags.Count > 0)
                        {
                            return CloneTags(f.AllTags);
                        }

                        // No embedded tags: do not derive tags from path/filename for matching.
                        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    }

                    // Group on repeated non-trash tag/value evidence, not fixed tag labels.
                    var identityGroups = BookImportUnitGroupingService.BuildIdentitySubgroups(files);

                    if (identityGroups.Count > 1)
                    {
                        try
                        {
                            var tagged = identityGroups.Count(g => !string.IsNullOrWhiteSpace(g.IdentityKey));
                            var empty = identityGroups.Count - tagged;
                            _logger.Debug("{0}[IDENTITY-SPLIT] Split group into {1} identity subgroups (tagged={2}, empty={3}) files={4} folder='{5}'",
                                cid, identityGroups.Count, tagged, empty, files.Count, normalizedFolder);
                        }
                        catch
                        {
                            // best-effort only
                        }
                    }

                    var isDirectChildOfRootFolder = false;
                    try
                    {
                        var firstFolder = GetFirstFolderUnderRoot(representative.Path);
                        if (!string.IsNullOrWhiteSpace(firstFolder) &&
                            !string.IsNullOrWhiteSpace(normalizedFolder) &&
                            normalizedFolder.PathEquals(firstFolder))
                        {
                            isDirectChildOfRootFolder = true;
                        }
                    }
                    catch
                    {
                        isDirectChildOfRootFolder = false;
                    }

                    void MatchPerFile(IEnumerable<DiscoveredFileWithMetadata> subset, Dictionary<string, (Dictionary<string, List<string>> Tags, FileMatch Match, List<CandidateRejection> Rejections)> cachedMatches = null)
                    {
                        foreach (var file in subset)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (file == null || string.IsNullOrWhiteSpace(file.Path))
                            {
                                continue;
                            }

                            Dictionary<string, List<string>> tags;
                            FileMatch m;
                            List<CandidateRejection> rejections;

                            if (cachedMatches != null && cachedMatches.TryGetValue(file.Path, out var cached))
                            {
                                tags = cached.Tags ?? GetTagsForUnscopedMatch(file);
                                m = cached.Match;
                                rejections = cached.Rejections;
                            }
                            else
                            {
                                tags = GetTagsForUnscopedMatch(file);
                                var res = TryMatchUnscopedOne(file, tags);
                                m = res.match;
                                rejections = res.rejections;
                            }

                            if (m != null)
                            {
                                var fm = CopyFileMatchForFile(m, file);
                                groupedMatched.Add(fm);
                                LogDecisionWithProvenance(
                                    file.Path,
                                    "MATCHED",
                                    "Matched via Holy Grail FTS (per-file split)",
                                    tags,
                                    mediaType,
                                    fm,
                                    tags,
                                    commandId: ProgressMessageContext.CommandModel?.Id,
                                    correlationId: correlationId);
                            }
                            else
                            {
                                var reason = "NO_MATCH";
                                var potentialAuthors = Array.Empty<AuthorSuggestion>();
                                if (allowV5Identification)
                                {
                                    try
                                    {
                                        var v5Suggested = TryV5SuggestionWithPathFallback(
                                            tags,
                                            mediaType,
                                            file.Path,
                                            usePathAsTagsFallback &&
                                            !disablePathFallback &&
                                            !HasContradictoryFallbackDisposition(rejections),
                                            v5QuestionsWithoutSuggestion);
                                        if (v5Suggested != null)
                                        {
                                            var s = v5Suggested;
                                            reason = s.Reason;
                                            potentialAuthors = new[] { CreateAuthorSuggestion(s) };
                                        }
                                    }
                                    catch
                                    {
                                        // best-effort only
                                    }
                                }

                                var um = new UnmatchedFile
                                {
                                    File = file,
                                    Reason = reason,
                                    PotentialAuthors = potentialAuthors
                                };
                                groupedUnmatched.Add(um);
                                LogDecisionWithProvenance(
                                    file.Path,
                                    "UNMATCHED",
                                    um.Reason,
                                    tags,
                                    mediaType,
                                    rejections: rejections,
                                    commandId: ProgressMessageContext.CommandModel?.Id,
                                    correlationId: correlationId);
                            }
                        }
                    }

                    foreach (var subgroup in identityGroups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (subgroup.Files == null || subgroup.Files.Count == 0)
                        {
                            continue;
                        }

                        var subgroupHasIdentity = !string.IsNullOrWhiteSpace(subgroup.IdentityKey);
                        var shouldForcePerFile = !subgroupHasIdentity && isDirectChildOfRootFolder;

                        // If we have no stable identity in a direct-under-root folder, never stamp across the whole folder.
                        if (shouldForcePerFile || subgroup.Files.Count == 1)
                        {
                            if (shouldForcePerFile && subgroup.Files.Count > 1)
                            {
                                _logger.Debug("{0}[IDENTITY-SPLIT] EMPTY_IDENTITY direct-under-root → per-file. files={1}", cid, subgroup.Files.Count);
                            }

                            MatchPerFile(subgroup.Files);
                            continue;
                        }

                        // Build subgroup tags from spread samples + consensus for better matching signals (e.g. TOTALDURATION).
                        var subgroupTags = BuildGroupConsensusTags(subgroup.Files);

                        var subgroupRep = subgroup.Files[0];
                        var subgroupMatchDurationSeconds = subgroupRep.DurationSeconds;
                        if (isAudiobook)
                        {
                            var totalSubgroupDurationSeconds = ResolveTotalDurationSeconds(subgroup.Files);
                            TryEnrichAudiobookTagsWithTotalDuration(subgroupTags, totalSubgroupDurationSeconds, subgroup.Files.Count, cid);
                            subgroupMatchDurationSeconds = ResolveGroupedMatchDurationSeconds(subgroupRep, totalSubgroupDurationSeconds, isAudiobook);
                        }

                        var subgroupFile = new DiscoveredFileWithMetadata
                        {
                            Path = subgroupRep.Path,
                            Size = subgroupRep.Size,
                            Modified = subgroupRep.Modified,
                            AllTags = subgroupTags,
                            GroupMemberTags = subgroup.Files
                                .Select(member => member?.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
                                .ToList(),
                            DurationSeconds = subgroupMatchDurationSeconds
                        };

                        var sampleMatchCache = new Dictionary<string, (Dictionary<string, List<string>> Tags, FileMatch Match, List<CandidateRejection> Rejections)>(StringComparer.OrdinalIgnoreCase);
                        var sampleBooksById = new Dictionary<int, Book>();

                        void PopulateSampleMatchCache()
                        {
                            if (sampleMatchCache.Count > 0)
                            {
                                return;
                            }

                            var seenSamplePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var sample in SelectSpreadSamples(subgroup.Files))
                            {
                                if (sample == null ||
                                    string.IsNullOrWhiteSpace(sample.Path) ||
                                    !seenSamplePaths.Add(sample.Path))
                                {
                                    continue;
                                }

                                cancellationToken.ThrowIfCancellationRequested();
                                var tags = GetTagsForUnscopedMatch(sample);
                                var result = TryMatchUnscopedOne(sample, tags);
                                sampleMatchCache[sample.Path] = (tags, result.match, result.rejections);
                            }
                        }

                        string GetIndependentSampleWorkKey(FileMatch sampleMatch)
                        {
                            if (sampleMatch?.IdentityProof == null)
                            {
                                return null;
                            }

                            var embeddedProof = new MatchIdentityProof(
                                sampleMatch.IdentityProof.Values.Where(value =>
                                    string.Equals(value.Source, "embedded_tag", StringComparison.OrdinalIgnoreCase)));
                            if (!MatchIdentityProofMembership.HasRequiredIdentity(embeddedProof))
                            {
                                return null;
                            }

                            return GetStableLogicalWorkKey(sampleMatch, sampleBooksById);
                        }

                        // A group seed may use only evidence shared by the members. Path fallback remains
                        // available when each file is rematched below, where one folder cannot stamp a mixed unit.
                        var groupMatchResult = TryMatchWithHolyGrail(subgroupFile, mediaType, disablePathFallback: true, hardAllowedBookIds);
                        var match = groupMatchResult?.Match;
                        if (match == null && usePathAsTagsFallback && !disablePathFallback)
                        {
                            PopulateSampleMatchCache();
                            var sampleWorkKeys = sampleMatchCache.Values
                                .Select(sample => sample.Match == null ? null : GetIndependentSampleWorkKey(sample.Match))
                                .ToList();
                            var samplesAgreeOnStableWork =
                                sampleWorkKeys.Count > 0 &&
                                sampleWorkKeys.All(key => !string.IsNullOrWhiteSpace(key)) &&
                                sampleWorkKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;

                            if (samplesAgreeOnStableWork)
                            {
                                _logger.Debug(
                                    "{0}[HOLY-GRAIL] Embedded group seed missed, but independent samples agree on provider work; retrying group with path evidence. files={1}",
                                    cid,
                                    subgroup.Files.Count);
                                groupMatchResult = TryMatchWithHolyGrail(subgroupFile, mediaType, disablePathFallback, hardAllowedBookIds);
                                match = groupMatchResult?.Match;
                            }
                        }

                        if (match != null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var proofEvidence = match.IdentityProof;

                            if (!HasRequiredAuthorAndBookProof(proofEvidence))
                            {
                                _logger.Debug("{0}[HOLY-GRAIL] GROUPED-SEED has no author+book proof; falling back to per-file. files={1}",
                                    cid, subgroup.Files.Count);
                                MatchPerFile(subgroup.Files, sampleMatchCache);
                                continue;
                            }

                            var membershipResults = subgroup.Files
                                .Select(file => new
                                {
                                    File = file,
                                    Membership = BuildHomogeneousProofMembership(
                                        match,
                                        file,
                                        GetTagsForUnscopedMatch(file),
                                        proofEvidence)
                                })
                                .ToList();

                            var groupedFiles = membershipResults
                                .Where(x => x.Membership.Passes)
                                .ToList();

                            var remainderFiles = membershipResults
                                .Where(x => !x.Membership.Passes)
                                .Select(x => x.File)
                                .ToList();

                            if (groupedFiles.Count == 0)
                            {
                                _logger.Debug("{0}[HOLY-GRAIL] GROUPED-SEED proof rejected every member; falling back to per-file. files={1}",
                                    cid, subgroup.Files.Count);
                                MatchPerFile(subgroup.Files, sampleMatchCache);
                                continue;
                            }

                            if (remainderFiles.Count > 0)
                            {
                                _logger.Debug("{0}[HOLY-GRAIL] GROUPED-SEED proof accepted {1}/{2}; rematching remainder={3}",
                                    cid, groupedFiles.Count, subgroup.Files.Count, remainderFiles.Count);
                            }

                            _logger.Debug("{0}[HOLY-GRAIL] GROUPED-SEED accepted: bk={1} '{2}' ed={3} author='{4}' files={5}",
                                cid, match.BookId, match.BookTitle, match.EditionId, match.AuthorName, groupedFiles.Count);

                            foreach (var grouped in groupedFiles)
                            {
                                var file = grouped.File;
                                var fm = CopyFileMatchForFile(match, file, grouped.Membership.IdentityProof);
                                groupedMatched.Add(fm);
                                LogDecisionWithProvenance(
                                    file.Path,
                                    "MATCHED",
                                    "Matched via Holy Grail FTS (grouped seed)",
                                    file.AllTags ?? subgroupTags,
                                    mediaType,
                                    fm,
                                    grouped.Membership.ProofTags ?? BuildIdentityProofTags(proofEvidence),
                                    groupMatchResult?.Evaluation?.PathFallbackUsed,
                                    groupMatchResult?.Evaluation?.PathFallbackSuppressedReason);
                            }

                            if (remainderFiles.Count > 0)
                            {
                                MatchPerFile(remainderFiles, sampleMatchCache);
                            }

                            continue;
                        }

                        PopulateSampleMatchCache();

                        if (allowGroupedV5Suggestions &&
                            allowV5Identification &&
                            subgroupHasIdentity &&
                            subgroupTags.Count > 0 &&
                            sampleMatchCache.Values.All(m => m.Match == null))
                        {
                            try
                            {
                                var subgroupHasContradictoryEvidence = sampleMatchCache.Values.Any(item =>
                                    HasContradictoryFallbackDisposition(item.Rejections));
                                var v5Suggested = TryV5SuggestionWithPathFallback(
                                    subgroupTags,
                                    mediaType,
                                    subgroupRep.Path,
                                    usePathAsTagsFallback &&
                                    !disablePathFallback &&
                                    !subgroupHasContradictoryEvidence,
                                    v5QuestionsWithoutSuggestion);
                                if (v5Suggested != null)
                                {
                                    var s = v5Suggested;
                                    var potentialAuthors = new[] { CreateAuthorSuggestion(s) };

                                    _logger.Debug("{0}[FALLBACK-V5] GROUPED manual-preview suggestion: author='{1}' providerId='{2}' files={3}",
                                        cid, s.AuthorName, s.ProviderId, subgroup.Files.Count);

                                    // V5 identifies provider identity; local matching still decides the Edition.
                                    // Resolve the suggested work through work-scoped aliases while the original
                                    // subgroup, member tags, and summed duration are intact, then run the shared
                                    // matcher once behind a hard Book boundary.
                                    if (TryResolveLocalV5WorkBoundary(
                                            s,
                                            mediaType,
                                            out var localSuggestedAuthor,
                                            out var localSuggestedBookIds,
                                            out var localBoundaryReason))
                                    {
                                        var localEvaluation = EvaluateHolyGrailMatchFileInternal(
                                            subgroupFile,
                                            mediaType,
                                            localSuggestedAuthor.Id,
                                            disablePathFallback,
                                            inferAuthorFromPathDuringPathFallback: true,
                                            unscoped: false,
                                            hardAllowedBookIds: localSuggestedBookIds);
                                        var localMatch = localEvaluation?.Match;
                                        if (localMatch != null)
                                        {
                                            var membershipResults = subgroup.Files
                                                .Select(file => new
                                                {
                                                    File = file,
                                                    Membership = BuildHomogeneousProofMembership(
                                                        localMatch,
                                                        file,
                                                        GetTagsForUnscopedMatch(file),
                                                        localMatch.IdentityProof)
                                                })
                                                .ToList();
                                            var accepted = membershipResults.Where(item => item.Membership.Passes).ToList();
                                            var outliers = membershipResults
                                                .Where(item => !item.Membership.Passes)
                                                .Select(item => item.File)
                                                .ToList();

                                            foreach (var acceptedMember in accepted)
                                            {
                                                var memberMatch = CopyFileMatchForFile(
                                                    localMatch,
                                                    acceptedMember.File,
                                                    acceptedMember.Membership.IdentityProof);
                                                if (memberMatch.Provenance != null)
                                                {
                                                    memberMatch.Provenance.Route = $"v5_provider_work_group/{memberMatch.Provenance.Route ?? "local_match"}";
                                                }

                                                groupedMatched.Add(memberMatch);
                                                LogDecisionWithProvenance(
                                                    acceptedMember.File.Path,
                                                    "MATCHED",
                                                    "V5 identified a local provider work; one grouped hard-Book rerun selected the Edition",
                                                    acceptedMember.File.AllTags ?? subgroupTags,
                                                    mediaType,
                                                    memberMatch,
                                                    acceptedMember.Membership.ProofTags,
                                                    localEvaluation.PathFallbackUsed,
                                                    localEvaluation.PathFallbackSuppressedReason);
                                            }

                                            if (accepted.Count > 0)
                                            {
                                                _logger.Debug(
                                                    "{0}[FALLBACK-V5] GROUPED local recovery accepted {1}/{2} files for BookIds=[{3}] EditionId={4}",
                                                    cid,
                                                    accepted.Count,
                                                    subgroup.Files.Count,
                                                    string.Join(",", localSuggestedBookIds.OrderBy(id => id)),
                                                    localMatch.EditionId);
                                                if (outliers.Count > 0)
                                                {
                                                    MatchPerFile(outliers, sampleMatchCache);
                                                }

                                                continue;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _logger.Debug(
                                            "{0}[FALLBACK-V5] GROUPED local provider-work recovery unavailable: {1}",
                                            cid,
                                            localBoundaryReason);
                                    }

                                    foreach (var file in subgroup.Files)
                                    {
                                        var um = new UnmatchedFile
                                        {
                                            File = file,
                                            Reason = s.Reason,
                                            PotentialAuthors = potentialAuthors
                                        };
                                        groupedUnmatched.Add(um);
                                        LogDecisionWithProvenance(
                                            file.Path,
                                            "UNMATCHED",
                                            um.Reason,
                                            subgroupTags,
                                            mediaType,
                                            rejections: sampleMatchCache.Values.SelectMany(m => m.Rejections ?? new List<CandidateRejection>()).Take(20).ToList(),
                                            commandId: ProgressMessageContext.CommandModel?.Id,
                                            correlationId: correlationId);
                                    }

                                    continue;
                                }
                            }
                            catch
                            {
                                // best-effort only; fall back to per-file handling below
                            }
                        }

                        _logger.Debug("{0}[HOLY-GRAIL] GROUPED-SEED miss: matching per-file. files={1}", cid, subgroup.Files.Count);
                        MatchPerFile(subgroup.Files, sampleMatchCache);
                    }

                    continue;
                }

                _logger.Debug("[NON-RESTRICT][HOLY-GRAIL-START] path='{0}' media={1}", representative.Path, mediaType);
                var matchResult = TryMatchWithHolyGrail(groupFile, mediaType, disablePathFallback, hardAllowedBookIds);
                _logger.Debug("[NON-RESTRICT][HOLY-GRAIL-END] path='{0}' matched={1}", representative.Path, matchResult?.Match != null);

                if (matchResult?.Match != null)
                {
                    foreach (var file in files)
                    {
                        var memberTags = file.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        var membership = BuildHomogeneousProofMembership(
                            matchResult.Match,
                            file,
                            memberTags,
                            matchResult.Match.IdentityProof);
                        var memberMatch = membership.Passes
                            ? CopyFileMatchForFile(matchResult.Match, file, membership.IdentityProof)
                            : EvaluateHolyGrailMatchFileInternal(
                                file,
                                mediaType,
                                restrictToAuthorId: null,
                                disablePathFallback,
                                inferAuthorFromPathDuringPathFallback: true,
                                unscoped: false,
                                hardAllowedBookIds: hardAllowedBookIds)?.Match;

                        if (memberMatch != null)
                        {
                            groupedMatched.Add(memberMatch);
                            LogDecisionWithProvenance(
                                file.Path,
                                "MATCHED",
                                membership.Passes ? "Matched via grouped FTS" : "Matched via per-file fallback after exact group proof failed",
                                memberTags,
                                mediaType,
                                memberMatch,
                                membership.Passes ? membership.ProofTags : memberTags,
                                matchResult.Evaluation?.PathFallbackUsed == true,
                                matchResult.Evaluation?.PathFallbackSuppressedReason);
                        }
                        else
                        {
                            var unmatched = new UnmatchedFile
                            {
                                File = file,
                                Reason = $"NO_MATCH_AFTER_GROUP_PROOF:{membership.Reason}",
                                PotentialAuthors = Array.Empty<AuthorSuggestion>()
                            };
                            groupedUnmatched.Add(unmatched);
                            LogDecisionWithProvenance(file.Path, "UNMATCHED", unmatched.Reason, memberTags, mediaType);
                        }
                    }
                }
                else
                {
                    V5SuggestionInfo v5Suggested = null;
                    if (allowV5Identification)
                    {
                        var groupedPathFallbackWasContradictory = string.Equals(
                            matchResult?.Evaluation?.PathFallbackSuppressedReason,
                            "blocked_by_embedded_contradiction",
                            StringComparison.OrdinalIgnoreCase);
                        v5Suggested = TryV5SuggestionWithPathFallback(
                            homogeneousTags,
                            mediaType,
                            representative.Path,
                            usePathAsTagsFallback &&
                            !disablePathFallback &&
                            !groupedPathFallbackWasContradictory,
                            v5QuestionsWithoutSuggestion);
                    }

                    // Short-circuit: confirmed v5 author + containment → resolve author folder (≥ 0.98), enqueue import, prune.
                        if (v5Suggested != null && allowAuthorImport && deferUnmatchedToAuthorReady)
                        {
                            var s = v5Suggested;
                            _logger.Debug("[PREAUTHOR][V5-AUTHOR-SUGGEST] file='{0}' author='{1}' providerId='{2}' reason='{3}'", representative.Path, s.AuthorName, s.ProviderId, s.Reason);
                            if (_containmentValidator.ValidateAuthorInTags(s.AuthorName, homogeneousTags))
                            {
                                // Resolve author folder by walking from root toward file, fuzzy matching author name
                                var resolvedAuthorFolder = GetAuthorFolder(representative.Path, s.AuthorName);
                                if (!string.IsNullOrWhiteSpace(resolvedAuthorFolder))
                                {
                                _logger.Debug("[SHORT-CIRCUIT] Author folder resolved: {0}", resolvedAuthorFolder);
                            }

                            if (!string.IsNullOrEmpty(resolvedAuthorFolder))
                            {
                                // Guard: if the author already exists, try to link files locally instead of queueing
                                try
                                {
                                    var colon = s.ProviderId?.IndexOf(':') ?? -1;
                                    if (colon <= 0 || string.IsNullOrWhiteSpace(s.ProviderId))
                                    {
                                        // Provider ID missing/invalid: do not drop files; mark as unmatched with hint
                                        if (!restrictToAuthorId.HasValue)
                                        {
                                            processedAuthorFolders.Add(resolvedAuthorFolder);
                                        }
                                        _logger.Debug("[SHORT-CIRCUIT] Missing or invalid providerId ('{0}') for suggested author '{1}'; marking {2} files as UNMATCHED",
                                            s.ProviderId ?? "<null>", s.AuthorName, files.Count);
                                        foreach (var file in files)
                                        {
                                            var um = new UnmatchedFile
                                            {
                                                File = file,
                                                Reason = "INVALID_PROVIDER_ID",
                                                PotentialAuthors = new[] { CreateAuthorSuggestion(s) }
                                            };
                                            groupedUnmatched.Add(um);
                                            LogDecisionWithProvenance(file.Path, "UNMATCHED", um.Reason, homogeneousTags, mediaType);
                                        }
                                        continue;
                                    }
                                    if (colon > 0)
                                    {
                                        var prefix = s.ProviderId.Substring(0, colon);
                                        var rawId = s.ProviderId.Substring(colon + 1);
                                        var existing = _authorService.FindByProviderId(prefix, rawId);
                                        if (existing != null)
                                        {
                                            // Ensure the local catalog contains the needed media type for this file group.
                                            // With per-root hydration, an author may exist locally but only for the other media type.
                                            var existingBooks = _bookService.GetBooksByAuthor(existing.Id) ?? new List<Book>();
                                            var hasRequestedMediaType = existingBooks.Any(b => b.MediaType == mediaType);
                                            if (!hasRequestedMediaType)
                                            {
                                                _logger.Debug("[SHORT-CIRCUIT] Existing author missing {0} books; backfilling from API before matching: {1}", mediaType, existing.Name);
                                                try
                                                {
                                                    // Backfill via library service (will not duplicate author)
                                                    if (TryBuildSuggestedAuthorMonitoringConfig(existing.Name, groupFile.Path, mediaType, out var backfillConfig) &&
                                                        HasExplicitRootFolderForMediaType(backfillConfig, mediaType))
                                                    {
                                                        _authorLibraryService.AddAuthorAsync(s.ProviderId, backfillConfig).GetAwaiter().GetResult();
                                                    }
                                                    else
                                                    {
                                                        _logger.Debug("[SHORT-CIRCUIT] Skipping existing author backfill for {0}; could not resolve a {1} root folder from '{2}'",
                                                            existing.Name, mediaType, groupFile.Path);
                                                    }
                                                }
                                                catch (Exception backfillEx)
                                                {
                                                    _logger.Warn(backfillEx, "[SHORT-CIRCUIT] Backfill failed for existing author {0}", existing.Name);
                                                }
                                            }

                                            // Attempt local edition match restricted to this author using Holy Grail
                                            var localMatch = EvaluateHolyGrailMatchFileInternal(
                                                groupFile,
                                                mediaType,
                                                existing.Id,
                                                disablePathFallback: true,
                                                inferAuthorFromPathDuringPathFallback: true,
                                                unscoped: false,
                                                hardAllowedBookIds: hardAllowedBookIds)?.Match;
                                            if (localMatch != null)
                                            {
                                                _logger.Debug("[SHORT-CIRCUIT] Local match found for existing author {0}: Book '{1}'", existing.Name, localMatch.BookTitle);
                                                foreach (var file in files)
                                                {
                                                    var memberTags = file.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                                                    var membership = BuildHomogeneousProofMembership(
                                                        localMatch,
                                                        file,
                                                        memberTags,
                                                        localMatch.IdentityProof);
                                                    var memberMatch = membership.Passes
                                                        ? CopyFileMatchForFile(localMatch, file, membership.IdentityProof)
                                                        : EvaluateHolyGrailMatchFileInternal(
                                                            file,
                                                            mediaType,
                                                            existing.Id,
                                                            disablePathFallback: true,
                                                            inferAuthorFromPathDuringPathFallback: true,
                                                            unscoped: false,
                                                            hardAllowedBookIds: hardAllowedBookIds)?.Match;

                                                    if (memberMatch != null)
                                                    {
                                                        groupedMatched.Add(memberMatch);
                                                        LogDecisionWithProvenance(
                                                            file.Path,
                                                            "MATCHED",
                                                            membership.Passes
                                                                ? "Matched via Holy Grail FTS (existing author short-circuit)"
                                                                : "Matched per-file after exact short-circuit group proof failed",
                                                            memberTags,
                                                            mediaType,
                                                            memberMatch,
                                                            membership.Passes ? membership.ProofTags : memberTags,
                                                            pathFallbackUsed: false,
                                                            pathFallbackSuppressedReason: "disabled_by_context");
                                                    }
                                                    else
                                                    {
                                                        var unmatched = new UnmatchedFile
                                                        {
                                                            File = file,
                                                            Reason = $"NO_MATCH_AFTER_GROUP_PROOF:{membership.Reason}",
                                                            PotentialAuthors = Array.Empty<AuthorSuggestion>()
                                                        };
                                                        groupedUnmatched.Add(unmatched);
                                                        LogDecisionWithProvenance(file.Path, "UNMATCHED", unmatched.Reason, memberTags, mediaType);
                                                    }
                                                }
                                                continue;
                                            }

                                            // If local match failed, do not spam queue if author exists.
                                            // IMPORTANT: Do NOT drop the files on the floor. Return them as UNMATCHED
                                            // so the orchestrator can record a result and avoid infinite requeue loops.
                                            if (!restrictToAuthorId.HasValue)
                                            {
                                                processedAuthorFolders.Add(resolvedAuthorFolder);
                                            }
                                            _logger.Debug("[SHORT-CIRCUIT] Author exists but no local edition match; marking {0} files as UNMATCHED for author {1}", files.Count, s.ProviderId);

                                            foreach (var file in files)
                                            {
                                                var um = new UnmatchedFile
                                                {
                                                    File = file,
                                                    Reason = $"NO_EDITION_FOUND (authorId={existing.Id})",
                                                    PotentialAuthors = new[] { CreateAuthorSuggestion(s) }
                                                };
                                                groupedUnmatched.Add(um);
                                                LogDecisionWithProvenance(file.Path, "UNMATCHED", um.Reason, homogeneousTags, mediaType);
                                            }
                                            continue;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(ex, "[SHORT-CIRCUIT] Error checking existing author {0}", s.ProviderId);
                                }

                                // Build monitoring config based on the root folder's media type (inheritance rules)
                                var rf = _rootFolderService.GetBestRootFolder(representative.Path);
                                var config = new MonitoringConfig
                                {
                                    AuthorName = s.AuthorName,
                                    DiscoveredAuthorFolderPath = resolvedAuthorFolder,
                                    QueueIfUnavailable = true,
                                    RequestedBy = "FileMatchingService"
                                };

                                switch (rf.FolderType)
                                {
                                    case FolderType.Audiobook:
                                        config.CreateAudiobook = true;
                                        config.CreateEbook = false; // ebook settings remain NULL intentionally
                                        config.AudiobookRootFolderPath = rf.Path;
                                        var a = rf.GetAudiobookSettings();
                                        if (a != null)
                                        {
                                            config.AudiobookQualityProfileId = a.QualityProfileId;
                                            config.AudiobookMetadataProfileId = a.MetadataProfileId;
                                            config.AudiobookMonitored = a.Monitored;
                                            config.AudiobookMonitorNewItems = a.MonitorNewItems;
                                            config.AudiobookMonitorExistingMode = ResolveRootMonitorExistingMode(a);
                                            config.MergeTagsForMediaType(BookMediaType.Audiobook, a.Tags);
                                        }
                                        break;
                                    case FolderType.Ebook:
                                        config.CreateAudiobook = false; // audiobook settings remain NULL intentionally
                                        config.CreateEbook = true;
                                        config.EbookRootFolderPath = rf.Path;
                                        var e = rf.GetEbookSettings();
                                        if (e != null)
                                        {
                                            config.EbookQualityProfileId = e.QualityProfileId;
                                            config.EbookMetadataProfileId = e.MetadataProfileId;
                                            config.EbookMonitored = e.Monitored;
                                            config.EbookMonitorNewItems = e.MonitorNewItems;
                                            config.EbookMonitorExistingMode = ResolveRootMonitorExistingMode(e);
                                            config.MergeTagsForMediaType(BookMediaType.Ebook, e.Tags);
                                        }
                                        break;
                                    case FolderType.Mixed:
                                    default:
                                        var mixedTypes = ResolveCreateMediaTypes(rf, mediaType);
                                        config.CreateAudiobook = mixedTypes.CreateAudiobook;
                                        config.CreateEbook = mixedTypes.CreateEbook;
                                        if (config.CreateAudiobook)
                                        {
                                            config.AudiobookRootFolderPath = rf.Path;
                                            var ma = rf.GetAudiobookSettings();
                                            if (ma != null)
                                            {
                                                config.AudiobookQualityProfileId = ma.QualityProfileId;
                                                config.AudiobookMetadataProfileId = ma.MetadataProfileId;
                                                config.AudiobookMonitored = ma.Monitored;
                                                config.AudiobookMonitorNewItems = ma.MonitorNewItems;
                                                config.AudiobookMonitorExistingMode = ResolveRootMonitorExistingMode(ma);
                                                config.MergeTagsForMediaType(BookMediaType.Audiobook, ma.Tags);
                                            }
                                        }
                                        if (config.CreateEbook)
                                        {
                                            config.EbookRootFolderPath = rf.Path;
                                            var me = rf.GetEbookSettings();
                                            if (me != null)
                                            {
                                                config.EbookQualityProfileId = me.QualityProfileId;
                                                config.EbookMetadataProfileId = me.MetadataProfileId;
                                                config.EbookMonitored = me.Monitored;
                                                config.EbookMonitorNewItems = me.MonitorNewItems;
                                                config.EbookMonitorExistingMode = ResolveRootMonitorExistingMode(me);
                                                config.MergeTagsForMediaType(BookMediaType.Ebook, me.Tags);
                                            }
                                        }
                                        break;
                                }

                                try
                                {
                                    // Import the author immediately; this triggers AuthorRefreshCompleteEvent
                                    var added = _authorLibraryService.AddAuthorAsync(s.ProviderId, config).GetAwaiter().GetResult();
                                    if (added != null && added.Id > 0)
                                    {
                                        _logger.Debug("[SHORT-CIRCUIT] Added author '{0}' ({1}); skipping remaining groups under: {2}",
                                            s.AuthorName, s.ProviderId, resolvedAuthorFolder);
                                    }
                                    else
                                    {
                                        _logger.Debug("[SHORT-CIRCUIT] Author '{0}' ({1}) already existed or could not be added now", s.AuthorName, s.ProviderId);
                                    }

                                    // Publish a quick progress tick for UI
                                    authorsQueued++;
                                    var qevt = new MediaFiles.Events.ImportStageProgressEvent(
                                        MediaFiles.Events.ImportStage.ImportingAuthorsToDatabase,
                                        $"Imported/verified author '{s.AuthorName}'",
                                        currentProgress: authorsQueued,
                                        totalProgress: authorsQueued)
                                    {
                                        AuthorsImported = authorsQueued,
                                        CurrentItemName = s.AuthorName,
                                        CurrentItemType = "author"
                                    };
                                    qevt.CommandId = ProgressMessageContext.CommandModel?.Id;
                                    _eventAggregator.PublishEvent(qevt);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(ex, "[SHORT-CIRCUIT] Failed to add author immediately for {0}", s.ProviderId);
                                }

                                if (!restrictToAuthorId.HasValue)
                                {
                                    processedAuthorFolders.Add(resolvedAuthorFolder);
                                }
                                shortCircuitedAny = true;

                                // No candidates found for this group: explicitly record all files as unmatched
                                foreach (var file in files)
                                {
                                    var finalUnmatched = new UnmatchedFile
                                    {
                                        File = file,
                                        Reason = "NO_FTS_RESULTS",
                                        PotentialAuthors = new AuthorSuggestion[0]
                                    };
                                    groupedUnmatched.Add(finalUnmatched);
                                    LogDecisionWithProvenance(file.Path, "UNMATCHED", finalUnmatched.Reason, homogeneousTags, mediaType);
                                }
                                // Move to next group
                                continue;
                            }
                        }
                    }
                    // In restricted path we can safely emit unmatched; in gated path this is not reached
                    var finalReason = "NO_MATCH";
                    var finalPotentialAuthors = Array.Empty<AuthorSuggestion>();
                    if (v5Suggested != null)
                    {
                        var s = v5Suggested;
                        finalReason = s.Reason ?? finalReason;
                        finalPotentialAuthors = new[] { CreateAuthorSuggestion(s) };
                    }
                    foreach (var file in files)
                    {
                        var finalUnmatched = new UnmatchedFile
                        {
                            File = file,
                            Reason = finalReason,
                            PotentialAuthors = finalPotentialAuthors
                        };
                        groupedUnmatched.Add(finalUnmatched);
                        LogDecisionWithProvenance(file.Path, "UNMATCHED", finalUnmatched.Reason, homogeneousTags, mediaType);
                    }
                }
                // Publish per-group progress after completing this group
                processedUnits++;
                try
                {
                    var pevt = new MediaFiles.Events.ImportStageProgressEvent(
                        MediaFiles.Events.ImportStage.MatchingAuthorsLocally,
                        $"Processed {processedUnits} of {groups.Count} file groups",
                        currentProgress: processedUnits,
                        totalProgress: groups.Count)
                    {
                        ProcessedBookFolders = processedUnits,
                        BookUnitsDiscovered = groups.Count,
                        AuthorsQueued = authorsQueued,
                        CurrentItemName = GetSafeFolderDisplayName(group.Key.Folder),
                        CurrentItemType = "folder"
                    };
                    pevt.CommandId = ProgressMessageContext.CommandModel?.Id;
                    _eventAggregator.PublishEvent(pevt);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[PROGRESS] Failed to publish per-group progress event");
                }
            }

            if (groupedMatched.Count > 0 || groupedUnmatched.Count > 0 || shortCircuitedAny)
            {
                result.MatchedFiles = groupedMatched.ToArray();
                result.UnmatchedFiles = groupedUnmatched.ToArray();

                totalStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] File matching complete: {0} matched, {1} unmatched. Total time: {2}ms ({3:F2}s)",
                    result.MatchedFiles.Length, result.UnmatchedFiles.Length,
                    totalStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds / 1000.0);

                _logger.Debug("[FILE-FLOW] ====== MatchFilesToLibraryAsync END ======");
                _logger.Debug("[FILE-FLOW] Matched files:");
                foreach (var match in result.MatchedFiles.Take(5))
                {
                    _logger.Debug("[FILE-FLOW]   - {0} -> Book '{1}' (ID: {2})", match.File.Path, match.BookTitle, match.BookId);
                }
                _logger.Debug("[FILE-FLOW] Unmatched files:");
                foreach (var unmatched in result.UnmatchedFiles.Take(5))
                {
                    _logger.Debug("[FILE-FLOW]   - {0}: {1}", unmatched.File.Path, unmatched.Reason);
                }

                return Task.FromResult(result);
            }

            var matchedList = new System.Collections.Generic.List<FileMatch>();
            var unmatchedList = new System.Collections.Generic.List<UnmatchedFile>();

            // Process ALL files through batched FTS matching
            _logger.Debug("[FILE-MATCHING] Processing {0} files through batched FTS matching", filesWithMetadata.Length);

            foreach (var file in filesWithMetadata)
            {
                var fileStopwatch = Stopwatch.StartNew();

                // Determine media type from file extension
                var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
                var isAudiobook = MediaFileExtensions.AudioExtensions.Contains(ext);
                var mediaType = isAudiobook ? BookMediaType.Audiobook : BookMediaType.Ebook;
                _logger.Debug("[FILE-MATCHING] Processing file: {0}, Extension: {1}, MediaType: {2}",
                    Path.GetFileName(file.Path), ext, mediaType);

                // HOLY GRAIL: Use simple FTS + smoke test for all files
                if (restrictToAuthorId.HasValue)
                {
                    // Author-restricted mode: ONLY match within the specified author, NO fallback to other authors
                    try
                    {
                        var rejections = new List<CandidateRejection>();
                        var evaluation = RunWithRejectionCapture(
                            "scoped",
                            () => EvaluateHolyGrailMatchFileInternal(file, mediaType, restrictToAuthorId.Value, disablePathFallback: disablePathFallback, inferAuthorFromPathDuringPathFallback: true, unscoped: false, hardAllowedBookIds: hardAllowedBookIds),
                            rejections);
                        var match = evaluation?.Match;
                        if (match != null)
                        {
                            matchedList.Add(match);
                            LogDecisionWithProvenance(
                                file.Path,
                                "MATCHED",
                                "Matched via Holy Grail FTS + smoke test (author-restricted)",
                                file.AllTags,
                                mediaType,
                                match,
                                evaluation?.WinningTags,
                                evaluation?.PathFallbackUsed,
                                evaluation?.PathFallbackSuppressedReason);
                            fileStopwatch.Stop();
                            _logger.Debug("[PERFORMANCE] File matching (author-restricted, Holy Grail) for '{0}' took {1}ms",
                                Path.GetFileName(file.Path), fileStopwatch.ElapsedMilliseconds);
                            continue;
                        }
                        else
                        {
                            // No match for this author - optionally recover via embedded-tag V5 suggestion, but never fall back to unrestricted local search.
                            AuthorSuggestion[] potentialAuthors = Array.Empty<AuthorSuggestion>();
                            var unmatchedReason = $"No matching edition for author ID {restrictToAuthorId.Value}";
                            var embeddedTags = file.AllTags != null && file.AllTags.Count > 0
                                ? CloneTags(file.AllTags)
                                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            if (allowV5Identification)
                            {
                                var recovery = TryRecoverRestrictedMissViaV5(
                                    file,
                                    embeddedTags,
                                    mediaType,
                                    allowAuthorImport,
                                    usePathAsTagsFallback,
                                    v5QuestionsWithoutSuggestion,
                                    hardAllowedBookIds);
                                if (recovery.match != null)
                                {
                                    matchedList.Add(recovery.match);
                                    LogDecisionWithProvenance(
                                        file.Path,
                                        "MATCHED",
                                        "Matched via V5 author recovery + Holy Grail FTS (author-restricted)",
                                        file.AllTags,
                                        mediaType,
                                        recovery.match,
                                        embeddedTags,
                                        pathFallbackUsed: false,
                                        pathFallbackSuppressedReason: "recovered_author_scoped");
                                    fileStopwatch.Stop();
                                    _logger.Debug("[PERFORMANCE] File matching (author-restricted, V5 recovery) for '{0}' took {1}ms",
                                        Path.GetFileName(file.Path), fileStopwatch.ElapsedMilliseconds);
                                    continue;
                                }

                                if (recovery.suggestion != null)
                                {
                                    potentialAuthors = new[] { recovery.suggestion };
                                    unmatchedReason = recovery.reason ?? unmatchedReason;
                                }
                            }

                            unmatchedList.Add(new UnmatchedFile
                            {
                                File = file,
                                Reason = unmatchedReason,
                                PotentialAuthors = potentialAuthors
                            });
                            LogDecisionWithProvenance(
                                file.Path,
                                "UNMATCHED",
                                unmatchedReason,
                                file.AllTags,
                                mediaType,
                                proofTags: embeddedTags,
                                pathFallbackUsed: evaluation?.PathFallbackUsed,
                                pathFallbackSuppressedReason: evaluation?.PathFallbackSuppressedReason,
                                rejections: NullIfEmpty(rejections));
                            fileStopwatch.Stop();
                            _logger.Debug("[FILE-MATCHING] No author-restricted match for '{0}' (author ID {1})",
                                Path.GetFileName(file.Path), restrictToAuthorId.Value);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[FILE-MATCHING] Author-restricted Holy Grail matching failed for '{0}'", Path.GetFileName(file.Path));
                        unmatchedList.Add(new UnmatchedFile
                        {
                            File = file,
                            Reason = $"Error during author-restricted matching: {ex.Message}"
                        });
                        continue;
                    }
                }

                // Unrestricted mode: search all authors
                var matchResult = TryMatchWithHolyGrail(file, mediaType, disablePathFallback, hardAllowedBookIds);

                if (matchResult?.Match != null)
                {
                    matchedList.Add(matchResult.Match);
                    _logger.Debug("[FILE-MATCHING] Successfully matched '{0}' to {1} - {2}",
                        Path.GetFileName(file.Path),
                        matchResult.Match.AuthorName,
                        matchResult.Match.BookTitle);

                    // Log successful match
                    LogDecisionWithProvenance(
                        file.Path,
                        "MATCHED",
                        "Successfully matched via FTS",
                        file.AllTags,
                        mediaType,
                        matchResult.Match,
                        matchResult.Evaluation?.WinningTags ?? file.AllTags,
                        matchResult.Evaluation?.PathFallbackUsed,
                        matchResult.Evaluation?.PathFallbackSuppressedReason);
                }
                    else
                    {
                        V5SuggestionInfo v5Suggested = null;
                        if (allowV5Identification)
                        {
                            var localPathFallbackWasContradictory = string.Equals(
                                matchResult?.Evaluation?.PathFallbackSuppressedReason,
                                "blocked_by_embedded_contradiction",
                                StringComparison.OrdinalIgnoreCase);
                            v5Suggested = TryV5SuggestionWithPathFallback(
                                file.AllTags,
                                mediaType,
                                file.Path,
                                usePathAsTagsFallback && !localPathFallbackWasContradictory,
                                v5QuestionsWithoutSuggestion);
                        }

                        // Still unmatched — record
                        var finalUnmatched = matchResult?.UnmatchedFile;
                        if (v5Suggested != null)
                    {
                        var s = v5Suggested;
                        finalUnmatched = new UnmatchedFile
                        {
                            File = file,
                            Reason = s.Reason,
                            PotentialAuthors = new[] { CreateAuthorSuggestion(s) }
                        };
                        LogDecisionWithProvenance(
                            file.Path,
                            "UNMATCHED",
                            finalUnmatched.Reason,
                            file.AllTags,
                            mediaType,
                            proofTags: file.AllTags,
                            pathFallbackUsed: matchResult?.Evaluation?.PathFallbackUsed,
                            pathFallbackSuppressedReason: matchResult?.Evaluation?.PathFallbackSuppressedReason,
                            rejections: matchResult?.Rejections);
                    }
                    unmatchedList.Add(finalUnmatched);
                    _logger.Debug("[FILE-MATCHING] No match found for '{0}': {1}",
                        Path.GetFileName(file.Path),
                        finalUnmatched.Reason);

                    if (v5Suggested == null)
                    {
                        LogDecisionWithProvenance(
                            file.Path,
                            "UNMATCHED",
                            finalUnmatched.Reason,
                            file.AllTags,
                            mediaType,
                            proofTags: file.AllTags,
                            pathFallbackUsed: matchResult?.Evaluation?.PathFallbackUsed,
                            pathFallbackSuppressedReason: matchResult?.Evaluation?.PathFallbackSuppressedReason,
                            rejections: matchResult?.Rejections);
                    }
                }

                fileStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] File matching for '{0}' took {1}ms",
                    Path.GetFileName(file.Path), fileStopwatch.ElapsedMilliseconds);
            }

            result.MatchedFiles = matchedList.ToArray();
            result.UnmatchedFiles = unmatchedList.ToArray();

            totalStopwatch.Stop();
            _logger.Debug("[PERFORMANCE] File matching complete: {0} matched, {1} unmatched. Total time: {2}ms ({3:F2}s)",
                result.MatchedFiles.Length, result.UnmatchedFiles.Length,
                totalStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds / 1000.0);

            _logger.Debug("[FILE-FLOW] ====== MatchFilesToLibraryAsync END ======");
            _logger.Debug("[FILE-FLOW] Matched files:");
            foreach (var match in result.MatchedFiles.Take(5))
            {
                _logger.Debug("[FILE-FLOW]   - {0} -> Book '{1}' (ID: {2})", match.File.Path, match.BookTitle, match.BookId);
            }
            _logger.Debug("[FILE-FLOW] Unmatched files:");
            foreach (var unmatched in result.UnmatchedFiles.Take(5))
            {
                _logger.Debug("[FILE-FLOW]   - {0}: {1}", unmatched.File.Path, unmatched.Reason);
            }

            return Task.FromResult(result);
        }

        private V5SuggestionInfo TryV5Suggestion(
            Dictionary<string, List<string>> tags,
            BookMediaType mediaType,
            string filePath,
            bool includeFileNameEvidence,
            out bool contradictoryAuthorEvidence)
        {
            contradictoryAuthorEvidence = false;
            try
            {
                var q = CanonicalMatchInputBuilder.BuildEmbeddedQuery(tags);
                var media = mediaType == BookMediaType.Audiobook ? "audio" : "ebook";
                var matches = _v5MatchingService.SearchV5Matching(q, tags, media, includeFileNameEvidence ? filePath : null);
                var top = matches?.FirstOrDefault();
                if (top == null || string.IsNullOrWhiteSpace(top.id))
                {
                    return null;
                }

                // containment against provided tags
                if (!_containmentValidator.ValidateAuthorInTags(top.name, tags))
                {
                    var contradictionTags = CategorizeTagsForHolyGrail(tags);
                    contradictoryAuthorEvidence = matches
                        .Where(match => match != null && !string.IsNullOrWhiteSpace(match.name))
                        .Any(match => _containmentValidator.ValidateAuthorInTags(match.name, contradictionTags));
                    _logger.Debug("[FALLBACK-V5] Author '{0}' failed containment for '{1}'", top.name, Path.GetFileName(filePath));
                    return null;
                }

                var label = !string.IsNullOrWhiteSpace(top.work_title) ? $"{top.name} - {top.work_title}" : top.name;
                var reason = $"No local match; V5 suggested '{label}'";
                return new V5SuggestionInfo
                {
                    ProviderId = top.id,
                    AuthorName = top.name,
                    Confidence = 0.8,
                    BookProviderId = top.work_id,
                    BookTitle = top.work_title,
                    EditionHardcoverId = top.edition_hardcover_id,
                    EditionTitle = top.edition_title,
                    Reason = reason
                };
            }
            catch (Exception ex)
            {
                contradictoryAuthorEvidence = false;
                _logger.Debug(ex, "[FALLBACK-V5] Error during V5 suggestion for '{0}'", Path.GetFileName(filePath));
                return null;
            }
        }

        private V5SuggestionInfo TryV5SuggestionWithPathFallback(
            Dictionary<string, List<string>> tags,
            BookMediaType mediaType,
            string filePath,
            bool allowPathFallback,
            HashSet<string> questionsWithoutSuggestion)
        {
            var suggestion = TryV5SuggestionOnce(
                tags,
                mediaType,
                filePath,
                allowPathFallback,
                questionsWithoutSuggestion,
                out var contradictoryAuthorEvidence,
                out var questionWasSuppressed);
            if (suggestion != null)
            {
                return suggestion;
            }

            if (questionWasSuppressed || !allowPathFallback || contradictoryAuthorEvidence)
            {
                return null;
            }

            var pathTags = BuildPathDerivedTags(filePath);
            if (pathTags == null || pathTags.Count == 0)
            {
                return null;
            }

            var combinedTags = MergeEvidenceTags(tags, pathTags);
            _logger.Debug("[FALLBACK-V5] Retrying V5 suggestion with embedded plus path-derived evidence for '{0}'", Path.GetFileName(filePath));
            return TryV5SuggestionOnce(
                combinedTags,
                mediaType,
                filePath,
                includeFileNameEvidence: true,
                questionsWithoutSuggestion,
                out _,
                out _);
        }

        private V5SuggestionInfo TryV5SuggestionOnce(
            Dictionary<string, List<string>> tags,
            BookMediaType mediaType,
            string filePath,
            bool includeFileNameEvidence,
            HashSet<string> questionsWithoutSuggestion,
            out bool contradictoryAuthorEvidence,
            out bool questionWasSuppressed)
        {
            questionWasSuppressed = false;
            var transmittedFilePath = includeFileNameEvidence ? filePath : null;
            var questionKey = BuildV5QuestionKey(tags, mediaType, transmittedFilePath);
            if (questionKey != null && !questionsWithoutSuggestion.Add(questionKey))
            {
                contradictoryAuthorEvidence = false;
                questionWasSuppressed = true;
                _logger.Debug("[FALLBACK-V5] Skipping repeated no-suggestion question for '{0}'", Path.GetFileName(filePath));
                return null;
            }

            var suggestion = TryV5Suggestion(
                tags,
                mediaType,
                filePath,
                includeFileNameEvidence,
                out contradictoryAuthorEvidence);
            if (suggestion != null && questionKey != null)
            {
                questionsWithoutSuggestion.Remove(questionKey);
            }

            return suggestion;
        }

        private static string BuildV5QuestionKey(
            Dictionary<string, List<string>> tags,
            BookMediaType mediaType,
            string transmittedFilePath)
        {
            var evidenceKey = BookImportUnitGroupingService.BuildIdentityKey(tags);
            if (string.IsNullOrWhiteSpace(evidenceKey))
            {
                return null;
            }

            var fileNameKey = transmittedFilePath == null
                ? string.Empty
                : BookImportUnitGroupingService.NormalizeIdentityValue(Path.GetFileName(transmittedFilePath));
            if (transmittedFilePath != null && string.IsNullOrWhiteSpace(fileNameKey))
            {
                return null;
            }

            return $"{mediaType}\u001D{evidenceKey}\u001D{fileNameKey}";
        }

        private static AuthorSuggestion CreateAuthorSuggestion(V5SuggestionInfo suggestion)
        {
            if (suggestion == null)
            {
                return null;
            }

            return new AuthorSuggestion
            {
                ProviderId = suggestion.ProviderId,
                AuthorName = suggestion.AuthorName,
                Confidence = suggestion.Confidence,
                BookProviderId = suggestion.BookProviderId,
                BookTitle = suggestion.BookTitle,
                EditionHardcoverId = suggestion.EditionHardcoverId,
                EditionTitle = suggestion.EditionTitle
            };
        }

        private bool TryResolveLocalV5WorkBoundary(
            V5SuggestionInfo suggestion,
            BookMediaType mediaType,
            out Author author,
            out HashSet<int> allowedBookIds,
            out string reason)
        {
            author = null;
            allowedBookIds = null;
            reason = null;
            if (suggestion == null ||
                string.IsNullOrWhiteSpace(suggestion.ProviderId) ||
                string.IsNullOrWhiteSpace(suggestion.BookProviderId) ||
                _authorService == null ||
                _bookService == null)
            {
                reason = "V5_LOCAL_WORK_IDENTITY_MISSING";
                return false;
            }

            if (!ProviderIdHelper.TryNormalize(suggestion.ProviderId, defaultPrefix: null, out var authorProviderId) ||
                !ProviderIdHelper.TryNormalize(suggestion.BookProviderId, defaultPrefix: null, out var workProviderId))
            {
                reason = "V5_LOCAL_WORK_IDENTITY_INVALID";
                return false;
            }

            var authorSeparator = authorProviderId.IndexOf(':');
            Author resolvedAuthor;
            try
            {
                resolvedAuthor = _authorService.FindByProviderId(
                    authorProviderId.Substring(0, authorSeparator),
                    ProviderIdHelper.StripPrefix(authorProviderId));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FALLBACK-V5] Failed to resolve local provider author '{0}'", suggestion.ProviderId);
                reason = "V5_LOCAL_WORK_LOOKUP_FAILED";
                return false;
            }

            if (resolvedAuthor?.Id <= 0)
            {
                reason = "V5_AUTHOR_NOT_LOCAL";
                return false;
            }

            if (!LocalProviderWorkBoundaryResolver.TryResolve(
                    _bookService,
                    resolvedAuthor,
                    workProviderId,
                    mediaType,
                    _logger,
                    "FALLBACK-V5",
                    out var candidates,
                    out reason))
            {
                return false;
            }

            author = resolvedAuthor;
            allowedBookIds = candidates.Select(book => book.Id).ToHashSet();
            return true;
        }

        private (FileMatch match, AuthorSuggestion suggestion, string reason) TryRecoverRestrictedMissViaV5(
            DiscoveredFileWithMetadata file,
            Dictionary<string, List<string>> v5Tags,
            BookMediaType mediaType,
            bool allowAuthorImport,
            bool includeFileNameEvidence,
            HashSet<string> questionsWithoutSuggestion,
            IReadOnlySet<int> hardAllowedBookIds = null)
        {
            if (file?.Path == null || v5Tags == null || v5Tags.Count == 0)
            {
                return (null, null, null);
            }

            var v5Suggested = TryV5SuggestionOnce(
                v5Tags,
                mediaType,
                file.Path,
                includeFileNameEvidence,
                questionsWithoutSuggestion,
                out _,
                out _);
            if (v5Suggested == null)
            {
                return (null, null, null);
            }

            var s = v5Suggested;
            var suggestion = CreateAuthorSuggestion(s);

            if (!allowAuthorImport)
            {
                return (null, suggestion, s.Reason);
            }

            var recoveredAuthor = TryGetOrImportSuggestedAuthorForRestrictedRecovery(s.ProviderId, s.AuthorName, file.Path, mediaType);
            if (recoveredAuthor == null || recoveredAuthor.Id <= 0)
            {
                return (null, suggestion, "AUTHOR_IMPORT_FAILED");
            }

            var strictMatch = EvaluateHolyGrailMatchFileInternal(
                file,
                mediaType,
                recoveredAuthor.Id,
                disablePathFallback: true,
                inferAuthorFromPathDuringPathFallback: true,
                unscoped: false,
                hardAllowedBookIds: hardAllowedBookIds)?.Match;
            if (strictMatch != null)
            {
                return (strictMatch, suggestion, $"Recovered via V5 author '{s.AuthorName}'");
            }

            return (null, suggestion, $"NO_EDITION_FOUND (authorId={recoveredAuthor.Id})");
        }

        private Author TryGetOrImportSuggestedAuthorForRestrictedRecovery(string providerId, string authorName, string samplePath, BookMediaType mediaType)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            var colon = providerId.IndexOf(':');
            if (colon <= 0 || colon >= providerId.Length - 1)
            {
                return null;
            }

            var provider = providerId.Substring(0, colon);
            var rawId = providerId.Substring(colon + 1);

            Author existing = null;
            try
            {
                existing = _authorService?.FindByProviderId(provider, rawId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FALLBACK-V5] Failed provider lookup for '{0}'", providerId);
            }

            if (existing != null)
            {
                try
                {
                    var existingBooks = _bookService?.GetBooksByAuthor(existing.Id) ?? new List<Book>();
                    var hasRequestedMediaType = existingBooks.Any(b => b.MediaType == mediaType);
                    if (!hasRequestedMediaType && _authorLibraryService != null)
                    {
                        if (TryBuildSuggestedAuthorMonitoringConfig(existing.Name, samplePath, mediaType, out var backfillConfig) &&
                            HasExplicitRootFolderForMediaType(backfillConfig, mediaType))
                        {
                            _authorLibraryService.AddAuthorAsync(providerId, backfillConfig).GetAwaiter().GetResult();
                        }
                        else
                        {
                            _logger.Debug("[FALLBACK-V5] Skipping existing author backfill for '{0}'; could not resolve a {1} root folder from '{2}'",
                                providerId, mediaType, samplePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[FALLBACK-V5] Existing author backfill failed for '{0}'", providerId);
                }

                return existing;
            }

            if (_authorLibraryService == null || _rootFolderService == null)
            {
                return null;
            }

            if (!TryBuildSuggestedAuthorMonitoringConfig(authorName, samplePath, mediaType, out var config))
            {
                return null;
            }

            try
            {
                var added = _authorLibraryService.AddAuthorAsync(providerId, config).GetAwaiter().GetResult();
                if (added != null && added.Id > 0)
                {
                    return added;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FALLBACK-V5] Failed to import suggested author '{0}' ({1})", authorName ?? "<unknown>", providerId);
            }

            try
            {
                return _authorService?.FindByProviderId(provider, rawId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FALLBACK-V5] Provider lookup after import failed for '{0}'", providerId);
                return null;
            }
        }

        private static MonitorTypes? ResolveRootMonitorExistingMode(MediaTypeSettings settings)
        {
            return RootFolderSettingsResolver.ResolveInitialMonitorMode(settings?.MonitorExistingMode);
        }

        private bool TryBuildSuggestedAuthorMonitoringConfig(string authorName, string samplePath, BookMediaType mediaType, out MonitoringConfig config)
        {
            config = null;

            if (string.IsNullOrWhiteSpace(samplePath))
            {
                return false;
            }

            RootFolder rootFolder = null;
            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(samplePath);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[FALLBACK-V5] Failed to resolve root folder for '{0}'", samplePath);
            }

            var wantAudiobooks = mediaType == BookMediaType.Audiobook;
            var wantEbooks = mediaType == BookMediaType.Ebook;

            if (rootFolder != null)
            {
                if (rootFolder.FolderType == FolderType.Audiobook)
                {
                    wantEbooks = false;
                }
                else if (rootFolder.FolderType == FolderType.Ebook)
                {
                    wantAudiobooks = false;
                }
            }

            if (!wantAudiobooks && !wantEbooks)
            {
                return false;
            }

            var cfg = new MonitoringConfig
            {
                AuthorName = authorName,
                QueueIfUnavailable = false,
                RequestedBy = "FileMatchingService",
                CreateAudiobook = wantAudiobooks,
                CreateEbook = wantEbooks
            };

            if (rootFolder != null)
            {
                if (rootFolder.DefaultTags != null)
                {
                    if (cfg.CreateAudiobook)
                    {
                        cfg.MergeTagsForMediaType(BookMediaType.Audiobook, rootFolder.DefaultTags);
                    }

                    if (cfg.CreateEbook)
                    {
                        cfg.MergeTagsForMediaType(BookMediaType.Ebook, rootFolder.DefaultTags);
                    }
                }

                if (cfg.CreateAudiobook)
                {
                    cfg.AudiobookRootFolderPath = rootFolder.Path;
                    var a = rootFolder.GetAudiobookSettings();
                    if (a != null)
                    {
                        cfg.AudiobookQualityProfileId = a.QualityProfileId;
                        cfg.AudiobookMetadataProfileId = a.MetadataProfileId;
                        cfg.AudiobookMonitored = a.Monitored;
                        cfg.AudiobookMonitorNewItems = a.MonitorNewItems;
                        cfg.AudiobookMonitorExistingMode = ResolveRootMonitorExistingMode(a);
                        cfg.MergeTagsForMediaType(BookMediaType.Audiobook, a.Tags);
                    }
                }

                if (cfg.CreateEbook)
                {
                    cfg.EbookRootFolderPath = rootFolder.Path;
                    var e = rootFolder.GetEbookSettings();
                    if (e != null)
                    {
                        cfg.EbookQualityProfileId = e.QualityProfileId;
                        cfg.EbookMetadataProfileId = e.MetadataProfileId;
                        cfg.EbookMonitored = e.Monitored;
                        cfg.EbookMonitorNewItems = e.MonitorNewItems;
                        cfg.EbookMonitorExistingMode = ResolveRootMonitorExistingMode(e);
                        cfg.MergeTagsForMediaType(BookMediaType.Ebook, e.Tags);
                    }
                }
            }

            config = cfg;
            return true;
        }

        private static bool HasExplicitRootFolderForMediaType(MonitoringConfig config, BookMediaType mediaType)
        {
            if (config == null)
            {
                return false;
            }

            return mediaType == BookMediaType.Audiobook
                ? !string.IsNullOrWhiteSpace(config.AudiobookRootFolderPath)
                : !string.IsNullOrWhiteSpace(config.EbookRootFolderPath);
        }

        private static (bool CreateAudiobook, bool CreateEbook) ResolveCreateMediaTypes(RootFolder rootFolder, BookMediaType mediaType)
        {
            if (rootFolder?.FolderType == FolderType.Audiobook)
            {
                return (true, false);
            }

            if (rootFolder?.FolderType == FolderType.Ebook)
            {
                return (false, true);
            }

            return mediaType == BookMediaType.Audiobook ? (true, false) : (false, true);
        }

        /// <summary>
        /// Find the author folder by walking from root toward file and returning the HIGHEST
        /// (closest to root) folder that fuzzy-matches the author name. Returns null if no match.
        /// This is the primary method for determining author folder after V5 match returns the author name.
        /// </summary>
        private string GetAuthorFolder(string filePath, string authorName)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(authorName))
            {
                return null;
            }

            try
            {
                var rootPath = _rootFolderService?.GetBestRootFolderPath(filePath);
                if (string.IsNullOrWhiteSpace(rootPath) || _authorFolderMatchingService == null)
                {
                    return null;
                }

                var authorCandidate = new Author { Name = authorName };
                return NormalizeDirectory(_authorFolderMatchingService.FindAuthorFolderByWalkingUp(filePath, rootPath, authorCandidate));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get the first folder under root for path tokenization purposes.
        /// This is NOT for author folder resolution - use GetAuthorFolder(filePath, authorName) for that.
        /// This is only for building path-derived tags when we don't yet know the author.
        /// </summary>
        private string GetFirstFolderUnderRoot(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return null;
                }

                var rootPath = _rootFolderService?.GetBestRootFolderPath(filePath);
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    // No root folder - fall back to parent of book folder
                    return Directory.GetParent(dir)?.FullName;
                }

                var relative = Path.GetRelativePath(rootPath, dir);
                if (relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return null;
                }

                var firstSegment = relative
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(firstSegment) && firstSegment != "." && firstSegment != "..")
                {
                    return NormalizeDirectory(Path.Combine(rootPath, firstSegment));
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private string GetBookFolder(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                return dir != null ? NormalizeDirectory(dir) : null;
            }
            catch
            {
                return null;
            }
        }

        private string NormalizeDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return path;
                var full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

        private sealed class PinnedEditionFirstCrackTarget
        {
            public PinnedEditionFirstCrackTarget(Book book, Edition edition, IReadOnlyList<string> narratorCandidates)
            {
                Book = book;
                Edition = edition;
                NarratorCandidates = narratorCandidates;
            }

            public Book Book { get; }
            public Edition Edition { get; }
            public IReadOnlyList<string> NarratorCandidates { get; }
        }

        private PinnedEditionFirstCrackTarget TryBuildPinnedEditionFirstCrackTarget(int targetBookId, int? restrictToAuthorId)
        {
            if (targetBookId <= 0 || _bookService == null)
            {
                return null;
            }

            Book book;
            try
            {
                book = _bookService.GetBook(targetBookId);
            }
            catch
            {
                return null;
            }

            if (book == null)
            {
                return null;
            }

            if (restrictToAuthorId.HasValue && book.AuthorId != restrictToAuthorId.Value)
            {
                return null;
            }

            // Minimal scope: narrator-based pinned editions (audiobooks only).
            if (book.MediaType != BookMediaType.Audiobook)
            {
                return null;
            }

            var monitoredEdition = book.Editions?
                .Where(e => e != null && e.Monitored)
                .OrderByDescending(e => e.ManualAdd)
                .FirstOrDefault();

            if (monitoredEdition == null)
            {
                return null;
            }

            var isPinned = !book.AnyEditionOk || monitoredEdition.ManualAdd;
            if (!isPinned)
            {
                return null;
            }

            var narratorCandidates = GetNarratorCandidates(monitoredEdition);
            if (narratorCandidates.Count == 0)
            {
                return null;
            }

            // Ensure author name is available for the smoke test.
            if (book.Author == null && _authorService != null)
            {
                try
                {
                    book.Author = _authorService.GetAuthor(book.AuthorId);
                }
                catch
                {
                    // Ignore - we'll validate below.
                }
            }

            if (string.IsNullOrWhiteSpace(book.Author?.Name) || string.IsNullOrWhiteSpace(monitoredEdition.Title))
            {
                return null;
            }

            // CreateFileMatchFromEdition expects Edition.Book to be populated.
            monitoredEdition.Book ??= book;
            monitoredEdition.Book.Author ??= book.Author;

            return new PinnedEditionFirstCrackTarget(book, monitoredEdition, narratorCandidates);
        }

        private PinnedFirstCrackEvaluation EvaluatePinnedEditionFirstCrack(
            DiscoveredFileWithMetadata file,
            IDictionary<string, List<string>> tags,
            BookMediaType mediaType,
            PinnedEditionFirstCrackTarget target,
            out string matchedNarrator)
        {
            matchedNarrator = null;

            if (file == null || tags == null || tags.Count == 0 || target == null)
            {
                return new PinnedFirstCrackEvaluation { FailureReason = "missing_tags_or_target" };
            }

            if (_containmentValidator == null)
            {
                return new PinnedFirstCrackEvaluation { FailureReason = "missing_containment_validator" };
            }

            if (mediaType != target.Book.MediaType)
            {
                return new PinnedFirstCrackEvaluation { FailureReason = "media_type_mismatch" };
            }

            // 1) Narrator evidence (scan all matchable tag values)
            if (!TryFindNarratorEvidence(target.NarratorCandidates, target.Book.Author?.Name, tags, out matchedNarrator))
            {
                return new PinnedFirstCrackEvaluation { FailureReason = "wanted narrator not present in embedded tags" };
            }

            // 2) Title smoke test (reuse existing containment logic)
            if (!_containmentValidator.ValidateEditionInTags(target.Edition.Title, tags))
            {
                return new PinnedFirstCrackEvaluation { FailureReason = "wanted edition title not present in embedded tags" };
            }

            // 3) Author smoke test (reuse existing containment logic)
            if (!_containmentValidator.ValidateAuthorInTags(target.Book.Author?.Name, tags))
            {
                return new PinnedFirstCrackEvaluation { FailureReason = "wanted author not present in embedded tags" };
            }

            var provenance = new MatchProvenance
            {
                Mode = GetConfiguredMatchingStrictness().ToString(),
                Route = "pinned_target/embedded_tags",
                MatchedVia = "pinned_first_crack",
                Summary = "Matched the pinned edition from title, author, and narrator evidence"
            };
            var identityValues = new List<MatchIdentityProofValue>();

            foreach (var titleEvidence in _containmentValidator.GetEditionTitleEvidence(target.Edition.Title, tags) ?? Array.Empty<EditionTitleEvidence>())
            {
                identityValues.Add(new MatchIdentityProofValue(
                    MatchIdentityRole.Title,
                    "embedded_tag",
                    titleEvidence.FieldName,
                    titleEvidence.FieldValue,
                    target.Edition.Title,
                    "edition",
                    "This logical field proved the pinned edition title."));
                provenance.SupportingSignals.Add(new MatchSignal
                {
                    Type = "title",
                    Scope = "edition",
                    Source = "embedded_tag",
                    Field = titleEvidence.FieldName,
                    Observed = LimitSignalValue(titleEvidence.FieldValue),
                    Expected = LimitSignalValue(target.Edition.Title),
                    Detail = "This logical field proved the pinned edition title."
                });
            }

            foreach (var authorEvidence in BuildRawAuthorEvidenceTags(target.Book.Author?.Name, tags))
            {
                foreach (var value in authorEvidence.Value ?? new List<string>())
                {
                    identityValues.Add(new MatchIdentityProofValue(
                        MatchIdentityRole.Author,
                        "embedded_tag",
                        authorEvidence.Key,
                        value,
                        target.Book.Author?.Name,
                        "book",
                        "This logical field proved the pinned book author."));
                    provenance.SupportingSignals.Add(new MatchSignal
                    {
                        Type = "author",
                        Scope = "book",
                        Source = "embedded_tag",
                        Field = authorEvidence.Key,
                        Observed = LimitSignalValue(value),
                        Expected = LimitSignalValue(target.Book.Author?.Name),
                        Detail = "This logical field proved the pinned book author."
                    });
                }
            }

            foreach (var narratorEvidence in FindNarratorEvidenceFields(matchedNarrator, target.Book.Author?.Name, tags))
            {
                provenance.SupportingSignals.Add(new MatchSignal
                {
                    Type = "narrator",
                    Scope = "edition",
                    Source = "embedded_tag",
                    Field = narratorEvidence.Key,
                    Observed = LimitSignalValue(narratorEvidence.Value),
                    Expected = LimitSignalValue(matchedNarrator),
                    Detail = "This logical field proved the pinned edition narrator."
                });
            }

            provenance.EvidenceValues = BuildEvidenceValuesFromSignals(provenance, tags, "embedded_tag");
            var identityProof = new MatchIdentityProof(PreferSourceIdentityValues(identityValues));

            var pinnedMatch = new FileMatch
            {
                File = file,
                AuthorId = target.Book.AuthorId,
                AuthorName = target.Book.Author?.Name ?? string.Empty,
                BookId = target.Book.Id,
                BookTitle = target.Book.Title ?? target.Edition.Title,
                EditionId = target.Edition.Id,
                MatchedVia = provenance.MatchedVia,
                Provenance = provenance,
                IdentityProof = identityProof
            };
            FinalizeMatchProvenance(
                pinnedMatch,
                tags,
                GetConfiguredMatchingStrictness(),
                "pinned_target/embedded_tags");

            return new PinnedFirstCrackEvaluation
            {
                MatchedNarrator = matchedNarrator,
                Match = pinnedMatch
            };
        }

        private static List<string> GetNarratorCandidates(Edition edition)
        {
            var output = new List<string>();

            if (edition?.NarratorNames != null && edition.NarratorNames.Any())
            {
                output.AddRange(edition.NarratorNames);
            }

            if (!string.IsNullOrWhiteSpace(edition?.Narrator))
            {
                output.Add(edition.Narrator);
            }

            return output
                .SelectMany(ExpandNarratorVariants)
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> ExpandNarratorVariants(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            yield return raw;

            // "Last, First" -> "First Last"
            if (raw.Contains(","))
            {
                var parts = raw.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                if (parts.Length >= 2)
                {
                    yield return string.Join(" ", parts.Skip(1).Concat(parts.Take(1)));
                }
            }
        }

        private bool TryFindNarratorEvidence(
            IReadOnlyList<string> narratorCandidates,
            string authorName,
            IDictionary<string, List<string>> tags,
            out string matchedNarrator)
        {
            matchedNarrator = null;

            if (narratorCandidates == null || narratorCandidates.Count == 0 || tags == null || tags.Count == 0)
            {
                return false;
            }

            foreach (var narratorRaw in narratorCandidates)
            {
                if (FindNarratorEvidenceFields(narratorRaw, authorName, tags).Count > 0)
                {
                    matchedNarrator = narratorRaw;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePersonNameForMatch(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            var t = s.Trim().ToLowerInvariant();
            var sb = new StringBuilder(t.Length);
            foreach (var ch in t)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(' ');
                }
            }

            return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
        }


        private HolyGrailAttemptResult TryMatchWithHolyGrail(
            DiscoveredFileWithMetadata file,
            BookMediaType mediaType,
            bool disablePathFallback,
            IReadOnlySet<int> hardAllowedBookIds = null)
        {
            // HOLY GRAIL: Simple FTS + smoke test matching replaces complex batched approach
            try
            {
                _logger.Debug("[HOLY-GRAIL] Starting for '{0}'", Path.GetFileName(file.Path));

                var rejections = new List<CandidateRejection>();
                var evaluation = RunWithRejectionCapture(
                    "unrestricted",
                    () => EvaluateHolyGrailMatchFileInternal(
                        file,
                        mediaType,
                        null,
                        disablePathFallback,
                        inferAuthorFromPathDuringPathFallback: true,
                        unscoped: false,
                        hardAllowedBookIds: hardAllowedBookIds),
                    rejections);
                var match = evaluation?.Match;

                if (match != null)
                {
                    _logger.Debug("[HOLY-GRAIL] SUCCESS: '{0}' → '{1}' by '{2}'",
                        Path.GetFileName(file.Path), match.BookTitle, match.AuthorName);
                    return new HolyGrailAttemptResult
                    {
                        Match = match,
                        Evaluation = evaluation,
                        Rejections = null
                    };
                }

                // No match found
                _logger.Debug("[HOLY-GRAIL] No match for '{0}'", Path.GetFileName(file.Path));
                var unmatchedFile = new UnmatchedFile
                {
                    File = file,
                    Reason = "NO_MATCH_HOLY_GRAIL",
                    PotentialAuthors = new AuthorSuggestion[0]
                };
                return new HolyGrailAttemptResult
                {
                    Match = null,
                    UnmatchedFile = unmatchedFile,
                    Evaluation = evaluation,
                    Rejections = NullIfEmpty(rejections)
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[HOLY-GRAIL] Exception during matching for '{0}'", file.Path);
                var errorUnmatchedFile = new UnmatchedFile
                {
                    File = file,
                    Reason = $"Matching error: {ex.Message}",
                    PotentialAuthors = new AuthorSuggestion[0]
                };
                return new HolyGrailAttemptResult
                {
                    Match = null,
                    UnmatchedFile = errorUnmatchedFile,
                    Evaluation = new HolyGrailEvaluation()
                };
            }
        }

        private Dictionary<string, List<string>> BuildPathDerivedTags(string filePath)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                var fileName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
                var components = new List<string>();

                if (!string.IsNullOrWhiteSpace(dir))
                {
                    // Collect ALL directory names from root to the immediate book folder
                    var di = new DirectoryInfo(dir);
                    var stack = new Stack<string>();
                    while (di != null)
                    {
                        var name = di.Name;
                        if (!string.IsNullOrWhiteSpace(name)) stack.Push(name);
                        di = di.Parent;
                    }
                    components.AddRange(stack);

                    // Populate standard tag keys to mimic real metadata structure for containment (best-effort)
                    var bookFolder = new DirectoryInfo(dir).Name;
                    var authorFolderPath = GetFirstFolderUnderRoot(filePath);
                    var authorFolder = !string.IsNullOrWhiteSpace(authorFolderPath)
                        ? Path.GetFileName(authorFolderPath)
                        : Directory.GetParent(dir)?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(bookFolder))
                    {
                        tags["ALBUM"] = new List<string> { NormalizeForPathTokens(bookFolder) };
                    }
                    if (!string.IsNullOrWhiteSpace(authorFolder))
                    {
                        var normAuthor = NormalizeForPathTokens(authorFolder);
                        tags["ARTIST"] = new List<string> { normAuthor };
                        tags["ALBUMARTIST"] = new List<string> { normAuthor };
                        tags["AUTHOR"] = new List<string> { normAuthor };
                    }
                }

                if (!string.IsNullOrWhiteSpace(fileName)) components.Add(fileName);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    tags["TITLE"] = new List<string> { NormalizeForPathTokens(fileName) };
                }

                // Split components into tokens by common separators
                var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in components)
                {
                    foreach (var t in SplitTokens(c))
                    {
                        if (t.Length > 0) tokens.Add(t);
                    }
                }

                if (tokens.Count > 0)
                {
                    tags["path"] = tokens.ToList();
                    var compList = components.Where(s => !string.IsNullOrWhiteSpace(s)).Select(NormalizeForPathTokens).ToList();
                    tags["folder"] = compList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    tags["pathcomponents"] = compList;
                    tags["filename"] = new List<string> { NormalizeForPathTokens(fileName) };
                }
            }
            catch
            {
                // ignore and return what we have
            }
            return tags;
        }

        private Dictionary<string, List<string>> BuildSupplementalPathEvidence(string filePath)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var legacyPathTags = BuildPathDerivedTags(filePath);
            void Copy(string sourceKey, string destinationKey)
            {
                if (legacyPathTags.TryGetValue(sourceKey, out var values) && values?.Count > 0)
                {
                    tags[destinationKey] = new List<string>(values);
                }
            }

            Copy("TITLE", "PATH:FILE_VALUE");
            Copy("ALBUM", "PATH:BOOK_VALUE");
            Copy("AUTHOR", "PATH:AUTHOR_VALUE");

            return tags;
        }

        private static Dictionary<string, List<string>> MergeEvidenceTags(
            IDictionary<string, List<string>> primary,
            IDictionary<string, List<string>> supplemental)
        {
            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            void Add(IDictionary<string, List<string>> source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var pair in source)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                    {
                        continue;
                    }

                    if (!merged.TryGetValue(pair.Key, out var values))
                    {
                        values = new List<string>();
                        merged[pair.Key] = values;
                    }

                    foreach (var value in pair.Value.Where(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        if (!values.Contains(value, StringComparer.Ordinal))
                        {
                            values.Add(value);
                        }
                    }
                }
            }

            Add(primary);
            Add(supplemental);
            return merged;
        }

        private Dictionary<string, List<string>> BuildGroupConsensusTags(IReadOnlyList<DiscoveredFileWithMetadata> files)
        {
            var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (files == null || files.Count == 0)
            {
                return output;
            }

            var spreadSamples = SelectSpreadSamples(files);
            output = BuildHomogeneousTags(spreadSamples, spreadSamples.Count);
            if (output.Count > 0)
            {
                return output;
            }

            if (files.Count == 1)
            {
                var representative = files[0];
                if (representative?.AllTags != null && representative.AllTags.Count > 0)
                {
                    return CloneTags(representative.AllTags);
                }
            }

            return output;
        }

        private static List<DiscoveredFileWithMetadata> SelectSpreadSamples(IReadOnlyList<DiscoveredFileWithMetadata> files, int maxSamples = 5)
        {
            var samples = new List<DiscoveredFileWithMetadata>();
            if (files == null || files.Count == 0)
            {
                return samples;
            }

            var usableMax = Math.Max(1, maxSamples);
            if (files.Count <= usableMax)
            {
                foreach (var file in files)
                {
                    if (file != null)
                    {
                        samples.Add(file);
                    }
                }

                return samples;
            }

            var sampleIndexes = new SortedSet<int>();
            var denominator = Math.Max(1, usableMax - 1);
            for (var i = 0; i < usableMax; i++)
            {
                var index = (int)Math.Round(i * (files.Count - 1) / (double)denominator);
                if (index < 0) index = 0;
                if (index >= files.Count) index = files.Count - 1;
                sampleIndexes.Add(index);
            }

            foreach (var index in sampleIndexes)
            {
                var file = files[index];
                if (file != null)
                {
                    samples.Add(file);
                }
            }

            return samples;
        }

        private Dictionary<string, List<string>> BuildHomogeneousTags(IReadOnlyCollection<DiscoveredFileWithMetadata> samples, int totalFileCount)
        {
            var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (samples == null || samples.Count == 0) return output;

            var tagSets = new List<Dictionary<string, List<string>>>();
            foreach (var s in samples)
            {
                if (s?.AllTags == null || s.AllTags.Count == 0)
                {
                    continue;
                }

                tagSets.Add(CloneTags(s.AllTags));
            }

            return UnitTagConsensusBuilder.BuildConsensus(tagSets, totalFileCount);
        }

        private static Dictionary<string, List<string>> CloneTags(IDictionary<string, List<string>> tags)
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


        private string NormalizeForPathTokens(string text)
        {
            return Services.BookImportUnitGroupingService.NormalizeForPathTokens(text);
        }

        private IEnumerable<string> SplitTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            // Apply comprehensive Unicode normalization
            text = NormalizeForPathTokens(text);

            // Keep hyphens and periods intact here; SQLite FTS tokenchars is "-." so they remain part of tokens.
            // Apostrophes are separators in our FTS configuration and are handled later by TokenizeText().
            var seps = new[] { ' ', '_', ',', ';', '(', ')', '[', ']', '{', '}', '&', '+' };
            var parts = text.Split(seps, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var s = p.Trim();
                if (s.Length > 0) yield return s;
            }
        }

        /// <summary>
        /// Holy Grail matching: Simple bag-of-words FTS + smoke test.
        /// Runs an OR-based FTS query then validates with the smoke test.
        /// </summary>
        public Books.EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, Books.BookMediaType mediaType)
        {
            var tokens = allTagTokens?.ToList() ?? new List<string>();
            return RunFtsWithSmokeTest(authorId, tokens, mediaType, "main-tags")?.Match;
        }

        /// <summary>
        /// HOLY GRAIL: Complete file matching with full fallback chain.
        /// Flow: embedded tags → FTS → smoke test → (optional) path-derived tags → FTS → smoke test.
        /// </summary>
        public FileMatch HolyGrailMatchFile(
            DiscoveredFileWithMetadata file,
            BookMediaType mediaType,
            int? restrictToAuthorId = null)
        {
            return HolyGrailMatchFile(file, mediaType, restrictToAuthorId, disablePathFallback: false);
        }

        public FileMatch HolyGrailMatchFile(
            DiscoveredFileWithMetadata file,
            BookMediaType mediaType,
            int? restrictToAuthorId,
            bool disablePathFallback)
        {
            return EvaluateHolyGrailMatchFileInternal(
                file,
                mediaType,
                restrictToAuthorId,
                disablePathFallback: disablePathFallback,
                inferAuthorFromPathDuringPathFallback: true,
                unscoped: false)?.Match;
        }

        private FileMatch HolyGrailMatchFileUnscopedNoAuthorInference(
            DiscoveredFileWithMetadata file,
            BookMediaType mediaType,
            bool disablePathFallback)
        {
            return EvaluateHolyGrailMatchFileInternal(
                file,
                mediaType,
                restrictToAuthorId: null,
                disablePathFallback: disablePathFallback,
                inferAuthorFromPathDuringPathFallback: false,
                unscoped: true)?.Match;
        }

        private HolyGrailEvaluation EvaluateHolyGrailMatchFileInternal(
            DiscoveredFileWithMetadata file,
            BookMediaType mediaType,
            int? restrictToAuthorId,
            bool disablePathFallback,
            bool inferAuthorFromPathDuringPathFallback,
            bool unscoped,
            IReadOnlySet<int> hardAllowedBookIds = null)
        {
            if (file == null)
            {
                _logger.Debug("[HOLY-GRAIL] Cannot match null file or tags");
                return new HolyGrailEvaluation();
            }

            var matchingStrictness = GetConfiguredMatchingStrictness();
            var usePathAsTagsFallback = !disablePathFallback && IsConfiguredPathAsTagsFallbackEnabled(matchingStrictness);
            var pathFallbackSuppressedReason = !usePathAsTagsFallback
                ? disablePathFallback ? "disabled_by_context" : "disabled_by_config"
                : null;

            var embeddedTags = file.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var hasEmbeddedTags = embeddedTags.Count > 0;
            var mainTags = hasEmbeddedTags
                ? CategorizeTagsForHolyGrail(embeddedTags)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var stopwatch = Stopwatch.StartNew();
            var identifierEvidence = ExtractIdentifierEvidence(file, embeddedTags);
            var shortCircuitEdition = TryShortCircuitByIdentifierCandidates(
                identifierEvidence,
                mediaType,
                out var winningIdentifierEvidence);
            if (shortCircuitEdition != null &&
                hardAllowedBookIds?.Count > 0 &&
                !hardAllowedBookIds.Contains(shortCircuitEdition.BookId))
            {
                RecordCapturePhaseRejection(
                    "identifier",
                    "HARD_BOOK_CONSTRAINT",
                    $"identifier BookId={shortCircuitEdition.BookId} is outside the provider-resolved work boundary");
                shortCircuitEdition = null;
                winningIdentifierEvidence = new List<IdentifierEvidenceCandidate>();
            }
            IdentifierCandidateProof identifierCandidateProof = null;
            if (shortCircuitEdition != null)
            {
                identifierCandidateProof = TryBuildIdentifierCandidateProof(
                    shortCircuitEdition,
                    winningIdentifierEvidence,
                    mainTags,
                    restrictToAuthorId,
                    out var identifierProofFailure);
                if (identifierCandidateProof == null)
                {
                    RecordCapturePhaseRejection(
                        "identifier",
                        identifierProofFailure,
                        $"identifier candidate EditionId={shortCircuitEdition.Id} did not satisfy the shared author/title proof");
                    shortCircuitEdition = null;
                    winningIdentifierEvidence = new List<IdentifierEvidenceCandidate>();
                }
            }
            if (shortCircuitEdition != null)
            {
                // The provider identifier selected this exact edition candidate. It is not identity proof:
                // the candidate reached this point only after the same author/title compatibility checks
                // used by ordinary matching.
                stopwatch.Stop();
                var identifierSources = winningIdentifierEvidence
                    .Select(evidence => evidence.Source)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var identifierRoute = identifierSources.Count > 1
                    ? "combined_identifier"
                    : string.Equals(identifierSources.SingleOrDefault(), "filename", StringComparison.OrdinalIgnoreCase)
                        ? "filename_identifier"
                        : "embedded_identifier";
                _logger.Debug("[HOLY-GRAIL] SHORT-CIRCUIT: Provider identifier from {0} selected Edition='{1}' and current author/title proof confirmed it",
                    string.Join(" and ", identifierSources),
                    shortCircuitEdition.Title);
                var identifierProvenance = BuildIdentifierMatchProvenance(
                    winningIdentifierEvidence,
                    matchingStrictness,
                    out var identifierProof,
                    embeddedTags,
                    identifierCandidateProof);
                var identifierMatch = CreateFileMatchFromEdition(
                    file,
                    shortCircuitEdition,
                    identifierProvenance,
                    identifierProof);
                FinalizeMatchProvenance(
                    identifierMatch,
                    embeddedTags,
                    matchingStrictness,
                    BuildDecisionRoute(identifierRoute, unscoped, restrictToAuthorId.HasValue));
                return new HolyGrailEvaluation
                {
                    Match = identifierMatch,
                    WinningTags = embeddedTags,
                    PathFallbackUsed = false
                };
            }

            if (!hasEmbeddedTags && !usePathAsTagsFallback)
            {
                _logger.Debug("[HOLY-GRAIL] No embedded tags for '{0}' (path/filename matching disabled)",
                    Path.GetFileName(file.Path));
                RecordCapturePhaseRejection("main-tags", "NO_TAGS", "No embedded tags");
                return new HolyGrailEvaluation
                {
                    Match = null,
                    WinningTags = embeddedTags,
                    PathFallbackUsed = false,
                    PathFallbackSuppressedReason = pathFallbackSuppressedReason
                };
            }

            _logger.Debug("[HOLY-GRAIL] === Starting match{0} for '{1}' ===",
                unscoped ? " (unscoped)" : "",
                Path.GetFileName(file.Path));

            var fileDurationSeconds = file.DurationSeconds;
            if (!fileDurationSeconds.HasValue && mediaType == BookMediaType.Audiobook && _mediaInfoExtractor != null)
            {
                fileDurationSeconds = MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(file.Path));
            }

            // No author pre-discovery from tags. FTS searches ALL authors — BM25 naturally
            // ranks candidates with matching author names higher since author tokens are in the query.
            // Only use restrictToAuthorId if the caller explicitly provides it.
            int? authorId = unscoped ? null : restrictToAuthorId;
            string authorName = null;
            if (authorId.HasValue)
            {
                var author = _authorService.GetAuthor(authorId.Value);
                authorName = author?.Name;
            }

            var embeddedRejections = new List<CandidateRejection>();
            var embeddedContradiction = false;
            var mainTokens = new List<string>();

            // Attempt 1: embedded tags (preferred)
            if (hasEmbeddedTags)
            {
                // Step 1: Extract matchable tags (excludes comments, trash, cover art, genre, etc.)
                // Tokenize main tags (excluding comments)
                mainTokens = TokenizeForHolyGrail(mainTags);
                _logger.Debug("[HOLY-GRAIL] Main tokens: {0}", mainTokens.Count);

                // Try FTS with main tags
                var matchResult = RunFtsWithSmokeTest(authorId, mainTokens, mediaType, "main-tags", mainTags, file.Path,
                    rejections: embeddedRejections,
                    fileDurationSeconds: fileDurationSeconds,
                    onContradictoryEvidence: () => embeddedContradiction = true,
                    groupMemberTags: file.GroupMemberTags,
                    hardAllowedBookIds: hardAllowedBookIds);
                var match = matchResult?.Match;
                if (match != null)
                {
                    stopwatch.Stop();
                    _logger.Debug("[HOLY-GRAIL] MATCHED via embedded tags{0} in {1}ms",
                        unscoped ? " (unscoped)" : "",
                        stopwatch.ElapsedMilliseconds);
                    var fileMatch = CreateFileMatch(
                        file,
                        match,
                        unscoped ? (int?)null : authorId,
                        unscoped ? null : authorName,
                        matchResult.MatchedVia,
                        matchResult.Provenance,
                        matchResult.IdentityProof);
                    FinalizeMatchProvenance(
                        fileMatch,
                        embeddedTags,
                        matchingStrictness,
                        BuildDecisionRoute("embedded_tags", unscoped, restrictToAuthorId.HasValue));
                    return new HolyGrailEvaluation
                    {
                        Match = fileMatch,
                        WinningTags = mainTags,
                        BooksById = matchResult.BooksById,
                        PathFallbackUsed = false
                    };
                }
            }
            else
            {
                RecordCapturePhaseRejection("main-tags", "NO_TAGS", "No embedded tags");
            }

            // Comments are NEVER used as FTS tokens — too noisy (plot summaries cause false matches).

            var embeddedDisposition = ClassifyEmbeddedEvidence(
                mainTokens.Count > 0,
                matched: false,
                embeddedContradiction,
                embeddedRejections);

            // Attempt 2: supplement insufficient embedded evidence with source-marked path values.
            // Keep embedded evidence in the same pass so provenance and contradictions remain honest.
            if (usePathAsTagsFallback)
            {
                if (embeddedDisposition == EmbeddedEvidenceDisposition.ContradictoryEvidence)
                {
                    stopwatch.Stop();
                    _logger.Debug("[HOLY-GRAIL] Skipping{0} path fallback because embedded tags produced concrete contradictory evidence ({1}ms)",
                        unscoped ? " unscoped" : "",
                        stopwatch.ElapsedMilliseconds);
                    return new HolyGrailEvaluation
                    {
                        Match = null,
                        WinningTags = embeddedTags,
                        PathFallbackUsed = false,
                        PathFallbackSuppressedReason = "blocked_by_embedded_contradiction"
                    };
                }

                _logger.Debug("[HOLY-GRAIL] No match from embedded tags{0}; supplementing with folder and filename evidence...",
                    unscoped ? " (unscoped)" : "");

                var pathTags = CategorizeTagsForHolyGrail(BuildPathDerivedTags(file.Path));
                var pathEvidenceTags = CategorizeTagsForHolyGrail(BuildSupplementalPathEvidence(file.Path));
                var pathTokens = TokenizeForHolyGrail(pathTags);
                _logger.Debug("[HOLY-GRAIL] Supplemental path values supplied {0} tokens", pathTokens.Count);

                if (pathTags.Count > 0 && pathTokens.Count > 0)
                {
                    if (!unscoped && inferAuthorFromPathDuringPathFallback && !authorId.HasValue)
                    {
                        var pathAuthor = FindAuthorFromPath(file.Path);
                        if (pathAuthor != null)
                        {
                            authorId = pathAuthor.Id;
                            authorName = pathAuthor.Name;
                            _logger.Debug("[HOLY-GRAIL] Found author from path: {0} (ID: {1})", authorName, authorId);
                        }
                    }

                    var matchResult = RunFtsWithSmokeTest(authorId, pathTokens, mediaType, "path-tags", pathTags, file.Path,
                        fileDurationSeconds: fileDurationSeconds,
                        allowNarratorEvidence: false,
                        hardAllowedBookIds: hardAllowedBookIds);
                    var match = matchResult?.Match;
                    if (match != null)
                    {
                        stopwatch.Stop();
                        _logger.Debug("[HOLY-GRAIL] MATCHED via path fallback{0} in {1}ms",
                            unscoped ? " (unscoped)" : "",
                            stopwatch.ElapsedMilliseconds);
                        var fileMatch = CreateFileMatch(
                            file,
                            match,
                            unscoped ? (int?)null : authorId,
                            unscoped ? null : authorName,
                            matchResult.MatchedVia,
                            matchResult.Provenance,
                            matchResult.IdentityProof);
                        AddRetainedEmbeddedFallbackEvidence(
                            fileMatch,
                            match,
                            mainTags,
                            pathEvidenceTags);
                        FinalizeMatchProvenance(
                            fileMatch,
                            embeddedTags,
                            matchingStrictness,
                            BuildDecisionRoute(mainTags.Count > 0 ? "supplemental_path" : "path_tags", unscoped, restrictToAuthorId.HasValue));
                        return new HolyGrailEvaluation
                        {
                            Match = fileMatch,
                            WinningTags = pathTags,
                            BooksById = matchResult.BooksById,
                            PathFallbackUsed = true
                        };
                    }
                }
            }

            stopwatch.Stop();
            _logger.Debug("[HOLY-GRAIL] NO MATCH FOUND for '{0}' after all fallbacks{1} ({2}ms)",
                Path.GetFileName(file.Path),
                unscoped ? " (unscoped)" : "",
                stopwatch.ElapsedMilliseconds);
                return new HolyGrailEvaluation
                {
                    Match = null,
                    WinningTags = embeddedTags,
                    PathFallbackUsed = false,
                    PathFallbackSuppressedReason = usePathAsTagsFallback ? null : pathFallbackSuppressedReason
                };
            }

        /// <summary>
        /// Checks if a candidate passes the leftover token gate.
        /// Returns (passes, leftovers) where leftovers contains the meaningful unexplained tokens when failing.
        /// On pass, returns an empty array for zero allocation.
        /// </summary>
            private (bool Passes, string FieldName, IReadOnlyList<string> Leftovers) PassesLeftoverTokenGate(
                EditionFtsMatch candidate,
                IReadOnlyList<EditionTitleEvidence> evidenceFields,
                IDictionary<string, List<string>> allTags,
                IDictionary<int, Book> booksById = null,
                BookMatchingStrictness strictness = BookMatchingStrictness.Balanced,
                SeriesPositionEvidence seriesPositionEvidence = null)
            {
                if (candidate == null || evidenceFields == null || evidenceFields.Count == 0)
                {
                    return (false, null, Array.Empty<string>());
                }

            seriesPositionEvidence ??= GetSeriesPositionEvidence(candidate, allTags, booksById);

            var explainableTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var explainableSeriesPositionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var explainableSeriesNameTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var boundaryTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var nearDistanceTokens = strictness == BookMatchingStrictness.Aggressive ? 1 : 3;

            void AddExplainable(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                foreach (var tok in TokenizeForLeftoverGate(text))
                {
                    explainableTokens.Add(tok);
                }
            }

            void AddBoundary(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                foreach (var tok in TokenizeForLeftoverGate(text))
                {
                    boundaryTokens.Add(tok);
                }
            }

            void AddSeriesPositionExplainable(string seriesPositionText)
            {
                AddExplainable(seriesPositionText);
                if (string.IsNullOrWhiteSpace(seriesPositionText))
                {
                    return;
                }

                foreach (var token in SeriesPositionTokenHelper.GetPositionTokens(seriesPositionText))
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        explainableSeriesPositionTokens.Add(token);
                    }
                }
            }

            void AddSeriesNameExplainable(string seriesName)
            {
                AddExplainable(seriesName);
                if (string.IsNullOrWhiteSpace(seriesName))
                {
                    return;
                }

                foreach (var token in TokenizeForLeftoverGate(seriesName))
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        explainableSeriesNameTokens.Add(token);
                    }
                }
            }

            // Candidate metadata
            AddExplainable(candidate.AuthorName);
            AddBoundary(candidate.AuthorName);
            AddExplainable(candidate.BookTitle);
            AddExplainable(candidate.EditionSubTitle);
            AddExplainable(candidate.NarratorNames);
            AddBoundary(candidate.NarratorNames);
            if (candidate.ReleaseDate.HasValue)
            {
                AddExplainable(candidate.ReleaseDate.Value.Year.ToString(CultureInfo.InvariantCulture));
            }

            // Series metadata (when present)
            if (booksById != null && booksById.TryGetValue(candidate.BookId, out var cachedBook) && cachedBook != null)
            {
                AddSeriesNameExplainable(cachedBook.SeriesName);
                foreach (var link in cachedBook.SeriesLinks ?? Enumerable.Empty<SeriesBookLink>())
                {
                    AddSeriesNameExplainable(link?.Series?.Value?.Title);
                }
                if (!string.IsNullOrWhiteSpace(cachedBook.SeriesName))
                {
                    explainableTokens.Add("series");
                }
                AddSeriesPositionExplainable(cachedBook.SeriesPosition);
            }
            else
            {
                try
                {
                    var book = _bookService.GetBook(candidate.BookId);
                    if (booksById != null)
                    {
                        booksById[candidate.BookId] = book;
                    }

                    if (book != null)
                    {
                        AddSeriesNameExplainable(book.SeriesName);
                        foreach (var link in book.SeriesLinks ?? Enumerable.Empty<SeriesBookLink>())
                        {
                            AddSeriesNameExplainable(link?.Series?.Value?.Title);
                        }
                        if (!string.IsNullOrWhiteSpace(book.SeriesName))
                        {
                            explainableTokens.Add("series");
                        }
                        AddSeriesPositionExplainable(book.SeriesPosition);
                    }
                }
                catch { }
            }

            // Track/disc style numbering is often embedded alongside title evidence; if we have canonical numbering
            // tags, treat those numbers as explainable to avoid false rejections.
            if (allTags != null && allTags.Count > 0)
            {
                var numericTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "TRACKNUMBER", "DISCNUMBER", "TOTALTRACKS", "TOTALDISCS",
                    "TRCK", "TPOS", "TRKN"
                };

                foreach (var kvp in allTags)
                {
                    if (!numericTagKeys.Contains(kvp.Key)) continue;
                    if (kvp.Value == null || kvp.Value.Count == 0) continue;

                    foreach (var v in kvp.Value.Where(x => !string.IsNullOrWhiteSpace(x)).Take(3))
                    {
                        foreach (Match m in Regex.Matches(v, @"\d+"))
                        {
                            var raw = m.Value;
                            if (string.IsNullOrWhiteSpace(raw)) continue;

                            explainableTokens.Add(raw);

                            var normalized = raw.TrimStart('0');
                            if (string.IsNullOrEmpty(normalized))
                            {
                                normalized = "0";
                            }

                            explainableTokens.Add(normalized);
                        }
                    }
                }
            }

            bool IsMeaningful(string tok)
            {
                if (string.IsNullOrWhiteSpace(tok)) return false;
                tok = tok.Trim();

                // Always treat these as meaningful disambiguators in title evidence, even though other
                // parts of the pipeline may treat them as structural tokens.
                if (tok.Equals("vol", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("volume", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("book", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (HolyGrailLeftoverHardNoiseTokens.Contains(tok)) return false;
                if (HolyGrailLeftoverStructuralTokens.Contains(tok)) return false;
                if (tok.All(char.IsDigit)) return true; // numbers are meaningful disambiguators (e.g., "Dune 2", "Season 3")
                if (SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(tok)) return true;
                if (tok.Length <= 2) return false;
                return true;
            }

                bool IsNumericPackagingMarkerToken(string tok)
                {
                    if (string.IsNullOrWhiteSpace(tok))
                    {
                        return false;
                    }

                    return HolyGrailLeftoverNumericPackagingTokens.Contains(tok);
                }

            static bool IsZeroPaddedNumber(string tok)
            {
                if (string.IsNullOrWhiteSpace(tok))
                {
                    return false;
                }

                tok = tok.Trim();
                return tok.Length > 1 && tok[0] == '0' && tok.All(char.IsDigit);
            }

                bool HasNearbyPackagingMarker(IReadOnlyList<string> fieldTokens, int index)
                {
                    if (fieldTokens == null || fieldTokens.Count == 0)
                    {
                        return false;
                    }

                var start = Math.Max(0, index - 2);
                var end = Math.Min(fieldTokens.Count - 1, index + 2);
                for (var i = start; i <= end; i++)
                {
                    var tok = fieldTokens[i];
                    if (IsNumericPackagingMarkerToken(tok))
                    {
                        return true;
                    }
                }

                    return false;
                }

            bool IsMetadataWallToken(string tok)
            {
                if (string.IsNullOrWhiteSpace(tok))
                {
                    return false;
                }

                tok = tok.Trim();

                // Boundary tokens are a strong signal even when they are short initials.
                if (boundaryTokens.Contains(tok))
                {
                    return true;
                }

                if (HolyGrailLeftoverHardNoiseTokens.Contains(tok))
                {
                    return false;
                }

                if (explainableTokens.Contains(tok))
                {
                    return true;
                }

                if (tok.All(char.IsDigit))
                {
                    var normalized = tok.TrimStart('0');
                    if (string.IsNullOrEmpty(normalized))
                    {
                        normalized = "0";
                    }

                    return explainableTokens.Contains(normalized);
                }

                return false;
            }

                var bestLeftoverCount = int.MaxValue;
                var bestFieldName = string.Empty;
                var bestLeftovers = new List<string>();
                var foundPassingField = false;
            List<string> bestPassingLeftovers = null;

            void RecordRejectedEvidenceField(IReadOnlyList<string> leftovers, string fieldName)
            {
                var normalized = leftovers?
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                if (normalized.Count > 0 && normalized.Count < bestLeftoverCount)
                {
                    bestLeftoverCount = normalized.Count;
                    bestFieldName = fieldName;
                    bestLeftovers = normalized;
                }
            }

            void RecordPassingLeftovers(IEnumerable<string> leftovers)
            {
                var normalized = leftovers?
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                if (bestPassingLeftovers == null || normalized.Count < bestPassingLeftovers.Count)
                {
                    bestPassingLeftovers = normalized;
                }
            }

            bool CanTolerateUnexplainedLeftovers(IReadOnlyList<string> leftovers)
            {
                if (strictness == BookMatchingStrictness.Strict ||
                    leftovers == null ||
                    leftovers.Count == 0)
                {
                    return false;
                }

                if (strictness == BookMatchingStrictness.Aggressive)
                {
                    return true;
                }
                // Balanced cannot infer that an unexplained word is a contradiction. A different
                // provider Book must explain more of the same observed occurrence to displace this
                // candidate; that comparison happens after all Books have been evaluated.
                return true;
            }

            foreach (var ev in evidenceFields)
            {
                var fieldValueForLeftovers = ev.FieldValue;
                var matchedTitleForAnchor = ev.MatchedTitle;

                // Positional tokenization (no dedupe) so we can compute distance from the title anchor.
                var fieldTokenSeq = TokenizeForLeftoverGateSequence(fieldValueForLeftovers);
                var titleTokenSeq = TokenizeForLeftoverGateSequence(matchedTitleForAnchor);
                var fieldPositionEvidence = seriesPositionEvidence.GetField(ev.FieldName);
                var consumeRecognizedSeriesDecoration =
                    fieldPositionEvidence?.Disposition == SeriesPositionDisposition.Match ||
                    (strictness != BookMatchingStrictness.Strict &&
                     fieldPositionEvidence?.Disposition == SeriesPositionDisposition.Mismatch);

                bool IsObservedSeriesPositionToken(string token)
                {
                    if (!consumeRecognizedSeriesDecoration || string.IsNullOrWhiteSpace(token))
                    {
                        return false;
                    }

                    return fieldPositionEvidence.ObservedPositionTokens.Contains(token.Trim());
                }

                bool IsCandidateSeriesPositionDecoration(string token, int? index)
                {
                    if (string.IsNullOrWhiteSpace(token) || !index.HasValue)
                    {
                        return false;
                    }

                    token = token.Trim();
                    if (explainableSeriesPositionTokens.Contains(token))
                    {
                        var neighbors = new[] { index.Value - 1, index.Value + 1 };
                        if (neighbors.Any(i =>
                                i >= 0 &&
                                i < fieldTokenSeq.Count &&
                                SeriesPositionDecorationTokens.Contains(fieldTokenSeq[i])))
                        {
                            return true;
                        }

                        // Series Name III / Series Name Three is a recognized position layout.
                        // Direction matters: do not let an unrelated word before the series name
                        // become position evidence merely because it is a numeric synonym.
                        return index.Value > 0 &&
                               explainableSeriesNameTokens.Contains(fieldTokenSeq[index.Value - 1]);
                    }

                    if (!SeriesPositionDecorationTokens.Contains(token))
                    {
                        return false;
                    }

                    var positionNeighbors = new[] { index.Value - 1, index.Value + 1 };
                    return positionNeighbors.Any(i =>
                        i >= 0 &&
                        i < fieldTokenSeq.Count &&
                        explainableSeriesPositionTokens.Contains(fieldTokenSeq[i]));
                }

                bool IsRecognizedSeriesDecoration(string token, int? index)
                {
                    if (IsCandidateSeriesPositionDecoration(token, index))
                    {
                        return true;
                    }

                    if (!consumeRecognizedSeriesDecoration || string.IsNullOrWhiteSpace(token))
                    {
                        return false;
                    }

                    token = token.Trim();
                    if (IsObservedSeriesPositionToken(token) ||
                        fieldPositionEvidence.RecognizedSeriesTokens.Contains(token))
                    {
                        return true;
                    }

                    if (!index.HasValue || !SeriesPositionDecorationTokens.Contains(token))
                    {
                        return false;
                    }

                    var neighbors = new[] { index.Value - 1, index.Value + 1 };
                    return neighbors.Any(i =>
                        i >= 0 &&
                        i < fieldTokenSeq.Count &&
                        IsObservedSeriesPositionToken(fieldTokenSeq[i]));
                }

                var recognizedSeriesDecorationTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fieldTokenSeq.Count; i++)
                {
                    if (IsRecognizedSeriesDecoration(fieldTokenSeq[i], i))
                    {
                        recognizedSeriesDecorationTokens.Add(fieldTokenSeq[i]);
                    }
                }

                // Treat multi-value tag fields as one logical field for numeric disambiguation.
                // If the tag key has a numeric-only sibling value (e.g., ["Impact Winter", "3"]),
                // make sure the leftover gate can "see" it even if the matched evidence value was only the title portion.
                var forcedNearTokens = new List<string>();
                if (allTags != null &&
                    !string.IsNullOrWhiteSpace(ev.FieldName) &&
                    allTags.TryGetValue(ev.FieldName, out var rawValues) &&
                    rawValues != null &&
                    rawValues.Count > 1)
                {
                    foreach (var v in rawValues)
                    {
                        var trimmed = v?.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;
                        if (!trimmed.All(char.IsDigit)) continue;

                        forcedNearTokens.AddRange(TokenizeForLeftoverGateSequence(trimmed));
                    }
                }

                // Find the smallest token window in the field that covers the matched title tokens.
                // Near-exact evidence keeps its own ordered alignment so typo/plural tokens are not
                // reintroduced as unexplained leftovers.
                TitleTokenAlignmentResult nearExactAlignment = null;
                (int Start, int End)? anchor = null;
                if (ev.IsNearExact &&
                    TitleTokenAlignment.TryAlignOrdered(
                        titleTokenSeq,
                        fieldTokenSeq,
                        allowNearExact: true,
                        allowTransposition: ev.RequiresAudiobookDurationCorroboration,
                        out var orderedAlignment))
                {
                    nearExactAlignment = orderedAlignment;
                    anchor = (orderedAlignment.ConsumedFieldIndexes.Min(), orderedAlignment.ConsumedFieldIndexes.Max());
                }
                else
                {
                    anchor = FindMinimumCoveringWindow(fieldTokenSeq, titleTokenSeq);
                }

                    if (!anchor.HasValue)
                    {
                        var originalFieldTokens = new HashSet<string>(TokenizeForLeftoverGate(fieldValueForLeftovers), StringComparer.OrdinalIgnoreCase);
                    foreach (var tok in forcedNearTokens)
                    {
                        originalFieldTokens.Add(tok);
                    }

                    var fieldTokens = new HashSet<string>(originalFieldTokens, StringComparer.OrdinalIgnoreCase);
                    var matchedTitleTokens = new HashSet<string>(TokenizeForLeftoverGate(matchedTitleForAnchor), StringComparer.OrdinalIgnoreCase);

                    fieldTokens.ExceptWith(matchedTitleTokens);
                    fieldTokens.ExceptWith(explainableTokens);
                    fieldTokens.ExceptWith(recognizedSeriesDecorationTokens);
                    fieldTokens.RemoveWhere(IsObservedSeriesPositionToken);
                    fieldTokens.RemoveWhere(t => HolyGrailLeftoverHardNoiseTokens.Contains(t));
                    fieldTokens.RemoveWhere(t =>
                        t.All(char.IsDigit) &&
                        explainableTokens.Contains(t.TrimStart('0').Length > 0 ? t.TrimStart('0') : "0"));

                        var meaningfulLeftovers = fieldTokens.Where(IsMeaningful).ToList();

                        if (meaningfulLeftovers.Count == 0)
                        {
                            RecordPassingLeftovers(Array.Empty<string>());
                            foundPassingField = true;
                            continue;
                        }

                            var allNumericLeftovers = meaningfulLeftovers.All(t => t.All(char.IsDigit) || SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(t));
                            if (allNumericLeftovers)
                            {
                                if (strictness == BookMatchingStrictness.Strict)
                                {
                                    // Strict: require explicit packaging markers (e.g., "Track", "Disc", "Part") to tolerate numeric leftovers.
                                    if (originalFieldTokens.Any(IsNumericPackagingMarkerToken))
                                    {
                                        RecordPassingLeftovers(Array.Empty<string>());
                                        foundPassingField = true;
                                        continue;
                                    }
                                }
                                else
                                {
                                    // Balanced/Aggressive: tolerate numeric-only leftovers when nothing else is contradictory.
                                    RecordPassingLeftovers(meaningfulLeftovers);
                                    foundPassingField = true;
                                    continue;
                                }
                            }

                    if (CanTolerateUnexplainedLeftovers(meaningfulLeftovers))
                    {
                        RecordPassingLeftovers(meaningfulLeftovers);
                        foundPassingField = true;
                        continue;
                    }

                    if (meaningfulLeftovers.Count < bestLeftoverCount)
                    {
                        bestLeftoverCount = meaningfulLeftovers.Count;
                        bestFieldName = ev.FieldName;
                        bestLeftovers = meaningfulLeftovers;
                    }

                        // This field has contradictions, but other evidence fields may still validate this candidate.
                        // Keep the smallest contradiction set for reporting if no evidence field passes.
                        continue;
                    }

                var (anchorStart, anchorEnd) = anchor.Value;

                var isTitleIndex = new bool[fieldTokenSeq.Count];
                if (nearExactAlignment != null)
                {
                    foreach (var consumedIndex in nearExactAlignment.ConsumedFieldIndexes)
                    {
                        if (consumedIndex >= 0 && consumedIndex < isTitleIndex.Length)
                        {
                            isTitleIndex[consumedIndex] = true;
                        }
                    }
                }
                else
                {
                    // Mark token indices that satisfy the title token multiset within the anchor window.
                    var requiredCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tok in titleTokenSeq.Where(t => !string.IsNullOrWhiteSpace(t)))
                    {
                        if (!requiredCounts.ContainsKey(tok))
                        {
                            requiredCounts[tok] = 0;
                        }
                        requiredCounts[tok]++;
                    }

                    var remainingCounts = new Dictionary<string, int>(requiredCounts, StringComparer.OrdinalIgnoreCase);
                    for (var i = anchorStart; i <= anchorEnd && i < fieldTokenSeq.Count; i++)
                    {
                        var tok = fieldTokenSeq[i];
                        if (string.IsNullOrWhiteSpace(tok))
                        {
                            continue;
                        }

                        if (remainingCounts.TryGetValue(tok, out var cnt) && cnt > 0)
                        {
                            remainingCounts[tok] = cnt - 1;
                            isTitleIndex[i] = true;
                        }
                    }
                }

                var contradictions = new List<string>();
                var ambiguousNumeric = new List<string>();

                void ConsiderNearToken(string tok, int? index)
                {
                    if (string.IsNullOrWhiteSpace(tok))
                    {
                        return;
                    }

                    tok = tok.Trim();

                    if (HolyGrailLeftoverHardNoiseTokens.Contains(tok))
                    {
                        return;
                    }

                    if (IsRecognizedSeriesDecoration(tok, index))
                    {
                        return;
                    }

                    if (explainableTokens.Contains(tok))
                    {
                        return;
                    }

                    // Normalize zero-padded numbers: "04" should match series position "4", etc.
                    if (tok.All(char.IsDigit))
                    {
                        var normalized = tok.TrimStart('0');
                        if (string.IsNullOrEmpty(normalized))
                        {
                            normalized = "0";
                        }

                        if (explainableTokens.Contains(normalized))
                        {
                            return;
                        }
                    }

                    bool HasAdjacentExplainedSeriesPositionToken(int idx)
                    {
                        var neighbors = new[] { idx - 1, idx + 1 };
                        foreach (var n in neighbors)
                        {
                            if (n < 0 || n >= fieldTokenSeq.Count)
                            {
                                continue;
                            }

                            var nt = fieldTokenSeq[n]?.Trim();
                            if (string.IsNullOrWhiteSpace(nt))
                            {
                                continue;
                            }

                            if (explainableSeriesPositionTokens.Contains(nt))
                            {
                                return true;
                            }

                            foreach (var token in SeriesPositionTokenHelper.GetPositionTokens(nt))
                            {
                                if (explainableSeriesPositionTokens.Contains(token))
                                {
                                    return true;
                                }
                            }
                        }

                        return false;
                    }

                    if (index.HasValue &&
                        HolyGrailLeftoverStructuralTokens.Contains(tok) &&
                        HasAdjacentExplainedSeriesPositionToken(index.Value))
                    {
                        return;
                    }

                    if (!IsMeaningful(tok))
                    {
                        return;
                    }

                    var isNumeric = tok.All(char.IsDigit) || SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(tok);
                    if (!isNumeric)
                    {
                        contradictions.Add(tok);
                        return;
                    }

                    var packaging = IsZeroPaddedNumber(tok) ||
                                    (index.HasValue && HasNearbyPackagingMarker(fieldTokenSeq, index.Value)) ||
                                    (index.HasValue && HasAdjacentExplainedSeriesPositionToken(index.Value));
                    if (packaging)
                    {
                        // Packaging numeric tokens near the title are treated as ambiguity/noise (do not block).
                        return;
                    }

                    if (strictness == BookMatchingStrictness.Strict)
                    {
                        contradictions.Add(tok);
                        return;
                    }

                    if (strictness == BookMatchingStrictness.Balanced)
                    {
                        ambiguousNumeric.Add(tok);
                    }
                }

                for (var i = anchorStart; i <= anchorEnd && i < fieldTokenSeq.Count; i++)
                {
                    if (isTitleIndex[i])
                    {
                        continue;
                    }

                    var tok = fieldTokenSeq[i];
                    if (string.IsNullOrWhiteSpace(tok))
                    {
                        continue;
                    }

                    ConsiderNearToken(tok, i);
                }

                void ScanSide(int startIndex, int step)
                {
                    var consecutiveMetadataWallTokens = 0;
                    var metadataWallEstablished = false;

                    for (var i = startIndex; i >= 0 && i < fieldTokenSeq.Count; i += step)
                    {
                        if (isTitleIndex[i])
                        {
                            continue;
                        }

                        var tok = fieldTokenSeq[i];
                        if (string.IsNullOrWhiteSpace(tok))
                        {
                            continue;
                        }

                        var dist = step < 0 ? anchorStart - i : i - anchorEnd;
                        if (dist > nearDistanceTokens)
                        {
                            break;
                        }

                        tok = tok.Trim();

                        if (IsMetadataWallToken(tok))
                        {
                            consecutiveMetadataWallTokens++;
                            if (consecutiveMetadataWallTokens >= 2)
                            {
                                metadataWallEstablished = true;
                            }

                            continue;
                        }

                        if (!IsMeaningful(tok))
                        {
                            // Once we've clearly moved into explainable metadata, treat interstitial
                            // noise as part of the same wall and keep scanning outward.
                            if (consecutiveMetadataWallTokens > 0)
                            {
                                continue;
                            }

                            continue;
                        }

                        if (metadataWallEstablished &&
                            !HolyGrailLeftoverStructuralTokens.Contains(tok) &&
                            !tok.All(char.IsDigit) &&
                            !SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(tok))
                        {
                            continue;
                        }

                        consecutiveMetadataWallTokens = 0;

                        ConsiderNearToken(tok, i);
                    }
                }

                if (contradictions.Count == 0)
                {
                    ScanSide(anchorStart - 1, -1);
                }

                if (contradictions.Count == 0)
                {
                    ScanSide(anchorEnd + 1, 1);
                }

                if (contradictions.Count == 0 && forcedNearTokens.Count > 0)
                {
                    foreach (var tok in forcedNearTokens)
                    {
                        // Numeric-only sibling values are treated as near evidence (no positional distance available).
                        ConsiderNearToken(tok, null);
                        if (contradictions.Count > 0)
                        {
                            break;
                        }
                    }
                }

                            if (contradictions.Count > 0)
                            {
                                // Keep the smallest contradiction set for reporting if no evidence field passes.
                                var distinct = contradictions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                                if (strictness == BookMatchingStrictness.Strict)
                                {
                                    _logger.Debug("[HOLY-GRAIL] Rejecting candidate '{0}' (EditionId={1}) due to leftovers in field '{2}' (strict): [{3}]",
                                        candidate.EditionTitle, candidate.EditionId, ev.FieldName, string.Join(", ", distinct.Take(8)));
                                    RecordRejectedEvidenceField(distinct, ev.FieldName);
                                    continue;
                                }

                                if (CanTolerateUnexplainedLeftovers(distinct))
                                {
                                    RecordPassingLeftovers(distinct);
                                    foundPassingField = true;
                                    continue;
                                }

                                if (distinct.Count < bestLeftoverCount)
                                {
                                    bestLeftoverCount = distinct.Count;
                                    bestFieldName = ev.FieldName;
                                    bestLeftovers = distinct;
                                }
                                continue;
                        }

                if (strictness == BookMatchingStrictness.Balanced && ambiguousNumeric.Count > 0)
                {
                    var distinct = ambiguousNumeric.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    RecordPassingLeftovers(distinct);
                    foundPassingField = true;
                    continue;
                }

                RecordPassingLeftovers(Array.Empty<string>());
                foundPassingField = true;
            }

                if (foundPassingField)
                {
                    return (true, null, bestPassingLeftovers ?? (IReadOnlyList<string>)Array.Empty<string>());
                }

            if (bestLeftovers.Count > 0)
            {
                _logger.Debug("[HOLY-GRAIL] Rejecting candidate '{0}' (EditionId={1}) based on leftovers in field '{2}': [{3}]",
                    candidate.EditionTitle, candidate.EditionId, bestFieldName, string.Join(", ", bestLeftovers.Take(8)));
            }

            return (false, bestFieldName, bestLeftovers);
        }

            private List<string> TokenizeForLeftoverGate(string text)
            {
                return Services.BookImportUnitGroupingService.TokenizeForLeftoverGate(text);
            }

                private List<string> TokenizeForLeftoverGateSequence(string text)
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return new List<string>();
                    }

                text = System.Text.RegularExpressions.Regex.Replace(text, "['\\u2018\\u2019]s\\b", "s");

                text = text.Normalize(NormalizationForm.FormD);
                var sb = new StringBuilder();
                foreach (var c in text)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    {
                        sb.Append(c);
                    }
                }
                text = sb.ToString();

                text = System.Text.RegularExpressions.Regex.Replace(text, @"[–—-]", " ");
                text = text.Replace('.', ' ');

                    bool TrySplitNumericPackagingToken(string tok, out string prefix, out string numericSuffix)
                    {
                        prefix = null;
                        numericSuffix = null;

                        if (string.IsNullOrWhiteSpace(tok) || tok.Length < 3)
                        {
                            return false;
                        }

                        var firstDigit = -1;
                        for (var i = 0; i < tok.Length; i++)
                        {
                            if (char.IsDigit(tok[i]))
                            {
                                firstDigit = i;
                                break;
                            }
                        }

                        if (firstDigit <= 0 || firstDigit >= tok.Length - 1)
                        {
                            return false;
                        }

                        var maybePrefix = tok.Substring(0, firstDigit);
                        var maybeSuffix = tok.Substring(firstDigit);

                        if (string.IsNullOrWhiteSpace(maybePrefix) || string.IsNullOrWhiteSpace(maybeSuffix))
                        {
                            return false;
                        }

                        if (!maybeSuffix.All(char.IsDigit))
                        {
                            return false;
                        }

                        // Only split when the prefix is a known structural/packaging marker (e.g., "pt01", "track2", "vol3").
                        if (!HolyGrailLeftoverStructuralTokens.Contains(maybePrefix) &&
                            !HolyGrailLeftoverNumericPackagingTokens.Contains(maybePrefix) &&
                            !HolyGrailLeftoverHardNoiseTokens.Contains(maybePrefix))
                        {
                            return false;
                        }

                        prefix = maybePrefix;
                        numericSuffix = maybeSuffix;
                        return true;
                    }

                    var matches = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{Nd}]+");
                    var tokens = new List<string>(matches.Count);
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var tok = m.Value;
                        if (string.IsNullOrWhiteSpace(tok))
                        {
                            continue;
                        }

                        if (TrySplitNumericPackagingToken(tok, out var prefix, out var numericSuffix))
                        {
                            tokens.Add(prefix);
                            tokens.Add(numericSuffix);
                            continue;
                        }

                        tokens.Add(tok);
                    }

                    return tokens;
                }

            private (int Start, int End)? FindMinimumCoveringWindow(IReadOnlyList<string> fieldTokens, IReadOnlyList<string> requiredTokens)
            {
                if (fieldTokens == null || requiredTokens == null || fieldTokens.Count == 0 || requiredTokens.Count == 0)
                {
                    return null;
                }

                var need = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var tok in requiredTokens)
                {
                    if (string.IsNullOrWhiteSpace(tok))
                    {
                        continue;
                    }

                    if (!need.ContainsKey(tok))
                    {
                        need[tok] = 0;
                    }
                    need[tok]++;
                }

                if (need.Count == 0)
                {
                    return null;
                }

                var have = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var formed = 0;
                var required = need.Count;

                var bestLen = int.MaxValue;
                var bestStart = 0;
                var bestEnd = 0;

                var left = 0;
                for (var right = 0; right < fieldTokens.Count; right++)
                {
                    var tok = fieldTokens[right];
                    if (need.TryGetValue(tok, out var neededCount))
                    {
                        if (!have.ContainsKey(tok))
                        {
                            have[tok] = 0;
                        }
                        have[tok]++;
                        if (have[tok] == neededCount)
                        {
                            formed++;
                        }
                    }

                    while (left <= right && formed == required)
                    {
                        var len = right - left + 1;
                        if (len < bestLen)
                        {
                            bestLen = len;
                            bestStart = left;
                            bestEnd = right;
                        }

                        var leftTok = fieldTokens[left];
                        if (need.TryGetValue(leftTok, out var leftNeed))
                        {
                            have[leftTok]--;
                            if (have[leftTok] < leftNeed)
                            {
                                formed--;
                            }
                        }
                        left++;
                    }
                }

                if (bestLen == int.MaxValue)
                {
                    return null;
                }

                return (bestStart, bestEnd);
            }

            private string StripTrailingParentheticals(string title)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }

                var remaining = title.Trim();
                while (remaining.EndsWith(")", StringComparison.Ordinal))
                {
                    var open = remaining.LastIndexOf('(');
                    if (open <= 0)
                    {
                        break;
                    }

                    var inside = remaining.Substring(open + 1, remaining.Length - open - 2).Trim();
                    if (string.IsNullOrWhiteSpace(inside))
                    {
                        break;
                    }

                    remaining = remaining.Substring(0, open).TrimEnd();
                }

                return remaining;
            }

            /// <summary>
            /// Run FTS search and smoke test each result. Returns first match that passes smoke test.
            /// </summary>
            private FtsSmokeTestResult RunFtsWithSmokeTest(
            int? authorId,
            List<string> tokens,
            BookMediaType mediaType,
            string phase,
            Dictionary<string, List<string>> allTags = null,
            string filePath = null,
            List<CandidateRejection> rejections = null,
            int maxRejections = 50,
            int? fileDurationSeconds = null,
            bool allowNarratorEvidence = true,
            Action onContradictoryEvidence = null,
            IReadOnlyList<Dictionary<string, List<string>>> groupMemberTags = null,
            IReadOnlySet<int> hardAllowedBookIds = null)
        {
            string Truncate(string value, int maxLen)
            {
                if (string.IsNullOrEmpty(value) || maxLen <= 0)
                {
                    return value;
                }

                return value.Length > maxLen ? value.Substring(0, maxLen) + "..." : value;
            }

            var captureContext = _rejectionCapture.Value;
            var rejectionPhase = phase;
            if (rejections == null && captureContext?.Rejections != null)
            {
                rejections = captureContext.Rejections;
                maxRejections = captureContext.MaxRejections;
                if (!string.IsNullOrWhiteSpace(captureContext.Scope))
                {
                    rejectionPhase = $"{captureContext.Scope}/{phase}";
                }
            }

            bool CanRecord() => rejections != null && rejections.Count < maxRejections;
            void MarkContradictoryEvidence() => onContradictoryEvidence?.Invoke();

            void RecordPhaseRejection(string reason, string detail = null)
            {
                if (IsMatchingTraceEnabled())
                {
                    RecordTrace("phase_rejected", rejectionPhase, reason: reason, detail: Truncate(detail, 200), filePath: filePath);
                }

                if (!CanRecord())
                {
                    return;
                }

                rejections.Add(new CandidateRejection
                {
                    Phase = rejectionPhase,
                    EditionId = null,
                    Score = null,
                    TitleSnippet = null,
                    Reason = reason,
                    Detail = Truncate(detail, 200)
                });
            }

            CandidateRejection RecordCandidateRejection(
                EditionFtsMatch candidate,
                string reason,
                string detail = null,
                string fallbackDisposition = null)
            {
                if (IsMatchingTraceEnabled())
                {
                    RecordTrace("candidate_rejected", rejectionPhase, candidate, reason, Truncate(detail, 200), filePath);
                }

                if (!CanRecord())
                {
                    return null;
                }

                var entry = new CandidateRejection
                {
                    Phase = rejectionPhase,
                    EditionId = candidate?.EditionId,
                    Score = candidate != null ? candidate.MatchScore : (double?)null,
                    TitleSnippet = Truncate(candidate?.EditionTitle, 80),
                    Reason = reason,
                    Detail = Truncate(detail, 200),
                    FallbackDisposition = fallbackDisposition
                };
                rejections.Add(entry);
                return entry;
            }

            if (tokens == null || tokens.Count == 0)
            {
                _logger.Debug("[HOLY-GRAIL][{0}] No tokens to search", phase);
                RecordPhaseRejection("NO_TOKENS", "No tokens to search");
                return null;
            }

            if (allTags == null)
            {
                RecordPhaseRejection("NO_TAGS", "No tags dictionary provided");
                allTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var strictness = GetConfiguredMatchingStrictness();

            // Series info comes from DB only. Tag labels are never trusted.

                var booksById = new Dictionary<int, Book>();

                var stagedFtsUsed = false;
                var stagedFieldQueries = new List<EditionFtsFieldQuery>();
                var stagedGroupFields = new List<GroupPhysicalField>();
                List<EditionFtsMatch> ftsResults;
                if (_editionFtsRepository is IStagedEditionFtsRepository stagedRepository)
                {
                    stagedFtsUsed = true;
                    Action<EditionFtsTraceEvent> trace = IsMatchingTraceEnabled()
                        ? evt => RecordFtsTrace(evt, rejectionPhase, filePath)
                        : null;
                    var stagedStopwatch = Stopwatch.StartNew();
                    trace?.Invoke(new EditionFtsTraceEvent
                    {
                        EventType = "input",
                        Step = "staged_matching",
                        Terms = tokens.ToList()
                    });
                    var recalledBooks = stagedRepository.RecallBooks(authorId, tokens, mediaType, trace, limit: 20);
                    var authorGateCache = new Dictionary<
                        string,
                        (bool Proven, bool TrustedScope, string ProvenName, IReadOnlyList<string> IdentityNames)>(
                        StringComparer.OrdinalIgnoreCase);
                    var authorNamesToConsume = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    string AuthorGateKey(BookFtsMatch recalledBook)
                    {
                        return recalledBook.AuthorId > 0
                            ? $"id:{recalledBook.AuthorId.ToString(CultureInfo.InvariantCulture)}"
                            : $"name:{recalledBook.AuthorName?.Trim() ?? string.Empty}";
                    }

                    IReadOnlyList<string> ResolveAuthorIdentityNames(BookFtsMatch recalledBook)
                    {
                        var names = new List<string>();
                        if (!string.IsNullOrWhiteSpace(recalledBook.AuthorName))
                        {
                            names.Add(recalledBook.AuthorName);
                        }

                        try
                        {
                            var author = recalledBook.AuthorId > 0
                                ? _authorService?.GetAuthor(recalledBook.AuthorId)
                                : null;
                            if (author != null && author.Id == recalledBook.AuthorId)
                            {
                                if (!string.IsNullOrWhiteSpace(author.Name))
                                {
                                    names.Add(author.Name);
                                }

                                names.AddRange(author.Pseudonyms ?? new List<string>());
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(
                                ex,
                                "[HOLY-GRAIL][{0}] Could not load pseudonyms for recalled author {1}; using the recalled canonical name",
                                phase,
                                recalledBook.AuthorId);
                        }

                        return names
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Select(name => name.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }

                    (bool Proven, bool TrustedScope, string ProvenName, IReadOnlyList<string> IdentityNames)
                        EvaluateAuthorGate(BookFtsMatch recalledBook)
                    {
                        if (recalledBook == null)
                        {
                            return (false, false, null, Array.Empty<string>());
                        }

                        var cacheKey = AuthorGateKey(recalledBook);
                        if (authorGateCache.TryGetValue(cacheKey, out var cached))
                        {
                            return cached;
                        }

                        var identityNames = ResolveAuthorIdentityNames(recalledBook);
                        var trustedScope = authorId.HasValue && recalledBook.AuthorId == authorId.Value;
                        string provenName = null;
                        if (!authorId.HasValue)
                        {
                            provenName = identityNames.FirstOrDefault(name =>
                                _containmentValidator.ValidateAuthorInTags(name, allTags) ||
                                TryBuildExactGroupIdentitySpanProof(name, groupMemberTags, out _));
                        }

                        var result = (
                            Proven: trustedScope || provenName != null,
                            TrustedScope: trustedScope,
                            ProvenName: provenName,
                            IdentityNames: identityNames);
                        authorGateCache[cacheKey] = result;
                        return result;
                    }

                    var gatedBooks = new List<BookFtsMatch>();
                    foreach (var recalledBook in recalledBooks ?? new List<BookFtsMatch>())
                    {
                        if (recalledBook == null)
                        {
                            continue;
                        }

                        if (hardAllowedBookIds?.Count > 0 &&
                            !hardAllowedBookIds.Contains(recalledBook.BookId))
                        {
                            RecordTrace(
                                "fts_hard_book_rejected",
                                rejectionPhase,
                                new EditionFtsMatch
                                {
                                    BookId = recalledBook.BookId,
                                    BookTitle = recalledBook.BookTitle,
                                    AuthorId = recalledBook.AuthorId,
                                    AuthorName = recalledBook.AuthorName,
                                    MatchScore = recalledBook.MatchScore
                                },
                                "HARD_BOOK_CONSTRAINT",
                                filePath: filePath);
                            continue;
                        }

                        var authorGate = EvaluateAuthorGate(recalledBook);
                        var rejectionReason = authorId.HasValue
                            ? "AUTHOR_SCOPE_MISMATCH"
                            : "AUTHOR_NOT_IN_TAGS";
                        var traceCandidate = new EditionFtsMatch
                        {
                            BookId = recalledBook.BookId,
                            BookTitle = recalledBook.BookTitle,
                            AuthorId = recalledBook.AuthorId,
                            AuthorName = recalledBook.AuthorName,
                            MatchScore = recalledBook.MatchScore,
                            BroadRecallScore = recalledBook.MatchScore
                        };
                        RecordTrace(
                            authorGate.Proven ? "fts_author_gate_passed" : "fts_author_gate_rejected",
                            rejectionPhase,
                            traceCandidate,
                            authorGate.Proven ? null : rejectionReason,
                            filePath: filePath,
                            data: new Dictionary<string, string>
                            {
                                ["authorName"] = recalledBook.AuthorName ?? string.Empty,
                                ["authorProof"] = authorGate.TrustedScope
                                    ? "trusted_scope"
                                    : authorGate.ProvenName != null
                                        ? "embedded_name"
                                        : "none",
                                ["authorProofName"] = authorGate.ProvenName ?? string.Empty,
                                ["authorsEvaluated"] = authorGateCache.Count.ToString(CultureInfo.InvariantCulture)
                            });
                        if (authorGate.Proven)
                        {
                            gatedBooks.Add(recalledBook);
                            foreach (var identityName in authorGate.IdentityNames)
                            {
                                authorNamesToConsume.Add(identityName);
                            }
                        }
                    }

                    if (gatedBooks.Count == 0)
                    {
                        RecordPhaseRejection(
                            "AUTHOR_GATE_EMPTY",
                            $"recalledBooks={recalledBooks?.Count ?? 0} uniqueAuthorsEvaluated={authorGateCache.Count} scoped={authorId.HasValue}");
                        return null;
                    }

                    stagedFieldQueries = BuildStagedFtsFieldQueries(
                        allTags,
                        gatedBooks,
                        authorNamesToConsume,
                        rejectionPhase,
                        filePath);
                    stagedGroupFields = BuildCandidateRelativeGroupFields(
                        groupMemberTags,
                        authorNamesToConsume);
                    ftsResults = stagedRepository.RankEditions(gatedBooks, stagedFieldQueries, mediaType, trace);
                    stagedStopwatch.Stop();
                    trace?.Invoke(new EditionFtsTraceEvent
                    {
                        EventType = "completed",
                        Step = "staged_matching",
                        ResultCount = ftsResults?.Count ?? 0,
                        DistinctBookCount = ftsResults?.Select(result => result.BookId).Distinct().Count() ?? 0,
                        TotalElapsedMilliseconds = stagedStopwatch.ElapsedMilliseconds,
                        ResultSource = "author-gated-stage2-field-ranking"
                    });
                    _logger.Debug(
                        "[HOLY-GRAIL][{0}] Staged FTS retained {1}/{2} recalled Books after {3} unique author evaluations and ranked {4} Editions from {5} residual fields",
                        phase,
                        gatedBooks.Count,
                        recalledBooks?.Count ?? 0,
                        authorGateCache.Count,
                        ftsResults?.Count ?? 0,
                        stagedFieldQueries.Count);
                }
                else
                {
                    // Test doubles and compatibility callers that only implement the historical contract.
                    ftsResults = IsMatchingTraceEnabled() && _editionFtsRepository is IEditionFtsTraceRepository traceRepository
                        ? traceRepository.SearchWithTwoStepWithTrace(
                            authorId,
                            tokens,
                            mediaType,
                            evt => RecordFtsTrace(evt, rejectionPhase, filePath),
                            limit: 20)
                        : _editionFtsRepository.SearchWithTwoStep(authorId, tokens, mediaType, limit: 20);
                }

                if (hardAllowedBookIds?.Count > 0 && ftsResults != null)
                {
                    ftsResults = ftsResults
                        .Where(candidate => candidate != null && hardAllowedBookIds.Contains(candidate.BookId))
                        .ToList();
                }
                if (ftsResults == null || ftsResults.Count == 0)
                {
                    _logger.Debug("[HOLY-GRAIL][{0}] No FTS results", phase);
                    if (CanRecord())
                    {
                        try
                        {
                            var preview = string.Join(" ", tokens.Take(10));
                            RecordPhaseRejection("NO_FTS_RESULTS", $"tokens={tokens.Count} preview='{Truncate(preview, 120)}'");
                        }
                        catch
                        {
                            RecordPhaseRejection("NO_FTS_RESULTS");
                        }
                    }
                    return null;
                }

                _logger.Debug("[HOLY-GRAIL][{0}] Got {1} FTS results, validating evidence and leftovers...", phase, ftsResults.Count);
                if (IsMatchingTraceEnabled())
                {
                    foreach (var candidate in ftsResults.Take(50))
                    {
                        if (candidate == null)
                        {
                            continue;
                        }

                        RecordTrace("fts_candidate", rejectionPhase, candidate, filePath: filePath, data: new Dictionary<string, string>
                        {
                            ["bookTitle"] = candidate.BookTitle ?? string.Empty,
                            ["authorName"] = candidate.AuthorName ?? string.Empty,
                            ["narratorNames"] = candidate.NarratorNames ?? string.Empty,
                            ["durationSeconds"] = candidate.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            ["releaseDate"] = candidate.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty
                        });
                    }
                }

                // desiredTitleTokens removed: FTS + evidence + leftover gate handle candidate quality.
                // explicitSeriesTitleTokens removed: replaced by DB-metadata series-only rejection below.

                // Preload all books from FTS results (used by series constraint, series-only rejection,
                // leftover gate, and scoring). Avoids N+1 queries.
                try
                {
                    var uniqueBookIds = ftsResults.Select(r => r.BookId).Distinct().ToList();
                    var books = _bookService.GetBooks(uniqueBookIds) ?? new List<Book>();
                    booksById = books
                        .Where(b => b != null)
                        .GroupBy(b => b.Id)
                        .ToDictionary(g => g.Key, g => g.First());
                }
                catch
                {
                    // best-effort book cache — non-fatal if it fails
                }

                HashSet<int> stagedPreferredBookIds = null;
                HashSet<string> stagedGroupTitleFieldNames = null;
                string stagedPreferredWorkKey = null;
                // Pre-compute file-level signals from tags (once, not per-candidate)
                    // IsGenreExcludeKey removed — no longer needed since callers pass mainTags
                    // (CategorizeTagsForHolyGrail already excludes genre/language/cover/etc.)

                    var tagTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> containmentFieldTokens = null;
                    var captureContainmentDetails = CanRecord();
                    Dictionary<string, List<string>> authorEvidenceTags = null;
                if (captureContainmentDetails)
                {
                    containmentFieldTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                    if (allTags != null)
                    {
                        // allTags is already mainTags (callers pass CategorizeTagsForHolyGrail output).
                        // Trash/exclude keys already filtered. All remaining tags treated uniformly.
                        const int maxContainmentEvidenceValueLength = 400;
                        authorEvidenceTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in allTags)
                        {
                            if (kv.Value == null) continue;
                            var authorValues = kv.Value
                                .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= maxContainmentEvidenceValueLength)
                                .ToList();
                            if (authorValues.Count > 0)
                            {
                                authorEvidenceTags[kv.Key] = authorValues;
                            }
                            foreach (var v in kv.Value)
                            {
                                if (string.IsNullOrWhiteSpace(v) || v.Length >= 5000) continue;
                                var valueTokens = TokenizeForLeftoverGate(v);
                                foreach (var t in valueTokens)
                                {
                                    tagTokens.Add(t);
                                    if (captureContainmentDetails && v.Length <= maxContainmentEvidenceValueLength)
                                    {
                                        containmentFieldTokens.Add(t);
                                    }
                                }
                            }
                        }
                    }

                    var candidateAuthorEvidenceCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    bool CandidateAuthorInTags(string candidateAuthorName)
                    {
                        if (string.IsNullOrWhiteSpace(candidateAuthorName))
                        {
                            return false;
                        }

                        if (candidateAuthorEvidenceCache.TryGetValue(candidateAuthorName, out var cached))
                        {
                            return cached;
                        }

                        var result = (authorEvidenceTags != null &&
                                      authorEvidenceTags.Count > 0 &&
                                      _containmentValidator.ValidateAuthorInTags(candidateAuthorName, authorEvidenceTags)) ||
                                     TryBuildExactGroupIdentitySpanProof(candidateAuthorName, groupMemberTags, out _);
                        candidateAuthorEvidenceCache[candidateAuthorName] = result;
                        return result;
                    }

                var narratorMatchCache = new Dictionary<int, int>();
                int GetNarratorMatchCountCached(EditionFtsMatch candidate)
                {
                    if (!allowNarratorEvidence)
                    {
                        return 0;
                    }

                    if (candidate == null || candidate.EditionId <= 0)
                    {
                        return 0;
                    }

                    if (narratorMatchCache.TryGetValue(candidate.EditionId, out var cached))
                    {
                        return cached;
                    }

                    var computed = CountNarratorMatchesInTags(candidate.NarratorNames, candidate.AuthorName, allTags);
                    narratorMatchCache[candidate.EditionId] = computed;
                    return computed;
                }

                bool HasCompleteNarratorEvidence(EditionFtsMatch candidate)
                {
                    if (candidate == null)
                    {
                        return false;
                    }

                    var narratorCount = SplitNarrators(candidate.NarratorNames).Count;
                    return narratorCount > 0 &&
                           GetNarratorMatchCountCached(candidate) >= narratorCount;
                }

                bool IsAudiobookEdition(EditionFtsMatch candidate)
                {
                    return candidate?.ReadingFormatId == 2;
                }

                bool IsRepresentativeEdition(EditionFtsMatch candidate)
                {
                    return candidate?.ReadingFormatId == 3 || candidate?.ReadingFormatId == 1;
                }

                bool IsAudiobookSiblingOf(EditionFtsMatch other, EditionFtsMatch candidate)
                {
                    return other != null &&
                           candidate != null &&
                           !SameEditionIdentity(other, candidate) &&
                           IsAudiobookEdition(other) &&
                           SameLogicalWork(other, candidate, booksById);
                }

                bool HasConflictingNarratorEvidence(EditionFtsMatch candidate)
                {
                    if (candidate == null || mediaType != BookMediaType.Audiobook)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(candidate.NarratorNames))
                    {
                        return false;
                    }

                    if (GetNarratorMatchCountCached(candidate) > 0)
                    {
                        return false;
                    }

                    return ftsResults.Any(other =>
                        IsAudiobookSiblingOf(other, candidate) &&
                        HasCompleteNarratorEvidence(other));
                }

                bool HasOtherNarratorMatchedCandidate(EditionFtsMatch candidate)
                {
                    if (candidate == null || mediaType != BookMediaType.Audiobook)
                    {
                        return false;
                    }

                    return ftsResults.Any(other =>
                        IsAudiobookSiblingOf(other, candidate) &&
                        HasCompleteNarratorEvidence(other));
                }

                bool HasInsufficientSelfNarratorSignal(EditionFtsMatch candidate)
                {
                    if (candidate == null ||
                        mediaType != BookMediaType.Audiobook ||
                        string.IsNullOrWhiteSpace(candidate.NarratorNames) ||
                        allTags == null ||
                        allTags.Count == 0 ||
                        !allowNarratorEvidence)
                    {
                        return false;
                    }

                    foreach (var narrator in SplitNarrators(candidate.NarratorNames))
                    {
                        if (!IsAuthorAsNarrator(narrator, candidate.AuthorName))
                        {
                            continue;
                        }

                        var normalizedNarrator = NormalizePersonNameForMatch(narrator);
                        if (string.IsNullOrWhiteSpace(normalizedNarrator))
                        {
                            continue;
                        }

                        var narratorNoSpace = normalizedNarrator.Replace(" ", string.Empty);
                        var narratorWords = normalizedNarrator.Split(' ').Where(w => w.Length > 1).ToList();
                        var distinctFieldCount = 0;

                        foreach (var kv in allTags)
                        {
                            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0 || IsExcludedFromMatching(kv.Key))
                            {
                                continue;
                            }

                            var fieldMatched = kv.Value.Any(rawValue =>
                            {
                                if (string.IsNullOrWhiteSpace(rawValue))
                                {
                                    return false;
                                }

                                var haystack = NormalizePersonNameForMatch(rawValue);
                                if (string.IsNullOrWhiteSpace(haystack))
                                {
                                    return false;
                                }

                                var haystackNoSpace = haystack.Replace(" ", string.Empty);
                                return haystackNoSpace.Contains(narratorNoSpace, StringComparison.Ordinal) ||
                                       (narratorWords.Count >= 2 && narratorWords.All(w => haystackNoSpace.Contains(w, StringComparison.Ordinal)));
                            });

                            if (fieldMatched)
                            {
                                distinctFieldCount++;
                            }
                        }

                        if (distinctFieldCount > 0 && distinctFieldCount < 2)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasEquivalentExplicitNarratorDuplicateSet(EditionFtsMatch candidate)
                {
                    if (candidate == null || mediaType != BookMediaType.Audiobook)
                    {
                        return false;
                    }

                    var candidateNarratorKey = BuildNarratorIdentityKey(candidate.NarratorNames);
                    if (string.IsNullOrWhiteSpace(candidateNarratorKey))
                    {
                        return false;
                    }

                    var sameWorkCandidates = ftsResults
                        .Where(other =>
                            other != null &&
                            (other.EditionId == candidate.EditionId || IsAudiobookEdition(other)) &&
                            SameLogicalWork(other, candidate, booksById))
                        .GroupBy(other => GetEditionIdentityKey(other.ForeignEditionId, other.EditionId), StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();

                    return sameWorkCandidates.Count > 1 &&
                           sameWorkCandidates.All(other =>
                               string.Equals(
                                   BuildNarratorIdentityKey(other.NarratorNames),
                                   candidateNarratorKey,
                                   StringComparison.OrdinalIgnoreCase));
                }

                bool LooksLikeMultipartAudiobookTrack()
                {
                    if (mediaType != BookMediaType.Audiobook || allTags == null || allTags.Count == 0)
                    {
                        return false;
                    }

                    foreach (var kv in allTags)
                    {
                        if (IsSeriesEvidenceNonSeriesNumericKey(kv.Key))
                        {
                            return true;
                        }

                        foreach (var rawValue in kv.Value ?? Enumerable.Empty<string>())
                        {
                            if (string.IsNullOrWhiteSpace(rawValue))
                            {
                                continue;
                            }

                            if (Regex.IsMatch(
                                    rawValue,
                                    @"\b(?:track|trk|disc|cd|chapter|chapters|chap|part|pt)\s*#?\s*\d{1,3}\b",
                                    RegexOptions.IgnoreCase) ||
                                Regex.IsMatch(
                                    rawValue,
                                    @"\bt\d{1,3}(?:\s*[-/]\s*\d{1,3})?\b",
                                    RegexOptions.IgnoreCase))
                            {
                                return true;
                            }

                            if (Regex.IsMatch(rawValue, @"\b\d{1,3}\s*[-/]\s*\d{1,3}\b", RegexOptions.IgnoreCase))
                            {
                                return true;
                            }
                        }
                    }

                    var extension = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetExtension(filePath);
                    return fileDurationSeconds.HasValue &&
                           fileDurationSeconds.Value > 0 &&
                           fileDurationSeconds.Value < 30 * 60 &&
                           string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase);
                }

                bool TryGetDurationFallbackDetail(EditionFtsMatch candidate, out string detail)
                {
                    detail = null;

                    if (candidate == null ||
                        !candidate.DurationSeconds.HasValue ||
                        candidate.DurationSeconds.Value <= 0)
                    {
                        detail = "missing_candidate_duration";
                        return false;
                    }

                    if (!fileDurationSeconds.HasValue || fileDurationSeconds.Value <= 0)
                    {
                        detail = "missing_file_duration";
                        return false;
                    }

                    var diffSeconds = Math.Abs(candidate.DurationSeconds.Value - fileDurationSeconds.Value);
                    var allowedSeconds = AudiobookDurationTolerance.ForMatchingSeconds(candidate.DurationSeconds.Value);
                    detail = $"candidateSec={candidate.DurationSeconds.Value} observedSec={fileDurationSeconds.Value} durationDiffSec={diffSeconds} allowedSec={allowedSeconds}";
                    return diffSeconds <= allowedSeconds;
                }

                bool CanMatchAudiobookWithoutNarratorEvidence(
                    EditionFtsMatch candidate,
                    IReadOnlyList<EditionTitleEvidence> evidence,
                    out string detail,
                    out bool usedUndistinguishedEditionFallback)
                {
                    detail = null;
                    usedUndistinguishedEditionFallback = false;

                    if (mediaType != BookMediaType.Audiobook)
                    {
                        return true;
                    }

                    if (GetNarratorMatchCountCached(candidate) > 0)
                    {
                        return true;
                    }

                    var hasAudiobookSibling = ftsResults.Any(other =>
                        IsAudiobookSiblingOf(other, candidate));
                    var isMultipartTrack = LooksLikeMultipartAudiobookTrack();

                    if (HasEquivalentExplicitNarratorDuplicateSet(candidate))
                    {
                        return true;
                    }

                    if (!hasAudiobookSibling &&
                        !HasOtherNarratorMatchedCandidate(candidate))
                    {
                        return true;
                    }

                    if (evidence == null || evidence.Count == 0)
                    {
                        detail = "no_title_evidence";
                        return false;
                    }

                    if (HasOtherNarratorMatchedCandidate(candidate))
                    {
                        detail = "another_audiobook_edition_has_narrator_evidence";
                        return false;
                    }

                    if (!isMultipartTrack && TryGetDurationFallbackDetail(candidate, out detail))
                    {
                        return true;
                    }

                    var hasFileDuration = fileDurationSeconds.HasValue && fileDurationSeconds.Value > 0;
                    var hasCandidateDuration = candidate.DurationSeconds.HasValue && candidate.DurationSeconds.Value > 0;
                    var hasConcreteDurationConflict = !isMultipartTrack && hasFileDuration && hasCandidateDuration;
                    if (hasConcreteDurationConflict)
                    {
                        detail = AppendDiagnosticDetail(detail ?? "no_near_exact_duration", "fallback=true");
                        return false;
                    }

                    usedUndistinguishedEditionFallback = true;
                    detail = isMultipartTrack
                        ? "multipart_track_cannot_distinguish_full_audiobook_editions"
                        : AppendDiagnosticDetail(detail ?? "missing_narrator_or_duration_for_audiobook_sibling", "undistinguished_native=true");
                    return true;
                }

                bool HasStrongDirectEditionTitleEvidence(IReadOnlyList<EditionTitleEvidence> directEvidence)
                {
                    return directEvidence != null &&
                           directEvidence.Any(e => e != null &&
                                                   !e.IsNearExact &&
                                                   !e.RequiresAudiobookDurationCorroboration);
                }

                string BuildContainmentMissingTokenDetail(string editionTitle)
                {
                    if (!captureContainmentDetails || containmentFieldTokens == null)
                    {
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(editionTitle))
                    {
                        return null;
                    }

                    var edTokens = TokenizeForLeftoverGate(editionTitle)
                        .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (edTokens.Count == 0)
                    {
                        return null;
                    }

                    var missing = edTokens
                        .Where(t => !containmentFieldTokens.Contains(t))
                        .Take(6)
                        .ToList();

                    return missing.Count > 0 ? $"missing=[{string.Join(", ", missing)}]" : null;
                }

                List<string> NormalizeSeriesPrefixTokens(string value)
                {
                    return TokenizeForLeftoverGateSequence(value)
                        .Where(token => !string.IsNullOrWhiteSpace(token) &&
                                        !SeriesNameNoiseTokens.Contains(token))
                        .ToList();
                }

                IEnumerable<string> GetBookSeriesNames(Book book)
                {
                    if (book == null)
                    {
                        yield break;
                    }

                    if (!string.IsNullOrWhiteSpace(book.SeriesName))
                    {
                        yield return book.SeriesName;
                    }

                    foreach (var link in book.SeriesLinks ?? Enumerable.Empty<SeriesBookLink>())
                    {
                        var name = link?.Series?.Value?.Title;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            yield return name;
                        }
                    }
                }

                bool BaseTitleMatchesSeriesPrefix(EditionFtsMatch candidate, string baseTitle)
                {
                    var baseTokens = NormalizeSeriesPrefixTokens(baseTitle);
                    if (baseTokens.Count == 0)
                    {
                        return true;
                    }

                    var candidateBook = TryGetBookCached(candidate.BookId, booksById);
                    if (candidateBook == null)
                    {
                        return true;
                    }

                    var authorIdForScope = candidate.AuthorId > 0 ? candidate.AuthorId : candidateBook.AuthorId;
                    var scopedBooks = (booksById?.Values ?? Enumerable.Empty<Book>())
                        .Where(book => book != null &&
                                       (authorIdForScope <= 0 || book.AuthorId <= 0 || book.AuthorId == authorIdForScope))
                        .Append(candidateBook)
                        .GroupBy(book => book.Id)
                        .Select(group => group.First());

                    foreach (var seriesName in scopedBooks.SelectMany(GetBookSeriesNames).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var seriesTokens = NormalizeSeriesPrefixTokens(seriesName);
                        if (seriesTokens.Count >= baseTokens.Count &&
                            baseTokens.SequenceEqual(
                                seriesTokens.Take(baseTokens.Count),
                                StringComparer.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasOnlyAttestedSubtitleDelta(string fullTitle, string baseTitle, string subtitle)
                {
                    var fullTokens = TokenizeForLeftoverGateSequence(fullTitle);
                    var expectedTokens = TokenizeForLeftoverGateSequence(baseTitle)
                        .Concat(TokenizeForLeftoverGateSequence(subtitle));
                    return fullTokens.Count > 0 &&
                           fullTokens.SequenceEqual(expectedTokens, StringComparer.OrdinalIgnoreCase);
                }

                bool CandidateExplainsSingleField(EditionFtsMatch candidate, IDictionary<string, List<string>> singleFieldTags)
                {
                    if ((_containmentValidator.GetEditionTitleEvidence(candidate.EditionTitle, singleFieldTags)?.Count ?? 0) > 0 ||
                        (!string.IsNullOrWhiteSpace(candidate.BookTitle) &&
                         !string.Equals(candidate.BookTitle.Trim(), candidate.EditionTitle?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                         (_containmentValidator.GetEditionTitleEvidence(candidate.BookTitle, singleFieldTags)?.Count ?? 0) > 0))
                    {
                        return true;
                    }

                    return ReleaseTitleMatchScorer.TryGetKnownSubtitleBaseTitle(
                               candidate.EditionTitle,
                               candidate.EditionSubTitle,
                               out var otherBaseTitle) &&
                           (_containmentValidator.GetEditionTitleEvidence(otherBaseTitle, singleFieldTags)?.Count ?? 0) > 0;
                }

                IReadOnlyList<EditionTitleEvidence> GetUnambiguousBaseTitleEvidence(
                    EditionFtsMatch candidate,
                    IReadOnlyList<EditionTitleEvidence> directEvidence,
                    bool includeDurationGatedNearExact,
                    Func<EditionTitleEvidence, bool> evidenceIsEligible)
                {
                    if (candidate == null ||
                        directEvidence?.Count > 0 ||
                        !ReleaseTitleMatchScorer.TryGetKnownSubtitleBaseTitle(
                            candidate.EditionTitle,
                            candidate.EditionSubTitle,
                            out var baseTitle) ||
                        !HasOnlyAttestedSubtitleDelta(candidate.EditionTitle, baseTitle, candidate.EditionSubTitle) ||
                        SeriesPositionTokenHelper.HasPositionIdentity(candidate.EditionSubTitle) ||
                        BaseTitleMatchesSeriesPrefix(candidate, baseTitle))
                    {
                        return Array.Empty<EditionTitleEvidence>();
                    }

                    var baseEvidence = _containmentValidator.GetEditionTitleEvidence(baseTitle, allTags, includeDurationGatedNearExact);
                    if (baseEvidence == null || baseEvidence.Count == 0)
                    {
                        return Array.Empty<EditionTitleEvidence>();
                    }

                    var acceptedEvidence = new List<EditionTitleEvidence>();
                    foreach (var item in baseEvidence)
                    {
                        if (item == null || (evidenceIsEligible != null && !evidenceIsEligible(item)))
                        {
                            continue;
                        }

                        var singleFieldTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [string.IsNullOrWhiteSpace(item.FieldName) ? "FIELD" : item.FieldName] = new List<string> { item.FieldValue }
                        };

                        var isAmbiguous = ftsResults.Any(other =>
                            other != null &&
                            other.EditionId != candidate.EditionId &&
                            !SameLogicalWork(other, candidate, booksById) &&
                            (candidate.AuthorId <= 0 || other.AuthorId <= 0 || other.AuthorId == candidate.AuthorId) &&
                            CandidateExplainsSingleField(other, singleFieldTags));

                        if (!isAmbiguous)
                        {
                            acceptedEvidence.Add(item);
                        }
                    }

                    if (acceptedEvidence.Count > 0)
                    {
                        _logger.Debug(
                            "[HOLY-GRAIL][{0}] Subtitle-only containment rescue: EditionId={1} BaseTitle='{2}' Fields=[{3}]",
                            phase, candidate.EditionId, Truncate(baseTitle, 80),
                            string.Join(", ", acceptedEvidence.Select(item => item.FieldName).Distinct(StringComparer.OrdinalIgnoreCase)));
                    }

                    return acceptedEvidence;
                }


                var fileYear = TryExtractYearFromTags(allTags);
                if (stagedFtsUsed)
                {
                    var stagedDecision = SelectStagedBookByFieldRepresentation(
                        ftsResults,
                        stagedFieldQueries,
                        mediaType,
                        stagedGroupFields,
                        booksById,
                        fileDurationSeconds,
                        fileYear,
                        !LooksLikeMultipartAudiobookTrack(),
                        rejectionPhase,
                        filePath);
                    if (stagedDecision != null)
                    {
                        stagedPreferredBookIds = stagedDecision.BookIds;
                        stagedGroupTitleFieldNames = stagedDecision.GroupTitleFieldNames;
                        stagedPreferredWorkKey = stagedDecision.WorkKey;
                    }
                }

                // fileDurationSeconds comes from the parameter (computed during discovery, not re-extracted from tags)
                var useGroupedSelection =
                    (mediaType == BookMediaType.Audiobook &&
                     fileDurationSeconds.HasValue &&
                     ftsResults.Any(result => result?.DurationSeconds.HasValue == true)) ||
                    (mediaType == BookMediaType.Ebook && fileYear.HasValue);

                var titleEvidenceCandidateCount = 0;
                var strictSeriesPositionRejectedCount = 0;
                var leftoverRejectedCount = 0;
                var authorRejectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<EditionTitleEvidence> GetCandidateTitleEvidence(
                    string title,
                    bool includeDurationGatedNearExact)
                {
                    var combined = new List<EditionTitleEvidence>();
                    var ordinaryEvidence = _containmentValidator.GetEditionTitleEvidence(
                        title,
                        allTags,
                        includeDurationGatedNearExact);
                    if (ordinaryEvidence != null)
                    {
                        combined.AddRange(ordinaryEvidence.Where(item => item != null));
                    }

                    if (stagedGroupTitleFieldNames?.Count > 0)
                    {
                        combined.AddRange(GetCandidateRelativeGroupTitleEvidence(
                            title,
                            stagedGroupFields,
                            stagedGroupTitleFieldNames));
                    }

                    var distinct = new List<EditionTitleEvidence>();
                    foreach (var item in combined)
                    {
                        if (item == null ||
                            distinct.Any(existing =>
                                string.Equals(existing.FieldName, item.FieldName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(existing.FieldValue, item.FieldValue, StringComparison.Ordinal) &&
                                string.Equals(existing.MatchedTitle, item.MatchedTitle, StringComparison.OrdinalIgnoreCase) &&
                                existing.IsNearExact == item.IsNearExact &&
                                existing.RequiresAudiobookDurationCorroboration == item.RequiresAudiobookDurationCorroboration))
                        {
                            continue;
                        }

                        distinct.Add(item);
                    }

                    return distinct;
                }

                ScoredCandidate TryScoreCandidate(EditionFtsMatch result)
                {
                    if (allTags == null || allTags.Count == 0)
                    {
                        return null;
                    }

                    var includeDurationGatedNearExact = mediaType == BookMediaType.Audiobook && fileDurationSeconds.HasValue;
                    var unfilteredDirectEditionEvidence = GetCandidateTitleEvidence(result.EditionTitle, includeDurationGatedNearExact);
                    var directEditionEvidence = unfilteredDirectEditionEvidence;
                    var seriesPositionEvidence = GetSeriesPositionEvidence(result, allTags, booksById);
                    var invalidatedTitleEvidenceFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var titleEvidenceCounted = false;

                    bool IsTitleEvidenceFieldEligible(EditionTitleEvidence item)
                    {
                        if (item == null)
                        {
                            return false;
                        }

                        if (!titleEvidenceCounted)
                        {
                            titleEvidenceCandidateCount++;
                            titleEvidenceCounted = true;
                        }

                        if (strictness != BookMatchingStrictness.Strict)
                        {
                            return true;
                        }

                        var fieldPosition = seriesPositionEvidence.GetField(item.FieldName);
                        if (fieldPosition?.Disposition != SeriesPositionDisposition.Mismatch)
                        {
                            return true;
                        }

                        invalidatedTitleEvidenceFields.Add(fieldPosition.FieldName);
                        return false;
                    }

                    IReadOnlyList<EditionTitleEvidence> ApplyStrictSeriesPositionFieldPolicy(IReadOnlyList<EditionTitleEvidence> titleEvidence)
                    {
                        if (titleEvidence == null || titleEvidence.Count == 0)
                        {
                            return titleEvidence;
                        }

                        return titleEvidence.Where(IsTitleEvidenceFieldEligible).ToList();
                    }

                    directEditionEvidence = ApplyStrictSeriesPositionFieldPolicy(directEditionEvidence);
                    var evidence = directEditionEvidence;
                    var candidateBook = TryGetBookCached(result.BookId, booksById);
                    var bookTitleIsSeriesTitle = !string.IsNullOrWhiteSpace(candidateBook?.SeriesName) &&
                                                 string.Equals(result.BookTitle?.Trim(), candidateBook.SeriesName.Trim(), StringComparison.OrdinalIgnoreCase);
                    var hasCompleteNarratorProof = HasCompleteNarratorEvidence(result);
                    var hasEditionProofForBookTitle = mediaType == BookMediaType.Audiobook &&
                                                      IsAudiobookEdition(result) &&
                                                      !bookTitleIsSeriesTitle &&
                                                      (hasCompleteNarratorProof || TryGetDurationFallbackDetail(result, out _));
                    if ((evidence == null || evidence.Count == 0) &&
                        hasEditionProofForBookTitle &&
                        !string.IsNullOrWhiteSpace(result.BookTitle) &&
                        !string.Equals(result.BookTitle.Trim(), result.EditionTitle?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        evidence = ApplyStrictSeriesPositionFieldPolicy(GetCandidateTitleEvidence(
                            result.BookTitle,
                            includeDurationGatedNearExact));
                    }
                    if (evidence == null || evidence.Count == 0)
                    {
                        evidence = GetSeriesExplainableEditionTitleEvidence(
                            result,
                            allTags,
                            booksById,
                            IsTitleEvidenceFieldEligible);
                    }

                    if (evidence == null || evidence.Count == 0)
                    {
                        try
                        {
                            var stripped = StripTrailingParentheticals(result.EditionTitle);
                            var hasTrailingParenthetical = !string.IsNullOrWhiteSpace(stripped) &&
                                                           !string.Equals(stripped, result.EditionTitle, StringComparison.Ordinal);
                            if (hasTrailingParenthetical)
                            {
                                var baseEvidence = ApplyStrictSeriesPositionFieldPolicy(
                                    _containmentValidator.GetEditionTitleEvidence(stripped, allTags, includeDurationGatedNearExact));
                                if (baseEvidence != null && baseEvidence.Count > 0)
                                {
                                    var hasCleanSiblingEdition = ftsResults.Any(r =>
                                        r != null &&
                                        SameLogicalWork(r, result, booksById) &&
                                        r.EditionId != result.EditionId &&
                                        string.Equals(r.EditionTitle?.Trim(), stripped, StringComparison.OrdinalIgnoreCase));

                                    if (hasCleanSiblingEdition)
                                    {
                                        return null;
                                    }

                                    evidence = baseEvidence;
                                }
                            }

                            if (evidence == null || evidence.Count == 0)
                            {
                                var baseTitleEvidence = GetUnambiguousBaseTitleEvidence(
                                    result,
                                    unfilteredDirectEditionEvidence,
                                    includeDurationGatedNearExact,
                                    IsTitleEvidenceFieldEligible);
                                if (baseTitleEvidence.Count > 0)
                                {
                                    evidence = baseTitleEvidence;
                                }
                            }


                        }
                        catch
                        {
                            // best-effort only
                        }
                    }

                    if (evidence == null || evidence.Count == 0)
                    {
                        if (strictness == BookMatchingStrictness.Strict && invalidatedTitleEvidenceFields.Count > 0)
                        {
                            MarkContradictoryEvidence();
                            strictSeriesPositionRejectedCount++;
                            var invalidatedFields = string.Join(", ", invalidatedTitleEvidenceFields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                            var detail = $"fields=[{invalidatedFields}] detected=[{seriesPositionEvidence.DetectedPositions}] candidate=[{seriesPositionEvidence.CandidatePositions}]";
                            _logger.Debug(
                                "[HOLY-GRAIL][{0}] SERIES_POSITION_MISMATCH rejecting candidate after field validation: EditionId={1} Title='{2}' {3}",
                                phase,
                                result.EditionId,
                                Truncate(result.EditionTitle, 60),
                                detail);
                            RecordCandidateRejection(result, "SERIES_POSITION_MISMATCH", detail, "contradictory");
                            return null;
                        }

                        if (CanRecord())
                        {
                            try
                            {
                                var detail = BuildContainmentMissingTokenDetail(result.EditionTitle);
                                RecordCandidateRejection(
                                    result,
                                    "CONTAINMENT_FAILED",
                                    detail,
                                    "insufficient");
                            }
                            catch
                            {
                                RecordCandidateRejection(
                                    result,
                                    "CONTAINMENT_FAILED",
                                    fallbackDisposition: "insufficient");
                            }
                        }

                        return null;
                    }

                    if (invalidatedTitleEvidenceFields.Count > 0)
                    {
                        _logger.Debug(
                            "[HOLY-GRAIL][{0}] Candidate EditionId={1} ignored series-position-conflicting title field(s) [{2}] and retained clean title evidence field(s) [{3}]",
                            phase,
                            result.EditionId,
                            string.Join(", ", invalidatedTitleEvidenceFields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)),
                            string.Join(", ", evidence.Select(e => e.FieldName).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase)));
                    }

                    // A book title that happens to equal its series name remains valid title proof.
                    // The narrow exception is when every title-proof field also carries a position
                    // that points away from the generic candidate; one clean field preserves it.
                    if (HasSeriesTitlePositionContradiction(
                            result,
                            booksById,
                            evidence,
                            seriesPositionEvidence))
                    {
                        _logger.Debug("[HOLY-GRAIL][{0}] Rejecting generic series-title candidate because no clean title field remains after position evidence: EditionId={1} Title='{2}'",
                            phase, result.EditionId,
                            result.EditionTitle?.Length > 60 ? result.EditionTitle.Substring(0, 60) + "..." : result.EditionTitle);
                        RecordCandidateRejection(result, "SERIES_ONLY");
                        return null;
                    }

                    if (mediaType == BookMediaType.Audiobook &&
                        evidence.Any(e => e.RequiresAudiobookDurationCorroboration) &&
                        !TryGetDurationFallbackDetail(result, out var durationGateDetail))
                    {
                        RecordCandidateRejection(result, "NEAR_EXACT_DURATION_GATE", durationGateDetail);
                        return null;
                    }

                        if (!stagedFtsUsed && !authorId.HasValue && !string.IsNullOrWhiteSpace(result.AuthorName))
                        {
                        try
                        {
                            if (!CandidateAuthorInTags(result.AuthorName))
                            {
                                authorRejectedNames.Add(result.AuthorName);

                                RecordCandidateRejection(
                                    result,
                                    "AUTHOR_NOT_IN_TAGS",
                                    $"author='{result.AuthorName}'",
                                    "insufficient");
                                return null;
                            }
                        }
                        catch
                        {
                            RecordCandidateRejection(result, "AUTHOR_NOT_IN_TAGS", fallbackDisposition: "insufficient");
                            return null;
                            }
                        }

                        var audiobookNarratorMatches = 0;
                        var audiobookDurationMatches = false;
                        var audiobookRepresentativeTitleMatch = false;
                        var audiobookUndistinguishedEditionFallback = false;

                        if (mediaType == BookMediaType.Audiobook)
                        {
                            if (HasInsufficientSelfNarratorSignal(result))
                            {
                                RecordCandidateRejection(result, "SELF_NARRATOR_SINGLE_FIELD");
                                return null;
                            }

                            audiobookNarratorMatches = GetNarratorMatchCountCached(result);
                            audiobookDurationMatches = TryGetDurationFallbackDetail(result, out _);
                            audiobookRepresentativeTitleMatch = IsRepresentativeEdition(result) &&
                                                                  HasStrongDirectEditionTitleEvidence(directEditionEvidence);

                            if (IsRepresentativeEdition(result))
                            {
                                if (!audiobookRepresentativeTitleMatch)
                                {
                                    RecordCandidateRejection(result, "REPRESENTATIVE_TITLE_NOT_EVIDENCED");
                                    return null;
                                }
                            }
                            else if (result.ReadingFormatId.HasValue && !IsAudiobookEdition(result))
                            {
                                RecordCandidateRejection(result, "UNSUPPORTED_AUDIO_READING_FORMAT", $"readingFormatId={result.ReadingFormatId.Value.ToString(CultureInfo.InvariantCulture)}");
                                return null;
                            }
                            else if (audiobookNarratorMatches == 0 && HasConflictingNarratorEvidence(result))
                            {
                                MarkContradictoryEvidence();
                                RecordCandidateRejection(
                                    result,
                                    "NARRATOR_CONFLICT",
                                    $"narrator='{Truncate(result.NarratorNames, 80)}'",
                                    "contradictory");
                                return null;
                            }
                            else if (audiobookNarratorMatches == 0 &&
                                     !CanMatchAudiobookWithoutNarratorEvidence(
                                         result,
                                         evidence,
                                         out var narratorFallbackDetail,
                                         out audiobookUndistinguishedEditionFallback))
                            {
                                RecordCandidateRejection(result, "NARRATOR_MISSING_DURATION_GATE", narratorFallbackDetail);
                                return null;
                            }
                        }

                    var (passesLeftover, leftoverField, leftovers) = PassesLeftoverTokenGate(
                        result,
                        evidence,
                        allTags,
                        booksById,
                        strictness: strictness,
                        seriesPositionEvidence: seriesPositionEvidence);
                    if (!passesLeftover)
                    {
                        MarkContradictoryEvidence();
                        leftoverRejectedCount++;
                        var safeField = string.IsNullOrWhiteSpace(leftoverField) ? "unknown" : leftoverField;
                        _logger.Debug("[HOLY-GRAIL][{0}] Leftover gate failed: EditionId={1} Title='{2}', Field='{3}', Leftovers=[{4}]",
                            phase, result.EditionId,
                            result.EditionTitle?.Length > 60 ? result.EditionTitle.Substring(0, 60) + "..." : result.EditionTitle,
                            safeField,
                            string.Join(", ", leftovers.Take(6)));
                        if (CanRecord())
                        {
                            try
                            {
                                var preview = leftovers != null ? string.Join(", ", leftovers.Take(6)) : string.Empty;
                                RecordCandidateRejection(result, "LEFTOVER_GATE", $"field='{safeField}' leftovers=[{preview}]", "contradictory");
                            }
                            catch
                            {
                                RecordCandidateRejection(result, "LEFTOVER_GATE", fallbackDisposition: "contradictory");
                            }
                        }

                        return null;
                    }


                    var scoredCandidate = ScoreCandidate(
                        result,
                        evidence,
                        allTags,
                        tagTokens,
                        fileYear,
                        fileDurationSeconds,
                        mediaType,
                        leftovers,
                        seriesPositionEvidence.HasMatchingSignal,
                        seriesPositionEvidence,
                        booksById,
                        audiobookNarratorMatches,
                        audiobookDurationMatches,
                        audiobookRepresentativeTitleMatch,
                        audiobookUndistinguishedEditionFallback);
                    scoredCandidate.HasDurationProof = audiobookDurationMatches;
                    scoredCandidate.HasDirectEditionTitleProof = HasStrongDirectEditionTitleEvidence(directEditionEvidence);
                    scoredCandidate.TitleOccurrenceExplanations = BuildTitleOccurrenceExplanations(
                        result,
                        evidence,
                        allTags,
                        mediaType,
                        booksById);
                    bool? hasStrictlyCleanTitleOccurrence = scoredCandidate.TitleOccurrenceExplanations.Count > 0
                        ? scoredCandidate.TitleOccurrenceExplanations.Any(explanation => explanation.NearbyUnexplainedTokens.Count == 0)
                        : null;
                    if (strictness == BookMatchingStrictness.Strict &&
                        hasStrictlyCleanTitleOccurrence == false)
                    {
                        MarkContradictoryEvidence();
                        leftoverRejectedCount++;
                        var strictFailure = scoredCandidate.TitleOccurrenceExplanations
                            .OrderBy(explanation => explanation.NearbyUnexplainedTokens.Count)
                            .First();
                        var strictLeftovers = string.Join(", ", strictFailure.NearbyUnexplainedTokens.Take(8));
                        _logger.Debug(
                            "[HOLY-GRAIL][{0}] Strict occurrence gate rejected EditionId={1} Title='{2}' Field='{3}' Leftovers=[{4}]",
                            phase,
                            result.EditionId,
                            Truncate(result.EditionTitle, 60),
                            strictFailure.FieldName,
                            strictLeftovers);
                        RecordCandidateRejection(
                            result,
                            "STRICT_TITLE_OCCURRENCE_LEFTOVER",
                            $"field='{strictFailure.FieldName}' leftovers=[{strictLeftovers}]",
                            "contradictory");
                        return null;
                    }

                    return scoredCandidate;
                }

                void EnableSeriesPositionTiebreaks(IReadOnlyList<ScoredCandidate> candidates)
                {
                    if (candidates == null || candidates.Count < 2)
                    {
                        return;
                    }

                    foreach (var candidate in candidates)
                    {
                        candidate.SeriesPositionMatch = false;
                        if (!candidate.HasMatchingPositionSignal ||
                            candidate.TitleEvidenceFields == null ||
                            candidate.TitleEvidenceFields.Count == 0)
                        {
                            continue;
                        }

                        candidate.SeriesPositionMatch = candidates.Any(other =>
                            other != null &&
                            other != candidate &&
                            other.TitleEvidenceFields != null &&
                            candidate.TitleEvidenceFields.Overlaps(other.TitleEvidenceFields) &&
                            !SameLogicalWork(candidate.Match, other.Match, booksById));
                    }
                }

                void RecordProductionRanking(
                    IReadOnlyList<ScoredCandidate> candidates,
                    string selectionScope,
                    string logicalWorkKey = null)
                {
                    var sink = _matchingTraceSink.Value;
                    if (sink == null || candidates == null || candidates.Count == 0)
                    {
                        return;
                    }

                    try
                    {
                        var editionRankingPriority = mediaType == BookMediaType.Ebook
                            ? "titleEvidenceTier,exactYear,closestYear,publisherMatches,author,seriesName,seriesPosition,readingFormat,fewerLeftovers,ftsScore"
                            : "titleEvidenceTier,audiobookProofTier,durationProof,directEditionTitleProof,representativeRank,narratorTier,narratorMatches,author,seriesName,seriesPosition,readingFormat,closestDuration,exactYear,closestYear,fewerLeftovers,ftsScore,providerIdentityTie";
                        var rankingPriority = string.Equals(selectionScope, "global-stage2-selected-provider-book", StringComparison.OrdinalIgnoreCase)
                            ? "stage2ProviderBookScore," + editionRankingPriority
                            : selectionScope?.StartsWith("global-", StringComparison.OrdinalIgnoreCase) == true
                                ? "sameOccurrenceBookTitleDominance," + editionRankingPriority
                                : editionRankingPriority;

                        for (var i = 0; i < candidates.Count; i++)
                        {
                            var scored = candidates[i];
                            var candidate = scored?.Match;
                            if (candidate == null)
                            {
                                continue;
                            }

                            var durationDiff = scored.DurationDiff == int.MaxValue
                                ? "unknown"
                                : scored.DurationDiff.ToString(CultureInfo.InvariantCulture);
                            var yearDiff = scored.YearDiff == int.MaxValue
                                ? "unknown"
                                : scored.YearDiff.ToString(CultureInfo.InvariantCulture);
                            var evidenceFields = scored.TitleEvidenceFields == null
                                ? string.Empty
                                : string.Join(",", scored.TitleEvidenceFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));
                            var leftovers = scored.Leftovers == null
                                ? string.Empty
                                : string.Join(",", scored.Leftovers.Take(20));
                            var titleProof = scored.TitleEvidenceTier > 0 ? "exact" : "near-exact";
                            var audiobookProof = scored.AudiobookProofTier switch
                            {
                                2 => "narrator",
                                1 => "duration",
                                _ => "none"
                            };
                            var narratorProof = scored.NarratorTier > 0
                                ? $"matched({scored.NarratorMatchCount.ToString(CultureInfo.InvariantCulture)})"
                                : "none";

                            sink.Record(new MatchingTraceEvent
                            {
                                EventType = "candidate_ranked",
                                Phase = rejectionPhase,
                                FilePath = filePath,
                                EditionId = candidate.EditionId,
                                BookId = candidate.BookId,
                                AuthorId = candidate.AuthorId,
                                Score = candidate.MatchScore,
                                Title = candidate.EditionTitle ?? candidate.BookTitle,
                                Rank = i + 1,
                                Reason = i == 0 ? "production-winner-at-this-scope" : "eligible-runner-up",
                                Detail = $"titleProof={titleProof} in {scored.TitleEvidenceCount} field(s) [{evidenceFields}]; audiobookProof={audiobookProof}; durationProof={scored.HasDurationProof}; directEditionTitleProof={scored.HasDirectEditionTitleProof}; narrator={narratorProof}; author={scored.AuthorMatch}; series={scored.SeriesNameMatch}; position={scored.SeriesPositionMatch}; nativeFormat={scored.ReadingFormatMatch}; durationDiff={durationDiff}; yearDiff={yearDiff}; publisherMatches={scored.PublisherMatchCount}; leftovers={scored.LeftoverCount}; fts={scored.Bm25Score.ToString("R", CultureInfo.InvariantCulture)}",
                                Data = new Dictionary<string, string>
                                {
                                    ["selectionScope"] = selectionScope ?? string.Empty,
                                    ["logicalWorkKey"] = logicalWorkKey ?? string.Empty,
                                    ["mediaType"] = mediaType.ToString(),
                                    ["rankingPriority"] = rankingPriority,
                                    ["foreignEditionId"] = candidate.ForeignEditionId ?? string.Empty,
                                    ["matchedVia"] = scored.MatchedVia ?? string.Empty,
                                    ["titleEvidenceTier"] = scored.TitleEvidenceTier.ToString(CultureInfo.InvariantCulture),
                                    ["titleEvidenceCount"] = scored.TitleEvidenceCount.ToString(CultureInfo.InvariantCulture),
                                    ["titleEvidenceFields"] = evidenceFields,
                                    ["audiobookProofTier"] = scored.AudiobookProofTier.ToString(CultureInfo.InvariantCulture),
                                    ["durationProof"] = scored.HasDurationProof.ToString(CultureInfo.InvariantCulture),
                                    ["directEditionTitleProof"] = scored.HasDirectEditionTitleProof.ToString(CultureInfo.InvariantCulture),
                                    ["representativeRank"] = scored.RepresentativeRank.ToString(CultureInfo.InvariantCulture),
                                    ["narratorTier"] = scored.NarratorTier.ToString(CultureInfo.InvariantCulture),
                                    ["narratorMatchCount"] = scored.NarratorMatchCount.ToString(CultureInfo.InvariantCulture),
                                    ["authorMatch"] = scored.AuthorMatch.ToString(CultureInfo.InvariantCulture),
                                    ["seriesNameMatch"] = scored.SeriesNameMatch.ToString(CultureInfo.InvariantCulture),
                                    ["seriesPositionMatch"] = scored.SeriesPositionMatch.ToString(CultureInfo.InvariantCulture),
                                    ["readingFormatMatch"] = scored.ReadingFormatMatch.ToString(CultureInfo.InvariantCulture),
                                    ["durationDiffSeconds"] = durationDiff,
                                    ["yearDiff"] = yearDiff,
                                    ["publisherMatchCount"] = scored.PublisherMatchCount.ToString(CultureInfo.InvariantCulture),
                                    ["leftoverCount"] = scored.LeftoverCount.ToString(CultureInfo.InvariantCulture),
                                    ["leftovers"] = leftovers,
                                    ["bm25Score"] = scored.Bm25Score.ToString("R", CultureInfo.InvariantCulture),
                                    ["undistinguishedAudiobookEditionFallback"] = scored.UndistinguishedAudiobookEditionFallback.ToString(CultureInfo.InvariantCulture)
                                }
                            });
                        }
                    }
                    catch
                    {
                        // Trace sinks are diagnostics only. Matching decisions must never depend on them.
                    }
                }

                EditionFtsMatch winner = null;
                ScoredCandidate selectedCandidate = null;
                string selectionReason = null;
                var scoredCandidates = new List<ScoredCandidate>();
                var primaryResults = stagedPreferredBookIds == null
                    ? ftsResults
                    : ftsResults.Where(result => result != null && stagedPreferredBookIds.Contains(result.BookId)).ToList();
                foreach (var result in primaryResults)
                {
                    var scored = TryScoreCandidate(result);
                    if (scored != null)
                    {
                        scoredCandidates.Add(scored);
                    }
                }

                var stagedBookSelectionSucceeded = stagedPreferredBookIds != null && scoredCandidates.Count > 0;
                if (stagedPreferredBookIds != null && scoredCandidates.Count == 0)
                {
                    _logger.Warn(
                        "[HOLY-GRAIL][{0}][STAGED-FTS-FALLBACK] Stage 2 selected provider Book '{1}', but none of its Editions passed eligibility; running deep analysis on the remaining author-gated Books.",
                        phase,
                        stagedPreferredWorkKey);
                    RecordTrace(
                        "fts_stage2_selected_book_rejected",
                        rejectionPhase,
                        reason: "STAGE2_SELECTED_BOOK_INELIGIBLE",
                        detail: "No Edition under the Stage 2 provider-Book winner passed normal eligibility; deep candidate analysis resumed.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["providerWorkKey"] = stagedPreferredWorkKey ?? string.Empty,
                            ["localBookIds"] = string.Join(",", stagedPreferredBookIds.OrderBy(id => id))
                        });
                    foreach (var result in ftsResults.Where(result => result != null && !stagedPreferredBookIds.Contains(result.BookId)))
                    {
                        var scored = TryScoreCandidate(result);
                        if (scored != null)
                        {
                            scoredCandidates.Add(scored);
                        }
                    }
                }

                if (scoredCandidates.Count > 0)
                {
                    if (stagedBookSelectionSucceeded)
                    {
                        EnableSeriesPositionTiebreaks(scoredCandidates);
                        scoredCandidates.Sort((left, right) => CompareScoredCandidates(right, left, mediaType));
                        RecordProductionRanking(
                            scoredCandidates,
                            "global-stage2-selected-provider-book",
                            stagedPreferredWorkKey);
                        selectedCandidate = scoredCandidates.FirstOrDefault();
                        if (selectedCandidate != null)
                        {
                            selectedCandidate.MatchedVia = "staged_field_representation";
                        }
                        winner = selectedCandidate?.Match;
                        selectionReason = selectedCandidate == null
                            ? null
                            : selectedCandidate.LeftoverCount == 0
                                ? "staged-fts-book-edition-selection"
                                : "staged-fts-book-edition-selection-leftover";
                    }
                    else
                    {
                    // Book identity is resolved before Edition identity. Each provider Book contributes its
                    // best eligible Edition, while a Book whose best same-occurrence title explanation is a
                    // strict subset of another Book's explanation cannot win on later Edition-only signals.
                    var occurrenceSelection = BuildOccurrenceSelection(
                        scoredCandidates,
                        ftsResults,
                        allTags,
                        mediaType,
                        booksById);

                    foreach (var work in occurrenceSelection.Works)
                    {
                        var withinWork = work.Candidates.ToList();
                        withinWork.Sort((left, right) => CompareScoredCandidates(right, left, mediaType));
                        RecordProductionRanking(withinWork, "within-logical-work", work.LogicalWorkKey);

                        if (!work.IsDominated || work.EditionWinner?.Match == null)
                        {
                            continue;
                        }

                        var incomingEdges = occurrenceSelection.DominanceEdges
                            .Where(edge => edge.EndsWith(">" + work.LogicalWorkKey, StringComparison.OrdinalIgnoreCase));
                        RecordCandidateRejection(
                            work.EditionWinner.Match,
                            "BOOK_TITLE_OCCURRENCE_DOMINATED",
                            $"same-occurrence supersets=[{string.Join(" | ", incomingEdges.Take(8))}]",
                            "contradictory");
                    }

                    // Keep the established Edition selector intact. Duration/year-shaped inputs select one
                    // Edition per Book before the global comparison; other inputs retain the existing global
                    // Edition comparison. The occurrence ledger only removes a Book when another Book proves
                    // a strict same-value token-position superset.
                    var globalCandidates = useGroupedSelection
                        ? occurrenceSelection.NonDominatedWorkWinners.ToList()
                        : occurrenceSelection.Works
                            .Where(work => !work.IsDominated)
                            .SelectMany(work => work.Candidates)
                            .ToList();
                    EnableSeriesPositionTiebreaks(globalCandidates);
                    if (HasNearExactTitleAmbiguity(phase, globalCandidates, booksById))
                    {
                        // Near-exact ambiguity is shared weak evidence, not positive competing proof.
                        // It stays InsufficientEvidence so folder/filename evidence may disambiguate.
                        return null;
                    }

                    globalCandidates.Sort((left, right) => CompareScoredCandidates(right, left, mediaType));
                    RecordProductionRanking(
                        globalCandidates,
                        useGroupedSelection ? "global-logical-work-winners" : "global-eligible-candidates");
                    selectedCandidate = globalCandidates.FirstOrDefault();
                    winner = selectedCandidate?.Match;
                    selectionReason = selectedCandidate == null
                        ? null
                        : selectedCandidate.LeftoverCount == 0
                            ? "book-occurrence-edition-selection"
                            : "book-occurrence-edition-selection-leftover";
                    }
                }

                if (winner == null)
                {
                    var hasCompetingAuthorEvidence = authorRejectedNames.Count > 0 &&
                        ftsResults
                            .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.AuthorName))
                            .Select(candidate => candidate.AuthorName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Any(authorName =>
                                !authorRejectedNames.Contains(authorName) &&
                                CandidateAuthorInTags(authorName));
                    if (hasCompetingAuthorEvidence)
                    {
                        MarkContradictoryEvidence();
                        foreach (var rejection in rejections ?? new List<CandidateRejection>())
                        {
                            if (string.Equals(rejection?.Reason, "AUTHOR_NOT_IN_TAGS", StringComparison.OrdinalIgnoreCase))
                            {
                                rejection.FallbackDisposition = "contradictory";
                            }
                        }
                    }

                    _logger.Debug(
                        "[HOLY-GRAIL][{0}] No eligible candidate. Title-evidenced={1}, strict series-position field rejections={2}, leftover rejections={3}.",
                        phase,
                        titleEvidenceCandidateCount,
                        strictSeriesPositionRejectedCount,
                        leftoverRejectedCount);
                    return null;
                }

                _logger.Debug("[HOLY-GRAIL][{0}] MATCH ACCEPTED ({1}): EditionId={2}, Title='{3}'",
                    phase,
                    selectionReason,
                    winner.EditionId,
                    winner.EditionTitle?.Length > 50 ? winner.EditionTitle.Substring(0, 50) + "..." : winner.EditionTitle);
                RecordTrace("match_selected", rejectionPhase, winner, selectionReason, filePath: filePath);
                var identityProof = BuildSelectedMatchIdentityProof(
                    selectedCandidate,
                    allTags,
                    phase,
                    groupMemberTags);
                return new FtsSmokeTestResult
                {
                    Match = winner,
                    MatchedVia = selectedCandidate?.MatchedVia,
                    Provenance = BuildSelectedMatchProvenance(
                        selectedCandidate,
                        identityProof,
                        allTags,
                        mediaType,
                        fileDurationSeconds,
                        fileYear,
                        strictness,
                        phase,
                        booksById),
                    IdentityProof = identityProof,
                    BooksById = booksById
                };
            }

            /// <summary>
            /// Internal class to hold a scored candidate with all signals for unified sorting.
            /// </summary>
            private sealed class ScoredCandidate
            {
                public EditionFtsMatch Match { get; set; }
                public int LeftoverCount { get; set; }
                public IReadOnlyList<string> Leftovers { get; set; } = Array.Empty<string>();
                public int NarratorTier { get; set; }
                public int NarratorMatchCount { get; set; }
                public int TitleEvidenceTier { get; set; }
                public int TitleEvidenceCount { get; set; }
                public IReadOnlyList<EditionTitleEvidence> TitleEvidence { get; set; } = Array.Empty<EditionTitleEvidence>();
                public HashSet<string> TitleEvidenceFields { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public SeriesPositionEvidence PositionEvidence { get; set; }
                public bool AuthorMatch { get; set; }
                public int PublisherMatchCount { get; set; }
                public bool SeriesNameMatch { get; set; }
                public bool HasMatchingPositionSignal { get; set; }
                public bool SeriesPositionMatch { get; set; }
                public bool ReadingFormatMatch { get; set; }
                public int AudiobookProofTier { get; set; }
                public bool HasDurationProof { get; set; }
                public bool HasDirectEditionTitleProof { get; set; }
                public int RepresentativeRank { get; set; }
                public bool UndistinguishedAudiobookEditionFallback { get; set; }
                public string MatchedVia { get; set; }
                public int DurationDiff { get; set; } = int.MaxValue;
                public int YearDiff { get; set; } = int.MaxValue;
                public double Bm25Score { get; set; }
                public IReadOnlyList<TitleOccurrenceExplanation> TitleOccurrenceExplanations { get; set; } = Array.Empty<TitleOccurrenceExplanation>();
            }

            /// <summary>
            /// Candidate-relative account of the exact token positions consumed from one observed value.
            /// OccurrenceKey intentionally omits the tag label: raw/canonical aliases carrying the same
            /// normalized value are one conservative occurrence, not independent votes.
            /// </summary>
            private sealed class TitleOccurrenceExplanation
            {
                public string OccurrenceKey { get; set; }
                public string FieldName { get; set; }
                public bool IsNearExact { get; set; }
                public HashSet<int> MeaningfulConsumedIndexes { get; set; } = new HashSet<int>();
                public IReadOnlyList<string> NearbyUnexplainedTokens { get; set; } = Array.Empty<string>();
            }

            private sealed class OccurrenceWorkSelection
            {
                public string LogicalWorkKey { get; set; }
                public bool HasProviderBookIdentity { get; set; }
                public List<ScoredCandidate> Candidates { get; set; } = new List<ScoredCandidate>();
                public List<TitleOccurrenceExplanation> Explanations { get; set; } = new List<TitleOccurrenceExplanation>();
                public ScoredCandidate EditionWinner { get; set; }
                public bool IsDominated { get; set; }
            }

            private sealed class OccurrenceSelectionResult
            {
                public List<string> DominanceEdges { get; set; } = new List<string>();
                public List<OccurrenceWorkSelection> Works { get; set; } = new List<OccurrenceWorkSelection>();
                public List<ScoredCandidate> NonDominatedWorkWinners { get; set; } = new List<ScoredCandidate>();
            }

            /// <summary>
            /// Compute all scoring signals for a candidate (matching Python's score_candidate + leftover + pos).
            /// </summary>
            private ScoredCandidate ScoreCandidate(
                EditionFtsMatch candidate,
                IReadOnlyList<EditionTitleEvidence> evidence,
                Dictionary<string, List<string>> allTags,
                HashSet<string> tagTokens,
                int? fileYear,
                int? fileDurationSeconds,
                BookMediaType mediaType,
                IReadOnlyList<string> leftovers,
                bool hasMatchingPositionSignal,
                SeriesPositionEvidence seriesPositionEvidence,
                IDictionary<int, Book> booksById = null,
                int audiobookNarratorMatches = 0,
                bool audiobookDurationMatches = false,
                bool audiobookRepresentativeTitleMatch = false,
                bool audiobookUndistinguishedEditionFallback = false)
            {
                var titleEvidenceFields = evidence?
                    .Select(e => e?.FieldName)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var scored = new ScoredCandidate
                {
                    Match = candidate,
                    LeftoverCount = leftovers?.Count ?? 0,
                    Leftovers = leftovers ?? Array.Empty<string>(),
                    TitleEvidenceTier = evidence != null && evidence.Any(e => !e.IsNearExact) ? 1 : 0,
                    TitleEvidenceCount = titleEvidenceFields.Count,
                    TitleEvidence = evidence ?? Array.Empty<EditionTitleEvidence>(),
                    TitleEvidenceFields = titleEvidenceFields,
                    PositionEvidence = seriesPositionEvidence,
                    HasMatchingPositionSignal = hasMatchingPositionSignal,
                    SeriesPositionMatch = false,
                    Bm25Score = candidate.MatchScore
                };

                // ReadingFormat match: audiobooks prefer RF=2, ebooks prefer RF=3
                if (candidate.ReadingFormatId.HasValue)
                {
                    var expectedRf = mediaType == BookMediaType.Ebook ? 3 : 2;
                    scored.ReadingFormatMatch = candidate.ReadingFormatId.Value == expectedRf;
                }

                if (mediaType == BookMediaType.Audiobook)
                {
                    scored.AudiobookProofTier = audiobookNarratorMatches > 0
                        ? 2
                        : audiobookDurationMatches && candidate.ReadingFormatId == 2
                            ? 1
                            : 0;

                    if (audiobookRepresentativeTitleMatch)
                    {
                        scored.RepresentativeRank = candidate.ReadingFormatId == 3
                            ? 2
                            : candidate.ReadingFormatId == 1
                                ? 1
                                : 0;
                        scored.MatchedVia = "escape_hatch";
                    }
                    else if (audiobookUndistinguishedEditionFallback)
                    {
                        scored.UndistinguishedAudiobookEditionFallback = true;
                        scored.MatchedVia = "undistinguished_audiobook_edition";
                    }
                }

                    // Narrator signal (audiobooks):
                    // Boost only when the candidate's narrator (not the author) is contained in a single tag field.
                    // Do not scrape comment/description fields.
                    if (mediaType == BookMediaType.Audiobook &&
                        !string.IsNullOrWhiteSpace(candidate.NarratorNames) &&
                        allTags != null &&
                        allTags.Count > 0)
                    {
                        var narratorMatches = CountNarratorMatchesInTags(candidate.NarratorNames, candidate.AuthorName, allTags);
                        if (narratorMatches > 0)
                        {
                            scored.NarratorTier = 2;
                            scored.NarratorMatchCount = narratorMatches;
                        }
                    }

                // Duration signal
                if (fileDurationSeconds.HasValue &&
                    candidate.DurationSeconds.HasValue &&
                    candidate.DurationSeconds.Value > 0)
                {
                    scored.DurationDiff = Math.Abs(candidate.DurationSeconds.Value - fileDurationSeconds.Value);
                }

                // Year signal
                if (fileYear.HasValue && candidate.ReleaseDate.HasValue)
                {
                    scored.YearDiff = Math.Abs(candidate.ReleaseDate.Value.Year - fileYear.Value);
                }

                // Publisher signal (ebooks only)
                if (mediaType == BookMediaType.Ebook && tagTokens.Count > 0 && !string.IsNullOrWhiteSpace(candidate.Publisher))
                {
                    scored.PublisherMatchCount = PublisherTokenMatchCount(candidate.Publisher, tagTokens);
                }


                    // Series name match — majority of tokens must appear (Python parity)
                    var book = TryGetBookCached(candidate.BookId, booksById);
                    var seriesName = book?.SeriesName;
                    if (!string.IsNullOrWhiteSpace(seriesName) && tagTokens.Count > 0)
                    {
                    var seriesTokens = TokenizeForLeftoverGate(seriesName)
                        .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (seriesTokens.Count > 0)
                    {
                        var matched = seriesTokens.Count(t => tagTokens.Contains(t));
                        scored.SeriesNameMatch = matched >= Math.Max(1.0, seriesTokens.Count * 0.5);
                    }
                }

                // Author match — majority of tokens must appear (Python parity: * 0.5 not integer / 2)
                if (!string.IsNullOrWhiteSpace(candidate.AuthorName) && tagTokens.Count > 0)
                {
                    var authorTokens = TokenizeForLeftoverGate(candidate.AuthorName)
                        .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (authorTokens.Count > 0)
                    {
                        var matched = authorTokens.Count(t => tagTokens.Contains(t));
                        scored.AuthorMatch = matched >= Math.Max(1.0, authorTokens.Count * 0.5);
                    }
                }

                    return scored;
                }

            private IReadOnlyList<TitleOccurrenceExplanation> BuildTitleOccurrenceExplanations(
                EditionFtsMatch candidate,
                IReadOnlyList<EditionTitleEvidence> evidence,
                IDictionary<string, List<string>> allTags,
                BookMediaType mediaType,
                IDictionary<int, Book> booksById,
                bool includeDirectEditionTitleAlignment = false)
            {
                if (candidate == null ||
                    allTags == null ||
                    allTags.Count == 0 ||
                    ((evidence == null || evidence.Count == 0) && !includeDirectEditionTitleAlignment))
                {
                    return Array.Empty<TitleOccurrenceExplanation>();
                }

                var explainableTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var explainablePositionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void AddExplainable(string value)
                {
                    foreach (var token in TokenizeForLeftoverGateSequence(value))
                    {
                        explainableTokens.Add(token);
                    }
                }

                void AddPositionExplainable(string value)
                {
                    AddExplainable(value);
                    foreach (var token in SeriesPositionTokenHelper.GetPositionTokens(value))
                    {
                        explainableTokens.Add(token);
                        explainablePositionTokens.Add(token);
                    }
                }

                AddExplainable(candidate.EditionTitle);
                AddExplainable(candidate.BookTitle);
                AddExplainable(candidate.EditionSubTitle);
                AddExplainable(candidate.AuthorName);
                AddExplainable(candidate.NarratorNames);
                AddExplainable(candidate.Publisher);
                if (candidate.ReleaseDate.HasValue)
                {
                    AddExplainable(candidate.ReleaseDate.Value.Year.ToString(CultureInfo.InvariantCulture));
                }

                var candidateBook = TryGetBookCached(candidate.BookId, booksById);
                var candidateSeriesNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(candidateBook?.SeriesName))
                {
                    candidateSeriesNames.Add(candidateBook.SeriesName);
                }

                AddExplainable(candidateBook?.SeriesName);
                AddPositionExplainable(candidateBook?.SeriesPosition);
                foreach (var link in candidateBook?.SeriesLinks ?? Enumerable.Empty<SeriesBookLink>())
                {
                    if (!string.IsNullOrWhiteSpace(link?.Series?.Value?.Title))
                    {
                        candidateSeriesNames.Add(link.Series.Value.Title);
                    }

                    AddExplainable(link?.Series?.Value?.Title);
                    AddPositionExplainable(link?.Position);
                    if (link?.SeriesPosition > 0)
                    {
                        AddPositionExplainable(link.SeriesPosition.ToString(CultureInfo.InvariantCulture));
                    }
                }

                if (mediaType == BookMediaType.Audiobook)
                {
                    AddExplainable("audio audiobook");
                }
                else if (mediaType == BookMediaType.Ebook)
                {
                    AddExplainable("ebook e-book");
                }

                bool IsBackedByObservedValue(EditionTitleEvidence item)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.FieldName) || string.IsNullOrWhiteSpace(item.FieldValue))
                    {
                        return false;
                    }

                    return allTags.TryGetValue(item.FieldName, out var values) &&
                           values != null &&
                           values.Any(value => string.Equals(value?.Trim(), item.FieldValue.Trim(), StringComparison.Ordinal));
                }

                bool IsGlueToken(string token)
                {
                    return token is "a" or "an" or "and" or "or" or "the" or "of" or "in" or "on" or "at" or "by" or "for" or "with" or "to" or "from" or "as";
                }

                bool IsPhysicalPackagingMarker(string token)
                {
                    return token is "part" or "pt" or "chapter" or "chapters" or "chap" or "ch" or "disc" or "cd" or "track" or "trk";
                }

                bool HasNearbyPhysicalPackagingMarker(IReadOnlyList<string> valueTokens, int index)
                {
                    var start = Math.Max(0, index - 2);
                    var end = Math.Min(valueTokens.Count - 1, index + 2);
                    for (var i = start; i <= end; i++)
                    {
                        if (i != index && IsPhysicalPackagingMarker(valueTokens[i]))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsComparativeTitleToken(IReadOnlyList<string> valueTokens, int index)
                {
                    var token = valueTokens[index];
                    if (string.IsNullOrWhiteSpace(token) || IsGlueToken(token))
                    {
                        return false;
                    }

                    // These describe the file/container or introduce another metadata role. They are
                    // not Book-title discriminators merely because a catalog row happens to contain them.
                    if (token is "audio" or "audiobook" or "ebook" or "narrated" or "narrator" or "narration" or "read" or "performed" or "novel" ||
                        IsPhysicalPackagingMarker(token))
                    {
                        return false;
                    }

                    if ((token.All(char.IsDigit) || SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(token)) &&
                        HasNearbyPhysicalPackagingMarker(valueTokens, index))
                    {
                        return false;
                    }

                    return token.Length > 1 || token.All(char.IsDigit) || SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(token);
                }

                var candidateSeriesTokenSequences = candidateSeriesNames
                    .Select(TokenizeForLeftoverGateSequence)
                    .Where(tokens => tokens.Count > 0)
                    .ToList();

                bool IsBookIdentityComparativeToken(IReadOnlyList<string> titleTokens, int index)
                {
                    if (!IsComparativeTitleToken(titleTokens, index))
                    {
                        return false;
                    }

                    var token = titleTokens[index];
                    var isPositionToken = token.All(char.IsDigit) ||
                                          SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(token) ||
                                          explainablePositionTokens.Contains(token);
                    var neighbors = new[] { index - 1, index + 1 };
                    if (SeriesPositionDecorationTokens.Contains(token) &&
                        neighbors.Any(neighbor =>
                            neighbor >= 0 &&
                            neighbor < titleTokens.Count &&
                            (titleTokens[neighbor].All(char.IsDigit) ||
                             SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(titleTokens[neighbor]) ||
                             explainablePositionTokens.Contains(titleTokens[neighbor]))))
                    {
                        return false;
                    }

                    if (isPositionToken &&
                        neighbors.Any(neighbor =>
                            neighbor >= 0 &&
                            neighbor < titleTokens.Count &&
                            SeriesPositionDecorationTokens.Contains(titleTokens[neighbor])))
                    {
                        return false;
                    }

                    if (!isPositionToken)
                    {
                        return true;
                    }

                    // A position bound to the candidate-owned series is corroboration, not Book-title
                    // identity. It must not let a generic series container resist a more specific Book.
                    return !candidateSeriesTokenSequences.Any(seriesTokens =>
                        index >= seriesTokens.Count &&
                        titleTokens
                            .Skip(index - seriesTokens.Count)
                            .Take(seriesTokens.Count)
                            .SequenceEqual(seriesTokens, StringComparer.OrdinalIgnoreCase));
                }

                bool IsStrictlyIgnorableResidual(IReadOnlyList<string> valueTokens, int index)
                {
                    var token = valueTokens[index];
                    if (string.IsNullOrWhiteSpace(token) || IsGlueToken(token))
                    {
                        return true;
                    }

                    if (token is "audio" or "audiobook" or "ebook" or "narrated" or "narrator" or "narration" or "read" or "performed" or "novel" ||
                        IsPhysicalPackagingMarker(token))
                    {
                        return true;
                    }

                    if (token is "book" or "vol" or "volume" or "edition")
                    {
                        var neighbors = new[] { index - 1, index + 1 };
                        if (neighbors.Any(neighbor =>
                                neighbor >= 0 &&
                                neighbor < valueTokens.Count &&
                                explainablePositionTokens.Contains(valueTokens[neighbor])))
                        {
                            return true;
                        }
                    }

                    return (token.All(char.IsDigit) || SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(token)) &&
                           HasNearbyPhysicalPackagingMarker(valueTokens, index);
                }

                HashSet<int> FindConsumedIndexes(EditionTitleEvidence item, IReadOnlyList<string> fieldTokens, IReadOnlyList<string> titleTokens)
                {
                    if (fieldTokens.Count == 0 || titleTokens.Count == 0)
                    {
                        return null;
                    }

                    if (item.IsNearExact &&
                        TitleTokenAlignment.TryAlignOrdered(
                            titleTokens,
                            fieldTokens,
                            allowNearExact: true,
                            allowTransposition: item.RequiresAudiobookDurationCorroboration,
                            out var orderedAlignment))
                    {
                        return orderedAlignment.ConsumedFieldIndexes.ToHashSet();
                    }

                    var anchor = FindMinimumCoveringWindow(fieldTokens, titleTokens);
                    if (!anchor.HasValue)
                    {
                        return null;
                    }

                    var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var token in titleTokens.Where(token => !string.IsNullOrWhiteSpace(token)))
                    {
                        remaining[token] = remaining.TryGetValue(token, out var count) ? count + 1 : 1;
                    }

                    var consumed = new HashSet<int>();
                    for (var i = anchor.Value.Start; i <= anchor.Value.End && i < fieldTokens.Count; i++)
                    {
                        var token = fieldTokens[i];
                        if (remaining.TryGetValue(token, out var count) && count > 0)
                        {
                            remaining[token] = count - 1;
                            consumed.Add(i);
                        }
                    }

                    return remaining.Values.Any(count => count > 0) ? null : consumed;
                }

                var explanations = new List<TitleOccurrenceExplanation>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                void AddExplanation(
                    string fieldName,
                    IReadOnlyList<string> fieldTokens,
                    HashSet<int> consumed,
                    bool isNearExact)
                {
                    if (fieldTokens == null || fieldTokens.Count == 0 || consumed == null || consumed.Count == 0)
                    {
                        return;
                    }

                    var meaningfulConsumed = consumed
                        .Where(index => index >= 0 && index < fieldTokens.Count && IsBookIdentityComparativeToken(fieldTokens, index))
                        .ToHashSet();
                    if (meaningfulConsumed.Count == 0)
                    {
                        return;
                    }

                    // Normalized value identity deliberately collapses duplicate raw/canonical aliases. If two
                    // genuinely independent fields carry identical text, treating them as one is conservative.
                    var occurrenceKey = string.Join("\u001f", fieldTokens);
                    var dedupeKey = occurrenceKey + "\u001e" + string.Join(",", meaningfulConsumed.OrderBy(index => index)) + "\u001e" + isNearExact;
                    if (!seen.Add(dedupeKey))
                    {
                        return;
                    }

                    var anchorStart = consumed.Min();
                    var anchorEnd = consumed.Max();
                    var nearbyUnexplained = new List<string>();
                    var scanStart = Math.Max(0, anchorStart - 3);
                    var scanEnd = Math.Min(fieldTokens.Count - 1, anchorEnd + 3);
                    for (var i = scanStart; i <= scanEnd; i++)
                    {
                        if (consumed.Contains(i) || IsStrictlyIgnorableResidual(fieldTokens, i))
                        {
                            continue;
                        }

                        var token = fieldTokens[i];
                        if (explainableTokens.Contains(token))
                        {
                            continue;
                        }

                        if (token.All(char.IsDigit))
                        {
                            var normalized = token.TrimStart('0');
                            if (explainableTokens.Contains(string.IsNullOrEmpty(normalized) ? "0" : normalized))
                            {
                                continue;
                            }
                        }

                        if (token.Length > 1 || token.All(char.IsDigit) || SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(token))
                        {
                            nearbyUnexplained.Add(token);
                        }
                    }

                    explanations.Add(new TitleOccurrenceExplanation
                    {
                        OccurrenceKey = occurrenceKey,
                        FieldName = fieldName,
                        IsNearExact = isNearExact,
                        MeaningfulConsumedIndexes = meaningfulConsumed,
                        NearbyUnexplainedTokens = nearbyUnexplained.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    });
                }

                foreach (var item in evidence ?? Array.Empty<EditionTitleEvidence>())
                {
                    // Multi-value evidence currently carries a synthetic joined string rather than physical
                    // value lineage. Do not invent an occurrence for it; the ordinary matcher still uses it.
                    if (!IsBackedByObservedValue(item))
                    {
                        continue;
                    }

                    var fieldTokens = TokenizeForLeftoverGateSequence(item.FieldValue);
                    var titleTokens = TokenizeForLeftoverGateSequence(item.MatchedTitle);
                    var consumed = FindConsumedIndexes(item, fieldTokens, titleTokens);
                    AddExplanation(item.FieldName, fieldTokens, consumed, item.IsNearExact);
                }

                if (includeDirectEditionTitleAlignment)
                {
                    var editionTitleTokens = TokenizeForLeftoverGateSequence(candidate.EditionTitle);
                    var comparativeEditionTitleTokens = editionTitleTokens
                        .Where((_, index) => IsBookIdentityComparativeToken(editionTitleTokens, index))
                        .ToList();
                    if (comparativeEditionTitleTokens.Count > 0)
                    {
                        foreach (var field in allTags)
                        {
                            if (IsExcludedFromMatching(field.Key) || field.Value == null)
                            {
                                continue;
                            }

                            foreach (var rawValue in field.Value.Where(value => !string.IsNullOrWhiteSpace(value)))
                            {
                                var fieldTokens = TokenizeForLeftoverGateSequence(rawValue);
                                if (!TitleTokenAlignment.TryAlignOrdered(
                                        comparativeEditionTitleTokens,
                                        fieldTokens,
                                        allowNearExact: false,
                                        allowTransposition: false,
                                        out var alignment))
                                {
                                    continue;
                                }

                                AddExplanation(
                                    field.Key,
                                    fieldTokens,
                                    alignment.ConsumedFieldIndexes.ToHashSet(),
                                    isNearExact: false);
                            }
                        }
                    }
                }

                return explanations;
            }

            private static bool StrictlyExplainsMoreOfSameOccurrence(
                TitleOccurrenceExplanation left,
                TitleOccurrenceExplanation right)
            {
                if (left == null || right == null ||
                    string.IsNullOrWhiteSpace(left.OccurrenceKey) ||
                    !string.Equals(left.OccurrenceKey, right.OccurrenceKey, StringComparison.Ordinal))
                {
                    return false;
                }

                // A fuzzy explanation cannot displace an exact explanation merely by consuming more positions.
                if (left.IsNearExact && !right.IsNearExact)
                {
                    return false;
                }

                return left.MeaningfulConsumedIndexes.IsProperSupersetOf(right.MeaningfulConsumedIndexes);
            }

            private OccurrenceSelectionResult BuildOccurrenceSelection(
                IReadOnlyList<ScoredCandidate> scoredCandidates,
                IReadOnlyList<EditionFtsMatch> recalledCandidates,
                IDictionary<string, List<string>> allTags,
                BookMediaType mediaType,
                IDictionary<int, Book> booksById)
            {
                var result = new OccurrenceSelectionResult();
                if (scoredCandidates == null || scoredCandidates.Count == 0)
                {
                    return result;
                }

                var eligibleCandidates = scoredCandidates.Where(candidate => candidate?.Match != null).ToList();
                var works = eligibleCandidates
                    .GroupBy(
                        candidate => GetProviderOccurrenceWorkKey(candidate.Match, booksById) ?? $"local-book-row:{candidate.Match.BookId.ToString(CultureInfo.InvariantCulture)}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var candidates = group.ToList();
                        var ordered = candidates.ToList();
                        ordered.Sort((left, right) => CompareScoredCandidates(right, left, mediaType));
                        var explanations = candidates
                            .SelectMany(candidate => candidate.TitleOccurrenceExplanations ?? Array.Empty<TitleOccurrenceExplanation>())
                            .ToList();
                        return new OccurrenceWorkSelection
                        {
                            LogicalWorkKey = group.Key,
                            HasProviderBookIdentity = GetProviderOccurrenceWorkKey(candidates[0].Match, booksById) != null,
                            Candidates = candidates,
                            // A weaker sibling Edition must not make its Book lose when another sibling under
                            // that same provider Book explains the occurrence just as fully as the rival does.
                            Explanations = explanations,
                            EditionWinner = ordered[0]
                        };
                    })
                    .ToList();

                result.Works = works;

                void KeepOnlyMaximalWorkExplanations()
                {
                    foreach (var work in works)
                    {
                        var explanations = work.Explanations.ToList();
                        work.Explanations = explanations
                            .Where(explanation => !explanations.Any(other =>
                                !ReferenceEquals(other, explanation) &&
                                StrictlyExplainsMoreOfSameOccurrence(other, explanation)))
                            .ToList();
                    }
                }

                void ApplyProviderBookDominance()
                {
                    result.DominanceEdges.Clear();
                    foreach (var work in works)
                    {
                        work.IsDominated = false;
                    }

                    for (var leftIndex = 0; leftIndex < works.Count; leftIndex++)
                    {
                        for (var rightIndex = leftIndex + 1; rightIndex < works.Count; rightIndex++)
                        {
                            var left = works[leftIndex];
                            var right = works[rightIndex];
                            if (!left.HasProviderBookIdentity || !right.HasProviderBookIdentity)
                            {
                                // A local row relationship can keep sibling Editions together for the existing
                                // selector, but it is not durable Book identity and cannot drive cross-Book dominance.
                                continue;
                            }

                            var leftExplainsMore = left.Explanations.Any(leftExplanation =>
                                right.Explanations.Any(rightExplanation => StrictlyExplainsMoreOfSameOccurrence(leftExplanation, rightExplanation)));
                            var rightExplainsMore = right.Explanations.Any(rightExplanation =>
                                left.Explanations.Any(leftExplanation => StrictlyExplainsMoreOfSameOccurrence(rightExplanation, leftExplanation)));

                            // Conflicting independent occurrences are not a license for evaluation order to choose.
                            // Only a one-way same-occurrence superset dominates the less complete Book explanation.
                            if (leftExplainsMore == rightExplainsMore)
                            {
                                continue;
                            }

                            var winner = leftExplainsMore ? left : right;
                            var loser = leftExplainsMore ? right : left;
                            loser.IsDominated = true;
                            result.DominanceEdges.Add($"{winner.LogicalWorkKey}>{loser.LogicalWorkKey}");
                        }
                    }
                }

                KeepOnlyMaximalWorkExplanations();
                ApplyProviderBookDominance();

                var needsRecalledSiblingTitles =
                    works.Count > 1 &&
                    eligibleCandidates.Any(candidate =>
                        candidate.TitleOccurrenceExplanations.Any(explanation => explanation.NearbyUnexplainedTokens.Count > 0));
                if (needsRecalledSiblingTitles)
                {
                    var worksByKey = works.ToDictionary(work => work.LogicalWorkKey, StringComparer.OrdinalIgnoreCase);
                    foreach (var recalledCandidate in recalledCandidates ?? Array.Empty<EditionFtsMatch>())
                    {
                        if (recalledCandidate == null)
                        {
                            continue;
                        }

                        var workKey = GetProviderOccurrenceWorkKey(recalledCandidate, booksById) ??
                                      $"local-book-row:{recalledCandidate.BookId.ToString(CultureInfo.InvariantCulture)}";
                        if (!worksByKey.TryGetValue(workKey, out var work))
                        {
                            continue;
                        }

                        work.Explanations.AddRange(BuildTitleOccurrenceExplanations(
                            recalledCandidate,
                            Array.Empty<EditionTitleEvidence>(),
                            allTags,
                            mediaType,
                            booksById,
                            includeDirectEditionTitleAlignment: true));
                    }

                    KeepOnlyMaximalWorkExplanations();
                    ApplyProviderBookDominance();
                }

                var nonDominatedWinners = works
                    .Where(work => !work.IsDominated)
                    .Select(work => work.EditionWinner)
                    .ToList();
                if (nonDominatedWinners.Count == 0 && works.Count > 0)
                {
                    // A cycle means independent observed occurrences conflict. Preserve every work and
                    // let the established evidence ladder decide; iteration order must not break the tie.
                    foreach (var work in works)
                    {
                        work.IsDominated = false;
                    }

                    result.DominanceEdges.Clear();
                    nonDominatedWinners = works.Select(work => work.EditionWinner).ToList();
                }

                result.NonDominatedWorkWinners = nonDominatedWinners;
                return result;
            }

            private MatchProvenance BuildSelectedMatchProvenance(
                ScoredCandidate selected,
                MatchIdentityProof identityProof,
                IDictionary<string, List<string>> evidenceTags,
                BookMediaType mediaType,
                int? observedDurationSeconds,
                int? observedYear,
                BookMatchingStrictness strictness,
                string phase,
                IDictionary<int, Book> booksById)
            {
                if (selected?.Match == null)
                {
                    return null;
                }

                var candidate = selected.Match;
                var source = string.Equals(phase, "path-tags", StringComparison.OrdinalIgnoreCase)
                    ? "path"
                    : "embedded_tag";
                var provenance = new MatchProvenance
                {
                    Mode = strictness.ToString(),
                    Route = phase,
                    MatchedVia = selected.MatchedVia,
                    Summary = string.Equals(source, "path", StringComparison.OrdinalIgnoreCase)
                        ? "Matched from the file path"
                        : "Matched from file tags"
                };

                string SourceForField(string field)
                {
                    return ResolveEvidenceSource(field, source);
                }

                void AddSignal(List<MatchSignal> bucket, MatchSignal signal)
                {
                    if (bucket == null || signal == null || string.IsNullOrWhiteSpace(signal.Type))
                    {
                        return;
                    }

                    signal.Observed = LimitSignalValue(signal.Observed);
                    signal.Expected = LimitSignalValue(signal.Expected);
                    signal.Detail = LimitSignalValue(signal.Detail);

                    if (!bucket.Any(existing =>
                            existing != null &&
                            string.Equals(existing.Type, signal.Type, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Scope, signal.Scope, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Field, signal.Field, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Observed, signal.Observed, StringComparison.Ordinal) &&
                            string.Equals(existing.Expected, signal.Expected, StringComparison.Ordinal)))
                    {
                        bucket.Add(signal);
                    }
                }

                foreach (var proofValue in identityProof?.Values ?? Array.Empty<MatchIdentityProofValue>())
                {
                    if (proofValue.Role != MatchIdentityRole.Title && proofValue.Role != MatchIdentityRole.Author)
                    {
                        continue;
                    }

                    AddSignal(provenance.SupportingSignals, new MatchSignal
                    {
                        Type = proofValue.Role == MatchIdentityRole.Title ? "title" : "author",
                        Scope = proofValue.Scope,
                        Source = proofValue.Source,
                        Field = proofValue.Field,
                        Observed = proofValue.Observed,
                        Expected = proofValue.Expected,
                        Detail = proofValue.Detail
                    });
                }

                if (selected.AuthorMatch)
                {
                    var authorProof = (identityProof?.Values ?? Array.Empty<MatchIdentityProofValue>())
                        .Where(value => value.Role == MatchIdentityRole.Author)
                        .ToList();
                    var hasPathAuthorEvidence = authorProof.Any(value =>
                        string.Equals(value.Source, "path", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value.Source, "filename", StringComparison.OrdinalIgnoreCase));
                    var hasEmbeddedAuthorEvidence = authorProof.Any(value =>
                        string.Equals(value.Source, "embedded_tag", StringComparison.OrdinalIgnoreCase));
                    if (hasPathAuthorEvidence && !hasEmbeddedAuthorEvidence)
                    {
                        AddSignal(provenance.NeutralSignals, new MatchSignal
                        {
                            Type = "author",
                            Scope = "book",
                            Source = "embedded_tag",
                            Expected = candidate.AuthorName,
                            Detail = "No embedded tag value supplied author evidence; the folder path supplied it."
                        });
                    }

                    if (!provenance.SupportingSignals.Any(signal => string.Equals(signal.Type, "author", StringComparison.OrdinalIgnoreCase)))
                    {
                        AddSignal(provenance.SupportingSignals, new MatchSignal
                        {
                            Type = "author",
                            Scope = "book",
                            Source = source,
                            Expected = candidate.AuthorName,
                            Detail = "Author tokens corroborated this candidate."
                        });
                    }
                }

                var book = TryGetBookCached(candidate.BookId, booksById);
                if (selected.SeriesNameMatch)
                {
                    AddSignal(provenance.SupportingSignals, new MatchSignal
                    {
                        Type = "series_name",
                        Scope = "book",
                        Source = source,
                        Expected = book?.SeriesName,
                        Detail = "Series-name tokens corroborated this book."
                    });
                }

                var positionEvidence = selected.PositionEvidence;
                if (positionEvidence?.Fields != null)
                {
                    foreach (var field in positionEvidence.Fields.Where(item => item != null))
                    {
                        var observed = field.DetectedPositions.Count > 0
                            ? string.Join(", ", field.DetectedPositions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                            : string.Join(", ", field.ObservedPositionTokens.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                        var expected = positionEvidence.CandidatePositions;

                        if (field.Disposition == SeriesPositionDisposition.Match)
                        {
                            AddSignal(provenance.SupportingSignals, new MatchSignal
                            {
                                Type = "series_position",
                                Scope = "book",
                                Source = SourceForField(field.FieldName),
                                Field = field.FieldName,
                                Observed = observed,
                                Expected = expected,
                                Detail = selected.SeriesPositionMatch
                                    ? "Matching series position was eligible to break a title tie."
                                    : "Matching series position corroborated the title-eligible candidate."
                            });
                        }
                        else if (field.Disposition == SeriesPositionDisposition.Mismatch)
                        {
                            AddSignal(provenance.ConflictingSignals, new MatchSignal
                            {
                                Type = "series_position",
                                Scope = "book",
                                Source = SourceForField(field.FieldName),
                                Field = field.FieldName,
                                Observed = observed,
                                Expected = expected,
                                Detail = strictness == BookMatchingStrictness.Strict
                                    ? "This field's position conflicted, so Strict did not use that field as title proof."
                                    : "The position conflicted, but this mode did not let position override stronger title proof."
                            });
                        }
                    }
                }

                if (positionEvidence?.CandidatePositionValues?.Count > 0 && positionEvidence.HasSignal == false)
                {
                    AddSignal(provenance.NeutralSignals, new MatchSignal
                    {
                        Type = "series_position",
                        Scope = "book",
                        Source = source,
                        Expected = positionEvidence.CandidatePositions,
                        Detail = "No usable field-bound series-position evidence was present."
                    });
                }

                if (mediaType == BookMediaType.Audiobook)
                {
                    if (observedDurationSeconds.HasValue && candidate.DurationSeconds.HasValue && candidate.DurationSeconds.Value > 0)
                    {
                        var difference = Math.Abs(candidate.DurationSeconds.Value - observedDurationSeconds.Value);
                        var allowed = AudiobookDurationTolerance.ForMatchingSeconds(candidate.DurationSeconds.Value);
                        var durationSignal = new MatchSignal
                        {
                            Type = "duration",
                            Scope = "edition",
                            Source = "media_info",
                            Observed = FormatDurationSeconds(observedDurationSeconds.Value),
                            Expected = FormatDurationSeconds(candidate.DurationSeconds.Value),
                            Detail = selected.UndistinguishedAudiobookEditionFallback
                                ? "This file is a multipart track, so its duration was not compared with the full-book edition duration."
                                : $"Difference {difference} seconds; matching tolerance {allowed} seconds."
                        };

                        AddSignal(
                            selected.UndistinguishedAudiobookEditionFallback
                                ? provenance.NeutralSignals
                                : difference <= allowed
                                    ? provenance.SupportingSignals
                                    : provenance.ConflictingSignals,
                            durationSignal);
                    }
                    else
                    {
                        AddSignal(provenance.NeutralSignals, new MatchSignal
                        {
                            Type = "duration",
                            Scope = "edition",
                            Source = "media_info",
                            Observed = observedDurationSeconds.HasValue ? FormatDurationSeconds(observedDurationSeconds.Value) : null,
                            Expected = candidate.DurationSeconds.HasValue ? FormatDurationSeconds(candidate.DurationSeconds.Value) : null,
                            Detail = observedDurationSeconds.HasValue
                                ? "The catalog edition has no usable duration."
                                : "The file has no usable duration."
                        });
                    }

                    if (selected.NarratorMatchCount > 0)
                    {
                        var narratorEvidence = FindNarratorEvidenceFields(candidate.NarratorNames, candidate.AuthorName, evidenceTags);
                        foreach (var item in narratorEvidence)
                        {
                            AddSignal(provenance.SupportingSignals, new MatchSignal
                            {
                                Type = "narrator",
                                Scope = "edition",
                                Source = SourceForField(item.Key),
                                Field = item.Key,
                                Observed = item.Value,
                                Expected = candidate.NarratorNames,
                                Detail = "Narrator evidence corroborated this edition."
                            });
                        }

                        if (!provenance.SupportingSignals.Any(signal => string.Equals(signal.Type, "narrator", StringComparison.OrdinalIgnoreCase)))
                        {
                            AddSignal(provenance.SupportingSignals, new MatchSignal
                            {
                                Type = "narrator",
                                Scope = "edition",
                                Source = source,
                                Expected = candidate.NarratorNames,
                                Detail = "Narrator tokens corroborated this edition."
                            });
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(candidate.NarratorNames))
                    {
                        AddSignal(provenance.NeutralSignals, new MatchSignal
                        {
                            Type = "narrator",
                            Scope = "edition",
                            Source = source,
                            Expected = candidate.NarratorNames,
                            Detail = "No usable narrator evidence was present for this edition."
                        });
                    }

                    if (selected.UndistinguishedAudiobookEditionFallback)
                    {
                        AddSignal(provenance.NeutralSignals, new MatchSignal
                        {
                            Type = "edition_selection",
                            Scope = "edition",
                            Source = "catalog",
                            Detail = "Multiple audiobook editions were available, but the file supplied no usable narrator or full-book duration evidence to distinguish them. Chaptarr selected a native audiobook using its normal deterministic edition order."
                        });
                    }
                }

                if (mediaType == BookMediaType.Ebook)
                {
                    if (observedYear.HasValue && candidate.ReleaseDate.HasValue)
                    {
                        var yearSignal = new MatchSignal
                        {
                            Type = "publication_year",
                            Scope = "edition",
                            Source = source,
                            Observed = observedYear.Value.ToString(CultureInfo.InvariantCulture),
                            Expected = candidate.ReleaseDate.Value.Year.ToString(CultureInfo.InvariantCulture),
                            Detail = observedYear.Value == candidate.ReleaseDate.Value.Year
                                ? "Publication year corroborated this edition."
                                : "Publication year conflicted with this edition; stronger book identity evidence won."
                        };
                        AddSignal(
                            observedYear.Value == candidate.ReleaseDate.Value.Year
                                ? provenance.SupportingSignals
                                : provenance.ConflictingSignals,
                            yearSignal);
                    }
                    else
                    {
                        AddSignal(provenance.NeutralSignals, new MatchSignal
                        {
                            Type = "publication_year",
                            Scope = "edition",
                            Source = source,
                            Observed = observedYear?.ToString(CultureInfo.InvariantCulture),
                            Expected = candidate.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture),
                            Detail = observedYear.HasValue
                                ? "The catalog edition has no usable publication year."
                                : "No usable publication year was found in the matchable metadata."
                        });
                    }

                    if (selected.PublisherMatchCount > 0)
                    {
                        AddSignal(provenance.SupportingSignals, new MatchSignal
                        {
                            Type = "publisher",
                            Scope = "edition",
                            Source = source,
                            Expected = candidate.Publisher,
                            Detail = "Publisher tokens corroborated this edition."
                        });
                    }
                }


                if (candidate.ReadingFormatId.HasValue)
                {
                    var formatSignal = new MatchSignal
                    {
                        Type = "reading_format",
                        Scope = "edition",
                        Source = "catalog",
                        Observed = ReadingFormatName(candidate.ReadingFormatId.Value),
                        Expected = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                        Detail = selected.ReadingFormatMatch
                            ? "Edition format matches the discovered file type."
                            : "A representative non-native edition was selected because no better native edition was proven."
                    };
                    AddSignal(
                        selected.ReadingFormatMatch ? provenance.SupportingSignals : provenance.ConflictingSignals,
                        formatSignal);
                }

                if (selected.Leftovers?.Count > 0)
                {
                    AddSignal(provenance.NeutralSignals, new MatchSignal
                    {
                        Type = "unexplained_metadata",
                        Scope = "book",
                        Source = source,
                        Observed = string.Join(", ", selected.Leftovers),
                        Detail = "These nearby tokens were tolerated by the active matcher mode."
                    });
                }

                provenance.EvidenceValues = BuildEvidenceValuesFromSignals(
                    provenance,
                    evidenceTags,
                    source,
                    selected.TitleEvidence);

                return provenance;
            }

            private MatchIdentityProof BuildSelectedMatchIdentityProof(
                ScoredCandidate selected,
                IDictionary<string, List<string>> evidenceTags,
                string phase,
                IReadOnlyList<Dictionary<string, List<string>>> groupMemberTags)
            {
                if (selected?.Match == null)
                {
                    return new MatchIdentityProof(Array.Empty<MatchIdentityProofValue>());
                }

                var candidate = selected.Match;
                var defaultSource = string.Equals(phase, "path-tags", StringComparison.OrdinalIgnoreCase)
                    ? "path"
                    : "embedded_tag";
                var values = new List<MatchIdentityProofValue>();

                foreach (var titleEvidence in selected.TitleEvidence ?? Array.Empty<EditionTitleEvidence>())
                {
                    if (titleEvidence == null ||
                        string.IsNullOrWhiteSpace(titleEvidence.FieldName) ||
                        string.IsNullOrWhiteSpace(titleEvidence.FieldValue))
                    {
                        continue;
                    }

                    var matchedTitle = titleEvidence.MatchedTitle ?? candidate.EditionTitle;
                    var titleScope = !string.IsNullOrWhiteSpace(candidate.BookTitle) &&
                                     string.Equals(matchedTitle?.Trim(), candidate.BookTitle.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? "book"
                        : "edition";
                    var titleDetail = string.Equals(titleScope, "book", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(candidate.EditionTitle?.Trim(), candidate.BookTitle?.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? "This logical field supplied book-title containment proof."
                        : titleEvidence.IsNearExact
                            ? "Near-exact title evidence accepted by the active matcher mode."
                            : "This logical field supplied title containment proof.";

                    values.Add(new MatchIdentityProofValue(
                        MatchIdentityRole.Title,
                        ResolveEvidenceSource(titleEvidence.FieldName, defaultSource),
                        titleEvidence.FieldName,
                        titleEvidence.FieldValue,
                        matchedTitle,
                        titleScope,
                        titleDetail));
                }

                if (selected.AuthorMatch)
                {
                    foreach (var field in BuildAuthorEvidenceTags(candidate.AuthorName, evidenceTags))
                    {
                        foreach (var value in field.Value ?? new List<string>())
                        {
                            values.Add(new MatchIdentityProofValue(
                                MatchIdentityRole.Author,
                                ResolveEvidenceSource(field.Key, defaultSource),
                                field.Key,
                                value,
                                candidate.AuthorName,
                                "book",
                                "Author identity was present in matchable metadata."));
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(candidate.AuthorName) &&
                    TryBuildExactGroupIdentitySpanProof(candidate.AuthorName, groupMemberTags, out var groupAuthorProof))
                {
                    foreach (var observed in groupAuthorProof.ObservedValues.Distinct(StringComparer.Ordinal))
                    {
                        values.Add(new MatchIdentityProofValue(
                            MatchIdentityRole.Author,
                            ResolveEvidenceSource(groupAuthorProof.FieldName, defaultSource),
                            groupAuthorProof.FieldName,
                            observed,
                            candidate.AuthorName,
                            "book",
                            "The same normalized author span appeared in this physical field throughout the group."));
                    }
                }

                return new MatchIdentityProof(PreferSourceIdentityValues(values));
            }

            private static IEnumerable<MatchIdentityProofValue> PreferSourceIdentityValues(
                IEnumerable<MatchIdentityProofValue> values)
            {
                foreach (var roleGroup in (values ?? Enumerable.Empty<MatchIdentityProofValue>()).GroupBy(value => value.Role))
                {
                    var roleValues = roleGroup.ToList();
                    var sourceValues = roleValues.Where(value => IsSourceTagKey(value.Field)).ToList();
                    foreach (var value in sourceValues.Count > 0 ? sourceValues : roleValues)
                    {
                        yield return value;
                    }
                }
            }

            private List<MatchEvidenceValue> BuildEvidenceValuesFromSignals(
                MatchProvenance provenance,
                IDictionary<string, List<string>> evidenceTags,
                string source,
                IReadOnlyList<EditionTitleEvidence> titleEvidence = null)
            {
                var builder = new MatchEvidenceValueBuilder();
                var buckets = new[]
                {
                    (Signals: provenance?.SupportingSignals, Disposition: "supporting"),
                    (Signals: provenance?.ConflictingSignals, Disposition: "conflicting"),
                    (Signals: provenance?.NeutralSignals, Disposition: "neutral")
                };

                IEnumerable<string> GetFieldValues(string field)
                {
                    if (string.IsNullOrWhiteSpace(field) ||
                        evidenceTags == null ||
                        !evidenceTags.TryGetValue(field, out var values) ||
                        values == null)
                    {
                        return Enumerable.Empty<string>();
                    }

                    return values.Where(value => !string.IsNullOrWhiteSpace(value));
                }

                var titleValues = new List<(string Field, string Value)>();

                foreach (var bucket in buckets)
                {
                    foreach (var signal in bucket.Signals ?? new List<MatchSignal>())
                    {
                        if (signal == null || string.IsNullOrWhiteSpace(signal.Type))
                        {
                            continue;
                        }

                        var signalSource = !string.IsNullOrWhiteSpace(signal.Source)
                            ? signal.Source
                            : ResolveEvidenceSource(signal.Field, source);

                        if (string.Equals(signal.Type, "series_position", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(signal.Field) &&
                            !string.IsNullOrWhiteSpace(signal.Observed))
                        {
                            var positionTokens = TokenizeForLeftoverGate(signal.Observed);
                            foreach (var rawValue in GetFieldValues(signal.Field))
                            {
                                builder.AddMatchingTokens(
                                    signalSource,
                                    signal.Field,
                                    rawValue,
                                    positionTokens,
                                    bucket.Disposition,
                                    signal.Type,
                                    signal.Scope,
                                    signal.Detail);
                            }

                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(signal.Field) &&
                            !string.IsNullOrWhiteSpace(signal.Observed) &&
                            !string.IsNullOrWhiteSpace(signal.Expected))
                        {
                            builder.AddPhrase(
                                signalSource,
                                signal.Field,
                                signal.Observed,
                                signal.Expected,
                                bucket.Disposition,
                                signal.Type,
                                signal.Scope,
                                signal.Detail,
                                allowNearExact: string.Equals(signal.Type, "title", StringComparison.OrdinalIgnoreCase));

                            if (string.Equals(signal.Type, "title", StringComparison.OrdinalIgnoreCase))
                            {
                                titleValues.Add((signal.Field, signal.Observed));
                            }

                            continue;
                        }

                        if ((string.Equals(signal.Type, "series_name", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(signal.Type, "publisher", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(signal.Type, "subtitle", StringComparison.OrdinalIgnoreCase)) &&
                            !string.IsNullOrWhiteSpace(signal.Expected))
                        {
                            var expectedTokens = TokenizeForLeftoverGate(signal.Expected);
                            foreach (var pair in evidenceTags ?? new Dictionary<string, List<string>>())
                            {
                                if (IsExcludedFromMatching(pair.Key))
                                {
                                    continue;
                                }

                                foreach (var rawValue in pair.Value ?? new List<string>())
                                {
                                    builder.AddMatchingTokens(
                                        ResolveEvidenceSource(pair.Key, source),
                                        pair.Key,
                                        rawValue,
                                        expectedTokens,
                                        bucket.Disposition,
                                        signal.Type,
                                        signal.Scope,
                                        signal.Detail);
                                }
                            }

                            continue;
                        }

                        if ((string.Equals(signal.Type, "publication_year", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(signal.Type, "provider_identifier", StringComparison.OrdinalIgnoreCase)) &&
                            !string.IsNullOrWhiteSpace(signal.Observed))
                        {
                            foreach (var pair in evidenceTags ?? new Dictionary<string, List<string>>())
                            {
                                if (IsExcludedFromMatching(pair.Key))
                                {
                                    continue;
                                }

                                foreach (var rawValue in pair.Value ?? new List<string>())
                                {
                                    builder.AddLiteral(
                                        ResolveEvidenceSource(pair.Key, source),
                                        pair.Key,
                                        rawValue,
                                        signal.Observed,
                                        bucket.Disposition,
                                        signal.Type,
                                        signal.Scope,
                                        signal.Detail);
                                }
                            }
                        }
                    }
                }

                foreach (var evidence in titleEvidence ?? Array.Empty<EditionTitleEvidence>())
                {
                    if (evidence == null || string.IsNullOrWhiteSpace(evidence.FieldValue))
                    {
                        continue;
                    }

                    if (!titleValues.Any(value =>
                            string.Equals(value.Field, evidence.FieldName, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(value.Value, evidence.FieldValue, StringComparison.Ordinal)))
                    {
                        titleValues.Add((evidence.FieldName, evidence.FieldValue));
                    }
                }

                foreach (var titleValue in titleValues.Distinct())
                {
                    builder.AddNeutralRemainder(
                        ResolveEvidenceSource(titleValue.Field, source),
                        titleValue.Field,
                        titleValue.Value,
                        "book",
                        "This text was considered but did not support or contradict the selected match.");
                }

                return builder.Build();
            }

            private static string ResolveEvidenceSource(string field, string defaultSource)
            {
                if (string.IsNullOrWhiteSpace(field) ||
                    !field.StartsWith("PATH:", StringComparison.OrdinalIgnoreCase))
                {
                    return defaultSource;
                }

                return string.Equals(field, "PATH:FILE_VALUE", StringComparison.OrdinalIgnoreCase)
                    ? "filename"
                    : "path";
            }

            private void AddRetainedEmbeddedFallbackEvidence(
                FileMatch fileMatch,
                EditionFtsMatch selected,
                Dictionary<string, List<string>> embeddedTags,
                Dictionary<string, List<string>> pathTags)
            {
                var provenance = fileMatch?.Provenance;
                if (provenance == null || selected == null)
                {
                    return;
                }

                embeddedTags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                pathTags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                void AddSignal(List<MatchSignal> bucket, MatchSignal signal)
                {
                    if (signal == null || string.IsNullOrWhiteSpace(signal.Type))
                    {
                        return;
                    }

                    if (!bucket.Any(existing =>
                            existing != null &&
                            string.Equals(existing.Type, signal.Type, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Scope, signal.Scope, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Source, signal.Source, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Field, signal.Field, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Observed, signal.Observed, StringComparison.Ordinal) &&
                            string.Equals(existing.Expected, signal.Expected, StringComparison.Ordinal)))
                    {
                        bucket.Add(signal);
                    }
                }

                void RemapPathSignals(List<MatchSignal> bucket)
                {
                    foreach (var signal in (bucket ?? new List<MatchSignal>()).ToList())
                    {
                        if (signal == null ||
                            !string.Equals(signal.Source, "path", StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(signal.Observed))
                        {
                            continue;
                        }

                        var matchingPathValues = pathTags
                            .Where(pair => (pair.Value ?? new List<string>()).Any(value =>
                                string.Equals(value, signal.Observed, StringComparison.Ordinal)))
                            .Select(pair => pair.Key)
                            .ToList();
                        if (matchingPathValues.Count == 0)
                        {
                            continue;
                        }

                        bucket.Remove(signal);
                        foreach (var field in matchingPathValues)
                        {
                            var remapped = signal.Clone();
                            remapped.Field = field;
                            remapped.Source = ResolveEvidenceSource(field, "path");
                            AddSignal(bucket, remapped);
                        }
                    }
                }

                RemapPathSignals(provenance.SupportingSignals);
                RemapPathSignals(provenance.ConflictingSignals);
                RemapPathSignals(provenance.NeutralSignals);
                fileMatch.IdentityProof = RemapPathIdentityProof(fileMatch.IdentityProof, pathTags);

                if (embeddedTags.Count == 0)
                {
                    provenance.EvidenceValues = BuildEvidenceValuesFromSignals(
                        provenance,
                        pathTags,
                        "path");
                    return;
                }

                var titleEvidence = _containmentValidator.GetEditionTitleEvidence(selected.EditionTitle, embeddedTags);
                if ((titleEvidence == null || titleEvidence.Count == 0) &&
                    !string.IsNullOrWhiteSpace(selected.BookTitle) &&
                    !string.Equals(selected.BookTitle, selected.EditionTitle, StringComparison.OrdinalIgnoreCase))
                {
                    titleEvidence = _containmentValidator.GetEditionTitleEvidence(selected.BookTitle, embeddedTags);
                }

                var titleScope = string.Equals(selected.EditionTitle?.Trim(), selected.BookTitle?.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? "book"
                    : "edition";
                var retainedIdentityValues = (fileMatch.IdentityProof?.Values ?? Array.Empty<MatchIdentityProofValue>()).ToList();
                foreach (var evidence in titleEvidence ?? Array.Empty<EditionTitleEvidence>())
                {
                    retainedIdentityValues.Add(new MatchIdentityProofValue(
                        MatchIdentityRole.Title,
                        "embedded_tag",
                        evidence.FieldName,
                        evidence.FieldValue,
                        evidence.MatchedTitle ?? selected.EditionTitle,
                        titleScope,
                        "Embedded title evidence corroborated the candidate selected from the folder and filename."));
                    AddSignal(provenance.SupportingSignals, new MatchSignal
                    {
                        Type = "title",
                        Scope = titleScope,
                        Source = "embedded_tag",
                        Field = evidence.FieldName,
                        Observed = evidence.FieldValue,
                        Expected = evidence.MatchedTitle ?? selected.EditionTitle,
                        Detail = "Embedded title evidence corroborated the candidate selected from the folder and filename."
                    });
                }

                var embeddedAuthorEvidence = BuildAuthorEvidenceTags(selected.AuthorName, embeddedTags);
                foreach (var field in embeddedAuthorEvidence)
                {
                    foreach (var value in field.Value ?? new List<string>())
                    {
                        retainedIdentityValues.Add(new MatchIdentityProofValue(
                            MatchIdentityRole.Author,
                            "embedded_tag",
                            field.Key,
                            value,
                            selected.AuthorName,
                            "book",
                            "Embedded author evidence corroborated the path-selected candidate."));
                        AddSignal(provenance.SupportingSignals, new MatchSignal
                        {
                            Type = "author",
                            Scope = "book",
                            Source = "embedded_tag",
                            Field = field.Key,
                            Observed = value,
                            Expected = selected.AuthorName,
                            Detail = "Embedded author evidence corroborated the path-selected candidate."
                        });
                    }
                }

                var pathSuppliedAuthor = provenance.SupportingSignals.Any(signal =>
                    string.Equals(signal?.Type, "author", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(signal.Source, "path", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(signal.Source, "filename", StringComparison.OrdinalIgnoreCase)));
                if (pathSuppliedAuthor && embeddedAuthorEvidence.Count == 0)
                {
                    AddSignal(provenance.NeutralSignals, new MatchSignal
                    {
                        Type = "author",
                        Scope = "book",
                        Source = "embedded_tag",
                        Expected = selected.AuthorName,
                        Detail = "No embedded tag value supplied author evidence; the folder path supplied it."
                    });
                }

                fileMatch.IdentityProof = new MatchIdentityProof(retainedIdentityValues);

                provenance.Summary = "Matched from file tags and the file path";
                provenance.EvidenceValues = BuildEvidenceValuesFromSignals(
                    provenance,
                    MergeEvidenceTags(embeddedTags, pathTags),
                    "embedded_tag");
            }

            private static MatchIdentityProof RemapPathIdentityProof(
                MatchIdentityProof identityProof,
                IDictionary<string, List<string>> pathTags)
            {
                if (identityProof == null)
                {
                    return null;
                }

                var remapped = new List<MatchIdentityProofValue>();
                foreach (var proofValue in identityProof.Values)
                {
                    if (!string.Equals(proofValue.Source, "path", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(proofValue.Source, "filename", StringComparison.OrdinalIgnoreCase))
                    {
                        remapped.Add(proofValue);
                        continue;
                    }

                    var fields = (pathTags ?? new Dictionary<string, List<string>>())
                        .Where(pair => (pair.Value ?? new List<string>()).Any(value =>
                            string.Equals(value, proofValue.Observed, StringComparison.Ordinal)))
                        .Select(pair => pair.Key)
                        .ToList();
                    foreach (var field in fields)
                    {
                        remapped.Add(new MatchIdentityProofValue(
                            proofValue.Role,
                            ResolveEvidenceSource(field, "path"),
                            field,
                            proofValue.Observed,
                            proofValue.Expected,
                            proofValue.Scope,
                            proofValue.Detail));
                    }
                }

                return new MatchIdentityProof(remapped);
            }

            private static string LimitSignalValue(string value)
            {
                const int maxLength = 500;
                if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
                {
                    return value;
                }

                return value.Substring(0, maxLength) + "...";
            }

            private static string FormatDurationSeconds(int seconds)
            {
                return $"{Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture)} seconds";
            }

            private static string ReadingFormatName(int readingFormatId)
            {
                return readingFormatId switch
                {
                    1 => "print",
                    2 => "audiobook",
                    3 => "ebook",
                    _ => $"format {readingFormatId.ToString(CultureInfo.InvariantCulture)}"
                };
            }

                /// <summary>
                /// Compare two scored candidates for sorting (higher = better).
            /// Audiobooks prioritize narrator-aware sibling selection; ebooks prioritize year/publisher.
            /// </summary>
            private static int CompareScoredCandidates(ScoredCandidate a, ScoredCandidate b, BookMediaType mediaType)
            {
                return mediaType == BookMediaType.Ebook
                    ? CompareEbookScoredCandidates(a, b)
                    : CompareAudiobookScoredCandidates(a, b);
            }

            private static int CompareAudiobookScoredCandidates(ScoredCandidate a, ScoredCandidate b)
            {
                int cmp;

                cmp = a.TitleEvidenceTier.CompareTo(b.TitleEvidenceTier);
                if (cmp != 0) return cmp;

                cmp = a.AudiobookProofTier.CompareTo(b.AudiobookProofTier);
                if (cmp != 0) return cmp;

                cmp = a.HasDurationProof.CompareTo(b.HasDurationProof);
                if (cmp != 0) return cmp;

                cmp = a.HasDirectEditionTitleProof.CompareTo(b.HasDirectEditionTitleProof);
                if (cmp != 0) return cmp;

                cmp = a.RepresentativeRank.CompareTo(b.RepresentativeRank);
                if (cmp != 0) return cmp;

                cmp = a.NarratorTier.CompareTo(b.NarratorTier);
                if (cmp != 0) return cmp;

                cmp = a.NarratorMatchCount.CompareTo(b.NarratorMatchCount);
                if (cmp != 0) return cmp;



                cmp = a.AuthorMatch.CompareTo(b.AuthorMatch);
                if (cmp != 0) return cmp;

                cmp = a.SeriesNameMatch.CompareTo(b.SeriesNameMatch);
                if (cmp != 0) return cmp;

                cmp = a.SeriesPositionMatch.CompareTo(b.SeriesPositionMatch);
                if (cmp != 0) return cmp;

                cmp = a.ReadingFormatMatch.CompareTo(b.ReadingFormatMatch);
                if (cmp != 0) return cmp;

                cmp = (a.DurationDiff == 0).CompareTo(b.DurationDiff == 0);
                if (cmp != 0) return cmp;

                if (a.DurationDiff != int.MaxValue || b.DurationDiff != int.MaxValue)
                {
                    cmp = b.DurationDiff.CompareTo(a.DurationDiff);
                    if (cmp != 0) return cmp;
                }

                cmp = (a.YearDiff == 0).CompareTo(b.YearDiff == 0);
                if (cmp != 0) return cmp;

                if (a.YearDiff != int.MaxValue || b.YearDiff != int.MaxValue)
                {
                    cmp = b.YearDiff.CompareTo(a.YearDiff);
                    if (cmp != 0) return cmp;
                }

                cmp = b.LeftoverCount.CompareTo(a.LeftoverCount);
                if (cmp != 0) return cmp;

                cmp = a.Bm25Score.CompareTo(b.Bm25Score);
                if (cmp != 0) return cmp;

                // Only the ruled no-corroboration cohort needs a new final ordering rule. Keep the
                // existing evidence ladder authoritative, then make an otherwise exact tie stable
                // by provider identity. Local database row IDs are never semantic matching evidence.
                if (a.UndistinguishedAudiobookEditionFallback && b.UndistinguishedAudiobookEditionFallback)
                {
                    var aProviderId = a.Match.ForeignEditionId?.Trim();
                    var bProviderId = b.Match.ForeignEditionId?.Trim();
                    var aHasProviderId = !string.IsNullOrWhiteSpace(aProviderId);
                    var bHasProviderId = !string.IsNullOrWhiteSpace(bProviderId);

                    cmp = aHasProviderId.CompareTo(bHasProviderId);
                    if (cmp != 0) return cmp;

                    if (aHasProviderId)
                    {
                        // CompareScoredCandidates is called through a descending sort adapter, so
                        // invert the ordinal comparison to make the lower provider ID win.
                        return string.Compare(bProviderId, aProviderId, StringComparison.OrdinalIgnoreCase);
                    }
                }

                return 0;
            }

            private static int CompareEbookScoredCandidates(ScoredCandidate a, ScoredCandidate b)
            {
                int cmp;

                cmp = a.TitleEvidenceTier.CompareTo(b.TitleEvidenceTier);
                if (cmp != 0) return cmp;


                cmp = (a.YearDiff == 0).CompareTo(b.YearDiff == 0);
                if (cmp != 0) return cmp;

                if (a.YearDiff != int.MaxValue || b.YearDiff != int.MaxValue)
                {
                    cmp = b.YearDiff.CompareTo(a.YearDiff);
                    if (cmp != 0) return cmp;
                }

                cmp = a.PublisherMatchCount.CompareTo(b.PublisherMatchCount);
                if (cmp != 0) return cmp;


                cmp = a.AuthorMatch.CompareTo(b.AuthorMatch);
                if (cmp != 0) return cmp;

                cmp = a.SeriesNameMatch.CompareTo(b.SeriesNameMatch);
                if (cmp != 0) return cmp;

                cmp = a.SeriesPositionMatch.CompareTo(b.SeriesPositionMatch);
                if (cmp != 0) return cmp;

                cmp = a.ReadingFormatMatch.CompareTo(b.ReadingFormatMatch);
                if (cmp != 0) return cmp;

                cmp = b.LeftoverCount.CompareTo(a.LeftoverCount);
                if (cmp != 0) return cmp;

                return a.Bm25Score.CompareTo(b.Bm25Score);
            }

            private bool HasNearExactTitleAmbiguity(string phase, List<ScoredCandidate> scoredCandidates, IDictionary<int, Book> booksById)
            {
                if (scoredCandidates == null || scoredCandidates.Count < 2)
                {
                    return false;
                }

                // Exact evidence remains authoritative. Only reject when every viable candidate depends on
                // the typo/plural fallback and those candidates point at different logical works.
                if (scoredCandidates.Any(c => c.TitleEvidenceTier > 0))
                {
                    return false;
                }

                var workKeys = scoredCandidates
                    .Select(c => GetLogicalWorkKey(c.Match, booksById) ?? $"book:{c.Match.BookId.ToString(CultureInfo.InvariantCulture)}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();

                if (workKeys.Count <= 1)
                {
                    return false;
                }

                _logger.Debug("[HOLY-GRAIL][{0}] Rejecting near-exact title ambiguity across logical works: {1}",
                    phase,
                    string.Join(" | ", scoredCandidates.Take(5).Select(c =>
                    {
                        var title = c.Match.EditionTitle ?? string.Empty;
                        return $"EditionId={c.Match.EditionId} Title='{(title.Length > 60 ? title.Substring(0, 60) + "..." : title)}'";
                    })));

                return true;
            }


                private int PublisherTokenMatchCount(string publisher, HashSet<string> tagTokens)
                {
                    if (string.IsNullOrWhiteSpace(publisher) || tagTokens == null || tagTokens.Count == 0)
                    {
                        return 0;
                    }

                    var publisherTokens = TokenizeForLeftoverGate(publisher)
                        .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 2)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (publisherTokens.Count == 0)
                    {
                        return 0;
                    }

                    var count = 0;
                    foreach (var tok in publisherTokens)
                    {
                        if (tagTokens.Contains(tok))
                        {
                            count++;
                        }
                    }

                    return count;
                }

                private bool IsAuthorPresentInNonCommentNonTrashTags(string authorName, IDictionary<string, List<string>> nonCommentTags)
                {
                    if (string.IsNullOrWhiteSpace(authorName) || nonCommentTags == null || nonCommentTags.Count == 0)
                    {
                    return false;
                }

                var filtered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in nonCommentTags)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                    {
                        continue;
                    }

                    if (IsExcludedFromMatching(kv.Key))
                    {
                        continue;
                    }

                    var values = kv.Value?
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .ToList();

                    if (values == null || values.Count == 0)
                    {
                        continue;
                    }

                    filtered[kv.Key] = values;
                }

                if (filtered.Count == 0)
                {
                    return false;
                }

                return _containmentValidator.ValidateAuthorInTags(authorName, filtered);
            }

            private enum SeriesPositionDisposition
            {
                NoSignal,
                Match,
                Mismatch
            }

            private sealed class SeriesPositionFieldEvidence
            {
                public string FieldName { get; set; }
                public SeriesPositionDisposition Disposition { get; set; }
                public HashSet<string> DetectedPositions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public HashSet<string> ObservedPositionTokens { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public HashSet<string> RecognizedSeriesTokens { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            private sealed class CandidateSeriesContext
            {
                public HashSet<string> NameTokens { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public HashSet<string> PositionValues { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            private sealed class SeriesPositionEvidence
            {
                public List<SeriesPositionFieldEvidence> Fields { get; set; } = new List<SeriesPositionFieldEvidence>();
                public HashSet<string> CandidatePositionValues { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                public bool HasSignal => Fields.Any(f => f.Disposition != SeriesPositionDisposition.NoSignal);
                public bool HasMatchingSignal => Fields.Any(f => f.Disposition == SeriesPositionDisposition.Match);

                public string DetectedPositions => string.Join(", ", Fields
                    .SelectMany(f => f.DetectedPositions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

                public string CandidatePositions => CandidatePositionValues.Count == 0
                    ? "none"
                    : string.Join(", ", CandidatePositionValues.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

                public SeriesPositionFieldEvidence GetField(string fieldName)
                {
                    if (string.IsNullOrWhiteSpace(fieldName))
                    {
                        return null;
                    }

                    return Fields.FirstOrDefault(f => string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
                }
            }

            private SeriesPositionEvidence GetSeriesPositionEvidence(
                EditionFtsMatch candidate,
                IDictionary<string, List<string>> allTags,
                IDictionary<int, Book> booksById)
            {
                var noSignal = new SeriesPositionEvidence();

                if (candidate == null || allTags == null || allTags.Count == 0 || _bookService == null)
                {
                    return noSignal;
                }

                var book = TryGetBookCached(candidate.BookId, booksById);
                if (book == null)
                {
                    return noSignal;
                }

                var candidateSeriesContexts = GetCandidateSeriesContexts(book);
                if (candidateSeriesContexts.Count == 0)
                {
                    return noSignal;
                }

                var fieldEvidence = ExtractSeriesPositionsFromTags(allTags, candidateSeriesContexts);
                if (fieldEvidence.Count == 0)
                {
                    return noSignal;
                }

                var candidatePositions = candidateSeriesContexts
                    .SelectMany(c => c.PositionValues)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return new SeriesPositionEvidence
                {
                    CandidatePositionValues = candidatePositions,
                    Fields = fieldEvidence
                };
            }

            private Book TryGetBookCached(int bookId, IDictionary<int, Book> booksById)
            {
                if (booksById != null && booksById.TryGetValue(bookId, out var cached))
                {
                    return cached;
                }

                try
                {
                    var loaded = _bookService.GetBook(bookId);
                    if (booksById != null)
                    {
                        booksById[bookId] = loaded;
                    }
                    return loaded;
                }
                catch
                {
                    return null;
                }
            }

            private static string GetEditionIdentityKey(string providerEditionId, int localEditionId)
            {
                var normalizedProviderId = providerEditionId?.Trim();
                return !string.IsNullOrWhiteSpace(normalizedProviderId)
                    ? $"provider:{normalizedProviderId}"
                    : $"local:{localEditionId.ToString(CultureInfo.InvariantCulture)}";
            }

            private static bool SameEditionIdentity(EditionFtsMatch left, EditionFtsMatch right)
            {
                if (left == null || right == null)
                {
                    return false;
                }

                return string.Equals(
                    GetEditionIdentityKey(left.ForeignEditionId, left.EditionId),
                    GetEditionIdentityKey(right.ForeignEditionId, right.EditionId),
                    StringComparison.OrdinalIgnoreCase);
            }

            private bool SameLogicalWork(EditionFtsMatch left, EditionFtsMatch right, IDictionary<int, Book> booksById)
            {
                if (left == null || right == null)
                {
                    return false;
                }

                if (left.BookId == right.BookId)
                {
                    return true;
                }

                var leftKey = GetLogicalWorkKey(left, booksById);
                var rightKey = GetLogicalWorkKey(right, booksById);

                return !string.IsNullOrWhiteSpace(leftKey) &&
                       !string.IsNullOrWhiteSpace(rightKey) &&
                       string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
            }

            private string GetStableLogicalWorkKey(FileMatch match, IDictionary<int, Book> booksById)
            {
                if (match == null)
                {
                    return null;
                }

                var book = TryGetBookCached(match.BookId, booksById);
                var stableId = GetStableLogicalWorkComponent(book);
                if (stableId == null)
                {
                    return null;
                }

                var authorComponent = match.AuthorId > 0
                    ? match.AuthorId.ToString(CultureInfo.InvariantCulture)
                    : (book?.AuthorId ?? 0).ToString(CultureInfo.InvariantCulture);
                var mediaComponent = (book?.MediaType ?? BookMediaType.Audiobook).ToString();
                return $"{authorComponent}|{mediaComponent}|{stableId}";
            }

            private string GetLogicalWorkKey(FileMatch match, IDictionary<int, Book> booksById)
            {
                if (match == null)
                {
                    return null;
                }

                var book = TryGetBookCached(match.BookId, booksById);
                var stableId = GetStableLogicalWorkComponent(book) ??
                    $"book:{match.BookId.ToString(CultureInfo.InvariantCulture)}";

                var authorComponent = match.AuthorId > 0
                    ? match.AuthorId.ToString(CultureInfo.InvariantCulture)
                    : (book?.AuthorId ?? 0).ToString(CultureInfo.InvariantCulture);

                var mediaComponent = (book?.MediaType ?? BookMediaType.Audiobook).ToString();
                return $"{authorComponent}|{mediaComponent}|{stableId}";
            }

            private string GetLogicalWorkKey(EditionFtsMatch candidate, IDictionary<int, Book> booksById)
            {
                if (candidate == null)
                {
                    return null;
                }

                var book = TryGetBookCached(candidate.BookId, booksById);
                // Stay conservative: cluster only on stable provider/work identity copied across clone rows.
                // Do not fall back to title/title-slug here or we risk merging unrelated same-author siblings.
                var stableId = GetStableLogicalWorkComponent(book) ??
                    $"book:{candidate.BookId.ToString(CultureInfo.InvariantCulture)}";

                var authorComponent = candidate.AuthorId > 0
                    ? candidate.AuthorId.ToString(CultureInfo.InvariantCulture)
                    : (book?.AuthorId ?? 0).ToString(CultureInfo.InvariantCulture);

                var mediaComponent = (book?.MediaType ?? BookMediaType.Audiobook).ToString();
                return $"{authorComponent}|{mediaComponent}|{stableId}";
            }

            private string GetProviderOccurrenceWorkKey(EditionFtsMatch candidate, IDictionary<int, Book> booksById)
            {
                if (candidate == null)
                {
                    return null;
                }

                var book = TryGetBookCached(candidate.BookId, booksById);
                var providerBookIdentity = GetStableLogicalWorkComponent(book);
                if (string.IsNullOrWhiteSpace(providerBookIdentity))
                {
                    return null;
                }

                var mediaComponent = (book?.MediaType ?? BookMediaType.Audiobook).ToString();
                return $"{mediaComponent}|{providerBookIdentity}";
            }

            private static string GetStableLogicalWorkComponent(Book book)
            {
                return
                    BuildLogicalWorkComponent("base", book?.BaseBookId) ??
                    BuildLogicalWorkComponent("hc", book?.HardcoverBookId) ??
                    BuildLogicalWorkComponent("grw", book?.GoodreadsWorkId) ??
                    BuildLogicalWorkComponent("gr", book?.GoodreadsBookId) ??
                    BuildLogicalWorkComponent("olw", book?.OpenLibraryWorkId) ??
                    BuildLogicalWorkComponent("gb", book?.GoogleBooksId) ??
                    BuildLogicalWorkComponent("aud", book?.AudibleASIN) ??
                    BuildLogicalWorkComponent("az", book?.ASIN);
            }

            private static string BuildLogicalWorkComponent(string prefix, string rawValue)
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    return null;
                }

                return $"{prefix}:{rawValue.Trim().ToLowerInvariant()}";
            }

            private HashSet<string> GetCandidateSeriesPositions(Book book)
            {
                var positions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (book == null)
                {
                    return positions;
                }

                if (book.SeriesLinks?.Any() == true)
                {
                    foreach (var link in book.SeriesLinks)
                    {
                        var linkPosition = NormalizeSeriesPosition(link?.Position) ??
                                           NormalizeSeriesPosition(link != null && link.SeriesPosition > 0
                                               ? link.SeriesPosition.ToString(CultureInfo.InvariantCulture)
                                               : null);
                        AddPositionWithRangeParts(positions, linkPosition);
                    }
                }

                AddPositionWithRangeParts(positions, NormalizeSeriesPosition(book.SeriesPosition));
                return positions;
            }

            private void AddPositionWithRangeParts(HashSet<string> output, string normalizedPosition)
            {
                if (output == null || string.IsNullOrWhiteSpace(normalizedPosition))
                {
                    return;
                }

                output.Add(normalizedPosition);

                // NormalizeSeriesPosition can return ranges like "3-4" (rare). Treat each component as acceptable too.
                if (normalizedPosition.Contains('-', StringComparison.Ordinal))
                {
                    foreach (var part in normalizedPosition.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = part?.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            output.Add(trimmed);
                        }
                    }
                }
            }

            private List<CandidateSeriesContext> GetCandidateSeriesContexts(Book book)
            {
                var contexts = new List<CandidateSeriesContext>();
                if (book == null)
                {
                    return contexts;
                }

                void AddOrMergeContext(CandidateSeriesContext context)
                {
                    if (context == null || context.NameTokens.Count == 0)
                    {
                        return;
                    }

                    var existing = contexts.FirstOrDefault(c => c.NameTokens.SetEquals(context.NameTokens));
                    if (existing == null)
                    {
                        contexts.Add(context);
                        return;
                    }

                    existing.PositionValues.UnionWith(context.PositionValues);
                }

                if (book.SeriesLinks?.Any() == true)
                {
                    foreach (var link in book.SeriesLinks)
                    {
                        var context = new CandidateSeriesContext();
                        AddSeriesTokens(context.NameTokens, link?.Series?.Value?.Title);
                        if (context.NameTokens.Count == 0)
                        {
                            continue;
                        }

                        var linkPosition = NormalizeSeriesPosition(link?.Position) ??
                                           NormalizeSeriesPosition(link != null && link.SeriesPosition > 0
                                               ? link.SeriesPosition.ToString(CultureInfo.InvariantCulture)
                                               : null);
                        AddPositionWithRangeParts(context.PositionValues, linkPosition);
                        AddOrMergeContext(context);
                    }
                }

                var bookContext = new CandidateSeriesContext();
                AddSeriesTokens(bookContext.NameTokens, book.SeriesName);
                if (bookContext.NameTokens.Count > 0)
                {
                    AddPositionWithRangeParts(bookContext.PositionValues, NormalizeSeriesPosition(book.SeriesPosition));
                    AddOrMergeContext(bookContext);
                }

                return contexts;
            }

            private HashSet<string> GetCandidateSeriesNameTokens(Book book)
            {
                var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (book == null)
                {
                    return tokens;
                }

                if (book.SeriesLinks?.Any() == true)
                {
                    foreach (var link in book.SeriesLinks)
                    {
                        var title = link?.Series?.Value?.Title;
                        AddSeriesTokens(tokens, title);
                    }
                }
                else
                {
                    AddSeriesTokens(tokens, book.SeriesName);
                }

                return tokens;
            }

            private void AddSeriesTokens(HashSet<string> output, string seriesName)
            {
                if (output == null || string.IsNullOrWhiteSpace(seriesName))
                {
                    return;
                }

                foreach (var t in TokenizeForLeftoverGate(seriesName))
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (SeriesNameNoiseTokens.Contains(t)) continue;
                    if (t.Length <= 2) continue;
                    output.Add(t);
                }
            }

            private List<SeriesPositionFieldEvidence> ExtractSeriesPositionsFromTags(
                IDictionary<string, List<string>> allTags,
                IReadOnlyList<CandidateSeriesContext> candidateSeriesContexts)
            {
                var detected = new List<SeriesPositionFieldEvidence>();
                if (allTags == null || allTags.Count == 0 || candidateSeriesContexts == null || candidateSeriesContexts.Count == 0)
                {
                    return detected;
                }

                foreach (var kv in allTags)
                {
                    if (kv.Value == null || kv.Value.Count == 0)
                    {
                        continue;
                    }

                    if (IsExcludedFromMatching(kv.Key))
                    {
                        continue;
                    }

                    if (IsSeriesEvidenceNonSeriesNumericKey(kv.Key))
                    {
                        continue;
                    }

                    // Avoid pulling numeric signals from large identifier fields.
                    if (!string.IsNullOrWhiteSpace(kv.Key) &&
                        (kv.Key.IndexOf("ISBN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         kv.Key.IndexOf("ASIN", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    var allFieldTokens = kv.Value
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .SelectMany(v => TokenizeForLeftoverGate(v))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var fieldContexts = candidateSeriesContexts
                        .Where(c => c.NameTokens.All(allFieldTokens.Contains))
                        .ToList();
                    var fieldHasSeriesContext = fieldContexts.Count > 0;
                    if (!fieldHasSeriesContext)
                    {
                        continue;
                    }

                    SeriesPositionFieldEvidence fieldEvidence = null;

                    foreach (var raw in kv.Value)
                    {
                        var value = raw?.Trim();
                        if (string.IsNullOrWhiteSpace(value) || value.Length >= 5000)
                        {
                            continue;
                        }

                        // Ignore numeric tokens in "packaging" contexts like "Track 04" / "Disc 1" / "Chapter 12".
                        var valueTokens = TokenizeForLeftoverGate(value);
                        if (valueTokens.Any(t => HolyGrailLeftoverNumericPackagingTokens.Contains(t)))
                        {
                            continue;
                        }

                        var valueContexts = fieldContexts
                            .Where(c => c.NameTokens.All(valueTokens.Contains))
                            .ToList();
                        var hasSeriesContext = valueContexts.Count > 0;
                        var isStandalonePosition = LooksLikeStandaloneSeriesPositionValue(value);

                        if (!hasSeriesContext &&
                            !(fieldHasSeriesContext && isStandalonePosition && fieldContexts.Count == 1))
                        {
                            continue;
                        }

                        foreach (Match m in Regex.Matches(value, @"\b\d{1,4}(?:\.\d+)?\b"))
                        {
                            if (!HasSeriesPositionContextForNumber(value, valueTokens, m, hasSeriesContext, fieldHasSeriesContext))
                            {
                                continue;
                            }

                            var normalized = NormalizeSeriesPosition(m.Value);
                            if (normalized == null)
                            {
                                continue;
                            }

                            fieldEvidence ??= new SeriesPositionFieldEvidence
                            {
                                FieldName = kv.Key,
                                Disposition = SeriesPositionDisposition.Match
                            };

                            fieldEvidence.DetectedPositions.Add(normalized);

                            var applicableContexts = hasSeriesContext ? valueContexts : fieldContexts;
                            foreach (var token in TokenizeForLeftoverGateSequence(m.Value))
                            {
                                if (!string.IsNullOrWhiteSpace(token))
                                {
                                    fieldEvidence.ObservedPositionTokens.Add(token);
                                }
                            }

                            foreach (var context in applicableContexts)
                            {
                                fieldEvidence.RecognizedSeriesTokens.UnionWith(context.NameTokens);
                            }

                            if (applicableContexts.Count == 0 ||
                                applicableContexts.Any(c => !c.PositionValues.Contains(normalized)))
                            {
                                fieldEvidence.Disposition = SeriesPositionDisposition.Mismatch;
                            }
                        }
                    }

                    if (fieldEvidence != null)
                    {
                        detected.Add(fieldEvidence);
                    }
                }

                return detected;
            }

            private bool HasSeriesPositionContextForNumber(
                string value,
                IReadOnlyCollection<string> valueTokens,
                Match numberMatch,
                bool valueHasSeriesContext,
                bool fieldHasSeriesContext)
            {
                if (numberMatch == null || string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                if (fieldHasSeriesContext && LooksLikeStandaloneSeriesPositionValue(value))
                {
                    return true;
                }

                if (valueTokens != null &&
                    (valueTokens.Contains("book", StringComparer.OrdinalIgnoreCase) ||
                     valueTokens.Contains("bk", StringComparer.OrdinalIgnoreCase) ||
                     valueTokens.Contains("volume", StringComparer.OrdinalIgnoreCase) ||
                     valueTokens.Contains("vol", StringComparer.OrdinalIgnoreCase) ||
                     valueTokens.Contains("number", StringComparer.OrdinalIgnoreCase) ||
                     valueTokens.Contains("no", StringComparer.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (Regex.IsMatch(value, @"#\s*\d{1,4}(?:\.\d+)?\b", RegexOptions.IgnoreCase))
                {
                    return true;
                }

                if (!valueHasSeriesContext)
                {
                    return false;
                }

                // Accept "Series Name 3" / "Series Name Season 3" style metadata, but do not
                // treat arbitrary interior numbers like "A 1001 Dark Nights Standalone" as a
                // position merely because the same title also includes the DB series name.
                return Regex.IsMatch(
                    value.Substring(numberMatch.Index),
                    @"^\d{1,4}(?:\.\d+)?\s*$",
                    RegexOptions.IgnoreCase);
            }

            private static bool IsSeriesEvidenceNonSeriesNumericKey(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return false;
                }

                if (SeriesEvidenceNonSeriesNumericKeys.Contains(key))
                {
                    return true;
                }

                var separatorIndex = key.LastIndexOf(':');
                return separatorIndex >= 0 &&
                       separatorIndex < key.Length - 1 &&
                       SeriesEvidenceNonSeriesNumericKeys.Contains(key.Substring(separatorIndex + 1));
            }

            private bool LooksLikeStandaloneSeriesPositionValue(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                // Some containers flatten custom metadata into one key, e.g.
                // MP4:---- = ["Audible Originals", "B0...", "Impact Winter", "3"].
                // Treat the standalone number as a series position only when the same tag key
                // also carries the candidate's DB series name; do not make "season" a book rule.
                return Regex.IsMatch(
                    value.Trim(),
                    @"^#?\s*(?:(?:book|bk|volume|vol|part|no|number)\s*)?\d{1,4}(?:\.\d+)?\s*$",
                    RegexOptions.IgnoreCase);
            }

            private List<string> SplitNarrators(string narratorNames)
            {
                if (string.IsNullOrWhiteSpace(narratorNames))
                {
                    return new List<string>();
                }

                return narratorNames
                    .Replace(" & ", ", ", StringComparison.OrdinalIgnoreCase)
                    .Replace(" and ", ", ", StringComparison.OrdinalIgnoreCase)
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => n.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
            }

            private int CountNarratorMatchesInTags(string narratorNames, string authorName, IDictionary<string, List<string>> allTags)
            {
                if (string.IsNullOrWhiteSpace(narratorNames) || allTags == null || allTags.Count == 0)
                {
                    return 0;
                }

                var matchedNarratorCount = 0;
                foreach (var narrator in SplitNarrators(narratorNames))
                {
                    if (FindNarratorEvidenceFields(narrator, authorName, allTags).Count > 0)
                    {
                        matchedNarratorCount++;
                    }
                }

                return matchedNarratorCount;
            }

            private int NarratorTokenMatchCount(string narrator, HashSet<string> tagTokens)
            {
                if (string.IsNullOrWhiteSpace(narrator) || tagTokens == null || tagTokens.Count == 0)
                {
                    return 0;
                }

                var narratorTokens = TokenizeForLeftoverGate(narrator)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (narratorTokens.Count == 0)
                {
                    return 0;
                }

                var count = 0;
                foreach (var tok in narratorTokens)
                {
                    if (tagTokens.Contains(tok))
                    {
                        count++;
                    }
                }

                return count;
            }

            private List<KeyValuePair<string, string>> FindNarratorEvidenceFields(
                string narratorRaw,
                string authorName,
                IDictionary<string, List<string>> tags)
            {
                var evidence = new List<KeyValuePair<string, string>>();

                if (string.IsNullOrWhiteSpace(narratorRaw) || tags == null || tags.Count == 0)
                {
                    return evidence;
                }

                const int maxValueLength = 400;

                var narrator = NormalizePersonNameForMatch(narratorRaw);
                if (string.IsNullOrWhiteSpace(narrator))
                {
                    return evidence;
                }

                var narratorNoSpace = narrator.Replace(" ", string.Empty);
                var narratorWords = narrator.Split(' ').Where(w => w.Length > 1).ToList();
                var normalizedAuthor = NormalizePersonNameForMatch(authorName);
                var selfNarrated = !string.IsNullOrWhiteSpace(normalizedAuthor) &&
                                   (string.Equals(narrator, normalizedAuthor, StringComparison.Ordinal) ||
                                    IsAuthorAsNarrator(narratorRaw, authorName));

                foreach (var kv in tags)
                {
                    if (kv.Value == null || kv.Value.Count == 0 || string.IsNullOrWhiteSpace(kv.Key))
                    {
                        continue;
                    }

                    if (IsExcludedFromMatching(kv.Key))
                    {
                        continue;
                    }

                    foreach (var rawValue in kv.Value)
                    {
                        if (string.IsNullOrWhiteSpace(rawValue) || rawValue.Length > maxValueLength)
                        {
                            continue;
                        }

                        var haystack = NormalizePersonNameForMatch(rawValue);
                        if (string.IsNullOrWhiteSpace(haystack))
                        {
                            continue;
                        }

                        var haystackNoSpace = haystack.Replace(" ", string.Empty);
                        var matchesNarrator =
                            haystackNoSpace.Contains(narratorNoSpace, StringComparison.Ordinal) ||
                            (narratorWords.Count >= 2 && narratorWords.All(w => haystackNoSpace.Contains(w, StringComparison.Ordinal)));

                        if (!matchesNarrator)
                        {
                            continue;
                        }

                        if (!evidence.Any(existing =>
                                string.Equals(existing.Key, kv.Key, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(existing.Value, rawValue, StringComparison.Ordinal)))
                        {
                            evidence.Add(new KeyValuePair<string, string>(kv.Key, rawValue));
                        }
                    }
                }

                if (!selfNarrated)
                {
                    return evidence;
                }

                var distinctFieldCount = evidence
                    .Select(item => item.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                return distinctFieldCount >= 2
                    ? evidence
                    : new List<KeyValuePair<string, string>>();
            }

            private static string BuildNarratorIdentityKey(string narratorNames)
            {
                if (string.IsNullOrWhiteSpace(narratorNames))
                {
                    return string.Empty;
                }

                return narratorNames
                    .Replace(" & ", ", ", StringComparison.OrdinalIgnoreCase)
                    .Replace(" and ", ", ", StringComparison.OrdinalIgnoreCase)
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizePersonNameForMatch)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Aggregate(string.Empty, (current, next) => string.IsNullOrEmpty(current) ? next : $"{current}|{next}");
            }

            private bool IsAuthorAsNarrator(string narrator, string authorName)
            {
                if (string.IsNullOrWhiteSpace(narrator) || string.IsNullOrWhiteSpace(authorName))
                {
                    return false;
                }

                var normalizedNarrator = NormalizePersonNameForMatch(narrator);
                var normalizedAuthor = NormalizePersonNameForMatch(authorName);
                if (!string.IsNullOrWhiteSpace(normalizedNarrator) &&
                    string.Equals(normalizedNarrator, normalizedAuthor, StringComparison.Ordinal))
                {
                    return true;
                }

                // Drop initials to avoid false mismatches (e.g., "J.K. Rowling" → ["rowling"]).
                var narratorTokens = TokenizeForLeftoverGate(narrator)
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var authorTokens = TokenizeForLeftoverGate(authorName)
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (narratorTokens.Count == 0 || authorTokens.Count == 0)
                {
                    return false;
                }

                var overlap = authorTokens.Count(t => narratorTokens.Contains(t));
                if (authorTokens.Count == 1)
                {
                    return overlap == 1;
                }

                // Require full match for 2-token names, majority match for longer names.
                var required = authorTokens.Count == 2 ? 2 : Math.Max(2, (int)Math.Ceiling(authorTokens.Count * 0.6));
                return overlap >= required;
            }

            /// <summary>
            /// Run a lightweight FTS query, get candidate authors from results, check if any appear
            /// in tag values. If yes → tags contain author evidence → disable path fallback.
            /// Mirrors Python's any_fts_author_in_tags().
            /// </summary>
            private bool AnyFtsAuthorInTags(
                List<string> tokens,
                BookMediaType mediaType,
                Dictionary<string, List<string>> allTags)
            {
                if (tokens == null || tokens.Count == 0 || allTags == null || allTags.Count == 0)
                {
                    return false;
                }

                    var tagValuesLower = new List<string>();
                    foreach (var kv in allTags)
                    {
                        if (IsExcludedFromMatching(kv.Key)) continue;
                        if (kv.Value == null) continue;
                        foreach (var v in kv.Value)
                        {
                            if (!string.IsNullOrWhiteSpace(v) && v.Length <= 400)
                            {
                                tagValuesLower.Add(v.Trim().ToLowerInvariant());
                            }
                        }
                    }

                if (tagValuesLower.Count == 0)
                {
                    return false;
                }

                try
                {
                    // Quick FTS to get candidate authors — use title columns only, limit 20
                    var ftsResults = _editionFtsRepository.SearchWithTwoStep(null, tokens, mediaType, limit: 20);
                    if (ftsResults == null || ftsResults.Count == 0)
                    {
                        return false;
                    }

                    // Get distinct author names
                    var candidateAuthors = ftsResults
                        .Select(r => r.AuthorName)
                        .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 3)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Check if any candidate author appears in tag values
                    foreach (var authorName in candidateAuthors)
                    {
                        var authorLower = authorName.ToLowerInvariant();
                        foreach (var tv in tagValuesLower)
                        {
                            if (tv.Contains(authorLower, StringComparison.Ordinal))
                            {
                                _logger.Debug("[HOLY-GRAIL] FTS candidate author '{0}' found in tags", authorName);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[HOLY-GRAIL] AnyFtsAuthorInTags failed");
                }

                return false;
            }

            private bool HasSeriesTitlePositionContradiction(
                EditionFtsMatch candidate,
                IDictionary<int, Book> booksById,
                IReadOnlyList<EditionTitleEvidence> titleEvidence,
                SeriesPositionEvidence seriesPositionEvidence)
            {
                if (candidate == null ||
                    string.IsNullOrWhiteSpace(candidate.EditionTitle) ||
                    titleEvidence == null ||
                    titleEvidence.Count == 0 ||
                    seriesPositionEvidence == null ||
                    !seriesPositionEvidence.HasSignal)
                {
                    return false;
                }

                var book = TryGetBookCached(candidate.BookId, booksById);
                if (book == null)
                {
                    return false;
                }

                var titleTokens = TokenizeForLeftoverGate(candidate.EditionTitle)
                    .Where(t => !string.IsNullOrWhiteSpace(t) &&
                                t.Length > 1 &&
                                t.Any(char.IsLetter) &&
                                !HolyGrailLeftoverHardNoiseTokens.Contains(t) &&
                                !HolyGrailLeftoverStructuralTokens.Contains(t))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (titleTokens.Count == 0 ||
                    !GetCandidateSeriesContexts(book).Any(context => titleTokens.SetEquals(context.NameTokens)))
                {
                    return false;
                }

                return titleEvidence.All(evidence =>
                    seriesPositionEvidence.GetField(evidence?.FieldName)?.Disposition == SeriesPositionDisposition.Mismatch);
            }

                private static readonly Regex YearRegex = new Regex(@"\b(1[0-9]{3}|20[0-9]{2})\b", RegexOptions.Compiled);

            private static int? TryExtractYearFromTags(Dictionary<string, List<string>> tags)
            {
                if (tags == null || tags.Count == 0)
                {
                    return null;
                }

                int? TryExtract(IEnumerable<string> values)
                {
                    if (values == null)
                    {
                        return null;
                    }

                    foreach (var v in values)
                    {
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        foreach (Match m in YearRegex.Matches(v))
                        {
                            if (int.TryParse(m.Value, out var y) && y >= 1000 && y <= 2100)
                            {
                                return y;
                            }
                        }
                    }

                    return null;
                }

                // Preferred keys first (when present).
                var preferredKeys = new[] { "YEAR", "DATE", "ORIGINALYEAR", "ORIGINALDATE", "ORIGINALRELEASEYEAR", "RELEASEDATE" };
                foreach (var key in preferredKeys)
                {
                    if (tags.TryGetValue(key, out var vals))
                    {
                        var y = TryExtract(vals);
                        if (y.HasValue) return y;
                    }
                }

                // Fallback: scan all values.
                foreach (var kv in tags)
                {
                    var y = TryExtract(kv.Value);
                    if (y.HasValue) return y;
                }

                return null;
            }

                private static int? ResolveGroupedMatchDurationSeconds(
                    DiscoveredFileWithMetadata representative,
                    int? totalDurationSeconds,
                    bool isAudiobook)
                {
                    if (!isAudiobook)
                    {
                        return representative?.DurationSeconds;
                    }

                    return totalDurationSeconds ?? representative?.DurationSeconds;
                }

                private void TryEnrichAudiobookTagsWithTotalDuration(
                    Dictionary<string, List<string>> tags,
                    int? totalSeconds,
                    int fileCount,
                    string correlationId)
                {
                    if (tags == null ||
                        fileCount <= 0)
                    {
                        return;
                    }

                    if (!totalSeconds.HasValue)
                    {
                        _logger.Debug("{0}[DURATION] Could not compute full-book duration for group (files={1})", correlationId, fileCount);
                        return;
                    }

                    tags["TOTALDURATION"] = new List<string> { totalSeconds.Value.ToString(CultureInfo.InvariantCulture) };
                    _logger.Debug("{0}[DURATION] Computed TOTALDURATION={1}s from {2} files", correlationId, totalSeconds.Value, fileCount);
                }

                private int? ResolveTotalDurationSeconds(IReadOnlyList<DiscoveredFileWithMetadata> files)
                {
                    var totalSeconds = TryCalculateTotalDurationSeconds(files);
                    if (totalSeconds.HasValue || _mediaInfoExtractor == null || files == null || files.Count == 0)
                    {
                        return totalSeconds;
                    }

                    long resolvedTotalSeconds = 0;
                    foreach (var file in files)
                    {
                        var seconds = file?.DurationSeconds;
                        if ((!seconds.HasValue || seconds.Value <= 0) &&
                            !string.IsNullOrWhiteSpace(file?.Path))
                        {
                            seconds = MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(file.Path));
                        }

                        if (!seconds.HasValue || seconds.Value <= 0)
                        {
                            return null;
                        }

                        resolvedTotalSeconds += seconds.Value;
                    }

                    return NormalizeTotalDurationSeconds(resolvedTotalSeconds);
                }

                private static int? TryCalculateTotalDurationSeconds(IReadOnlyList<DiscoveredFileWithMetadata> files)
                {
                    if (files == null || files.Count == 0)
                    {
                        return null;
                    }

                    var durationIdentityGroups = files
                        .Select(file => new { File = file, TagSignature = BuildExactTagSignature(file?.AllTags) })
                        .Where(item => item.File != null && item.File.Size > 0 &&
                                       item.File.DurationSeconds.GetValueOrDefault() > 0 && item.TagSignature != null)
                        .GroupBy(item => new { item.File.Size, DurationSeconds = item.File.DurationSeconds.Value, item.TagSignature })
                        .ToList();
                    var repeatedCopyCount = durationIdentityGroups.Count > 1
                        ? durationIdentityGroups[0].Count()
                        : 1;
                    var isRepeatedMultipartSet = repeatedCopyCount > 1 &&
                                                 durationIdentityGroups.Sum(group => group.Count()) == files.Count &&
                                                 durationIdentityGroups.All(group => group.Count() == repeatedCopyCount);

                    long totalSeconds = 0;
                    foreach (var f in files)
                    {
                        if (f?.DurationSeconds is not int s || s <= 0)
                        {
                            return null;
                        }

                        totalSeconds += s;
                    }

                    if (isRepeatedMultipartSet)
                    {
                        totalSeconds /= repeatedCopyCount;
                    }

                    return NormalizeTotalDurationSeconds(totalSeconds);
                }

                private static string BuildExactTagSignature(IDictionary<string, List<string>> tags)
                {
                    if (tags == null || tags.Count == 0)
                    {
                        return null;
                    }

                    var parts = tags
                        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null &&
                                       !TagExclusionPolicy.IsExcludedFromMatching(pair.Key))
                        .SelectMany(pair => pair.Value
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => new { Field = pair.Key.Trim().ToLowerInvariant(), Value = value }))
                        .OrderBy(part => part.Field, StringComparer.Ordinal)
                        .ThenBy(part => part.Value, StringComparer.Ordinal)
                        .Select(part => $"{part.Field.Length.ToString(CultureInfo.InvariantCulture)}:{part.Field}{part.Value.Length.ToString(CultureInfo.InvariantCulture)}:{part.Value}")
                        .ToList();

                    return parts.Count > 0
                        ? string.Join("\u001D", parts)
                        : null;
                }

                private static int? NormalizeTotalDurationSeconds(long totalSeconds)
                {
                    if (totalSeconds <= 0)
                    {
                        return null;
                    }

                    var seconds = totalSeconds > int.MaxValue ? int.MaxValue : (int)totalSeconds;
                    if (seconds < 30 || seconds > 432000)
                    {
                        return null;
                    }

                    return seconds;
                }

        private IReadOnlyList<EditionTitleEvidence> GetSeriesExplainableEditionTitleEvidence(
            EditionFtsMatch candidate,
            IDictionary<string, List<string>> allTags,
            IDictionary<int, Book> booksById,
            Func<EditionTitleEvidence, bool> evidenceIsEligible = null)
        {
            if (candidate == null ||
                string.IsNullOrWhiteSpace(candidate.EditionTitle) ||
                allTags == null ||
                allTags.Count == 0 ||
                _containmentValidator == null ||
                _bookService == null)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            bool HasMeaningfulTokens(string title)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return false;
                }

                foreach (var t in TokenizeForLeftoverGate(title))
                {
                    if (string.IsNullOrWhiteSpace(t))
                    {
                        continue;
                    }

                    if (HolyGrailLeftoverHardNoiseTokens.Contains(t) || HolyGrailLeftoverStructuralTokens.Contains(t))
                    {
                        continue;
                    }

                    if (t.All(char.IsDigit))
                    {
                        return true;
                    }

                    if (t.Length > 2)
                    {
                        return true;
                    }
                }

                return false;
            }

            static bool IsSeriesLabelToken(string token)
            {
                return string.Equals(token, "series", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(token, "saga", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(token, "chronicles", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(token, "collection", StringComparison.OrdinalIgnoreCase);
            }

            Book book;
            try
            {
                book = TryGetBookCached(candidate.BookId, booksById);
            }
            catch
            {
                book = null;
            }

            if (book == null)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            // Gather series name strings (for prefix stripping)
            var seriesNames = new List<string>();
            try
            {
                if (book.SeriesLinks?.Any() == true)
                {
                    foreach (var link in book.SeriesLinks)
                    {
                        var title = link?.Series?.Value?.Title;
                        if (TryNormalizeSeriesName(title, out var normalized))
                        {
                            seriesNames.Add(normalized);
                        }
                    }
                }

                if (TryNormalizeSeriesName(book.SeriesName, out var normalizedBookSeries))
                {
                    seriesNames.Add(normalizedBookSeries);
                }
            }
            catch
            {
                // best-effort only
            }

            seriesNames = seriesNames
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (seriesNames.Count == 0)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            // Token sets for suffix stripping
            var seriesTokens = GetCandidateSeriesNameTokens(book);
            var positionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var positions = GetCandidateSeriesPositions(book);
                foreach (var pos in positions)
                {
                    if (string.IsNullOrWhiteSpace(pos)) continue;

                    foreach (var tok in TokenizeForLeftoverGate(pos))
                    {
                        if (!string.IsNullOrWhiteSpace(tok))
                        {
                            positionTokens.Add(tok);
                        }
                    }

                    foreach (var token in SeriesPositionTokenHelper.GetPositionTokens(pos))
                    {
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            positionTokens.Add(token);
                        }
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            var alternatives = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddAlternative(string alt)
            {
                if (string.IsNullOrWhiteSpace(alt))
                {
                    return;
                }

                alt = alt.Trim();
                if (!HasMeaningfulTokens(alt))
                {
                    return;
                }

                if (seen.Add(alt))
                {
                    alternatives.Add(alt);
                }
            }

            // 1) Suffix stripping: remove trailing series/position metadata tokens.
            // Allow pure series tails ("Black Sun Rising: Coldfire Trilogy") as well as
            // structural/position tails ("Dawn of Forever: Jack & Jill Series, Book 3").
            try
            {
                var titleTokens = TokenizeForLeftoverGate(candidate.EditionTitle)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (titleTokens.Count > 0)
                {
                    var idx = titleTokens.Count - 1;
                    var removedStructuralOrPosition = false;
                    var removedSeriesMetadata = false;

                    while (idx >= 0)
                    {
                        var tok = titleTokens[idx];

                        if (HolyGrailLeftoverStructuralTokens.Contains(tok))
                        {
                            removedStructuralOrPosition = true;
                            idx--;
                            continue;
                        }

                        if (positionTokens.Contains(tok))
                        {
                            removedStructuralOrPosition = true;
                            idx--;
                            continue;
                        }

                        if (seriesTokens.Contains(tok))
                        {
                            removedSeriesMetadata = true;
                            idx--;
                            continue;
                        }

                        if (IsSeriesLabelToken(tok))
                        {
                            removedSeriesMetadata = true;
                            idx--;
                            continue;
                        }

                        break;
                    }

                    var remainingCount = idx + 1;
                    if ((removedStructuralOrPosition || removedSeriesMetadata) &&
                        remainingCount > 0 &&
                        remainingCount < titleTokens.Count)
                    {
                        AddAlternative(string.Join(" ", titleTokens.Take(remainingCount)));
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            // 2) Prefix stripping: remove "Series: " style prefixes using explicit delimiters.
            try
            {
                var rawTitle = candidate.EditionTitle.Trim();
                foreach (var seriesName in seriesNames.OrderByDescending(s => s.Length))
                {
                    foreach (var sep in new[] { ":", " - ", " – ", " — " })
                    {
                        var prefix = seriesName + sep;
                        if (rawTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var stripped = rawTitle.Substring(prefix.Length).Trim();
                            AddAlternative(stripped);
                        }
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            if (alternatives.Count == 0)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            // Try the most specific alternatives first (more meaningful tokens retained).
            alternatives = alternatives
                .OrderByDescending(a => TokenizeForLeftoverGate(a)
                    .Count(t => !string.IsNullOrWhiteSpace(t) &&
                                t.Length > 2 &&
                                !HolyGrailLeftoverHardNoiseTokens.Contains(t) &&
                                !HolyGrailLeftoverStructuralTokens.Contains(t)))
                .ToList();

            foreach (var alt in alternatives)
            {
                var evidence = _containmentValidator.GetEditionTitleEvidence(alt, allTags);
                if (evidence != null && evidence.Count > 0)
                {
                    var eligibleEvidence = evidenceIsEligible == null
                        ? evidence
                        : evidence.Where(evidenceIsEligible).ToList();
                    if (eligibleEvidence.Count > 0)
                    {
                        return eligibleEvidence;
                    }
                }
            }

            return Array.Empty<EditionTitleEvidence>();
        }


        private bool SeriesNameMatches(string desiredSeriesName, string candidateSeriesName)
        {
            if (string.IsNullOrWhiteSpace(desiredSeriesName) || string.IsNullOrWhiteSpace(candidateSeriesName))
            {
                return false;
            }

            var desiredTokens = TokenizeForLeftoverGate(desiredSeriesName).Where(t => !SeriesNameNoiseTokens.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidateTokens = TokenizeForLeftoverGate(candidateSeriesName).Where(t => !SeriesNameNoiseTokens.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (desiredTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return false;
            }

            return desiredTokens.IsSubsetOf(candidateTokens) || candidateTokens.IsSubsetOf(desiredTokens);
        }

        private bool TryNormalizeSeriesName(string raw, out string seriesName)
        {
            seriesName = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();

            // Exclude obvious identifiers.
            if (AsinRegex.IsMatch(raw))
            {
                return false;
            }

            if (raw.All(char.IsDigit))
            {
                return false;
            }

            var tokens = TokenizeForLeftoverGate(raw)
                .Where(t => !SeriesNameNoiseTokens.Contains(t))
                .Where(t => t.Length > 2)
                .ToList();

            if (tokens.Count == 0)
            {
                return false;
            }

            seriesName = raw;
            return true;
        }

        private string NormalizeSeriesPosition(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            raw = raw.Trim();

            raw = raw
                .Replace('–', '-')
                .Replace('—', '-')
                .Replace('−', '-');

            if (Regex.IsMatch(raw, @"^[IVXLCDM]+$", RegexOptions.IgnoreCase))
            {
                if (TryParseRomanNumeral(raw, out var romanValue))
                {
                    return NormalizeSeriesPosition(romanValue.ToString(CultureInfo.InvariantCulture));
                }
                return null;
            }

            raw = Regex.Replace(raw, @"\s*-\s*", "-");

            var parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var normalizedParts = new List<string>();
            foreach (var part in parts)
            {
                var p = part.Trim();
                if (!Regex.IsMatch(p, @"^\d+(\.\d+)?$"))
                {
                    return null;
                }

                if (!decimal.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                {
                    return null;
                }

                if (value <= 0 || value > 9999m)
                {
                    return null;
                }

                // Avoid year-like tokens masquerading as positions.
                if (decimal.Truncate(value) == value && p.Length == 4 && value >= 1900m && value <= 2100m)
                {
                    return null;
                }

                string normalized;
                if (decimal.Truncate(value) == value)
                {
                    normalized = decimal.Truncate(value).ToString("0", CultureInfo.InvariantCulture);
                }
                else
                {
                    normalized = value.ToString("0.#############################", CultureInfo.InvariantCulture);
                }

                normalizedParts.Add(normalized);
            }

            return string.Join("-", normalizedParts);
        }

        private bool TryParseRomanNumeral(string raw, out int value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim().ToUpperInvariant();

            var map = new Dictionary<char, int>
            {
                ['I'] = 1,
                ['V'] = 5,
                ['X'] = 10,
                ['L'] = 50,
                ['C'] = 100,
                ['D'] = 500,
                ['M'] = 1000
            };

            var total = 0;
            var prev = 0;
            foreach (var ch in raw.Reverse())
            {
                if (!map.TryGetValue(ch, out var current))
                {
                    return false;
                }

                if (current < prev)
                {
                    total -= current;
                }
                else
                {
                    total += current;
                    prev = current;
                }
            }

            if (total <= 0)
            {
                return false;
            }

            value = total;
            return true;
        }

        /// <summary>
        /// Candidate-relative values from one physical tag key that exists on
        /// every member of a multipart group.
        /// Raw values remain member-specific for exact identity proof.
        /// </summary>
        private sealed class GroupPhysicalFieldValue
        {
            public string OriginalValue { get; set; }
            public string ResidualValue { get; set; }
        }

        private sealed class GroupPhysicalField
        {
            public string FieldName { get; set; }
            public List<List<GroupPhysicalFieldValue>> MemberValues { get; set; } = new();
        }

        private sealed class GroupIdentitySpanProof
        {
            public string FieldName { get; set; }
            public string ObservedSpanKey { get; set; }
            public List<string> ObservedValues { get; set; } = new();
        }

        private sealed class StagedGroupFieldRepresentation
        {
            public string WorkKey { get; set; }
            public EditionFtsMatch Candidate { get; set; }
            public string Phrase { get; set; }
            public List<HashSet<int>> ConsumedMemberIndexes { get; set; } = new();
            public List<string> ObservedValues { get; set; } = new();
            public string ObservedSpanKey { get; set; }
            public int Specificity { get; set; }

            public int ConsumedIndexCount => ConsumedMemberIndexes.Sum(indexes => indexes?.Count ?? 0);
        }

        private List<GroupPhysicalField> BuildCandidateRelativeGroupFields(
            IReadOnlyList<Dictionary<string, List<string>>> groupMemberTags,
            IReadOnlyCollection<string> authorNamesToConsume)
        {
            if (groupMemberTags == null || groupMemberTags.Count < 2)
            {
                return new List<GroupPhysicalField>();
            }

            var authorNames = (authorNamesToConsume ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name.Length)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var members = new List<Dictionary<string, List<GroupPhysicalFieldValue>>>();

            string ConsumeAuthors(string value)
            {
                var residual = value;
                foreach (var authorName in authorNames)
                {
                    while (TryFindStagedIdentitySpan(residual, authorName, out var start, out var end))
                    {
                        var before = residual;
                        residual = Regex.Replace(
                                before.Remove(start, end - start).Insert(start, " "),
                                @"\s+",
                                " ")
                            .Trim(' ', ',', ';', '-', '_', '&');
                        if (string.Equals(before, residual, StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }

                return residual;
            }

            foreach (var memberTags in groupMemberTags)
            {
                if (memberTags == null || memberTags.Count == 0)
                {
                    return new List<GroupPhysicalField>();
                }

                var physicalFields = new Dictionary<string, List<GroupPhysicalFieldValue>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in memberTags)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) ||
                        IsExcludedFromMatching(pair.Key) ||
                        pair.Value == null)
                    {
                        continue;
                    }

                    foreach (var rawValue in pair.Value)
                    {
                        var originalValue = rawValue?.Trim();
                        if (string.IsNullOrWhiteSpace(originalValue) || originalValue.Length >= 5000)
                        {
                            continue;
                        }

                        if (TagCanonicalizer.IsCanonicalAliasBackedByRawSource(pair.Key, originalValue, memberTags))
                        {
                            continue;
                        }

                        var residualValue = ConsumeAuthors(originalValue);
                        if (TokenizeForLeftoverGateSequence(residualValue).Count == 0)
                        {
                            continue;
                        }

                        if (!physicalFields.TryGetValue(pair.Key, out var values))
                        {
                            values = new List<GroupPhysicalFieldValue>();
                            physicalFields[pair.Key] = values;
                        }

                        if (!values.Any(value => string.Equals(value.OriginalValue, originalValue, StringComparison.Ordinal)))
                        {
                            values.Add(new GroupPhysicalFieldValue
                            {
                                OriginalValue = originalValue,
                                ResidualValue = residualValue
                            });
                        }
                    }
                }

                members.Add(physicalFields);
            }

            if (members.Count != groupMemberTags.Count || members.Count == 0)
            {
                return new List<GroupPhysicalField>();
            }

            return members[0].Keys
                .Where(fieldName => members.All(member =>
                    member.TryGetValue(fieldName, out var values) && values.Count > 0))
                .OrderBy(fieldName => fieldName, StringComparer.OrdinalIgnoreCase)
                .Select(fieldName => new GroupPhysicalField
                {
                    FieldName = fieldName,
                    MemberValues = members.Select(member => member[fieldName]).ToList()
                })
                .ToList();
        }

        private bool TryBuildExactGroupIdentitySpanProof(
            string expected,
            IReadOnlyList<Dictionary<string, List<string>>> groupMemberTags,
            out GroupIdentitySpanProof proof)
        {
            proof = null;
            if (string.IsNullOrWhiteSpace(expected) ||
                groupMemberTags == null ||
                groupMemberTags.Count < 2 ||
                groupMemberTags.Any(tags => tags == null || tags.Count == 0))
            {
                return false;
            }

            var commonFieldNames = groupMemberTags[0].Keys
                .Where(field => !string.IsNullOrWhiteSpace(field) && !IsExcludedFromMatching(field))
                .Where(field => groupMemberTags.All(tags => tags.ContainsKey(field)))
                .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var fieldName in commonFieldNames)
            {
                var observedValues = new List<string>();
                string observedSpanKey = null;
                var fieldProvesEveryMember = true;

                foreach (var memberTags in groupMemberTags)
                {
                    var matchingValues = new List<(string Value, string SpanKey)>();
                    foreach (var rawValue in memberTags[fieldName] ?? new List<string>())
                    {
                        if (string.IsNullOrWhiteSpace(rawValue) ||
                            rawValue.Length >= 5000 ||
                            TagCanonicalizer.IsCanonicalAliasBackedByRawSource(fieldName, rawValue, memberTags) ||
                            !TryFindStagedIdentitySpan(rawValue, expected, out var start, out var end))
                        {
                            continue;
                        }

                        var spanKey = string.Join(
                            "\u001f",
                            GetNormalizedWordSpans(rawValue.Substring(start, end - start)).Select(span => span.Token));
                        if (!string.IsNullOrWhiteSpace(spanKey))
                        {
                            matchingValues.Add((rawValue, spanKey));
                        }
                    }

                    var selected = matchingValues
                        .Where(value => observedSpanKey == null ||
                                        string.Equals(value.SpanKey, observedSpanKey, StringComparison.Ordinal))
                        .OrderBy(value => value.Value, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(selected.Value))
                    {
                        fieldProvesEveryMember = false;
                        break;
                    }

                    observedSpanKey ??= selected.SpanKey;
                    observedValues.Add(selected.Value);
                }

                if (fieldProvesEveryMember && observedValues.Count == groupMemberTags.Count)
                {
                    proof = new GroupIdentitySpanProof
                    {
                        FieldName = fieldName,
                        ObservedSpanKey = observedSpanKey,
                        ObservedValues = observedValues
                    };
                    return true;
                }
            }

            return false;
        }

        private bool TryAlignStagedTitlePhrase(
            string phrase,
            string observedValue,
            out HashSet<int> consumedIndexes,
            out string observedSpanKey,
            out int specificity)
        {
            consumedIndexes = null;
            observedSpanKey = null;
            specificity = 0;
            var phraseTokens = TokenizeForLeftoverGateSequence(phrase);
            var fieldTokens = TokenizeForLeftoverGateSequence(observedValue);
            if (phraseTokens.Count == 0 ||
                fieldTokens.Count == 0 ||
                !TitleTokenAlignment.TryAlignStructural(
                    phraseTokens,
                    fieldTokens,
                    allowNearExact: false,
                    allowTransposition: false,
                    out var alignment))
            {
                return false;
            }

            consumedIndexes = alignment.ConsumedFieldIndexes.ToHashSet();
            var first = consumedIndexes.Min();
            var last = consumedIndexes.Max();
            observedSpanKey = string.Join("\u001f", fieldTokens.Skip(first).Take(last - first + 1));
            specificity = phraseTokens.Count(token => !TitleTokenAlignment.IsStructuralGlueToken(token));
            return consumedIndexes.Count > 0 &&
                   !string.IsNullOrWhiteSpace(observedSpanKey) &&
                   specificity > 0;
        }

        private bool TryRepresentPhraseAcrossGroup(
            string phrase,
            GroupPhysicalField field,
            out List<HashSet<int>> consumedMemberIndexes,
            out List<string> observedValues,
            out string observedSpanKey,
            out int specificity)
        {
            consumedMemberIndexes = new List<HashSet<int>>();
            observedValues = new List<string>();
            observedSpanKey = null;
            specificity = 0;
            if (field?.MemberValues == null || field.MemberValues.Count < 2)
            {
                return false;
            }

            foreach (var memberValues in field.MemberValues)
            {
                GroupPhysicalFieldValue bestValue = null;
                HashSet<int> bestConsumed = null;
                string bestObservedSpanKey = null;
                var bestSpecificity = 0;
                foreach (var value in memberValues ?? new List<GroupPhysicalFieldValue>())
                {
                    if (value == null ||
                        !TryAlignStagedTitlePhrase(
                            phrase,
                            value.ResidualValue,
                            out var consumed,
                            out var valueObservedSpanKey,
                            out var valueSpecificity))
                    {
                        continue;
                    }

                    if (bestConsumed == null ||
                        consumed.Count > bestConsumed.Count ||
                        (consumed.Count == bestConsumed.Count && valueSpecificity > bestSpecificity))
                    {
                        bestValue = value;
                        bestConsumed = consumed;
                        bestObservedSpanKey = valueObservedSpanKey;
                        bestSpecificity = valueSpecificity;
                    }
                }

                if (bestValue == null || bestConsumed == null)
                {
                    consumedMemberIndexes.Clear();
                    observedValues.Clear();
                    observedSpanKey = null;
                    specificity = 0;
                    return false;
                }

                // Multipart identity is the physical key plus the exact normalized observed
                // span. Candidate matching may omit structural glue, but grouping may not:
                // "Goblet of Fire" and "Goblet of the Fire" are separate units even when
                // both can independently prove the same catalog title.
                if (observedSpanKey != null &&
                    !string.Equals(observedSpanKey, bestObservedSpanKey, StringComparison.Ordinal))
                {
                    consumedMemberIndexes.Clear();
                    observedValues.Clear();
                    observedSpanKey = null;
                    specificity = 0;
                    return false;
                }

                consumedMemberIndexes.Add(bestConsumed);
                observedValues.Add(bestValue.OriginalValue);
                observedSpanKey ??= bestObservedSpanKey;
                specificity = Math.Max(specificity, bestSpecificity);
            }

            return consumedMemberIndexes.Count == field.MemberValues.Count && specificity > 0;
        }

        private IReadOnlyList<EditionTitleEvidence> GetCandidateRelativeGroupTitleEvidence(
            string title,
            IReadOnlyList<GroupPhysicalField> groupFields,
            ISet<string> allowedFieldNames)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                groupFields == null ||
                groupFields.Count == 0 ||
                allowedFieldNames == null ||
                allowedFieldNames.Count == 0)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            var evidence = new List<EditionTitleEvidence>();
            foreach (var field in groupFields.Where(field =>
                         field != null && allowedFieldNames.Contains(field.FieldName)))
            {
                if (!TryRepresentPhraseAcrossGroup(
                        title,
                        field,
                        out _,
                        out var observedValues,
                        out _,
                        out _))
                {
                    continue;
                }

                foreach (var observedValue in observedValues.Distinct(StringComparer.Ordinal))
                {
                    evidence.Add(new EditionTitleEvidence(
                        field.FieldName,
                        observedValue,
                        title));
                }
            }

            return evidence;
        }

        private sealed class StagedFieldRepresentation
        {
            public string WorkKey { get; set; }
            public EditionFtsMatch Candidate { get; set; }
            public string Phrase { get; set; }
            public HashSet<int> ConsumedFieldIndexes { get; set; } = new();
            public int Specificity { get; set; }
        }

        private sealed class StagedWorkFieldVotes
        {
            public string WorkKey { get; set; }
            public HashSet<int> BookIds { get; set; } = new();
            public List<EditionFtsMatch> Candidates { get; set; } = new();
            public HashSet<string> TitleFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> GroupTitleFieldNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> DirectBookTitleFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> DetailFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public int TitleSpecificity { get; set; }
            public bool DurationCompatible { get; set; }
            public bool DurationConflict { get; set; }
            public bool YearExact { get; set; }
            public bool BookTitleIsOwnedSeriesTitle { get; set; }

            public int CorroborationTier =>
                (DetailFields.Count > 0 ? 2 : 0) +
                (DurationCompatible || YearExact ? 1 : 0);

            public int FieldCount => TitleFields.Count + DetailFields.Count;

            public EditionFtsMatch Representative => Candidates
                .OrderBy(candidate => string.IsNullOrWhiteSpace(candidate.ForeignEditionId) ? 1 : 0)
                .ThenBy(candidate => candidate.ForeignEditionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.EditionId)
                .FirstOrDefault();
        }

        private sealed class StagedBookDecision
        {
            public string WorkKey { get; set; }
            public HashSet<int> BookIds { get; set; } = new();
            public HashSet<string> GroupTitleFieldNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public EditionFtsMatch Representative { get; set; }
        }

        private StagedBookDecision SelectStagedBookByFieldRepresentation(
            IReadOnlyList<EditionFtsMatch> candidates,
            IReadOnlyList<EditionFtsFieldQuery> fieldQueries,
            BookMediaType mediaType,
            IReadOnlyList<GroupPhysicalField> groupFields,
            IDictionary<int, Book> booksById,
            int? fileDurationSeconds,
            int? fileYear,
            bool durationComparable,
            string phase,
            string filePath)
        {
            if (candidates == null || candidates.Count == 0 ||
                fieldQueries == null || fieldQueries.Count == 0)
            {
                return null;
            }

            var keyedCandidates = candidates
                .Where(candidate => candidate != null)
                .Select(candidate => new
                {
                    Candidate = candidate,
                    WorkKey = GetProviderOccurrenceWorkKey(candidate, booksById)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.WorkKey))
                .ToList();
            if (keyedCandidates.Count == 0)
            {
                RecordTrace(
                    "fts_stage2_unclear",
                    phase,
                    reason: "STAGE2_PROVIDER_IDENTITY_MISSING",
                    detail: "No author-gated candidate had stable provider Book identity; deep candidate analysis is required.",
                    filePath: filePath);
                return null;
            }

            bool BookTitleIsOwnedSeriesTitle(EditionFtsMatch candidate)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.BookTitle))
                {
                    return false;
                }

                var book = TryGetBookCached(candidate.BookId, booksById);
                if (book == null)
                {
                    return false;
                }

                var titleTokens = TokenizeForLeftoverGateSequence(candidate.BookTitle);
                if (titleTokens.Count == 0)
                {
                    return false;
                }

                var ownedSeriesNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(book.SeriesName))
                {
                    ownedSeriesNames.Add(book.SeriesName);
                }

                if (book.SeriesLinks?.Any() == true)
                {
                    ownedSeriesNames.AddRange(book.SeriesLinks
                        .Select(link => link?.Series?.Value?.Title)
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                }

                return ownedSeriesNames.Any(seriesName =>
                {
                    var seriesTokens = TokenizeForLeftoverGateSequence(
                        StripTrailingParentheticals(seriesName));
                    return seriesTokens.Count > 0 &&
                           titleTokens.SequenceEqual(seriesTokens, StringComparer.OrdinalIgnoreCase);
                });
            }

            var votesByWork = keyedCandidates
                .GroupBy(item => item.WorkKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new StagedWorkFieldVotes
                    {
                        WorkKey = group.Key,
                        BookIds = group.Select(item => item.Candidate.BookId).ToHashSet(),
                        Candidates = group.Select(item => item.Candidate).ToList(),
                        BookTitleIsOwnedSeriesTitle = group.Any(item => BookTitleIsOwnedSeriesTitle(item.Candidate))
                    },
                    StringComparer.OrdinalIgnoreCase);
            var titleReservedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seriesTitleDominatedWorks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            EditionFtsFieldHit FindFieldHit(EditionFtsMatch candidate, string fieldKey)
            {
                return candidate.Stage2FieldHits?
                    .FirstOrDefault(hit =>
                        hit != null &&
                        string.Equals(hit.FieldKey, fieldKey, StringComparison.OrdinalIgnoreCase));
            }

            IReadOnlyList<string> CandidateTitlePhrases(EditionFtsMatch candidate)
            {
                // Book title proves the provider Book; edition projections can explain more of
                // that same field. Every individual phrase still has to align in full.
                return new[]
                    {
                        candidate?.BookTitle,
                        candidate?.MatchingTitle,
                        candidate?.EditionTitle
                    }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            bool TryRepresentPhrase(
                string phrase,
                IReadOnlyList<string> fieldTokens,
                out HashSet<int> consumedIndexes,
                out int specificity)
            {
                consumedIndexes = null;
                specificity = 0;
                var phraseTokens = TokenizeForLeftoverGateSequence(phrase);
                if (phraseTokens.Count == 0 ||
                    !TitleTokenAlignment.TryAlignStructural(
                        phraseTokens,
                        fieldTokens,
                        allowNearExact: false,
                        allowTransposition: false,
                        out var alignment))
                {
                    return false;
                }

                consumedIndexes = alignment.ConsumedFieldIndexes.ToHashSet();
                specificity = phraseTokens.Count(token => !TitleTokenAlignment.IsStructuralGlueToken(token));
                return consumedIndexes.Count > 0 && specificity > 0;
            }

            foreach (var fieldQuery in fieldQueries.Where(query => query != null))
            {
                var fieldKey = fieldQuery.Key ?? string.Join(" ", fieldQuery.Terms ?? Array.Empty<string>());
                var residualValue = fieldQuery.ResidualValue ?? string.Join(" ", fieldQuery.Terms ?? Array.Empty<string>());
                var fieldTokens = TokenizeForLeftoverGateSequence(residualValue);
                if (fieldTokens.Count == 0)
                {
                    continue;
                }

                var representations = new List<StagedFieldRepresentation>();
                var directBookTitleWorks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in keyedCandidates)
                {
                    var hit = FindFieldHit(item.Candidate, fieldKey);
                    if (hit?.TitleHit != true)
                    {
                        continue;
                    }

                    if (TryRepresentPhrase(item.Candidate.BookTitle, fieldTokens, out _, out _))
                    {
                        directBookTitleWorks.Add(item.WorkKey);
                    }

                    foreach (var phrase in CandidateTitlePhrases(item.Candidate))
                    {
                        if (!TryRepresentPhrase(phrase, fieldTokens, out var consumed, out var specificity))
                        {
                            continue;
                        }

                        representations.Add(new StagedFieldRepresentation
                        {
                            WorkKey = item.WorkKey,
                            Candidate = item.Candidate,
                            Phrase = phrase,
                            ConsumedFieldIndexes = consumed,
                            Specificity = specificity
                        });
                    }
                }

                if (representations.Count == 0)
                {
                    continue;
                }

                // A field that represents any candidate title is spent as title evidence. It can
                // never be reused to earn narrator/publisher credit.
                titleReservedFields.Add(fieldKey);
                var bestByWork = representations
                    .GroupBy(representation => representation.WorkKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(representation => representation.ConsumedFieldIndexes.Count)
                        .ThenByDescending(representation => representation.Specificity)
                        .ThenBy(representation => representation.Phrase, StringComparer.OrdinalIgnoreCase)
                        .First())
                    .ToList();
                var maximal = bestByWork
                    .Where(representation => !bestByWork.Any(other =>
                        !string.Equals(other.WorkKey, representation.WorkKey, StringComparison.OrdinalIgnoreCase) &&
                        representation.ConsumedFieldIndexes.IsProperSubsetOf(other.ConsumedFieldIndexes)))
                    .ToList();
                var maximalWorks = maximal
                    .Select(representation => representation.WorkKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var seriesRepresentation in bestByWork.Where(representation =>
                             votesByWork[representation.WorkKey].BookTitleIsOwnedSeriesTitle))
                {
                    var displacingWorks = bestByWork
                        .Where(representation =>
                            !votesByWork[representation.WorkKey].BookTitleIsOwnedSeriesTitle &&
                            !representation.ConsumedFieldIndexes.SetEquals(seriesRepresentation.ConsumedFieldIndexes))
                        .Select(representation => representation.WorkKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (displacingWorks.Count == 0 ||
                        !seriesTitleDominatedWorks.Add(seriesRepresentation.WorkKey))
                    {
                        continue;
                    }

                    RecordTrace(
                        "fts_stage2_series_title_dominated",
                        phase,
                        votesByWork[seriesRepresentation.WorkKey].Representative,
                        detail: "A generic candidate whose Book title is its own series cannot outvote a different Book title represented in the same physical field.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = fieldKey,
                            ["residual"] = residualValue,
                            ["seriesTitleWorkKey"] = seriesRepresentation.WorkKey,
                            ["specificWorkKeys"] = string.Join(" | ", displacingWorks)
                        });
                }

                if (maximalWorks.Count > 1)
                {
                    RecordTrace(
                        "fts_stage2_field_ambiguous",
                        phase,
                        reason: "TITLE_FIELD_NOT_UNIQUE",
                        detail: "Whole-phrase title representations tied within one physical field; every represented Book keeps the field, so ambiguity cannot erase stronger evidence.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = fieldKey,
                            ["residual"] = residualValue,
                            ["providerWorkKeys"] = string.Join(" | ", maximalWorks)
                        });
                }

                foreach (var workKey in maximalWorks)
                {
                    var winningRepresentation = maximal
                        .Where(representation =>
                            string.Equals(representation.WorkKey, workKey, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(representation => representation.ConsumedFieldIndexes.Count)
                        .ThenByDescending(representation => representation.Specificity)
                        .First();
                    var workVotes = votesByWork[workKey];
                    if (workVotes.TitleFields.Add(fieldKey))
                    {
                        workVotes.TitleSpecificity += winningRepresentation.Specificity;
                    }

                    if (directBookTitleWorks.Contains(workKey))
                    {
                        workVotes.DirectBookTitleFields.Add(fieldKey);
                    }

                    RecordTrace(
                        "fts_stage2_title_field_assigned",
                        phase,
                        winningRepresentation.Candidate,
                        detail: maximalWorks.Count == 1
                            ? "One physical field cast one title vote after ordered whole-phrase confirmation."
                            : "One physical field represented this Book and tied another Book; both retain the field and the final agreement gate remains conservative.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = fieldKey,
                            ["residual"] = residualValue,
                            ["representedPhrase"] = winningRepresentation.Phrase,
                            ["providerWorkKey"] = workKey,
                            ["sharedRepresentation"] = (maximalWorks.Count > 1).ToString(CultureInfo.InvariantCulture),
                            ["specificity"] = winningRepresentation.Specificity.ToString(CultureInfo.InvariantCulture)
                        });
                }
            }

            bool IsProperSubsetAcrossGroup(
                StagedGroupFieldRepresentation left,
                StagedGroupFieldRepresentation right)
            {
                if (left?.ConsumedMemberIndexes == null ||
                    right?.ConsumedMemberIndexes == null ||
                    left.ConsumedMemberIndexes.Count != right.ConsumedMemberIndexes.Count)
                {
                    return false;
                }

                var foundStrictSuperset = false;
                for (var index = 0; index < left.ConsumedMemberIndexes.Count; index++)
                {
                    var leftIndexes = left.ConsumedMemberIndexes[index];
                    var rightIndexes = right.ConsumedMemberIndexes[index];
                    if (leftIndexes == null ||
                        rightIndexes == null ||
                        !leftIndexes.IsSubsetOf(rightIndexes))
                    {
                        return false;
                    }

                    foundStrictSuperset |= leftIndexes.Count < rightIndexes.Count;
                }

                return foundStrictSuperset;
            }

            bool SameGroupPositions(
                StagedGroupFieldRepresentation left,
                StagedGroupFieldRepresentation right)
            {
                if (left?.ConsumedMemberIndexes == null ||
                    right?.ConsumedMemberIndexes == null ||
                    left.ConsumedMemberIndexes.Count != right.ConsumedMemberIndexes.Count)
                {
                    return false;
                }

                return left.ConsumedMemberIndexes
                    .Select((indexes, index) => indexes.SetEquals(right.ConsumedMemberIndexes[index]))
                    .All(equal => equal);
            }

            string PhysicalFieldName(string fieldKey)
            {
                return string.IsNullOrWhiteSpace(fieldKey)
                    ? string.Empty
                    : Regex.Replace(fieldKey, @"\[\d+\]$", string.Empty);
            }

            var queriedPhysicalFields = fieldQueries
                .Where(query => query != null)
                .SelectMany(query => query.SourceFields ?? new[] { query.Key })
                .Select(PhysicalFieldName)
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var groupField in (groupFields ?? Array.Empty<GroupPhysicalField>())
                         .Where(field =>
                             field != null &&
                             !string.IsNullOrWhiteSpace(field.FieldName) &&
                             !queriedPhysicalFields.Contains(field.FieldName)))
            {
                var fieldKey = groupField.FieldName + "[group]";
                var representations = new List<StagedGroupFieldRepresentation>();
                var directBookTitleWorks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in keyedCandidates)
                {
                    if (TryRepresentPhraseAcrossGroup(
                            item.Candidate.BookTitle,
                            groupField,
                            out _,
                            out _,
                            out _,
                            out _))
                    {
                        directBookTitleWorks.Add(item.WorkKey);
                    }

                    foreach (var phrase in CandidateTitlePhrases(item.Candidate))
                    {
                        if (!TryRepresentPhraseAcrossGroup(
                                phrase,
                                groupField,
                                out var consumedMemberIndexes,
                                out var observedValues,
                                out var observedSpanKey,
                                out var specificity))
                        {
                            continue;
                        }

                        representations.Add(new StagedGroupFieldRepresentation
                        {
                            WorkKey = item.WorkKey,
                            Candidate = item.Candidate,
                            Phrase = phrase,
                            ConsumedMemberIndexes = consumedMemberIndexes,
                            ObservedValues = observedValues,
                            ObservedSpanKey = observedSpanKey,
                            Specificity = specificity
                        });
                    }
                }

                if (representations.Count == 0)
                {
                    continue;
                }

                titleReservedFields.Add(fieldKey);
                var bestByWork = representations
                    .GroupBy(representation => representation.WorkKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(representation => representation.ConsumedIndexCount)
                        .ThenByDescending(representation => representation.Specificity)
                        .ThenBy(representation => representation.Phrase, StringComparer.OrdinalIgnoreCase)
                        .First())
                    .ToList();
                var maximal = bestByWork
                    .Where(representation => !bestByWork.Any(other =>
                        !string.Equals(other.WorkKey, representation.WorkKey, StringComparison.OrdinalIgnoreCase) &&
                        IsProperSubsetAcrossGroup(representation, other)))
                    .ToList();
                var maximalWorks = maximal
                    .Select(representation => representation.WorkKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var seriesRepresentation in bestByWork.Where(representation =>
                             votesByWork[representation.WorkKey].BookTitleIsOwnedSeriesTitle))
                {
                    var displacingWorks = bestByWork
                        .Where(representation =>
                            !votesByWork[representation.WorkKey].BookTitleIsOwnedSeriesTitle &&
                            !SameGroupPositions(seriesRepresentation, representation))
                        .Select(representation => representation.WorkKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (displacingWorks.Count == 0 ||
                        !seriesTitleDominatedWorks.Add(seriesRepresentation.WorkKey))
                    {
                        continue;
                    }

                    RecordTrace(
                        "fts_stage2_series_title_dominated",
                        phase,
                        votesByWork[seriesRepresentation.WorkKey].Representative,
                        detail: "A generic candidate whose Book title is its own series cannot outvote a different Book title represented throughout the same physical group field.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = groupField.FieldName,
                            ["seriesTitleWorkKey"] = seriesRepresentation.WorkKey,
                            ["specificWorkKeys"] = string.Join(" | ", displacingWorks)
                        });
                }

                if (maximalWorks.Count > 1)
                {
                    RecordTrace(
                        "fts_stage2_field_ambiguous",
                        phase,
                        reason: "GROUP_TITLE_FIELD_NOT_UNIQUE",
                        detail: "Multiple provider Books remained maximal across the same physical field on every group member; each retains the field and the agreement gate remains conservative.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = groupField.FieldName,
                            ["providerWorkKeys"] = string.Join(" | ", maximalWorks)
                        });
                }

                foreach (var workKey in maximalWorks)
                {
                    var winningRepresentation = maximal
                        .Where(representation =>
                            string.Equals(representation.WorkKey, workKey, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(representation => representation.ConsumedIndexCount)
                        .ThenByDescending(representation => representation.Specificity)
                        .First();
                    var workVotes = votesByWork[workKey];
                    if (workVotes.TitleFields.Add(fieldKey))
                    {
                        workVotes.TitleSpecificity += winningRepresentation.Specificity;
                    }

                    workVotes.GroupTitleFieldNames.Add(groupField.FieldName);
                    if (directBookTitleWorks.Contains(workKey))
                    {
                        workVotes.DirectBookTitleFields.Add(fieldKey);
                    }

                    RecordTrace(
                        "fts_stage2_group_title_field_assigned",
                        phase,
                        winningRepresentation.Candidate,
                        detail: maximalWorks.Count == 1
                            ? "The candidate title appeared in the same physical field on every group member; varying surrounding text did not erase the field."
                            : "The candidate title appeared throughout the same physical group field but tied another maximal Book.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = groupField.FieldName,
                            ["memberCount"] = groupField.MemberValues.Count.ToString(CultureInfo.InvariantCulture),
                            ["representedPhrase"] = winningRepresentation.Phrase,
                            ["observedSpan"] = winningRepresentation.ObservedSpanKey ?? string.Empty,
                            ["providerWorkKey"] = workKey,
                            ["sharedRepresentation"] = (maximalWorks.Count > 1).ToString(CultureInfo.InvariantCulture),
                            ["specificity"] = winningRepresentation.Specificity.ToString(CultureInfo.InvariantCulture)
                        });
                }
            }

            var titleEligibleWorks = votesByWork.Values
                .Where(work => work.TitleFields.Count > 0 &&
                               !seriesTitleDominatedWorks.Contains(work.WorkKey))
                .Select(work => work.WorkKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fieldQuery in fieldQueries.Where(query => query != null))
            {
                var fieldKey = fieldQuery.Key ?? string.Join(" ", fieldQuery.Terms ?? Array.Empty<string>());
                if (titleReservedFields.Contains(fieldKey))
                {
                    continue;
                }

                var residualValue = fieldQuery.ResidualValue ?? string.Join(" ", fieldQuery.Terms ?? Array.Empty<string>());
                var fieldTokens = TokenizeForLeftoverGateSequence(residualValue);
                if (fieldTokens.Count == 0)
                {
                    continue;
                }

                var representedWorks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in keyedCandidates.Where(item => titleEligibleWorks.Contains(item.WorkKey)))
                {
                    var hit = FindFieldHit(item.Candidate, fieldKey);
                    if (hit?.DetailHit != true)
                    {
                        continue;
                    }

                    var detailPhrases = mediaType == BookMediaType.Audiobook
                        ? SplitNarrators(item.Candidate.NarratorNames)
                        : new List<string> { item.Candidate.Publisher };
                    if (detailPhrases
                        .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
                        .Any(phrase => TryRepresentPhrase(phrase, fieldTokens, out _, out _)))
                    {
                        representedWorks.Add(item.WorkKey);
                    }
                }

                if (representedWorks.Count == 0)
                {
                    continue;
                }

                if (representedWorks.Count > 1)
                {
                    RecordTrace(
                        "fts_stage2_field_ambiguous",
                        phase,
                        reason: "DETAIL_FIELD_NOT_UNIQUE",
                        detail: "A physical detail field represented multiple title-eligible provider Books; each represented Book keeps the independent corroboration.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = fieldKey,
                            ["residual"] = residualValue,
                            ["providerWorkKeys"] = string.Join(" | ", representedWorks)
                        });
                }

                foreach (var workKey in representedWorks)
                {
                    votesByWork[workKey].DetailFields.Add(fieldKey);
                    RecordTrace(
                        "fts_stage2_detail_field_assigned",
                        phase,
                        votesByWork[workKey].Representative,
                        detail: representedWorks.Count == 1
                            ? "One unspent physical field cast one narrator/publisher vote."
                            : "One unspent physical field corroborated this Book and at least one tied Book; final agreement remains conservative.",
                        filePath: filePath,
                        data: new Dictionary<string, string>
                        {
                            ["field"] = fieldKey,
                            ["residual"] = residualValue,
                            ["providerWorkKey"] = workKey,
                            ["sharedRepresentation"] = (representedWorks.Count > 1).ToString(CultureInfo.InvariantCulture),
                            ["detailKind"] = mediaType == BookMediaType.Audiobook ? "narrator" : "publisher"
                        });
                }
            }

            foreach (var work in votesByWork.Values.Where(work => work.TitleFields.Count > 0))
            {
                if (mediaType == BookMediaType.Audiobook &&
                    durationComparable &&
                    fileDurationSeconds.HasValue &&
                    fileDurationSeconds.Value > 0)
                {
                    var knownDurations = work.Candidates
                        .Where(candidate =>
                            candidate.ReadingFormatId == 2 &&
                            candidate.DurationSeconds.HasValue &&
                            candidate.DurationSeconds.Value > 0)
                        .Select(candidate => candidate.DurationSeconds.Value)
                        .Distinct()
                        .ToList();
                    work.DurationCompatible = knownDurations.Any(duration =>
                        Math.Abs(duration - fileDurationSeconds.Value) <=
                        AudiobookDurationTolerance.ForMatchingSeconds(duration));
                    work.DurationConflict = knownDurations.Count > 0 && !work.DurationCompatible;
                }

                if (mediaType == BookMediaType.Ebook && fileYear.HasValue)
                {
                    work.YearExact = work.Candidates.Any(candidate =>
                        candidate.ReleaseDate.HasValue &&
                        candidate.ReleaseDate.Value.Year == fileYear.Value);
                }

                RecordTrace(
                    "fts_stage2_work_votes",
                    phase,
                    work.Representative,
                    detail: "Structural Stage 2 evidence for one provider Book.",
                    filePath: filePath,
                    data: new Dictionary<string, string>
                    {
                        ["providerWorkKey"] = work.WorkKey,
                        ["localBookIds"] = string.Join(",", work.BookIds.OrderBy(id => id)),
                        ["corroborationTier"] = work.CorroborationTier.ToString(CultureInfo.InvariantCulture),
                        ["fieldCount"] = work.FieldCount.ToString(CultureInfo.InvariantCulture),
                        ["titleFieldCount"] = work.TitleFields.Count.ToString(CultureInfo.InvariantCulture),
                        ["groupTitleFields"] = string.Join(",", work.GroupTitleFieldNames.OrderBy(field => field, StringComparer.OrdinalIgnoreCase)),
                        ["directBookTitleFieldCount"] = work.DirectBookTitleFields.Count.ToString(CultureInfo.InvariantCulture),
                        ["detailFieldCount"] = work.DetailFields.Count.ToString(CultureInfo.InvariantCulture),
                        ["titleSpecificity"] = work.TitleSpecificity.ToString(CultureInfo.InvariantCulture),
                        ["durationCompatible"] = work.DurationCompatible.ToString(CultureInfo.InvariantCulture),
                        ["durationConflict"] = work.DurationConflict.ToString(CultureInfo.InvariantCulture),
                        ["yearExact"] = work.YearExact.ToString(CultureInfo.InvariantCulture)
                    });
            }

            var corroboratedProjectionOnlyWorks = votesByWork.Values
                .Where(work =>
                    work.TitleFields.Count > 0 &&
                    work.DirectBookTitleFields.Count == 0 &&
                    !seriesTitleDominatedWorks.Contains(work.WorkKey) &&
                    (work.DetailFields.Count > 0 || work.DurationCompatible || work.YearExact))
                .ToList();
            if (corroboratedProjectionOnlyWorks.Count > 0)
            {
                var projectionSummary = string.Join(
                    " | ",
                    corroboratedProjectionOnlyWorks
                        .OrderBy(work => work.WorkKey, StringComparer.OrdinalIgnoreCase)
                        .Select(work =>
                            $"{work.WorkKey}:titleFields={work.TitleFields.Count},detailFields={work.DetailFields.Count},durationCompatible={work.DurationCompatible},yearExact={work.YearExact}"));
                _logger.Warn(
                    "[HOLY-GRAIL][{0}][STAGED-FTS-UNCLEAR] Edition-title evidence physically corroborated a provider Book whose own title was not represented: {1}",
                    phase,
                    projectionSummary);
                RecordTrace(
                    "fts_stage2_unclear",
                    phase,
                    reason: "EDITION_PROJECTION_REQUIRES_DEEP_EVALUATOR",
                    detail: "A complete Edition-title projection plus independent physical evidence challenged direct Book-title evidence; the deep evaluator must resolve provider Book identity.",
                    filePath: filePath,
                    data: new Dictionary<string, string>
                    {
                        ["works"] = projectionSummary
                    });
                return null;
            }

            var eligibleWorks = votesByWork.Values
                .Where(work => work.DirectBookTitleFields.Count > 0 &&
                               !seriesTitleDominatedWorks.Contains(work.WorkKey))
                .ToList();
            if (eligibleWorks.Count == 0)
            {
                RecordTrace(
                    "fts_stage2_unclear",
                    phase,
                    reason: "NO_WHOLE_TITLE_FIELD",
                    detail: "No physical field uniquely represented a complete candidate title; deep candidate analysis is required.",
                    filePath: filePath);
                return null;
            }

            var bestTier = eligibleWorks.Max(work => work.CorroborationTier);
            var bestFieldCount = eligibleWorks.Max(work => work.FieldCount);
            var agreedWinners = eligibleWorks
                .Where(work =>
                    work.CorroborationTier == bestTier &&
                    work.FieldCount == bestFieldCount)
                .ToList();
            if (agreedWinners.Count != 1 || agreedWinners[0].DurationConflict)
            {
                var summary = string.Join(
                    " | ",
                    eligibleWorks
                        .OrderBy(work => work.WorkKey, StringComparer.OrdinalIgnoreCase)
                        .Select(work =>
                            $"{work.WorkKey}:tier={work.CorroborationTier},fields={work.FieldCount},durationConflict={work.DurationConflict}"));
                _logger.Warn(
                    "[HOLY-GRAIL][{0}][STAGED-FTS-UNCLEAR] Structural field evidence did not produce one physically compatible Book: {1}",
                    phase,
                    summary);
                RecordTrace(
                    "fts_stage2_unclear",
                    phase,
                    reason: "STRUCTURAL_SIGNALS_DISAGREE",
                    detail: "Corroboration tier, physical-field count, and duration compatibility did not agree on one provider Book; deep candidate analysis is required.",
                    filePath: filePath,
                    data: new Dictionary<string, string>
                    {
                        ["bestTier"] = bestTier.ToString(CultureInfo.InvariantCulture),
                        ["bestFieldCount"] = bestFieldCount.ToString(CultureInfo.InvariantCulture),
                        ["works"] = summary
                    });
                return null;
            }

            var winner = agreedWinners[0];
            RecordTrace(
                "fts_stage2_book_selected",
                phase,
                winner.Representative,
                detail: "Corroboration tier, physical-field count, and compatibility agreed on one provider Book.",
                filePath: filePath,
                data: new Dictionary<string, string>
                {
                    ["providerWorkKey"] = winner.WorkKey,
                    ["localBookIds"] = string.Join(",", winner.BookIds.OrderBy(id => id)),
                    ["corroborationTier"] = winner.CorroborationTier.ToString(CultureInfo.InvariantCulture),
                    ["fieldCount"] = winner.FieldCount.ToString(CultureInfo.InvariantCulture),
                    ["titleFields"] = string.Join(",", winner.TitleFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase)),
                    ["groupTitleFields"] = string.Join(",", winner.GroupTitleFieldNames.OrderBy(field => field, StringComparer.OrdinalIgnoreCase)),
                    ["detailFields"] = string.Join(",", winner.DetailFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
                });
            return new StagedBookDecision
            {
                WorkKey = winner.WorkKey,
                BookIds = winner.BookIds,
                GroupTitleFieldNames = winner.GroupTitleFieldNames,
                Representative = winner.Representative
            };
        }

        private sealed class ResidualTagOccurrence
        {
            public string Field { get; set; }
            public int ValueIndex { get; set; }
            public string OriginalValue { get; set; }
            public string ResidualValue { get; set; }
        }

        private sealed class NormalizedWordSpan
        {
            public string Token { get; set; }
            public int Start { get; set; }
            public int End { get; set; }
        }

        private List<EditionFtsFieldQuery> BuildStagedFtsFieldQueries(
            IDictionary<string, List<string>> tags,
            IReadOnlyList<BookFtsMatch> recalledBooks,
            IReadOnlyCollection<string> authorNamesToConsume,
            string phase,
            string filePath)
        {
            var occurrences = new List<ResidualTagOccurrence>();
            foreach (var pair in tags ?? new Dictionary<string, List<string>>())
            {
                if (IsExcludedFromMatching(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                for (var index = 0; index < pair.Value.Count; index++)
                {
                    var value = pair.Value[index];
                    if (string.IsNullOrWhiteSpace(value) || value.Length >= 5000)
                    {
                        continue;
                    }

                    if (TagCanonicalizer.IsCanonicalAliasBackedByRawSource(pair.Key, value, tags))
                    {
                        RecordTrace(
                            "fts_alias_view_suppressed",
                            phase,
                            detail: "Suppressed a generated canonical view of a raw physical tag occurrence.",
                            filePath: filePath,
                            data: new Dictionary<string, string>
                            {
                                ["field"] = pair.Key,
                                ["valueIndex"] = index.ToString(CultureInfo.InvariantCulture),
                                ["value"] = value
                            });
                        continue;
                    }

                    occurrences.Add(new ResidualTagOccurrence
                    {
                        Field = pair.Key,
                        ValueIndex = index,
                        OriginalValue = value,
                        ResidualValue = value
                    });
                }
            }

            void ConsumeAllAuthorOccurrences(string expected)
            {
                if (string.IsNullOrWhiteSpace(expected))
                {
                    return;
                }

                foreach (var occurrence in occurrences)
                {
                    while (TryFindStagedIdentitySpan(
                               occurrence.ResidualValue,
                               expected,
                               out var start,
                               out var end))
                    {
                        var before = occurrence.ResidualValue;
                        var after = Regex.Replace(
                                before.Remove(start, end - start).Insert(start, " "),
                                @"\s+",
                                " ")
                            .Trim(' ', ',', ';', '-', '_');
                        if (string.Equals(before, after, StringComparison.Ordinal))
                        {
                            break;
                        }

                        occurrence.ResidualValue = after;
                        RecordTrace(
                            "fts_context_consumed",
                            phase,
                            detail: "Consumed a proven author occurrence before Stage 2 FTS.",
                            filePath: filePath,
                            data: new Dictionary<string, string>
                            {
                                ["role"] = "author",
                                ["expected"] = expected,
                                ["field"] = occurrence.Field,
                                ["valueIndex"] = occurrence.ValueIndex.ToString(CultureInfo.InvariantCulture),
                                ["observed"] = before,
                                ["residual"] = after
                            });
                    }
                }
            }

            foreach (var authorName in (authorNamesToConsume ?? Array.Empty<string>())
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ConsumeAllAuthorOccurrences(authorName);
            }

            var retainedSeries = recalledBooks
                .Where(book => book != null && !string.IsNullOrWhiteSpace(book.SeriesName))
                .Select(book => book.SeriesName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(series => series, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (retainedSeries.Count > 0)
            {
                RecordTrace(
                    "fts_series_context_retained",
                    phase,
                    detail: "Series text remains available to distinguish a title from its siblings.",
                    filePath: filePath,
                    data: new Dictionary<string, string>
                    {
                        ["seriesNames"] = string.Join(" | ", retainedSeries)
                    });
            }

            return occurrences
                .Select(occurrence => new
                {
                    Occurrence = occurrence,
                    Terms = TokenizeText(occurrence.ResidualValue)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .Where(item => item.Terms.Count > 0)
                .Select(item =>
                {
                    var fieldKey = $"{item.Occurrence.Field}[{item.Occurrence.ValueIndex}]";
                    return new EditionFtsFieldQuery
                    {
                        Key = fieldKey,
                        ResidualValue = item.Occurrence.ResidualValue,
                        Terms = item.Terms,
                        SourceFields = new[] { fieldKey }
                    };
                })
                .ToList();
        }

        private static bool TryFindStagedIdentitySpan(
            string value,
            string expected,
            out int start,
            out int end)
        {
            start = 0;
            end = 0;
            var valueWords = GetNormalizedWordSpans(value);
            var expectedWords = GetNormalizedWordSpans(expected).Select(span => span.Token).ToList();
            if (valueWords.Count == 0 || expectedWords.Count == 0 || expectedWords.Count > valueWords.Count)
            {
                return false;
            }

            bool SameIdentityWord(string observed, string wanted)
            {
                if (string.Equals(observed, wanted, StringComparison.Ordinal))
                {
                    return true;
                }

                return (observed.Length == 1 && wanted.StartsWith(observed, StringComparison.Ordinal)) ||
                       (wanted.Length == 1 && observed.StartsWith(wanted, StringComparison.Ordinal));
            }

            for (var index = 0; index <= valueWords.Count - expectedWords.Count; index++)
            {
                var matches = true;
                for (var offset = 0; offset < expectedWords.Count; offset++)
                {
                    if (!SameIdentityWord(valueWords[index + offset].Token, expectedWords[offset]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                start = valueWords[index].Start;
                end = valueWords[index + expectedWords.Count - 1].End;
                return true;
            }

            return false;
        }

        private static List<NormalizedWordSpan> GetNormalizedWordSpans(string value)
        {
            var output = new List<NormalizedWordSpan>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return output;
            }

            foreach (Match match in Regex.Matches(value, @"[\p{L}\p{Nd}]+"))
            {
                var decomposed = match.Value.Normalize(NormalizationForm.FormD);
                var normalized = new string(decomposed
                        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                        .ToArray())
                    .ToLowerInvariant();
                if (normalized.Length == 0)
                {
                    continue;
                }

                output.Add(new NormalizedWordSpan
                {
                    Token = normalized,
                    Start = match.Index,
                    End = match.Index + match.Length
                });
            }

            return output;
        }

        /// <summary>
        /// Extract tags usable for matching (FTS + V5), excluding trash and display-only fields.
        /// </summary>
        private Dictionary<string, List<string>> CategorizeTagsForHolyGrail(Dictionary<string, List<string>> allTags)
        {
            var mainTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in allTags)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                if (IsExcludedFromMatching(kv.Key)) continue;

                var filteredValues = kv.Value
                    .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length < 5000)
                    .ToList();

                if (filteredValues.Count == 0) continue;

                mainTags[kv.Key] = filteredValues;
            }

            return mainTags;
        }

        /// <summary>
        /// Tokenize tags per blueprint: extract alphanumeric tokens, deduplicate while preserving order.
        /// </summary>
        private List<string> TokenizeForHolyGrail(Dictionary<string, List<string>> tags)
        {
            var allText = string.Join(" ", tags.Values.SelectMany(v => v));
            return TokenizeText(allText);
        }

        /// <summary>
        /// Tokenize path components for fallback.
        /// </summary>
        private List<string> TokenizePathForHolyGrail(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return new List<string>();

            var parts = new List<string>();

            // Add filename without extension
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!string.IsNullOrWhiteSpace(fileName)) parts.Add(fileName);

            // Add directory names (book folder, author folder). Prefer resolving author folder via root folders
            // so /Author/Series/Book/file layouts don't treat "Series" as the author.
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    var di = new DirectoryInfo(dir);
                    if (di.Exists)
                    {
                        parts.Add(di.Name); // Book folder

                        var firstFolderUnderRoot = GetFirstFolderUnderRoot(filePath);
                        var folderName = !string.IsNullOrWhiteSpace(firstFolderUnderRoot) ? Path.GetFileName(firstFolderUnderRoot) : null;
                        if (!string.IsNullOrWhiteSpace(folderName))
                        {
                            parts.Add(folderName);
                        }
                    }
                }
            }
            catch { }

            return TokenizeText(string.Join(" ", parts));
        }

        /// <summary>
        /// Core tokenization: lowercase alphanumeric, deduplicate preserving order.
        /// Now with diacritic and possessive normalization for robust matching.
        /// </summary>
        private List<string> TokenizeText(string text)
        {
            return Services.BookImportUnitGroupingService.TokenizeText(text);
        }

        /// <summary>
        /// Find author by last name (most distinctive part) per blueprint.
        /// </summary>
        private Author FindAuthorByLastName(string artistName)
        {
            if (string.IsNullOrWhiteSpace(artistName)) return null;

            // Per blueprint: search by last name (most distinctive part)
            var parts = artistName.Replace(".", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var meaningfulParts = parts.Where(p => p.Length > 2).ToList();

            var allAuthors = _authorService.GetAllAuthors() ?? new List<Author>();
            if (allAuthors.Count == 0)
            {
                return null;
            }

            // Try parts from end (last name first) to start
            foreach (var part in parts.Reverse())
            {
                if (part.Length <= 2) continue; // Skip initials

                var authors = allAuthors
                    .Where(a => a.Name != null && a.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (authors.Count == 1)
                {
                    return authors[0];
                }
                else if (authors.Count > 1)
                {
                    // Multiple matches - try to find exact match on full name
                    var exactMatch = authors.FirstOrDefault(a =>
                        a.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase));
                    if (exactMatch != null) return exactMatch;

                    // Try to narrow down by requiring all meaningful parts of the artist name.
                    if (meaningfulParts.Count > 1)
                    {
                        var narrowed = authors
                            .Where(a => meaningfulParts.All(p => a.Name?.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                            .ToList();

                        if (narrowed.Count == 1)
                        {
                            return narrowed[0];
                        }
                    }

                    // Ambiguous; do not arbitrarily pick an author (can cause wrong imports).
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Find author from path by checking first folder under root.
        /// </summary>
        private Author FindAuthorFromPath(string filePath)
        {
            try
            {
                var firstFolderUnderRoot = GetFirstFolderUnderRoot(filePath);
                var folderName = !string.IsNullOrWhiteSpace(firstFolderUnderRoot) ? Path.GetFileName(firstFolderUnderRoot) : null;
                return FindAuthorByLastName(folderName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Create FileMatch from FTS result.
        /// </summary>
        private void LogDecisionWithProvenance(
            string filePath,
            string decision,
            string reason,
            Dictionary<string, List<string>> extractedTags,
            BookMediaType mediaType,
            FileMatch match = null,
            Dictionary<string, List<string>> proofTags = null,
            bool? pathFallbackUsed = null,
            string pathFallbackSuppressedReason = null,
            string pinnedTargetResult = null,
            string pinnedTargetFailure = null,
            List<CandidateRejection> rejections = null,
            int? commandId = null,
            string correlationId = null)
            {
            var matchForLog = match == null
                ? null
                : new FileMatch
                {
                    File = match.File,
                    AuthorId = match.AuthorId,
                    AuthorName = match.AuthorName,
                    BookId = match.BookId,
                    BookTitle = match.BookTitle,
                    EditionId = match.EditionId,
                    MatchedVia = match.MatchedVia,
                    Provenance = match.Provenance?.Clone(),
                    IdentityProof = match.IdentityProof
                };
            var effectivePathFallbackUsed = pathFallbackUsed;
            if (effectivePathFallbackUsed != true &&
                (extractedTags == null || extractedTags.Count == 0) &&
                proofTags != null &&
                proofTags.Count > 0)
            {
                effectivePathFallbackUsed = true;
            }

            if (effectivePathFallbackUsed == true &&
                matchForLog?.Provenance?.Route?.IndexOf("embedded_tags", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matchForLog.Provenance.Route = Regex.Replace(
                    matchForLog.Provenance.Route,
                    "embedded_tags",
                    "path_tags",
                    RegexOptions.IgnoreCase);
                matchForLog.Provenance.Summary = "Matched from the file path";

                foreach (var signal in matchForLog.Provenance.SupportingSignals
                             .Concat(matchForLog.Provenance.ConflictingSignals)
                             .Concat(matchForLog.Provenance.NeutralSignals)
                             .Where(signal => signal != null && string.Equals(signal.Source, "embedded_tag", StringComparison.OrdinalIgnoreCase)))
                {
                    signal.Source = "path";
                }

                foreach (var evidenceValue in matchForLog.Provenance.EvidenceValues
                             .Where(value => value != null &&
                                             string.Equals(value.Source, "embedded_tag", StringComparison.OrdinalIgnoreCase)))
                {
                    evidenceValue.Source = "path";
                }

                // These exclusions describe synthetic path helper keys, not user metadata.
                matchForLog.Provenance.ExcludedSignals.Clear();
            }

                var result = BuildLoggedMatchResult(
                    decision,
                    reason,
                    mediaType,
                    matchForLog,
                    effectivePathFallbackUsed,
                    pathFallbackSuppressedReason,
                    pinnedTargetResult,
                    pinnedTargetFailure,
                    rejections);

                _matchingLogger.LogFinalDecision(filePath, result, extractedTags, commandId, correlationId);
            }

        private MatchResult BuildLoggedMatchResult(
            string decision,
            string reason,
            BookMediaType mediaType,
            FileMatch match,
            bool? pathFallbackUsed,
            string pathFallbackSuppressedReason,
            string pinnedTargetResult,
            string pinnedTargetFailure,
            List<CandidateRejection> rejections)
        {
            var result = new MatchResult
            {
                Success = string.Equals(decision, "MATCHED", StringComparison.OrdinalIgnoreCase),
                Reason = reason,
                Decision = decision,
                PathFallbackUsed = pathFallbackUsed,
                PathFallbackSuppressedReason = pathFallbackSuppressedReason,
                PinnedTargetResult = pinnedTargetResult,
                PinnedTargetFailure = pinnedTargetFailure,
                Outcome = decision,
                OutcomeReason = reason,
                Rejections = rejections
            };

            if (match == null)
            {
                return result;
            }

            var edition = TryGetEdition(match.EditionId);
            var authorMatched = $"{match.AuthorName} (ID:{match.AuthorId})";
            var bookMatched = $"{match.BookTitle} (ID:{match.BookId})";
            var editionMatched = edition != null ? $"{edition.Title} (ID:{edition.Id})" : null;

            result.AuthorMatched = authorMatched;
            result.BookMatched = bookMatched;
            result.EditionMatched = editionMatched;
            result.MatchedAuthor = match.AuthorName;
            result.MatchedBook = match.BookTitle;
            result.MatchedEdition = edition?.Title ?? match.BookTitle;
            result.MatchedVia = match.MatchedVia;
            result.Provenance = match.Provenance;
            result.MatchedEditionTitle = edition?.Title;
            result.MatchedEditionNarrators = BuildMatchedEditionNarrators(edition);
            result.AuthorProvedBy = BuildLogEvidence(match.Provenance, "author");
            result.BookProvedBy = BuildLogEvidence(match.Provenance, "title");
            result.NarratorProvedBy = BuildLogEvidence(match.Provenance, "narrator");

            return result;
        }

        private static List<MatchEvidence> BuildLogEvidence(MatchProvenance provenance, string signalType)
        {
            var evidence = (provenance?.SupportingSignals ?? new List<MatchSignal>())
                .Where(signal =>
                    signal != null &&
                    string.Equals(signal.Type, signalType, StringComparison.OrdinalIgnoreCase) &&
                    (!string.IsNullOrWhiteSpace(signal.Field) || !string.IsNullOrWhiteSpace(signal.Observed)))
                .Select(signal => new MatchEvidence
                {
                    Source = signal.Source,
                    Field = signal.Field,
                    Key = signal.Field,
                    Value = signal.Observed,
                    Note = signalType
                })
                .ToList();

            return evidence.Count > 0 ? evidence : null;
        }

        private static List<string> BuildMatchedEditionNarrators(Edition edition)
        {
            var narrators = new List<string>();
            if (edition?.NarratorNames?.Any() == true)
            {
                narrators.AddRange(edition.NarratorNames.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            if (!string.IsNullOrWhiteSpace(edition?.Narrator))
            {
                narrators.Add(edition.Narrator);
            }

            return narrators.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private Edition TryGetEdition(int editionId)
        {
            try
            {
                return editionId > 0 ? _editionService?.GetEdition(editionId) : null;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, List<string>> BuildAuthorEvidenceTags(string authorName, IDictionary<string, List<string>> tags)
        {
            return ExactMatchEvidenceBuilder.BuildAuthorEvidenceTags(authorName, tags, _containmentValidator);
        }

        private Dictionary<string, List<string>> BuildRawAuthorEvidenceTags(
            string authorName,
            IDictionary<string, List<string>> tags)
        {
            var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(authorName) || tags == null || _containmentValidator == null)
            {
                return evidence;
            }

            foreach (var field in tags.Where(pair => pair.Value != null))
            {
                foreach (var value in field.Value.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    var singleField = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [field.Key] = new List<string> { value }
                    };
                    if (!_containmentValidator.ValidateAuthorInTags(authorName, singleField))
                    {
                        continue;
                    }

                    if (!evidence.TryGetValue(field.Key, out var values))
                    {
                        values = new List<string>();
                        evidence[field.Key] = values;
                    }

                    values.Add(value);
                }
            }

            return evidence;
        }

        private static bool IsSourceTagKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.IndexOf(':') >= 0 ||
                   key.IndexOf('/') >= 0 ||
                   key.IndexOf('\u00A9') >= 0;
        }

        private static bool HasRequiredAuthorAndBookProof(MatchIdentityProof evidence)
        {
            return MatchIdentityProofMembership.HasRequiredIdentity(evidence);
        }

        private ProofMembership BuildHomogeneousProofMembership(
            FileMatch match,
            DiscoveredFileWithMetadata file,
            IDictionary<string, List<string>> tags,
            MatchIdentityProof proofEvidence)
        {
            if (match == null)
            {
                return new ProofMembership { Passes = false, Reason = "missing-match" };
            }

            if (!HasRequiredAuthorAndBookProof(proofEvidence))
            {
                return new ProofMembership { Passes = false, Reason = "missing-proof" };
            }

            if (tags == null || tags.Count == 0)
            {
                var needsOnlyPathEvidence = proofEvidence.Values.All(value =>
                    string.Equals(value.Source, "path", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value.Source, "filename", StringComparison.OrdinalIgnoreCase));
                if (!needsOnlyPathEvidence)
                {
                    return new ProofMembership { Passes = false, Reason = "missing-tags" };
                }
            }

            var memberProof = BuildExactMemberIdentityProof(proofEvidence, file, tags);
            if (!HasRequiredAuthorAndBookProof(memberProof))
            {
                return new ProofMembership { Passes = false, Reason = "exact-identity-proof-missing" };
            }

            var proofTags = BuildIdentityProofTags(memberProof);
            // Candidate eligibility was already decided by the active matching mode. Group membership
            // only asks whether this member contains the exact same source/field/value identity atoms.
            // Re-running title leftovers or series position here would silently impose Strict semantics.

            return new ProofMembership
            {
                Passes = true,
                ProofTags = proofTags,
                IdentityProof = memberProof
            };
        }

        private MatchIdentityProof BuildExactMemberIdentityProof(
            MatchIdentityProof seedProof,
            DiscoveredFileWithMetadata file,
            IDictionary<string, List<string>> embeddedTags)
        {
            if (seedProof == null)
            {
                return null;
            }

            return MatchIdentityProofMembership.Intersect(
                seedProof,
                embeddedTags,
                BuildSupplementalPathEvidence(file?.Path));
        }

        private static Dictionary<string, List<string>> BuildIdentityProofTags(
            MatchIdentityProof proof,
            MatchIdentityRole? role = null)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var proofValue in proof?.Values ?? Array.Empty<MatchIdentityProofValue>())
            {
                if (role.HasValue && proofValue.Role != role.Value)
                {
                    continue;
                }

                if (!tags.TryGetValue(proofValue.Field, out var values))
                {
                    values = new List<string>();
                    tags[proofValue.Field] = values;
                }

                if (!values.Any(value => string.Equals(value, proofValue.Observed, StringComparison.Ordinal)))
                {
                    values.Add(proofValue.Observed);
                }
            }

            return tags;
        }

        private IReadOnlyList<EditionTitleEvidence> GetBookEvidenceFields(FileMatch match, IDictionary<string, List<string>> tags)
        {
            if (match == null || tags == null || tags.Count == 0)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            var edition = TryGetEdition(match.EditionId);
            var evidence = !string.IsNullOrWhiteSpace(edition?.Title)
                ? _containmentValidator.GetEditionTitleEvidence(edition.Title, tags)
                : Array.Empty<EditionTitleEvidence>();

            if (evidence == null || evidence.Count == 0)
            {
                foreach (var provenTitle in (match.IdentityProof?.Values ?? Array.Empty<MatchIdentityProofValue>())
                             .Where(value => value.Role == MatchIdentityRole.Title)
                             .Select(value => value.Expected)
                             .Where(value => !string.IsNullOrWhiteSpace(value))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    evidence = _containmentValidator.GetEditionTitleEvidence(provenTitle, tags);
                    if (evidence?.Count > 0)
                    {
                        break;
                    }
                }
            }

            if ((evidence == null || evidence.Count == 0) && !string.IsNullOrWhiteSpace(match.BookTitle))
            {
                evidence = _containmentValidator.GetEditionTitleEvidence(match.BookTitle, tags);
            }

            return evidence ?? Array.Empty<EditionTitleEvidence>();
        }

        private EditionFtsMatch BuildMembershipCandidate(FileMatch match)
        {
            var edition = TryGetEdition(match.EditionId);

            return new EditionFtsMatch
            {
                EditionId = match.EditionId,
                ForeignEditionId = edition?.ForeignEditionId,
                BookId = match.BookId,
                EditionTitle = !string.IsNullOrWhiteSpace(edition?.Title) ? edition.Title : match.BookTitle,
                EditionSubTitle = edition?.Subtitle,
                BookTitle = match.BookTitle,
                AuthorId = match.AuthorId,
                AuthorName = match.AuthorName,
                NarratorNames = edition?.NarratorNames?.Count > 0 ? string.Join(" ", edition.NarratorNames) : edition?.Narrator,
                Publisher = edition?.Publisher,
                ReleaseDate = edition?.ReleaseDate,
                DurationSeconds = edition?.DurationSeconds,
                ReadingFormatId = edition?.ReadingFormatId
            };
        }

        private FileMatch CreateFileMatch(
            DiscoveredFileWithMetadata file,
            EditionFtsMatch ftsMatch,
            int? authorId,
            string authorName,
            string matchedVia = null,
            MatchProvenance provenance = null,
            MatchIdentityProof identityProof = null)
        {
            return new FileMatch
            {
                File = file,
                AuthorId = authorId ?? ftsMatch.AuthorId,
                AuthorName = authorName ?? ftsMatch.AuthorName,
                BookId = ftsMatch.BookId,
                BookTitle = ftsMatch.BookTitle,
                EditionId = ftsMatch.EditionId,
                MatchedVia = matchedVia,
                Provenance = provenance,
                IdentityProof = identityProof
            };
        }

        private FileMatch CopyFileMatchForFile(
            FileMatch match,
            DiscoveredFileWithMetadata file,
            MatchIdentityProof memberProof = null)
        {
            if (match == null)
            {
                return null;
            }

            memberProof ??= match.File != null && file != null &&
                            string.Equals(match.File.Path, file.Path, StringComparison.OrdinalIgnoreCase)
                ? match.IdentityProof
                : BuildExactMemberIdentityProof(match.IdentityProof, file, file?.AllTags);

            return new FileMatch
            {
                File = file,
                AuthorId = match.AuthorId,
                AuthorName = match.AuthorName,
                BookId = match.BookId,
                BookTitle = match.BookTitle,
                EditionId = match.EditionId,
                MatchedVia = match.MatchedVia,
                Provenance = BuildMemberMatchProvenance(match.Provenance, memberProof, file),
                IdentityProof = memberProof
            };
        }

        private MatchProvenance BuildMemberMatchProvenance(
            MatchProvenance source,
            MatchIdentityProof memberProof,
            DiscoveredFileWithMetadata file)
        {
            if (source == null)
            {
                return null;
            }

            var provenance = source.Clone();
            var embeddedTags = file?.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var pathTags = BuildSupplementalPathEvidence(file?.Path);
            var allEvidence = MergeEvidenceTags(embeddedTags, pathTags);

            bool IsIdentitySignal(MatchSignal signal)
            {
                return signal != null &&
                       (string.Equals(signal.Type, "author", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(signal.Type, "title", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(signal.Type, "provider_identifier", StringComparison.OrdinalIgnoreCase));
            }

            bool IsPresentOnMember(MatchSignal signal)
            {
                if (signal == null || string.IsNullOrWhiteSpace(signal.Field) || string.IsNullOrWhiteSpace(signal.Observed))
                {
                    return true;
                }

                var sourceTags = string.Equals(signal.Source, "embedded_tag", StringComparison.OrdinalIgnoreCase)
                    ? embeddedTags
                    : pathTags;
                return sourceTags.TryGetValue(signal.Field, out var values) &&
                       (values ?? new List<string>()).Any(value => string.Equals(value, signal.Observed, StringComparison.Ordinal));
            }

            provenance.SupportingSignals = provenance.SupportingSignals
                .Where(signal => !IsIdentitySignal(signal) && IsPresentOnMember(signal))
                .Select(signal => signal.Clone())
                .ToList();
            provenance.ConflictingSignals = provenance.ConflictingSignals
                .Where(IsPresentOnMember)
                .Select(signal => signal.Clone())
                .ToList();
            provenance.NeutralSignals = provenance.NeutralSignals
                .Where(IsPresentOnMember)
                .Select(signal => signal.Clone())
                .ToList();

            foreach (var proofValue in memberProof?.Values ?? Array.Empty<MatchIdentityProofValue>())
            {
                provenance.SupportingSignals.Add(new MatchSignal
                {
                    Type = proofValue.Role switch
                    {
                        MatchIdentityRole.Author => "author",
                        MatchIdentityRole.Title => "title",
                        MatchIdentityRole.ProviderIdentifier => "provider_identifier",
                        _ => null
                    },
                    Scope = proofValue.Scope,
                    Source = proofValue.Source,
                    Field = proofValue.Field,
                    Observed = LimitSignalValue(proofValue.Observed),
                    Expected = LimitSignalValue(proofValue.Expected),
                    Detail = LimitSignalValue(proofValue.Detail)
                });
            }

            provenance.ExcludedSignals = embeddedTags.Keys
                .Where(TagExclusionPolicy.IsExcludedFromMatching)
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
                .Select(field => new MatchSignal
                {
                    Type = "ignored_tag",
                    Scope = "metadata",
                    Source = "embedded_tag",
                    Field = field,
                    Detail = "This field was excluded from fuzzy/content matching by the shared tag policy."
                })
                .ToList();
            provenance.EvidenceValues = BuildEvidenceValuesFromSignals(provenance, allEvidence, "embedded_tag");
            return provenance;
        }

        private static string BuildDecisionRoute(string evidenceRoute, bool unscoped, bool authorScoped)
        {
            var scope = unscoped ? "unscoped" : authorScoped ? "author_scoped" : "global";
            return $"{scope}/{evidenceRoute}";
        }

        private IdentifierCandidateProof TryBuildIdentifierCandidateProof(
            Edition edition,
            IReadOnlyCollection<IdentifierEvidenceCandidate> winningEvidence,
            IDictionary<string, List<string>> matchableTags,
            int? restrictToAuthorId,
            out string failureCode)
        {
            failureCode = null;
            if (edition?.Book == null || _containmentValidator == null)
            {
                failureCode = "IDENTIFIER_CANDIDATE_INCOMPLETE";
                return null;
            }

            var proofTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in matchableTags ?? new Dictionary<string, List<string>>())
            {
                proofTags[pair.Key] = (pair.Value ?? new List<string>()).ToList();
            }

            // The leaf filename is valid evidence for this route even though generic path matching
            // remains excluded. Never add parent directories.
            foreach (var value in (winningEvidence ?? Array.Empty<IdentifierEvidenceCandidate>())
                         .Where(value => value != null &&
                                         string.Equals(value.Source, "filename", StringComparison.OrdinalIgnoreCase) &&
                                         !string.IsNullOrWhiteSpace(value.Field) &&
                                         !string.IsNullOrWhiteSpace(value.Observed)))
            {
                if (!proofTags.TryGetValue(value.Field, out var values))
                {
                    values = new List<string>();
                    proofTags[value.Field] = values;
                }

                if (!values.Contains(value.Observed, StringComparer.Ordinal))
                {
                    values.Add(value.Observed);
                }
            }

            var book = edition.Book;
            if (book.Author == null && _authorService != null)
            {
                try
                {
                    book.Author = _authorService.GetAuthor(book.AuthorId);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[HOLY-GRAIL] Could not load author proof for identifier candidate EditionId={0}", edition.Id);
                }
            }

            var trustedAuthorScope = false;
            string provenAuthorName;
            if (restrictToAuthorId.HasValue)
            {
                if (book.AuthorId != restrictToAuthorId.Value)
                {
                    failureCode = "IDENTIFIER_AUTHOR_SCOPE_MISMATCH";
                    return null;
                }

                trustedAuthorScope = true;
                provenAuthorName = book.Author?.Name;
            }
            else
            {
                var identityNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(book.Author?.Name))
                {
                    identityNames.Add(book.Author.Name);
                }

                identityNames.AddRange(book.Author?.Pseudonyms ?? new List<string>());
                provenAuthorName = identityNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(name => _containmentValidator.ValidateAuthorInTags(name, proofTags));
                if (provenAuthorName == null)
                {
                    failureCode = "IDENTIFIER_AUTHOR_PROOF_FAILED";
                    return null;
                }
            }

            string provenTitle = null;
            IReadOnlyList<EditionTitleEvidence> titleEvidence = Array.Empty<EditionTitleEvidence>();
            // A provider identifier selects an edition, so a stored edition title
            // must prove that edition. Falling back to a generic work title would
            // let "It" validate a specific translated/split volume. Use the work
            // title only when the edition genuinely has no title.
            var candidateTitles = !string.IsNullOrWhiteSpace(edition.Title)
                ? new[] { edition.Title }
                : new[] { book.Title };
            foreach (var title in candidateTitles
                         .Where(title => !string.IsNullOrWhiteSpace(title))
                         .Select(title => title.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var evidence = _containmentValidator.GetEditionTitleEvidence(title, proofTags);
                if (evidence?.Count > 0)
                {
                    provenTitle = title;
                    titleEvidence = evidence;
                    break;
                }
            }

            if (provenTitle == null)
            {
                failureCode = "IDENTIFIER_TITLE_PROOF_FAILED";
                return null;
            }

            return new IdentifierCandidateProof
            {
                Tags = proofTags,
                AuthorName = provenAuthorName,
                TrustedAuthorScope = trustedAuthorScope,
                Title = provenTitle,
                TitleEvidence = titleEvidence
            };
        }

        private MatchProvenance BuildIdentifierMatchProvenance(
            IReadOnlyCollection<IdentifierEvidenceCandidate> evidence,
            BookMatchingStrictness strictness,
            out MatchIdentityProof identityProof,
            IDictionary<string, List<string>> embeddedTags,
            IdentifierCandidateProof candidateProof)
        {
            var provenance = new MatchProvenance
            {
                Mode = strictness.ToString(),
                MatchedVia = "provider_identifier",
                Summary = "Matched by exact provider identifier confirmed by author and title proof"
            };

            var matchingValues = (evidence ?? Array.Empty<IdentifierEvidenceCandidate>())
                .Where(value => value != null &&
                                !string.IsNullOrWhiteSpace(value.Identifier) &&
                                !string.IsNullOrWhiteSpace(value.Field) &&
                                !string.IsNullOrWhiteSpace(value.Observed))
                .GroupBy(
                    value => $"{value.Source}\u001f{value.Field}\u001f{value.Observed}\u001f{value.Identifier}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var identityValues = matchingValues.Select(value =>
                new MatchIdentityProofValue(
                    MatchIdentityRole.ProviderIdentifier,
                    value.Source,
                    value.Field,
                    value.Observed,
                    value.Identifier,
                    "edition",
                    "An exact ASIN/Audible ASIN lookup selected this edition candidate."))
                .ToList();

            foreach (var value in matchingValues)
            {
                provenance.SupportingSignals.Add(new MatchSignal
                {
                    Type = "provider_identifier",
                    Scope = "edition",
                    Source = value.Source,
                    Field = value.Field,
                    Observed = value.Observed,
                    Expected = value.Identifier,
                    Detail = "An exact ASIN/Audible ASIN lookup selected this edition candidate."
                });
            }

            if (candidateProof?.TrustedAuthorScope == true)
            {
                var scopedAuthor = string.IsNullOrWhiteSpace(candidateProof.AuthorName)
                    ? "caller-selected author scope"
                    : candidateProof.AuthorName;
                identityValues.Add(new MatchIdentityProofValue(
                    MatchIdentityRole.Author,
                    "caller_scope",
                    "author_scope",
                    scopedAuthor,
                    scopedAuthor,
                    "book",
                    "The identifier candidate belongs to the caller-restricted author."));
                provenance.SupportingSignals.Add(new MatchSignal
                {
                    Type = "author",
                    Scope = "book",
                    Source = "caller_scope",
                    Field = "author_scope",
                    Observed = LimitSignalValue(scopedAuthor),
                    Expected = LimitSignalValue(scopedAuthor),
                    Detail = "The identifier candidate belongs to the caller-restricted author."
                });
            }
            else
            {
                foreach (var authorEvidence in BuildRawAuthorEvidenceTags(candidateProof?.AuthorName, candidateProof?.Tags))
                {
                    foreach (var value in authorEvidence.Value ?? new List<string>())
                    {
                        var source = matchingValues.Any(match =>
                            string.Equals(match.Source, "filename", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(match.Field, authorEvidence.Key, StringComparison.OrdinalIgnoreCase))
                            ? "filename"
                            : "embedded_tag";
                        identityValues.Add(new MatchIdentityProofValue(
                            MatchIdentityRole.Author,
                            source,
                            authorEvidence.Key,
                            value,
                            candidateProof.AuthorName,
                            "book",
                            "This logical field proved the identifier candidate's author."));
                        provenance.SupportingSignals.Add(new MatchSignal
                        {
                            Type = "author",
                            Scope = "book",
                            Source = source,
                            Field = authorEvidence.Key,
                            Observed = LimitSignalValue(value),
                            Expected = LimitSignalValue(candidateProof.AuthorName),
                            Detail = "This logical field proved the identifier candidate's author."
                        });
                    }
                }
            }

            foreach (var titleEvidence in candidateProof?.TitleEvidence ?? Array.Empty<EditionTitleEvidence>())
            {
                var source = matchingValues.Any(match =>
                    string.Equals(match.Source, "filename", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(match.Field, titleEvidence.FieldName, StringComparison.OrdinalIgnoreCase))
                    ? "filename"
                    : "embedded_tag";
                identityValues.Add(new MatchIdentityProofValue(
                    MatchIdentityRole.Title,
                    source,
                    titleEvidence.FieldName,
                    titleEvidence.FieldValue,
                    candidateProof.Title,
                    "edition",
                    "This logical field proved the identifier candidate's book or edition title."));
                provenance.SupportingSignals.Add(new MatchSignal
                {
                    Type = "title",
                    Scope = "edition",
                    Source = source,
                    Field = titleEvidence.FieldName,
                    Observed = LimitSignalValue(titleEvidence.FieldValue),
                    Expected = LimitSignalValue(candidateProof.Title),
                    Detail = "This logical field proved the identifier candidate's book or edition title."
                });
            }

            identityProof = new MatchIdentityProof(identityValues);
            provenance.EvidenceValues = BuildEvidenceValuesFromSignals(
                provenance,
                candidateProof?.Tags ?? embeddedTags,
                "embedded_tag");
            return provenance;
        }

        private static void FinalizeMatchProvenance(
            FileMatch match,
            IDictionary<string, List<string>> embeddedTags,
            BookMatchingStrictness strictness,
            string route)
        {
            if (match == null)
            {
                return;
            }

            match.Provenance ??= new MatchProvenance
            {
                Summary = "Matched without a structured evidence record"
            };
            match.Provenance.Mode ??= strictness.ToString();
            match.Provenance.Route = route;
            match.Provenance.MatchedVia ??= match.MatchedVia;

            foreach (var field in (embeddedTags ?? new Dictionary<string, List<string>>())
                         .Keys
                         .Where(TagExclusionPolicy.IsExcludedFromMatching)
                         .Where(field => !string.IsNullOrWhiteSpace(field))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
            {
                if (match.Provenance.ExcludedSignals.Any(signal =>
                        signal != null &&
                        string.Equals(signal.Field, field, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                match.Provenance.ExcludedSignals.Add(new MatchSignal
                {
                    Type = "ignored_tag",
                    Scope = "metadata",
                    Source = "embedded_tag",
                    Field = field,
                    Detail = "This field was excluded from fuzzy/content matching by the shared tag policy."
                });
            }
        }

        private sealed class IdentifierCandidateProof
        {
            public Dictionary<string, List<string>> Tags { get; init; }
            public string AuthorName { get; init; }
            public bool TrustedAuthorScope { get; init; }
            public string Title { get; init; }
            public IReadOnlyList<EditionTitleEvidence> TitleEvidence { get; init; }
        }

        private sealed class IdentifierEvidenceCandidate
        {
            public string Identifier { get; init; }
            public string Source { get; init; }
            public string Field { get; init; }
            public string Observed { get; init; }
        }

        private List<IdentifierEvidenceCandidate> ExtractIdentifierEvidence(
            DiscoveredFileWithMetadata file,
            IDictionary<string, List<string>> tags)
        {
            var evidence = new List<IdentifierEvidenceCandidate>();

            // Identifier discovery follows the same uniform exclusion policy as every
            // other matching surface. Field labels do not receive special authority;
            // the exact catalog lookup decides whether a token is real evidence.
            foreach (var pair in (tags ?? new Dictionary<string, List<string>>())
                         .Where(pair => !TagExclusionPolicy.IsExcludedFromMatching(pair.Key)))
            {
                foreach (var rawValue in (pair.Value ?? new List<string>())
                             .Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    foreach (Match match in AsinRegex.Matches(rawValue))
                    {
                        evidence.Add(new IdentifierEvidenceCandidate
                        {
                            Identifier = match.Value,
                            Source = "embedded_tag",
                            Field = pair.Key,
                            Observed = rawValue
                        });
                    }
                }
            }

            // The leaf filename is evidence for every route. Deliberately do not scan
            // parent directories: one identifier-shaped ancestor must not affect an
            // entire root or author tree.
            var fileName = Path.GetFileName(file?.Path);
            var fileNameValue = NormalizeForPathTokens(Path.GetFileNameWithoutExtension(file?.Path));
            if (!string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(fileNameValue))
            {
                foreach (Match match in AsinRegex.Matches(fileName))
                {
                    evidence.Add(new IdentifierEvidenceCandidate
                    {
                        Identifier = match.Value,
                        Source = "filename",
                        Field = "PATH:FILE_VALUE",
                        Observed = fileNameValue
                    });
                }
            }

            return evidence;
        }

        private Edition TryShortCircuitByIdentifierCandidates(
            IEnumerable<IdentifierEvidenceCandidate> identifierEvidence,
            BookMediaType mediaType,
            out IReadOnlyList<IdentifierEvidenceCandidate> winningEvidence)
        {
            winningEvidence = Array.Empty<IdentifierEvidenceCandidate>();
            if (_editionRepository == null || identifierEvidence == null)
            {
                return null;
            }

            var evidence = identifierEvidence
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Identifier))
                .ToList();
            var resolved = new List<(string Identifier, Edition Edition)>();
            foreach (var asin in evidence
                         .Select(value => value.Identifier.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var edition in _editionRepository.FindAllByAsin(asin, mediaType) ?? new List<Edition>())
                {
                    if (edition?.Book != null && edition.Book.MediaType == mediaType)
                    {
                        resolved.Add((asin, edition));
                    }
                }
            }

            if (resolved.Count == 0)
            {
                return null;
            }

            // Local copy rows are not distinct provider editions. Build connected
            // provider-identity components so rows sharing any provider-owned edition
            // identifier collapse before we decide whether the evidence is unique.
            var identityGroups = new List<List<(string Identifier, Edition Edition)>>();
            foreach (var hit in resolved)
            {
                var matchingGroups = identityGroups
                    .Where(group => group.Any(existing =>
                        IdentifierHitsShareProviderIdentity(existing, hit)))
                    .ToList();

                if (matchingGroups.Count == 0)
                {
                    identityGroups.Add(new List<(string Identifier, Edition Edition)> { hit });
                    continue;
                }

                var target = matchingGroups[0];
                target.Add(hit);
                foreach (var extra in matchingGroups.Skip(1))
                {
                    target.AddRange(extra);
                    identityGroups.Remove(extra);
                }
            }

            if (identityGroups.Count != 1)
            {
                _logger.Debug(
                    "[HOLY-GRAIL] Identifier evidence resolved to {0} distinct provider editions; continuing normal matching",
                    identityGroups.Count);
                return null;
            }

            var winner = identityGroups[0];
            var winningIdentifiers = winner
                .Select(hit => hit.Identifier)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            winningEvidence = evidence
                .Where(value => winningIdentifiers.Contains(value.Identifier))
                .ToList();

            // This local row is only the canonical input to the existing destination
            // router; copy-row ownership is resolved later by BookUnitDestinationService.
            var selected = winner
                .Select(hit => hit.Edition)
                .OrderBy(edition => edition.Book?.UnitKeyHash.IsNotNullOrWhiteSpace() == true)
                .ThenBy(edition => edition.Book?.BaseBookId.IsNotNullOrWhiteSpace() == true)
                .ThenBy(edition => BookEditionIdentity.GetTrustedForeignEditionId(edition) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(edition => edition.Id)
                .First();

            _logger.Debug(
                "[HOLY-GRAIL] SHORT-CIRCUIT: Identifier(s) '{0}' resolved to one provider edition, Title='{1}'",
                string.Join(", ", winningIdentifiers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                selected.Title);
            return selected;
        }

        private static bool IdentifierHitsShareProviderIdentity(
            (string Identifier, Edition Edition) left,
            (string Identifier, Edition Edition) right)
        {
            if (string.Equals(left.Identifier, right.Identifier, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var leftProviderIds = new HashSet<string>(
                BookEditionIdentity.GetRemoteEditionRehomeTokens(left.Edition),
                StringComparer.OrdinalIgnoreCase);
            return leftProviderIds.Count > 0 &&
                   BookEditionIdentity.GetRemoteEditionRehomeTokens(right.Edition).Any(leftProviderIds.Contains);
        }

        /// <summary>
        /// Create FileMatch from an Edition after provider selection and author/title proof.
        /// </summary>
        private FileMatch CreateFileMatchFromEdition(
            DiscoveredFileWithMetadata file,
            Edition edition,
            MatchProvenance provenance = null,
            MatchIdentityProof identityProof = null)
        {
            return new FileMatch
            {
                File = file,
                AuthorId = edition.Book?.AuthorId ?? 0,
                AuthorName = edition.Book?.Author?.Name ?? string.Empty,
                BookId = edition.BookId,
                BookTitle = edition.Book?.Title ?? edition.Title,
                EditionId = edition.Id,
                MatchedVia = provenance?.MatchedVia,
                Provenance = provenance,
                IdentityProof = identityProof
            };
        }

        /// <summary>
        /// Build path components as a tags dictionary for path-based smoke testing.
        /// When falling back to path matching, we must smoke test against path, not tags.
        /// </summary>
        private Dictionary<string, List<string>> BuildPathAsTags(string filePath)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return tags;
            }

            try
            {
                var filename = Path.GetFileNameWithoutExtension(filePath);
                var directory = Path.GetDirectoryName(filePath);
                var bookFolder = !string.IsNullOrWhiteSpace(directory) ? Path.GetFileName(directory) : null;
                var firstFolderUnderRoot = GetFirstFolderUnderRoot(filePath);
                var folderName = !string.IsNullOrWhiteSpace(firstFolderUnderRoot) ? Path.GetFileName(firstFolderUnderRoot) : null;

                if (!string.IsNullOrWhiteSpace(filename))
                {
                    tags["FILENAME"] = new List<string> { filename };
                }

                if (!string.IsNullOrWhiteSpace(bookFolder))
                {
                    tags["BOOKFOLDER"] = new List<string> { bookFolder };
                }

                if (!string.IsNullOrWhiteSpace(folderName))
                {
                    tags["AUTHORFOLDER"] = new List<string> { folderName };
                }

                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    tags["PATH"] = new List<string> { filePath };
                }
            }
            catch
            {
                // Ignore path parsing errors
            }

            return tags;
        }
    }
}
