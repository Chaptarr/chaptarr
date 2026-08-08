using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public static class BookEditionIdentity
    {
        public static void ClearBookLevelEditionIdentity(Book book)
        {
            if (book == null)
            {
                return;
            }

            book.GoodreadsBookId = null;
            book.ISBN10 = null;
            book.ISBN13 = null;
            book.OpenLibraryEditionId = null;
            book.GoogleBooksId = null;
            book.ASIN = null;
            book.AudibleASIN = null;
        }

        public static Edition GetMonitoredEdition(Book book)
        {
            if (book?.Editions == null)
            {
                return null;
            }

            return book.Editions
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id <= 0 ? int.MaxValue : e.Id)
                .ThenBy(e => e.ForeignEditionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        public static List<string> GetCanonicalWorkProviderIds(Book book)
        {
            var ids = new List<string>();
            if (book == null)
            {
                return ids;
            }

            Add(ids, NormalizeProviderIdOrNull(book.HardcoverBookId, "hc"));
            Add(ids, NormalizeProviderIdOrNull(book.GoodreadsWorkId, "gr"));
            Add(ids, NormalizeProviderIdOrNull(book.OpenLibraryWorkId, "ol"));

            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool HasCanonicalWorkProviderId(Book book, string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var normalized = providerId.Trim().Contains(":")
                ? ImportLists.ImportListProviderIdHelper.Normalize(providerId.Trim(), null)
                : providerId.Trim();

            return GetCanonicalWorkProviderIds(book)
                .Any(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ProviderIdHelper.StripPrefix(id), normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static List<string> GetCanonicalEditionProviderIds(Book book, Logger logger = null, string context = null)
        {
            var ids = new List<string>();

            foreach (var edition in GetOrderedEditions(book))
            {
                Add(ids, NormalizeHardcoverEditionId(edition?.HardcoverEditionId));
                Add(ids, NormalizeGoodreadsEditionId(edition?.GoodreadsEditionId));
                Add(ids, NormalizeProviderId(edition?.OpenLibraryEditionId, "ol"));
                Add(ids, NormalizeProviderId(edition?.GoogleBooksEditionId, "gb"));
                Add(ids, NormalizeAmazonProviderId(edition?.Asin));
                Add(ids, NormalizeAmazonProviderId(edition?.AudibleASIN));
            }

            if (ids.Count > 0)
            {
                return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            Add(ids, Fallback(book?.GoodreadsBookId, "GoodreadsBookId", logger, context, v => NormalizeProviderId(v, "gr")));
            Add(ids, Fallback(book?.OpenLibraryEditionId, "OpenLibraryEditionId", logger, context, v => NormalizeProviderId(v, "ol")));
            Add(ids, Fallback(book?.GoogleBooksId, "GoogleBooksId", logger, context, v => NormalizeProviderId(v, "gb")));
            Add(ids, Fallback(book?.ASIN, "ASIN", logger, context, NormalizeAmazonProviderId));
            Add(ids, Fallback(book?.AudibleASIN, "AudibleASIN", logger, context, NormalizeAmazonProviderId));

            return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> GetEditionLookupIds(Book book, Logger logger = null, string context = null)
        {
            var ids = new List<string>();
            ids.AddRange(GetCanonicalEditionProviderIds(book, logger, context));
            ids.AddRange(ids.Select(ProviderIdHelper.StripPrefix));

            foreach (var edition in GetOrderedEditions(book))
            {
                Add(ids, edition?.Isbn10?.Trim());
                Add(ids, edition?.Isbn13?.Trim());
            }

            if (ids.Count == 0)
            {
                Add(ids, Fallback(book?.ISBN10, "ISBN10", logger, context, v => v?.Trim()));
                Add(ids, Fallback(book?.ISBN13, "ISBN13", logger, context, v => v?.Trim()));
            }

            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetGoodreadsEditionProviderId(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                var id = NormalizeGoodreadsEditionId(edition?.GoodreadsEditionId);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            return Fallback(book?.GoodreadsBookId, "GoodreadsBookId", logger, context, v => NormalizeProviderId(v, "gr"));
        }

        public static string GetOpenLibraryEditionId(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                var id = NormalizeProviderId(edition?.OpenLibraryEditionId, "ol");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            return Fallback(book?.OpenLibraryEditionId, "OpenLibraryEditionId", logger, context, v => NormalizeProviderId(v, "ol"));
        }

        public static string GetGoogleBooksEditionId(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                var id = NormalizeProviderId(edition?.GoogleBooksEditionId, "gb");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            return Fallback(book?.GoogleBooksId, "GoogleBooksId", logger, context, v => NormalizeProviderId(v, "gb"));
        }

        public static string GetAsin(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                var id = NormalizeAmazonExternalId(edition?.Asin);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            return Fallback(book?.ASIN, "ASIN", logger, context, NormalizeAmazonExternalId);
        }

        public static string GetAudibleAsin(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                var id = NormalizeAmazonExternalId(edition?.AudibleASIN);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            return Fallback(book?.AudibleASIN, "AudibleASIN", logger, context, NormalizeAmazonExternalId);
        }

        public static string GetIsbn10(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                if (!string.IsNullOrWhiteSpace(edition?.Isbn10))
                {
                    return edition.Isbn10.Trim();
                }
            }

            return Fallback(book?.ISBN10, "ISBN10", logger, context, v => v?.Trim());
        }

        public static string GetIsbn13(Book book, Logger logger = null, string context = null)
        {
            foreach (var edition in GetOrderedEditions(book))
            {
                if (!string.IsNullOrWhiteSpace(edition?.Isbn13))
                {
                    return edition.Isbn13.Trim();
                }
            }

            return Fallback(book?.ISBN13, "ISBN13", logger, context, v => v?.Trim());
        }

        public static bool HasCanonicalEditionProviderId(Book book, string providerId, Logger logger = null, string context = null)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var trimmed = providerId.Trim();
            var normalized = trimmed.Contains(":")
                ? ImportLists.ImportListProviderIdHelper.Normalize(trimmed, null)
                : trimmed;

            return GetCanonicalEditionProviderIds(book, logger, context)
                .Any(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ProviderIdHelper.StripPrefix(id), normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static List<string> GetEditionProviderIds(Edition edition)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (edition == null)
            {
                return ids.ToList();
            }

            AddEditionProviderIdVariants(ids, edition.ForeignEditionId);
            AddHardcoverEditionProviderIdVariants(ids, edition.HardcoverEditionId);
            AddEditionProviderIdVariants(ids, NormalizeGoodreadsEditionId(edition.GoodreadsEditionId));
            AddEditionProviderIdVariants(ids, NormalizeProviderIdOrNull(edition.OpenLibraryEditionId, "ol"));
            AddEditionProviderIdVariants(ids, NormalizeProviderIdOrNull(edition.GoogleBooksEditionId, "gb"));
            AddAmazonProviderIdVariants(ids, edition.AudibleASIN);
            AddAmazonProviderIdVariants(ids, edition.Asin);

            foreach (var asin in edition.Asins ?? Enumerable.Empty<string>())
            {
                AddAmazonProviderIdVariants(ids, asin);
            }

            AddProviderId(ids, edition.Isbn13);
            AddProviderId(ids, edition.Isbn10);

            return ids.ToList();
        }

        public static List<string> GetEditionRehomeTokens(Edition edition)
        {
            if (edition == null)
            {
                return new List<string>();
            }

            var stable = GetStableEditionRehomeTokens(edition);
            if (stable.Any())
            {
                return stable;
            }

            return GetAmazonEditionRehomeTokens(edition);
        }

        public static List<string> GetRemoteEditionRehomeTokens(Edition edition)
        {
            if (edition == null)
            {
                return new List<string>();
            }

            return GetStableEditionRehomeTokens(edition)
                .Concat(GetAmazonEditionRehomeTokens(edition))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(token => token, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> GetStableEditionRehomeTokens(Edition edition)
        {
            var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (edition == null)
            {
                return stable.ToList();
            }

            AddHardcoverEditionRehomeToken(stable, edition.HardcoverEditionId);
            if (edition.GoodreadsEditionId.HasValue && edition.GoodreadsEditionId.Value > 0)
            {
                stable.Add($"gr:{edition.GoodreadsEditionId.Value}");
            }

            AddCanonicalRehomeToken(stable, edition.OpenLibraryEditionId, "ol");
            AddCanonicalRehomeToken(stable, edition.GoogleBooksEditionId, "gb");
            AddStableRehomeTokenFromForeignEditionId(stable, edition.ForeignEditionId);

            return stable.OrderBy(token => token, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> GetAmazonEditionRehomeTokens(Edition edition)
        {
            var amazon = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (edition == null)
            {
                return amazon.ToList();
            }

            AddAmazonRehomeToken(amazon, edition.Asin);
            AddAmazonRehomeToken(amazon, edition.AudibleASIN);
            foreach (var asin in edition.Asins ?? Enumerable.Empty<string>())
            {
                AddAmazonRehomeToken(amazon, asin);
            }

            return amazon.OrderBy(token => token, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string GetTrustedForeignEditionId(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            var hardcover = NormalizeHardcoverEditionIdValue(edition.HardcoverEditionId);
            if (!string.IsNullOrWhiteSpace(hardcover))
            {
                return $"hc:edition:{hardcover}";
            }

            var goodreads = NormalizeGoodreadsEditionId(edition.GoodreadsEditionId);
            if (!string.IsNullOrWhiteSpace(goodreads))
            {
                return goodreads;
            }

            var audibleAsin = NormalizeAmazonProviderId(edition.AudibleASIN);
            if (!string.IsNullOrWhiteSpace(audibleAsin))
            {
                return audibleAsin;
            }

            var asin = NormalizeAmazonProviderId(edition.Asin);
            if (!string.IsNullOrWhiteSpace(asin))
            {
                return asin;
            }

            foreach (var value in edition.Asins ?? Enumerable.Empty<string>())
            {
                var normalized = NormalizeAmazonProviderId(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            var existing = NormalizeTrustedForeignEditionId(edition.ForeignEditionId);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            return null;
        }

        public static string GetReadarrFacadeHardcoverEditionId(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            var hardcoverEditionId = NormalizeHardcoverEditionIdValue(edition.HardcoverEditionId);
            if (!string.IsNullOrWhiteSpace(hardcoverEditionId))
            {
                return hardcoverEditionId;
            }

            return NormalizeHardcoverEditionIdValue(edition.ForeignEditionId);
        }

        public static string GetReadarrFacadeGoodreadsEditionId(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            var goodreadsEditionId = NormalizeGoodreadsEditionId(edition.GoodreadsEditionId);
            if (!string.IsNullOrWhiteSpace(goodreadsEditionId))
            {
                return ProviderIdHelper.StripPrefix(goodreadsEditionId);
            }

            var foreignEditionId = StripKnownEditionMediaSuffix(edition.ForeignEditionId);
            if (string.IsNullOrWhiteSpace(foreignEditionId) ||
                !foreignEditionId.StartsWith("gr:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.StripPrefix(ProviderIdHelper.Canonicalize(foreignEditionId, "gr"));
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static bool EditionMatchesProviderId(Edition edition, string providerId)
        {
            if (edition == null || string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var editionIds = new HashSet<string>(GetEditionProviderIds(edition), StringComparer.OrdinalIgnoreCase);
            if (editionIds.Count == 0)
            {
                return false;
            }

            var requestedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddEditionProviderIdVariants(requestedIds, providerId);
            AddHardcoverEditionProviderIdVariants(requestedIds, providerId);

            return requestedIds.Any(editionIds.Contains);
        }

        public static bool EditionsMatch(Edition left, Edition right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            var leftIds = new HashSet<string>(GetEditionProviderIds(left), StringComparer.OrdinalIgnoreCase);
            return leftIds.Count > 0 && GetEditionProviderIds(right).Any(leftIds.Contains);
        }

        private static List<Edition> GetOrderedEditions(Book book)
        {
            if (book?.Editions == null)
            {
                return new List<Edition>();
            }

            var editions = book.Editions
                .Where(e => e != null)
                .ToList();

            return editions
                .OrderByDescending(e => e.Monitored ? 1 : 0)
                .ThenByDescending(e => e.ManualAdd ? 1 : 0)
                .ThenBy(e => e.Id <= 0 ? int.MaxValue : e.Id)
                .ThenBy(e => e.ForeignEditionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeProviderId(string value, string expectedPrefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(expectedPrefix) &&
                !value.Contains(":", StringComparison.Ordinal))
            {
                return null;
            }

            return ProviderIdHelper.Canonicalize(value.Trim(), expectedPrefix);
        }

        private static string NormalizeProviderIdOrNull(string value, string expectedPrefix)
        {
            try
            {
                return NormalizeProviderId(value, expectedPrefix);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static string NormalizeGoodreadsEditionId(long? id)
        {
            return id.HasValue && id.Value > 0
                ? $"gr:{id.Value}"
                : null;
        }

        private static string NormalizeHardcoverEditionId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return null;
            }

            return value.Trim();
        }

        private static string NormalizeAmazonExternalId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return null;
            }

            return ProviderIdHelper.StripPrefix(value).Trim().ToUpperInvariant();
        }

        private static string NormalizeAmazonProviderId(string value)
        {
            var asin = NormalizeAmazonExternalId(value);
            return string.IsNullOrWhiteSpace(asin)
                ? null
                : $"az:{asin}";
        }

        private static string NormalizeTrustedForeignEditionId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return null;
            }

            var trimmed = value.Trim();

            if (trimmed.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = NormalizeHardcoverEditionIdValue(trimmed);
                return string.IsNullOrWhiteSpace(raw) ? null : $"hc:edition:{raw}";
            }

            if (trimmed.StartsWith("az:", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeAmazonProviderId(trimmed);
            }

            return null;
        }

        private static void AddStableRehomeTokenFromForeignEditionId(HashSet<string> tokens, string foreignEditionId)
        {
            var value = StripKnownEditionMediaSuffix(foreignEditionId);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (value.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase))
            {
                AddHardcoverEditionRehomeToken(tokens, value);
                return;
            }

            if (value.StartsWith("gr:", StringComparison.OrdinalIgnoreCase))
            {
                AddCanonicalRehomeToken(tokens, value, "gr");
                return;
            }

            if (value.StartsWith("ol:", StringComparison.OrdinalIgnoreCase))
            {
                AddCanonicalRehomeToken(tokens, value, "ol");
                return;
            }

            if (value.StartsWith("gb:", StringComparison.OrdinalIgnoreCase))
            {
                AddCanonicalRehomeToken(tokens, value, "gb");
            }
        }

        private static void AddHardcoverEditionRehomeToken(HashSet<string> tokens, string value)
        {
            value = StripKnownEditionMediaSuffix(value);
            if (string.IsNullOrWhiteSpace(value) || ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return;
            }

            var raw = value.Trim();
            if (raw.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring("hc:edition:".Length);
            }

            raw = ProviderIdHelper.StripPrefix(raw)?.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains(":"))
            {
                return;
            }

            tokens.Add($"hc:edition:{raw}");
        }

        private static void AddCanonicalRehomeToken(HashSet<string> tokens, string value, string expectedPrefix)
        {
            value = StripKnownEditionMediaSuffix(value);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            try
            {
                var normalized = ProviderIdHelper.Canonicalize(value, expectedPrefix);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    tokens.Add(normalized);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void AddAmazonRehomeToken(HashSet<string> tokens, string value)
        {
            value = StripKnownEditionMediaSuffix(value);
            if (string.IsNullOrWhiteSpace(value) || ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return;
            }

            var raw = ProviderIdHelper.StripPrefix(value)?.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains(":"))
            {
                return;
            }

            try
            {
                var normalized = ProviderIdHelper.WithPrefix("az", raw);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    tokens.Add(normalized);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void AddEditionProviderIdVariants(HashSet<string> ids, string value)
        {
            AddProviderIdVariants(ids, value);

            var trimmed = value?.Trim();
            var withoutMediaSuffix = StripKnownEditionMediaSuffix(trimmed);
            if (!string.Equals(trimmed, withoutMediaSuffix, StringComparison.OrdinalIgnoreCase))
            {
                AddProviderIdVariants(ids, withoutMediaSuffix);
            }
        }

        private static void AddHardcoverEditionProviderIdVariants(HashSet<string> ids, string value)
        {
            var raw = NormalizeHardcoverEditionIdValue(value);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            AddProviderId(ids, raw);
            AddProviderId(ids, $"hc:{raw}");
            AddProviderId(ids, $"hc:edition:{raw}");
        }

        private static void AddAmazonProviderIdVariants(HashSet<string> ids, string value)
        {
            var asin = NormalizeAmazonExternalId(value);
            if (string.IsNullOrWhiteSpace(asin))
            {
                return;
            }

            AddProviderId(ids, asin);
            AddProviderId(ids, $"az:{asin}");
        }

        private static void AddProviderIdVariants(HashSet<string> ids, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            AddProviderId(ids, trimmed);

            var withoutMediaSuffix = StripKnownEditionMediaSuffix(trimmed);
            AddProviderId(ids, withoutMediaSuffix);

            if (withoutMediaSuffix.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase) ||
                withoutMediaSuffix.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
            {
                AddHardcoverEditionProviderIdVariants(ids, withoutMediaSuffix);
                return;
            }

            // Do not add stripped raw IDs for provider-prefixed values. gr:123 and hc:123
            // are different provider identities, even though their local numeric parts match.
        }

        private static void AddProviderId(HashSet<string> ids, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ids.Add(value.Trim());
            }
        }

        private static string StripKnownEditionMediaSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                ProviderIdHelper.ContainsProviderIdArtifact(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.EndsWith("-audiobook", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(0, trimmed.Length - "-audiobook".Length)
                : trimmed.EndsWith("-ebook", StringComparison.OrdinalIgnoreCase)
                    ? trimmed.Substring(0, trimmed.Length - "-ebook".Length)
                    : trimmed;
        }

        private static string NormalizeHardcoverEditionIdValue(string value)
        {
            var trimmed = StripKnownEditionMediaSuffix(value);
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (trimmed.Contains(":", StringComparison.Ordinal) &&
                !trimmed.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (trimmed.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("hc:edition:".Length);
            }
            else if (trimmed.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("hc:".Length);
            }

            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        private static string Fallback(string legacyValue, string field, Logger logger, string context, Func<string, string> normalize)
        {
            if (string.IsNullOrWhiteSpace(legacyValue))
            {
                return null;
            }

            var normalized = normalize?.Invoke(legacyValue);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            logger?.Debug("[BOOK-EDITION-FALLBACK] context={0} field={1} value={2}", context ?? "unknown", field, normalized);
            return normalized;
        }

        private static void Add(List<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }
    }
}
