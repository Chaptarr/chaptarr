using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Parser
{
    internal static class TitleNumberCatalog
    {
        private static readonly string[] Cardinals =
        {
            null,
            "one",
            "two",
            "three",
            "four",
            "five",
            "six",
            "seven",
            "eight",
            "nine",
            "ten",
            "eleven",
            "twelve",
            "thirteen",
            "fourteen",
            "fifteen",
            "sixteen",
            "seventeen",
            "eighteen",
            "nineteen"
        };

        private static readonly string[] TensCardinals =
        {
            null,
            null,
            "twenty",
            "thirty",
            "forty",
            "fifty",
            "sixty",
            "seventy",
            "eighty",
            "ninety"
        };

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> CardinalValuesByLanguage =
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["eng"] = BuildEnglishCardinalValues()
            };

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> CardinalWordsByLanguage =
            CardinalValuesByLanguage.ToDictionary(
                language => language.Key,
                language => (IReadOnlyDictionary<int, string>)language.Value.ToDictionary(value => value.Value, value => value.Key),
                StringComparer.OrdinalIgnoreCase);

        private static readonly (int Value, string Numeral)[] RomanNumeralValues =
        {
            (1000, "m"),
            (900, "cm"),
            (500, "d"),
            (400, "cd"),
            (100, "c"),
            (90, "xc"),
            (50, "l"),
            (40, "xl"),
            (10, "x"),
            (9, "ix"),
            (5, "v"),
            (4, "iv"),
            (1, "i")
        };

        public static IEnumerable<string> SupportedLanguages => CardinalValuesByLanguage.Keys;

        public static IEnumerable<(int TokenCount, decimal Value)> GetNumberMatches(IReadOnlyList<string> tokens, int index, string editionLanguage)
        {
            if (tokens == null || index < 0 || index >= tokens.Count)
            {
                yield break;
            }

            var token = NormalizeToken(tokens[index]);
            if (token.Length == 0)
            {
                yield break;
            }

            if (decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var literal))
            {
                yield return (1, literal);
                yield break;
            }

            if (UnicodeComparisonNormalizer.TryParseRomanNumeral(token, out var roman))
            {
                yield return (1, roman);
                yield break;
            }

            if (!TryGetCardinalValues(editionLanguage, out var cardinalValues))
            {
                yield break;
            }

            if (index + 1 < tokens.Count)
            {
                var next = NormalizeToken(tokens[index + 1]);
                if (cardinalValues.TryGetValue($"{token} {next}", out var phraseValue))
                {
                    yield return (2, phraseValue);
                    yield break;
                }
            }

            if (cardinalValues.TryGetValue(token, out var cardinalValue))
            {
                yield return (1, cardinalValue);
            }
        }

        public static IReadOnlyList<string> GetLookupAlternatives(string tokenOrPhrase, string editionLanguage)
        {
            var normalized = UnicodeComparisonNormalizer.NormalizeWords(tokenOrPhrase).Trim();
            if (normalized.Length == 0)
            {
                return Array.Empty<string>();
            }

            var tokens = UnicodeComparisonNormalizer.WordSpans(normalized)
                .Select(match => match.Value)
                .ToArray();
            var alternatives = new List<string> { normalized };
            if (tokens.Length == 0)
            {
                return alternatives;
            }

            var match = GetNumberMatches(tokens, 0, editionLanguage).FirstOrDefault();
            if (match.TokenCount != tokens.Length)
            {
                return alternatives;
            }

            var value = match.Value;
            if (value != decimal.Truncate(value))
            {
                return alternatives;
            }

            alternatives.Add(value.ToString("0", CultureInfo.InvariantCulture));
            if (value > 0 && value <= int.MaxValue)
            {
                var integer = (int)value;
                var roman = ToRomanNumeral(integer);
                if (roman != null)
                {
                    alternatives.Add(roman);
                }

                var cardinal = ToCardinalWords(integer, editionLanguage);
                if (cardinal != null)
                {
                    alternatives.Add(cardinal);
                }
            }

            return alternatives
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string ToCardinalWords(int number)
        {
            return ToCardinalWords(number, "eng");
        }

        public static string ToCardinalWords(int number, string editionLanguage)
        {
            if (number <= 0 || number >= 100)
            {
                return null;
            }

            var language = editionLanguage.CanonicalizeLanguage();
            return language != null && CardinalWordsByLanguage.TryGetValue(language, out var words) && words.TryGetValue(number, out var phrase)
                ? phrase
                : null;
        }

        public static string ToRomanNumeral(int number)
        {
            if (number <= 0 || number > 3999)
            {
                return null;
            }

            var result = new StringBuilder();
            foreach (var pair in RomanNumeralValues)
            {
                while (number >= pair.Value)
                {
                    result.Append(pair.Numeral);
                    number -= pair.Value;
                }
            }

            return result.ToString();
        }

        private static bool TryGetCardinalValues(string editionLanguage, out IReadOnlyDictionary<string, int> values)
        {
            values = null;
            var language = editionLanguage.CanonicalizeLanguage();
            return language != null && CardinalValuesByLanguage.TryGetValue(language, out values);
        }

        private static string NormalizeToken(string token)
        {
            return UnicodeComparisonNormalizer.NormalizeWords(token).Trim().ToLowerInvariant();
        }

        private static IReadOnlyDictionary<string, int> BuildEnglishCardinalValues()
        {
            var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var number = 1; number < 100; number++)
            {
                values[ToEnglishCardinalWords(number)] = number;
            }

            return values;
        }

        private static string ToEnglishCardinalWords(int number)
        {
            if (number <= 0 || number >= 100)
            {
                return null;
            }

            if (number < 20)
            {
                return Cardinals[number];
            }

            var ten = number / 10;
            var one = number % 10;
            return one == 0 ? TensCardinals[ten] : $"{TensCardinals[ten]} {Cardinals[one]}";
        }
    }
}
