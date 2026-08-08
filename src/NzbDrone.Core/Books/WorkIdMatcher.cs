using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MetadataSource;

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

            var leftIds = GetWorkProviderIds(left);
            var rightIds = GetWorkProviderIds(right);

            if (leftIds.Count == 0 || rightIds.Count == 0)
            {
                return false;
            }

            return leftIds.Intersect(rightIds, StringComparer.OrdinalIgnoreCase).Any();
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

        private static List<string> GetWorkProviderIds(Book book)
        {
            var ids = new List<string>();

            AddWorkIdOrIgnore(ids, book?.HardcoverBookId, expectedPrefix: "hc");
            AddWorkIdOrIgnore(ids, book?.GoodreadsWorkId, expectedPrefix: "gr");
            AddWorkIdOrIgnore(ids, book?.OpenLibraryWorkId, expectedPrefix: "ol");

            foreach (var providerId in book?.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                AddWorkIdOrIgnore(ids, providerId, expectedPrefix: null);
            }

            return ids
                .Where(id => id.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddWorkIdOrIgnore(List<string> ids, string providerId, string expectedPrefix)
        {
            if (ids == null || providerId.IsNullOrWhiteSpace())
            {
                return;
            }

            string canonical;
            try
            {
                canonical = ProviderIdHelper.Canonicalize(providerId.Trim(), expectedPrefix);
            }
            catch (Exception)
            {
                return;
            }

            if (canonical.IsNullOrWhiteSpace())
            {
                return;
            }

            var colonIndex = canonical.IndexOf(':');
            if (colonIndex <= 0)
            {
                return;
            }

            var prefix = canonical.Substring(0, colonIndex);
            if (!prefix.Equals("hc", StringComparison.OrdinalIgnoreCase) &&
                !prefix.Equals("gr", StringComparison.OrdinalIgnoreCase) &&
                !prefix.Equals("ol", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ids.Add(canonical);
        }
    }
}
