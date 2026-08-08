using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Books.Services
{
    public static class TextNormalizer
    {
        // Enhanced normalization with all the improvements from Database Engineer B
        public static List<string> NormalizeAndTokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var normalized = text.ToLowerInvariant();

            // Handle apostrophes and unicode quotes
            normalized = normalized.Replace("\u2019", "").Replace("'", "").Replace("`", "");

            // Handle hyphens in names (convert to spaces for tokenization)
            normalized = normalized.Replace('-', ' ');

            // Handle Roman numerals (convert to numbers)
            normalized = ReplaceRomanNumerals(normalized);

            // Handle written numbers (convert to digits)
            normalized = ReplaceWrittenNumbers(normalized);

            // Handle initials: "j.k." → "jk", "j. k." → "jk"
            normalized = Regex.Replace(normalized, @"\b(\w)\.(\w)\.", "$1$2");
            normalized = Regex.Replace(normalized, @"\b(\w)\.\s*(\w)\.", "$1$2");

            normalized = UnicodeComparisonNormalizer.NormalizeWords(normalized);

            return normalized.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Extract significant tokens from book titles (skip stop words appropriately)
        public static List<string> ExtractSignificantTokens(string title)
        {
            var tokens = NormalizeAndTokenize(title);

            // Short titles: every token matters
            if (tokens.Count <= 3)
                return tokens;

            // Medium titles: skip only very common words
            if (tokens.Count <= 5)
                return FilterTokens(tokens, new[] { "of", "and", "in", "to", "a", "an" });

            // Long titles: traditional stop words
            return FilterTokens(tokens, new[]
            {
                "the", "a", "an", "of", "and", "in", "to", "for", "with", "by", "at", "on", "is", "are"
            });
        }

        // Calculate required match count based on title length
        public static int CalculateRequiredMatches(List<string> bookTokens)
        {
            var count = bookTokens.Count;

            return count switch
            {
                <= 2 => count,                                  // 100% for "The Dark"
                <= 4 => count - 1,                             // 75% for "The Dark Tower"
                <= 6 => (int)(count * 0.75),                   // 75% for medium titles
                _ => (int)(count * 0.60)                       // 60% for long titles
            };
        }

        // Containment-based similarity (replaces Levenshtein distance)
        public static double CalculateContainmentSimilarity(string cleanText, string dirtyText)
        {
            if (string.IsNullOrEmpty(cleanText) || string.IsNullOrEmpty(dirtyText))
                return 0.0;

            // Normalize both texts
            var cleanTokens = NormalizeAndTokenize(cleanText);
            var dirtyTokens = NormalizeAndTokenize(dirtyText);

            if (!cleanTokens.Any()) return 0.0;

            // Count how many clean tokens exist in the dirty text
            var dirtyTokenSet = new HashSet<string>(dirtyTokens);
            var matchedTokens = cleanTokens.Count(token => dirtyTokenSet.Contains(token));

            // Return the percentage of clean tokens found in dirty text
            return (double)matchedTokens / cleanTokens.Count;
        }

        // Check if ALL author tokens exist in the field (prevents Stephen King disaster)
        public static bool AuthorContainmentCheck(string authorName, string fieldValue)
        {
            var authorTokens = NormalizeAndTokenize(authorName);
            var fieldTokens = NormalizeAndTokenize(fieldValue);
            var fieldTokenSet = new HashSet<string>(fieldTokens);

            // ALL author tokens must be present
            return authorTokens.All(token => fieldTokenSet.Contains(token));
        }

        // Check if enough significant book tokens are present
        public static double BookContainmentScore(string bookTitle, string fieldValue)
        {
            var bookTokens = ExtractSignificantTokens(bookTitle);
            var fieldTokens = NormalizeAndTokenize(fieldValue);
            var fieldTokenSet = new HashSet<string>(fieldTokens);

            var matchedTokens = bookTokens.Count(token => fieldTokenSet.Contains(token));

            return bookTokens.Any() ? (double)matchedTokens / bookTokens.Count : 0.0;
        }

        // Replace Roman numerals with numbers
        private static string ReplaceRomanNumerals(string text)
        {
            var romanMap = new Dictionary<string, string>
            {
                { " i ", " 1 " }, { " ii ", " 2 " }, { " iii ", " 3 " }, { " iv ", " 4 " },
                { " v ", " 5 " }, { " vi ", " 6 " }, { " vii ", " 7 " }, { " viii ", " 8 " },
                { " ix ", " 9 " }, { " x ", " 10 " }, { " xi ", " 11 " }, { " xii ", " 12 " }
            };

            foreach (var kvp in romanMap)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }
            return text;
        }

        // Replace written numbers with digits
        private static string ReplaceWrittenNumbers(string text)
        {
            var numberMap = new Dictionary<string, string>
            {
                { " one ", " 1 " }, { " two ", " 2 " }, { " three ", " 3 " },
                { " four ", " 4 " }, { " five ", " 5 " }, { " six ", " 6 " },
                { " seven ", " 7 " }, { " eight ", " 8 " }, { " nine ", " 9 " },
                { " ten ", " 10 " }, { " eleven ", " 11 " }, { " twelve ", " 12 " }
            };

            foreach (var kvp in numberMap)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }
            return text;
        }

        // Filter out stop words
        private static List<string> FilterTokens(List<string> tokens, string[] stopWords)
        {
            var stopWordSet = new HashSet<string>(stopWords);
            return tokens.Where(token => !stopWordSet.Contains(token)).ToList();
        }

        // Helper methods for token matching
        public static bool ContainsAllTokens(List<string> haystack, List<string> needles)
        {
            var haystackSet = new HashSet<string>(haystack);
            return needles.All(needle => haystackSet.Contains(needle));
        }

        public static int CountMatchingTokens(List<string> bookTokens, List<string> fieldTokens)
        {
            var fieldSet = new HashSet<string>(fieldTokens);
            return bookTokens.Count(token => fieldSet.Contains(token));
        }
    }
}
