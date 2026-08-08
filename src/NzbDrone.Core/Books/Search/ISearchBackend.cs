using System.Collections.Generic;

namespace NzbDrone.Core.Books.Search
{
    /// <summary>
    /// Abstraction for FTS backends (SQLite FTS5, PostgreSQL tsvector, etc)
    /// </summary>
    public interface ISearchBackend
    {
        /// <summary>
        /// Check if the search backend is available and properly configured
        /// </summary>
        bool IsAvailable();

        /// <summary>
        /// Search authors using the backend-specific FTS implementation
        /// </summary>
        /// <param name="fieldTerms">Terms grouped by field name (e.g., "author" -> ["stephen", "king"])</param>
        /// <param name="limit">Maximum results to return</param>
        /// <returns>List of matching authors with per-field scores</returns>
        List<AuthorSearchResult> SearchAuthors(Dictionary<string, List<string>> fieldTerms, int limit);

        /// <summary>
        /// Search editions for specific authors using the backend-specific FTS implementation
        /// </summary>
        /// <param name="authorIds">Author IDs to search within</param>
        /// <param name="fieldTerms">Terms grouped by field name</param>
        /// <param name="mediaType">Optional media type filter</param>
        /// <param name="limit">Maximum results to return</param>
        /// <returns>List of matching editions with per-field scores</returns>
        List<EditionSearchResult> SearchEditions(List<int> authorIds, Dictionary<string, List<string>> fieldTerms, BookMediaType? mediaType, int limit);

        /// <summary>
        /// Get the backend type name for logging
        /// </summary>
        string BackendType { get; }
    }

    public class AuthorSearchResult
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public Dictionary<string, double> FieldScores { get; set; } = new Dictionary<string, double>();
        public double TotalScore { get; set; }
        public List<string> MatchedTerms { get; set; } = new List<string>();
    }

    public class EditionSearchResult
    {
        public int EditionId { get; set; }
        public string EditionTitle { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public Dictionary<string, double> FieldScores { get; set; } = new Dictionary<string, double>();
        public double TotalScore { get; set; }
        public List<string> MatchedTerms { get; set; } = new List<string>();
    }
}