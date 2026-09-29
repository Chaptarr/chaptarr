using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.IndexerSearch.Definitions
{
    public class BookSearchCriteria : SearchCriteriaBase
    {
        private static readonly Regex BookNumberSuffixRegex = new Regex(@"\s*,?\s*book\s+\d+(\.\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string BookTitle { get; set; }
        public int BookYear { get; set; }
        public string BookIsbn { get; set; }
        public string Disambiguation { get; set; }

        // Title-only query: most indexers accept author/title separately or combine them themselves.
        // Search the main title section; the matcher still validates against the monitored edition title.
        // Including the author here can double-apply author terms (hurting recall) and breaks book-search endpoints
        // that expect a clean title (e.g., Newznab's t=book&author=...&title=...).
        public string BookQuery => GetQueryTitle(GetMainSearchTitle(BookTitle, Author?.Name));

        // Audible-style titles end in a series position ("..., Book 4") that release names almost never
        // carry. Null when the title has no such suffix, so callers can skip the extra search tier.
        public string BookQueryWithoutBookNumber
        {
            get
            {
                var mainTitle = GetMainSearchTitle(BookTitle, Author?.Name)?.Trim();
                if (string.IsNullOrWhiteSpace(mainTitle))
                {
                    return null;
                }

                var withoutBookNumber = BookNumberSuffixRegex.Replace(mainTitle, string.Empty).Trim();
                if (withoutBookNumber.Length == 0 ||
                    string.Equals(withoutBookNumber, mainTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return GetQueryTitle(withoutBookNumber);
            }
        }

        internal static string GetMainSearchTitle(string title, string author)
        {
            var titleWithoutAuthor = RemoveLeadingAuthorPrefix(title, author);
            if (string.IsNullOrWhiteSpace(titleWithoutAuthor))
            {
                return titleWithoutAuthor;
            }

            var mainTitle = titleWithoutAuthor.SplitBookTitle(author).Item1;
            return string.IsNullOrWhiteSpace(mainTitle) ? titleWithoutAuthor : mainTitle;
        }

        internal static string RemoveLeadingAuthorPrefix(string title, string author)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
            {
                return title;
            }

            var trimmedTitle = title.Trim();
            var trimmedAuthor = author.Trim();
            var prefixes = new[]
            {
                $"{trimmedAuthor}:",
                $"{trimmedAuthor} -",
                $"{trimmedAuthor} –",
                $"{trimmedAuthor} —"
            };

            foreach (var prefix in prefixes)
            {
                if (trimmedTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmedTitle.Substring(prefix.Length).Trim();
                }
            }

            return title;
        }

        public override string ToString()
        {
            return $"[{Author.Name} - {BookTitle}]";
        }
    }
}
