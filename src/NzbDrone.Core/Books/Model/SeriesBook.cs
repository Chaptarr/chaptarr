namespace NzbDrone.Core.Books
{
    /// <summary>
    /// Display metadata for books in a series, populated from API responses.
    /// </summary>
    public class SeriesBook
    {
        /// <summary>
        /// Provider ID from the API with provider prefix (e.g., "hc:123456", "gr:789012", "ol:345678").
        /// This is STRICTLY for handshaking/matching when interacting with the metadata server.
        /// NEVER use this for local database operations - ALL local operations use database IDs.
        /// All book-series relationships are stored in SeriesBookLink with database IDs only.
        ///
        /// Provider prefixes:
        /// - hc: Hardcover
        /// - gr: Goodreads
        /// - ol: OpenLibrary
        /// - gb: Google Books
        /// - au: Audible
        /// </summary>
        public string BookId { get; set; }

        public string Title { get; set; }
        public string Position { get; set; }
        public string CoverUrl { get; set; }

        // Null when the V5 API did not surface a primary flag for this slot. Consumers should
        // default null to true to preserve pre-2026-06-02 behavior for not-yet-refreshed authors.
        public bool? IsPrimary { get; set; }
    }
}
