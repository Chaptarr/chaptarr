using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public static class BookSeries
    {
        private static readonly Regex LeadingPosition = new Regex(@"^\s*(?<number>[+-]?[0-9]+(?:\.[0-9]+)?)(?=\s*(?:$|[-–—,]))", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static void LoadLinks(IEnumerable<Book> books, Func<List<int>, List<SeriesBookLink>> load, bool reload = false)
        {
            var pending = books.Where(book => book?.Id > 0 && (reload || book.LazySeriesLinks?.IsLoaded == false)).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            var links = load(pending.Select(book => book.Id).Distinct().ToList())
                .GroupBy(link => link.BookId).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var book in pending)
            {
                book.SeriesLinks = links.TryGetValue(book.Id, out var membership) ? membership : new List<SeriesBookLink>();
            }
        }

        // Memberships own the paired name/position. The book-level summary is not an
        // alternative membership source, even when a persisted book currently has no links.
        public static IEnumerable<SeriesBookLink> GetLinks(Book book)
        {
            return (book?.SeriesLinks ?? Enumerable.Empty<SeriesBookLink>())
                .Where(link => link?.Series?.Value is Series series &&
                    series.MediaType == book.MediaType &&
                    !string.IsNullOrWhiteSpace(series.GoodreadsSeriesId) &&
                    !string.IsNullOrWhiteSpace(series.Title))
                .OrderBy(link => PositionOrder(link.Position))
                .ThenBy(link => link.Series.Value.GoodreadsSeriesId, StringComparer.Ordinal)
                .ThenBy(link => link.Position, StringComparer.Ordinal);
        }

        internal static IEnumerable<string> GetNames(Book book)
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

        public static SeriesBookLink Select(Book book)
        {
            var links = GetLinks(book).ToList();
            return GetHeaderLink(book, links) ?? links
                .OrderByDescending(link => link.IsPrimary == true)
                .ThenBy(link => link.IsPrimary == false)
                .FirstOrDefault();
        }

        public static SeriesBookLink GetHeaderLink(Book book) => book?.SeriesId is > 0
            ? GetHeaderLink(book, GetLinks(book).ToList()) : null;

        internal static SeriesBookLink GetHeaderLink(Book book, IReadOnlyList<SeriesBookLink> links)
        {
            if (book?.SeriesId is not > 0)
            {
                return null;
            }

            // The server identifies a Goodreads membership, not a local Series row.
            var headerSeriesId = ProviderIdHelper.Canonicalize(book.SeriesId.Value.ToString(CultureInfo.InvariantCulture), "gr");
            return links.FirstOrDefault(link => CanonicalGoodreadsId(link.Series.Value.GoodreadsSeriesId) == headerSeriesId);
        }

        private static string CanonicalGoodreadsId(string id)
        {
            try
            {
                return ProviderIdHelper.Canonicalize(id, "gr");
            }
            catch (InvalidOperationException)
            {
                // Unusable legacy identities cannot select a header membership.
                return null;
            }
        }

        public static string Label(SeriesBookLink link)
        {
            return link.Series.Value.Title + (string.IsNullOrWhiteSpace(link.Position) ? string.Empty : $" #{link.Position}");
        }

        // A leading number orders decimal, range and list positions for presentation only.
        // Preserve the complete provider string; position never identifies or merges a book.
        public static decimal PositionOrder(string position)
        {
            var number = LeadingPosition.Match(position ?? string.Empty).Groups["number"].Value;
            return decimal.TryParse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value : decimal.MaxValue;
        }
    }
}
