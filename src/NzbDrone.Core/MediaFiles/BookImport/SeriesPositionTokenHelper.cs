using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class SeriesPositionTokenHelper
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
        private static readonly Regex IdentityTokenRegex = new Regex(@"\p{L}+|\p{Nd}+", RegexOptions.Compiled);

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

            raw = raw.Trim();
            tokens.Add(raw.ToLowerInvariant());

            if (WordToNumber.TryGetValue(raw.ToLowerInvariant(), out var wordNumber))
            {
                AddNumberTokens(tokens, wordNumber);
            }

            if (TryParseRomanNumeral(raw, out var romanNumber))
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

            var tokens = IdentityTokenRegex.Matches(raw)
                .Cast<Match>()
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
                       TryParseRomanNumeral(token, out _);
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
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim().ToLowerInvariant();
            if (token.Length > 8)
            {
                return false;
            }

            foreach (var ch in token)
            {
                switch (ch)
                {
                    case 'i':
                    case 'v':
                    case 'x':
                    case 'l':
                    case 'c':
                    case 'd':
                    case 'm':
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        public static string ToRomanNumeral(int number)
        {
            if (number <= 0 || number >= 40)
            {
                return null;
            }

            var ones = new[] { "", "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix" };
            var tens = new[] { "", "x", "xx", "xxx" };
            return tens[number / 10] + ones[number % 10];
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

            AddWordTokens(tokens, ToCardinalWords(number));
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

        private static string ToCardinalWords(int number)
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
            return one == 0 ? TensOrdinals[ten] : $"{TensCardinals[ten]} {Ordinals[one]}";
        }

        private static bool TryParseRomanNumeral(string raw, out int value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(raw) || !LooksLikeRomanNumeralToken(raw))
            {
                return false;
            }

            raw = raw.Trim().ToUpperInvariant();

            var map = new Dictionary<char, int>
            {
                ['I'] = 1,
                ['V'] = 5,
                ['X'] = 10,
                ['L'] = 50,
                ['C'] = 100,
                ['D'] = 500,
                ['M'] = 1000
            };

            var total = 0;
            var prev = 0;
            foreach (var ch in raw.Reverse())
            {
                if (!map.TryGetValue(ch, out var current))
                {
                    return false;
                }

                if (current < prev)
                {
                    total -= current;
                }
                else
                {
                    total += current;
                    prev = current;
                }
            }

            if (total <= 0)
            {
                return false;
            }

            value = total;
            return true;
        }

        private static Dictionary<string, int> BuildWordToNumber()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var n = 1; n < 100; n++)
            {
                AddWordMapping(map, ToCardinalWords(n), n);
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
