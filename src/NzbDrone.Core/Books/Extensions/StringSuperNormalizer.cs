using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Books.Extensions
{
    public static class StringSuperNormalizer
    {
        // Regex that matches anything that is NOT a Unicode letter or digit
        // This preserves ALL languages (Chinese, Arabic, etc.) while removing ALL punctuation/spaces
        private static readonly Regex NonAlphanumericRegex = new Regex(@"[^\p{L}\p{Nd}]", RegexOptions.Compiled);

        /// <summary>
        /// Supernormalizes text by removing ALL non-letter and non-digit characters.
        /// Preserves Unicode letters from all languages.
        /// Examples:
        /// "J.K. Rowling" → "JKRowling"
        /// "José García-Márquez" → "JoséGarcíaMárquez"
        /// "村上春樹" → "村上春樹"
        /// "Фёдор Достоевский" → "ФёдорДостоевский"
        /// </summary>
        public static string SuperNormalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Convert to lowercase and remove all non-letter/non-digit characters
            return NonAlphanumericRegex.Replace(text.ToLowerInvariant(), "");
        }

        /// <summary>
        /// Creates a supernormalized version for database storage or comparison
        /// </summary>
        public static string ForDatabase(string text)
        {
            return SuperNormalize(text);
        }

        /// <summary>
        /// Checks if a supernormalized text contains another supernormalized text
        /// </summary>
        public static bool SuperNormalizedContains(string text, string searchFor)
        {
            return SuperNormalize(text).Contains(SuperNormalize(searchFor));
        }

        // ========================================================================
        // FTS MATCHING NORMALIZATION
        // Used for computing MatchingTitle and normalizing query tokens.
        // Canonical implementation - all code paths should use these functions.
        // ========================================================================

        // Regex for possessive normalization: matches 's at word boundary
        // Covers: ' (U+0027 straight), ' (U+2019 right curly), ' (U+2018 left curly)
        private static readonly Regex PossessiveRegex = new Regex("['\\u2018\\u2019]s\\b", RegexOptions.Compiled);

        /// <summary>
        /// Normalizes text for FTS matching:
        /// 1. Merges possessives ('s → s): Philosopher's → Philosophers
        /// 2. Strips diacritics: café → cafe, ménage → menage
        /// 3. Returns lowercase
        ///
        /// Use this for computing MatchingTitle column and normalizing query tokens.
        /// </summary>
        public static string NormalizeForFts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Step 1: Normalize possessives ('s → s)
            // Handles: Philosopher's → Philosophers, Housemaid's → Housemaids
            // Does NOT affect: Can't, O'Brien, ma'am (not word-final 's)
            text = PossessiveRegex.Replace(text, "s");

            // Step 2: Normalize diacritics (NFD decomposition + strip combining marks)
            // Handles: café → cafe, ménage → menage, Hervé → Herve, señor → senor
            text = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Computes the MatchingTitle for an edition.
        /// This is the canonical function - use it everywhere MatchingTitle is generated.
        /// </summary>
        public static string ComputeMatchingTitle(string title)
        {
            return NormalizeForFts(title);
        }
    }
}
