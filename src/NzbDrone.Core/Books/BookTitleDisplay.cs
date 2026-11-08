using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books
{
    public static class BookTitleDisplay
    {
        public static string GetTitle(string bookTitle, string monitoredEditionTitle, IEnumerable<string> seriesLabels)
        {
            var title = monitoredEditionTitle ?? bookTitle ?? string.Empty;
            foreach (var label in seriesLabels)
            {
                title = StripDuplicatedSeriesSuffix(title, label);
            }

            return title;
        }

        private static string StripDuplicatedSeriesSuffix(string title, string seriesTitle)
        {
            title ??= string.Empty;
            title = title.Trim();

            if (title.Length == 0 || string.IsNullOrWhiteSpace(seriesTitle))
            {
                return title;
            }

            var match = Regex.Match(title, @"^(?<base>.*?)(?:\s*\((?<suffix>[^()]*)\))$");
            if (!match.Success)
            {
                return title;
            }

            var suffix = match.Groups["suffix"].Value;
            if (!SuffixMatchesSeriesTitle(suffix, seriesTitle))
            {
                return title;
            }

            return match.Groups["base"].Value.TrimEnd();
        }

        private static bool SuffixMatchesSeriesTitle(string suffix, string seriesTitle)
        {
            if (suffix.IsNullOrWhiteSpace() || seriesTitle.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (!suffix.Contains('#'))
            {
                return false;
            }

            return NormalizeSeriesSuffix(suffix) == NormalizeSeriesSuffix(seriesTitle);
        }

        private static string NormalizeSeriesSuffix(string value)
        {
            var normalized = value.ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}#]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+#", " #");
            return normalized;
        }
    }
}
