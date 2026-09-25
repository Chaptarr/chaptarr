namespace NzbDrone.Core.Books
{
    public static class WorkIdMatcher
    {
        public static bool WorkIdMatches(Book left, Book right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return BookIdentity.MatchesByProviderIdIntersection(left, right);
        }

        /// <summary>
        /// Work-level match only (Hardcover/Goodreads/OpenLibrary work IDs).
        /// Excludes edition-level IDs (ASIN/AudibleASIN, GB edition IDs, etc) so callers don't accidentally treat two
        /// different editions as the "same work" for cross-format operations (delete, colocate, etc).
        /// </summary>
        public static bool WorkProviderIdMatches(Book left, Book right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            var leftIds = BookIdentity.GetStableWorkProviderIdentityTokens(left);
            var rightIds = BookIdentity.GetStableWorkProviderIdentityTokens(right);

            if (leftIds.Count == 0 || rightIds.Count == 0)
            {
                return false;
            }

            return leftIds.Overlaps(rightIds);
        }

        /// <summary>
        /// Cross-format-safe matcher for "same work" grouping.
        /// Same-format comparisons may use broader edition/work identity, but audiobook↔ebook
        /// comparisons must only use work-level provider IDs to avoid linking unrelated editions.
        /// </summary>
        public static bool CrossFormatSafeMatches(Book left, Book right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (left.MediaType != right.MediaType)
            {
                return WorkProviderIdMatches(left, right);
            }

            return WorkIdMatches(left, right);
        }
    }
}
