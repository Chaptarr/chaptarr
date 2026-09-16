using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Books
{
    public static class BookSeries
    {
        private static readonly Regex LeadingPosition = new Regex(@"^\s*(?<number>[+-]?[0-9]+(?:\.[0-9]+)?)(?=\s*(?:$|[-–—,]))", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        public static SeriesBookLink Select(Book book)
        {
            return GetLinks(book).OrderByDescending(link => link.IsPrimary == true).FirstOrDefault();
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
