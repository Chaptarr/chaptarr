using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books
{
    /// <summary>
    /// Picks the single <see cref="SeriesBookLink"/> that represents a book for display, and formats
    /// the "Title #Position" label from it.
    ///
    /// The links are the authoritative series membership: they are reconciled on every refresh and
    /// carry the position that belongs to that specific series. The denormalized
    /// <see cref="Book.SeriesName"/>/<see cref="Book.SeriesPosition"/> pair is stamped once when the
    /// book is added and can drift (wrong translation, wrong number, or a title from one series with
    /// the number from another), so it is only a last-resort fallback for books with no links.
    ///
    /// Because both halves of the label always come from the same link, a book can never be labelled
    /// with one series' title and a different series' number.
    /// </summary>
    public static class BookSeriesLabel
    {
        /// <summary>
        /// Chooses the link that best represents the book:
        /// primary slots first, then links that actually carry a position, then the most specific
        /// series (an umbrella like "The Cosmere Universe" loses to "The Stormlight Archive"),
        /// with stable tie-breakers so the same book always resolves to the same series.
        /// </summary>
        public static SeriesBookLink SelectDisplayLink(IEnumerable<SeriesBookLink> links)
        {
            if (links == null)
            {
                return null;
            }

            return links
                .Where(link => link != null && ResolveSeriesTitle(link).IsNotNullOrWhiteSpace())
                .OrderByDescending(link => link.IsPrimary)
                .ThenByDescending(link => link.Position.IsNotNullOrWhiteSpace())
                .ThenBy(link => GetSeriesSize(link))
                .ThenBy(link => link.SeriesPosition <= 0 ? int.MaxValue : link.SeriesPosition)
                .ThenBy(link => ResolveSeriesTitle(link), StringComparer.OrdinalIgnoreCase)
                .ThenBy(link => link.SeriesId)
                .FirstOrDefault();
        }

        /// <summary>
        /// Builds the display label ("Malazan Book of the Fallen #9") for the book's series links,
        /// or null when none of them resolve to a titled series.
        /// </summary>
        public static string Build(IEnumerable<SeriesBookLink> links)
        {
            var link = SelectDisplayLink(links);

            return link == null
                ? null
                : Format(ResolveSeriesTitle(link), link.Position);
        }

        /// <summary>
        /// Every label this book could legitimately be shown under, one per link. Used where a match
        /// against any of the book's series is wanted rather than the single display label.
        /// </summary>
        public static IEnumerable<string> BuildAll(IEnumerable<SeriesBookLink> links)
        {
            if (links == null)
            {
                return Enumerable.Empty<string>();
            }

            return links
                .Where(link => link != null)
                .Select(link => Format(ResolveSeriesTitle(link), link.Position))
                .Where(label => label != null)
                .ToList();
        }

        /// <summary>
        /// Formats a series title and position into the display label used across the app.
        /// </summary>
        public static string Format(string seriesTitle, string position)
        {
            if (seriesTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            return position.IsNullOrWhiteSpace()
                ? seriesTitle.Trim()
                : $"{seriesTitle.Trim()} #{position.Trim()}";
        }

        private static string ResolveSeriesTitle(SeriesBookLink link)
        {
            return link?.Series?.Value?.Title;
        }

        /// <summary>
        /// How many books the series holds, used to prefer the narrower series. Series that don't
        /// report a size sort last so a known-size series always wins over an unknown one.
        /// </summary>
        private static int GetSeriesSize(SeriesBookLink link)
        {
            var series = link?.Series?.Value;
            if (series == null)
            {
                return int.MaxValue;
            }

            foreach (var count in new[] { series.PrimaryWorkCount, series.PrimaryBooks, series.WorkCount, series.TotalBooks })
            {
                if (count > 0)
                {
                    return count;
                }
            }

            return int.MaxValue;
        }
    }
}
