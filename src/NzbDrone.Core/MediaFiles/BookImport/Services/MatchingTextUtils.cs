using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    internal static class MatchingTextUtils
    {
        // Unicode-aware normalization for comparison/tokenization.
        // Preserves letters/digits from any script, strips diacritics by default,
        // and normalizes punctuation/whitespace to token boundaries.
        public static string NormalizeUnicode(string s, bool stripDiacritics = true)
        {
            return UnicodeComparisonNormalizer.NormalizeWords(s, stripDiacritics);
        }

        // Unicode-aware tokenization using NormalizeUnicode
        public static List<string> TokenizeUnicodeToList(string s, bool stripDiacritics = true)
        {
            var norm = NormalizeUnicode(s, stripDiacritics);
            if (string.IsNullOrWhiteSpace(norm)) return new List<string>();
            return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static HashSet<string> TokenizeUnicodeToSet(string s, bool stripDiacritics = true)
        {
            return new HashSet<string>(TokenizeUnicodeToList(s, stripDiacritics), StringComparer.Ordinal);
        }
        public static string NormalizeBasic(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var lower = s.ToLowerInvariant();
            var arr = lower.Select(ch => (char)(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '-' ? ch : ' ')).ToArray();
            var norm = new string(arr);
            return Regex.Replace(norm, "\\s+", " ").Trim();
        }

        public static List<string> TokenizeToList(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static HashSet<string> TokenizeToSet(string s)
        {
            return new HashSet<string>(TokenizeToList(s), StringComparer.Ordinal);
        }

        public static string ExtractLanguageFromTags(Dictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0) return null;
            var keys = new[] { "LANGUAGE", "LANG", "TLANG", "LANGUAGECODE", "LANG_CODE", "LOCALE" };
            foreach (var k in keys)
            {
                if (tags.TryGetValue(k, out var vals) && vals != null)
                {
                    foreach (var v in vals)
                    {
                        var norm = NormalizeLangCode(v);
                        if (!string.IsNullOrEmpty(norm)) return norm;
                    }
                }
            }
            return null;
        }

        public static string NormalizeLangCode(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = input.Trim().ToLowerInvariant();
            if (s.Length >= 2)
            {
                var m = Regex.Match(s, @"([a-z]{2})");
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }
    }
}
