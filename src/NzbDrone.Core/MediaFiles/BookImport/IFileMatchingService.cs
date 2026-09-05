using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace NzbDrone.Core.MediaFiles.BookImport
{
    public sealed class MatchingContext
    {
        // Manual preview needs an exact verdict for every file, so sibling
        // files must not share cached negative results.
        public bool SuppressNegativeUnitCache { get; set; }

        /// <summary>
        /// When true, call V5 to identify potential authors for unmatched files.
        /// Populates <see cref="UnmatchedFile.PotentialAuthors"/>.
        /// </summary>
        public bool AllowV5Identification { get; set; }

        /// <summary>
        /// When true, the matcher may import authors into the local library as part of author gating.
        /// Prefer keeping this false outside of root-scan style workflows.
        /// </summary>
        public bool AllowAuthorImport { get; set; }

        /// <summary>
        /// When true, defer matching for unrestricted groups to the author-ready handler.
        /// </summary>
        public bool DeferUnmatchedToAuthorReady { get; set; }

        /// <summary>
        /// When true, allow falling back from author-scoped to unscoped matching.
        /// </summary>
        public bool AllowUnscopedFallback { get; set; }

        /// <summary>
        /// When true, path-derived fallback is disabled even if the global setting would normally allow it.
        /// This is intended for scoped/author-confirmed rematch branches that must fail closed.
        /// </summary>
        public bool DisablePathFallback { get; set; }

        /// <summary>
        /// When true, avoid stamping one representative match across a multi-file group and instead return per-file decisions.
        /// </summary>
        public bool PerFileMatching { get; set; }

        /// <summary>
        /// When true with <see cref="PerFileMatching"/>, unmatched identity subgroups may use one grouped V5 suggestion.
        /// This keeps manual preview scans from making a server call per track while still splitting mixed tag identities.
        /// </summary>
        public bool AllowGroupedV5Suggestions { get; set; }

        /// <summary>
        /// Optional: book IDs that the current workflow is targeting (e.g., a completed download grabbed for a specific book).
        /// When provided, the matcher may try a fast "pinned edition first crack" smoke match for these targets before
        /// falling back to general-purpose matching.
        /// </summary>
        public List<int> TargetBookIds { get; set; }

        /// <summary>
        /// Optional hard local retrieval boundary. Every identifier and FTS candidate must belong
        /// to one of these Book rows. Callers may populate it only after resolving a provider work
        /// identity; unlike <see cref="TargetBookIds"/>, matching never widens beyond this set.
        /// </summary>
        public List<int> HardAllowedBookIds { get; set; }

        /// <summary>
        /// Optional harness/debug sink. Production callers leave this null so detailed candidate tracing has no allocation cost.
        /// </summary>
        public IMatchingTraceSink TraceSink { get; set; }

        /// <summary>
        /// Optional cancellation token for request-bound matching work such as manual import preview.
        /// Command/background callers normally leave this unset.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }
    }

    public interface IMatchingTraceSink
    {
        void Record(MatchingTraceEvent evt);
    }

    public sealed class MatchingTraceEvent
    {
        public string EventType { get; set; }
        public string Phase { get; set; }
        public string FilePath { get; set; }
        public int? EditionId { get; set; }
        public int? BookId { get; set; }
        public int? AuthorId { get; set; }
        public double? Score { get; set; }
        public string Title { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
        public int? Rank { get; set; }
        public int? DistinctBookRank { get; set; }
        public long? ElapsedMilliseconds { get; set; }
        public long? TotalElapsedMilliseconds { get; set; }
        public int? ResultCount { get; set; }
        public int? DistinctBookCount { get; set; }
        public List<string> Terms { get; set; }
        public string Columns { get; set; }
        public string Query { get; set; }
        public Dictionary<string, string> Data { get; set; }
    }

    /// <summary>
    /// Read-only service for matching discovered files to existing library entries.
    /// This service NEVER modifies the library - it only identifies matches.
    /// </summary>
    public interface IFileMatchingService
    {
        /// <summary>
        /// Matches discovered files with metadata to existing library entries.
        /// Returns matched files and unmatched files with potential author suggestions.
        /// </summary>
        Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata);
        
        /// <summary>
        /// Matches discovered files but restricts matches to a specific author ID when provided.
        /// Helpful for post-author-import processing to avoid cross-author matches.
        /// </summary>
        Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId);

        /// <summary>
        /// Matches discovered files with an option to indicate download mode (no author-folder short-circuit).
        /// When forDownloads is true, matching never short-circuits and always returns a decision per file.
        /// </summary>
        Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads);

        /// <summary>
        /// Matches discovered files with an explicit context (preferred).
        /// </summary>
        Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context);
        
        /// <summary>
        /// Holy Grail matching: Simple bag-of-words FTS + smoke test.
        /// Extracts all tokens from file tags, runs OR FTS query, validates with smoke test.
        /// Returns the first edition that passes smoke test, or null if none pass.
        /// No weighting, no boosting, no complex algorithms - just BM25 ranking + containment check.
        /// </summary>
        /// <param name="authorId">Author ID to restrict search (null for unrestricted)</param>
        /// <param name="allTagTokens">All unique tokens extracted from file tags</param>
        /// <param name="mediaType">Media type filter (0=Audiobook, 1=Ebook)</param>
        /// <returns>Best matching edition that passes smoke test, or null</returns>
        Books.EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, Books.BookMediaType mediaType);

        /// <summary>
        /// HOLY GRAIL: Complete file matching with full fallback chain.
        /// This is THE method to use for all file matching. Implements blueprint exactly.
        ///
        /// Flow:
        /// 1. Extract main tags (exclude comments) → tokenize → FTS → smoke test
        /// 2. If no match: add path components → retry
        /// 3. ALWAYS smoke test every candidate
        /// </summary>
        /// <param name="file">The discovered file with metadata tags</param>
        /// <param name="mediaType">Media type (0=Audiobook, 1=Ebook) based on file extension</param>
        /// <param name="restrictToAuthorId">Optional: restrict matches to specific author</param>
        /// <returns>FileMatch if found, null otherwise</returns>
        FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, Books.BookMediaType mediaType, int? restrictToAuthorId = null);
    }

    public class FileMatchResult
    {
        public FileMatch[] MatchedFiles { get; set; } = new FileMatch[0];
        public UnmatchedFile[] UnmatchedFiles { get; set; } = new UnmatchedFile[0];

        // Convenience properties
        public FileMatch[] Matched => MatchedFiles;
        public UnmatchedFile[] Unmatched => UnmatchedFiles;
        public AuthorSuggestion[] UnmatchedAuthors
        {
            get
            {
                var authors = new List<AuthorSuggestion>();
                foreach (var unmatched in UnmatchedFiles)
                {
                    if (unmatched.PotentialAuthors != null)
                    {
                        authors.AddRange(unmatched.PotentialAuthors);
                    }
                }
                return authors.Distinct().ToArray();
            }
        }
    }

    public class FileMatch
    {
        public DiscoveredFileWithMetadata File { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public int BookId { get; set; }
        public string BookTitle { get; set; }
        public int EditionId { get; set; }
        public string MatchedVia { get; set; }
        public MatchProvenance Provenance { get; set; }

        // Untruncated decision-time identity proof. This is intentionally internal:
        // persisted/display provenance must never become matching authority.
        internal MatchIdentityProof IdentityProof { get; set; }
    }

    public class UnmatchedFile
    {
        public DiscoveredFileWithMetadata File { get; set; }
        public string Reason { get; set; }
        public AuthorSuggestion[] PotentialAuthors { get; set; } = new AuthorSuggestion[0];
    }

    public class AuthorSuggestion
    {
        public string ProviderId { get; set; } // e.g., "hc:12345"
        public string AuthorName { get; set; }
        public double Confidence { get; set; }
        public string BookProviderId { get; set; } // Work/book provider ID from V5 match (optional)
        public string BookTitle { get; set; }
        public string EditionHardcoverId { get; set; } // Edition ID from V5 match (optional)
        public string EditionTitle { get; set; }
    }
}
