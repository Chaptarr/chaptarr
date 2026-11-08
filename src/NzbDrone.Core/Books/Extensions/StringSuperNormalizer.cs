using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Utilities;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books.Extensions
{
    public static class StringSuperNormalizer
    {
        // ========================================================================
        // FTS MATCHING NORMALIZATION
        // Used for computing MatchingTitle and normalizing query tokens.
        // Canonical implementation - all code paths should use these functions.
        // ========================================================================

        // Regex for possessive normalization: matches 's at word boundary
        // Preserve the released stored-key alphabet: straight and left/right curly apostrophes.
        internal static readonly Regex PossessiveRegex = new Regex("['\\u2018\\u2019]s\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Normalizes text for FTS matching:
        /// 1. Merges possessives ('s → s): Philosopher's → Philosophers
        /// 2. Strips diacritics: café → cafe, ménage → menage
        /// 3. Applies the shared numeric and scalar comparison rules
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

            return UnicodeComparisonNormalizer.NormalizeText(text);
        }

        public static string ComputeBookComparisonKey(string title)
        {
            return (title ?? string.Empty).CleanBookTitle().CleanAuthorName();
        }

        internal static string NormalizeNarratorsForFts(IEnumerable<string> names)
        {
            return NormalizeForFts(string.Join(" ", names?.Where(name => !string.IsNullOrWhiteSpace(name)) ?? Enumerable.Empty<string>()));
        }

        internal static IEnumerable<string> GetFtsQueryTerms(string text, bool preserveFtsConnectors = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Enumerable.Empty<string>();
            }

            return UnicodeComparisonNormalizer.WordSpans(NormalizeForFts(text), preserveFtsConnectors)
                .Select(word => word.Value)
                .Distinct(StringComparer.Ordinal);
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
