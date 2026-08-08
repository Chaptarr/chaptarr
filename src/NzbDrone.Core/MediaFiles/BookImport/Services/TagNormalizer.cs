using System;
using System.Text.RegularExpressions;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public interface ITagNormalizer
    {
        /// <summary>
        /// Normalizes text for containment checking using a Unicode-aware key form.
        /// </summary>
        string NormalizeForContainment(string text);

        /// <summary>
        /// Normalizes text for subset comparison using Unicode-aware token boundaries.
        /// </summary>
        string NormalizeForSubsetComparison(string text);

        /// <summary>
        /// Normalizes narrator names by removing common suffixes
        /// </summary>
        string NormalizeNarrator(string narrator);
    }

    public class TagNormalizer : ITagNormalizer
    {
        private static readonly Regex NarratorSuffixRegex = new Regex(@"\s*-?\s*narrator\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string NormalizeForContainment(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return UnicodeComparisonNormalizer.NormalizeKey(text);
        }

        public string NormalizeForSubsetComparison(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return UnicodeComparisonNormalizer.NormalizeWords(text);
        }

        public string NormalizeNarrator(string narrator)
        {
            if (string.IsNullOrWhiteSpace(narrator))
                return string.Empty;

            // Remove common narrator suffixes
            var normalized = NarratorSuffixRegex.Replace(narrator, "");

            return UnicodeComparisonNormalizer.NormalizeWords(normalized);
        }

    }
}
