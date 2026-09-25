using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class SeriesPositionTokenHelper
    {
        private static readonly string[] Ordinals =
        {
            null,
            "first",
            "second",
            "third",
            "fourth",
            "fifth",
            "sixth",
            "seventh",
            "eighth",
            "ninth",
            "tenth",
            "eleventh",
            "twelfth",
            "thirteenth",
            "fourteenth",
            "fifteenth",
            "sixteenth",
            "seventeenth",
            "eighteenth",
            "nineteenth"
        };

        private static readonly string[] TensOrdinals =
        {
            null,
            null,
            "twentieth",
            "thirtieth",
            "fortieth",
            "fiftieth",
            "sixtieth",
            "seventieth",
            "eightieth",
            "ninetieth"
        };

        private static readonly Dictionary<string, int> WordToNumber = BuildWordToNumber();

        internal static readonly IReadOnlySet<string> PositionDecorationTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "book", "bk",
            "volume", "vol",
            "part",
            "number", "no"
        };

        public static HashSet<string> GetPositionTokens(string raw)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return tokens;
            }

            raw = UnicodeComparisonNormalizer.NormalizeText(raw).Trim();
            tokens.Add(raw.ToLowerInvariant());

            if (WordToNumber.TryGetValue(raw.ToLowerInvariant(), out var wordNumber))
            {
                AddNumberTokens(tokens, wordNumber);
            }

            if (LooksLikeRomanNumeralToken(raw) && UnicodeComparisonNormalizer.TryParseRomanNumeral(raw, out var romanNumber))
            {
                AddNumberTokens(tokens, romanNumber);
            }

            foreach (Match match in Regex.Matches(raw, @"\b\d{1,4}\b"))
            {
                if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    AddNumberTokens(tokens, number);
                }
            }

            return tokens;
        }

        public static bool HasPositionIdentity(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var tokens = UnicodeComparisonNormalizer.WordSpans(UnicodeComparisonNormalizer.NormalizeText(raw), splitNumbers: true)
                .Select(match => match.Value.ToLowerInvariant())
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();
            if (tokens.Count == 0)
            {
                return false;
            }

            bool IsPositionValue(string token)
            {
                return token.All(char.IsDigit) ||
                       WordToNumber.ContainsKey(token) ||
                       (LooksLikeRomanNumeralToken(token) && UnicodeComparisonNormalizer.TryParseRomanNumeral(token, out _));
            }

            var positionIndexes = Enumerable.Range(0, tokens.Count)
                .Where(index => IsPositionValue(tokens[index]))
                .ToList();
            if (positionIndexes.Count == 0)
            {
                return false;
            }

            if (tokens.Count == 1)
            {
                return true;
            }

            if (positionIndexes.Any(index =>
                    (index > 0 && PositionDecorationTokens.Contains(tokens[index - 1])) ||
                    (index + 1 < tokens.Count && PositionDecorationTokens.Contains(tokens[index + 1]))))
            {
                return true;
            }

            // Keep the veto language-neutral: compact "label + 2" / "第2巻" shapes are
            // identity-risk even when the surrounding label is not in an English vocabulary.
            if (tokens.Count <= 3)
            {
                return true;
            }

            return positionIndexes.Count > 1 &&
                   tokens.All(token => IsPositionValue(token) || PositionDecorationTokens.Contains(token));
        }

        public static bool LooksLikeRomanNumeralToken(string token)
        {
            return UnicodeComparisonNormalizer.IsCanonicalRomanNumeral(token);
        }

        public static string ToRomanNumeral(int number)
        {
            if (number <= 0 || number >= 40)
            {
                return null;
            }

            return TitleNumberCatalog.ToRomanNumeral(number);
        }

        private static void AddNumberTokens(HashSet<string> tokens, int number)
        {
            if (tokens == null || number <= 0 || number > 9999)
            {
                return;
            }

            tokens.Add(number.ToString(CultureInfo.InvariantCulture));

            if (number < 100)
            {
                tokens.Add(number.ToString("00", CultureInfo.InvariantCulture));
            }

            var roman = ToRomanNumeral(number);
            if (!string.IsNullOrWhiteSpace(roman))
            {
                tokens.Add(roman);
            }

            AddWordTokens(tokens, TitleNumberCatalog.ToCardinalWords(number));
            AddWordTokens(tokens, ToOrdinalWords(number));
        }

        private static void AddWordTokens(HashSet<string> tokens, string phrase)
        {
            if (tokens == null || string.IsNullOrWhiteSpace(phrase))
            {
                return;
            }

            foreach (var token in phrase
                         .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => t.Trim().ToLowerInvariant()))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    tokens.Add(token);
                }
            }
        }

        private static string ToOrdinalWords(int number)
        {
            if (number <= 0 || number >= 100)
            {
                return null;
            }

            if (number < 20)
            {
                return Ordinals[number];
            }

            var ten = number / 10;
            var one = number % 10;
            return one == 0 ? TensOrdinals[ten] : $"{TitleNumberCatalog.ToCardinalWords(ten * 10)} {Ordinals[one]}";
        }

        internal static bool TryParseNumber(string raw, out decimal value, bool allowRoman = true)
        {
            raw = UnicodeComparisonNormalizer.NormalizeText(raw).Trim();
            if (decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (WordToNumber.TryGetValue(raw, out var wordNumber))
            {
                value = wordNumber;
                return true;
            }

            if (allowRoman && UnicodeComparisonNormalizer.TryParseRomanNumeral(raw, out var romanNumber))
            {
                value = romanNumber;
                return true;
            }

            return false;
        }

        private static Dictionary<string, int> BuildWordToNumber()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var n = 1; n < 100; n++)
            {
                AddWordMapping(map, TitleNumberCatalog.ToCardinalWords(n), n);
                AddWordMapping(map, ToOrdinalWords(n), n);
            }

            return map;
        }

        private static void AddWordMapping(Dictionary<string, int> map, string phrase, int number)
        {
            if (map == null || string.IsNullOrWhiteSpace(phrase))
            {
                return;
            }

            map[phrase.Trim().ToLowerInvariant()] = number;

            foreach (var token in phrase.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = token.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    map.TryAdd(normalized, number);
                }
            }
        }
    }
}
