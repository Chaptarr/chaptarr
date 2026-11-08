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

    }

    public class TagNormalizer : ITagNormalizer
    {
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

    }
}
