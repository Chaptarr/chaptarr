using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Common.Extensions
{
    public static class StringExtensions
    {
        private static readonly Regex CamelCaseRegex = new Regex("(?<!^)[A-Z]", RegexOptions.Compiled);

        public static string NullSafe(this string target)
        {
            return ((object)target).NullSafe().ToString();
        }

        public static object NullSafe(this object target)
        {
            if (target != null)
            {
                return target;
            }

            return "[NULL]";
        }

        public static string FirstCharToLower(this string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            return char.ToLowerInvariant(input.First()) + input.Substring(1);
        }

        public static string FirstCharToUpper(this string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            return char.ToUpperInvariant(input.First()) + input.Substring(1);
        }

        public static string Inject(this string format, params object[] formattingArgs)
        {
            return string.Format(format, formattingArgs);
        }

        private static readonly Regex CollapseSpace = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Replace(this string text, int index, int length, string replacement)
        {
            text = text.Remove(index, length);
            text = text.Insert(index, replacement);
            return text;
        }

        public static string RemoveAccent(this string text)
        {
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        public static string TrimEnd(this string text, string postfix)
        {
            if (text.EndsWith(postfix))
            {
                text = text.Substring(0, text.Length - postfix.Length);
            }

            return text;
        }

        public static string Join(this IEnumerable<string> values, string separator)
        {
            return string.Join(separator, values);
        }

        public static string CleanSpaces(this string text)
        {
            return CollapseSpace.Replace(text, " ").Trim();
        }

        public static bool IsNullOrWhiteSpace(this string text)
        {
            return string.IsNullOrWhiteSpace(text);
        }

        public static bool IsNotNullOrWhiteSpace(this string text)
        {
            return !string.IsNullOrWhiteSpace(text);
        }

        public static bool StartsWithIgnoreCase(this string text, string startsWith)
        {
            return text.StartsWith(startsWith, StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool EqualsIgnoreCase(this string text, string equals)
        {
            return text.Equals(equals, StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool ContainsIgnoreCase(this string text, string contains)
        {
            return text.IndexOf(contains, StringComparison.InvariantCultureIgnoreCase) > -1;
        }

        public static string WrapInQuotes(this string text)
        {
            if (!text.Contains(" "))
            {
                return text;
            }

            return "\"" + text + "\"";
        }

        public static byte[] HexToByteArray(this string input)
        {
            return Enumerable.Range(0, input.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(input.Substring(x, 2), 16))
                             .ToArray();
        }

        public static string ToHexString(this byte[] input)
        {
            return string.Concat(Array.ConvertAll(input, x => x.ToString("X2")));
        }

        public static string FromOctalString(this string octalValue)
        {
            octalValue = octalValue.TrimStart('\\');

            var first = int.Parse(octalValue.Substring(0, 1));
            var second = int.Parse(octalValue.Substring(1, 1));
            var third = int.Parse(octalValue.Substring(2, 1));
            var byteResult = (byte)((first << 6) | (second << 3) | third);

            return Encoding.ASCII.GetString(new[] { byteResult });
        }

        public static string SplitCamelCase(this string input)
        {
            return CamelCaseRegex.Replace(input, match => " " + match.Value);
        }

        /// <summary>
        /// DEPRECATED: This fuzzy matching method is being phased out in favor of deterministic matching.
        /// Use IDeterministicMatcher or IExactStringMatcher instead.
        /// </summary>
        [Obsolete("Use IDeterministicMatcher or IExactStringMatcher for matching logic")]
        public static double FuzzyMatch(this string a, string b)
        {
            if (a.IsNullOrWhiteSpace() || b.IsNullOrWhiteSpace())
            {
                return 0;
            }

            // Check for exact match first (fast path)
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }

            // For multi-word strings, use enhanced logic
            if (a.Contains(" ") && b.Contains(" "))
            {
                var partsA = a.Split(' ');
                var partsB = b.Split(' ');

                // Calculate sequential word bonus (enhanced matching logic)
                var sequentialBonus = CalculateSequentialWordBonus(partsA, partsB);

                // Original component matching
                var componentScore = (FuzzyMatchComponents(partsA, partsB) + FuzzyMatchComponents(partsB, partsA)) / (partsA.Length + partsB.Length);

                // Combine with sequential bonus
                var enhancedScore = Math.Max(componentScore, sequentialBonus);

                // Reward longer matches (minimum 3 words for bonus)
                var lengthBonus = Math.Min(partsA.Length, partsB.Length) >= 3 ? 0.1 : 0.0;

                // Return best score, capped at 1.0
                return Math.Min(1.0, Math.Max(enhancedScore + lengthBonus, LevenshteinCoefficient(a, b)));
            }
            else
            {
                return LevenshteinCoefficient(a, b);
            }
        }

        private static double CalculateSequentialWordBonus(string[] wordsA, string[] wordsB)
        {
            if (wordsA.Length == 0 || wordsB.Length == 0)
            {
                return 0.0;
            }

            // Try both directions for sequential matching
            var maxConsecutiveAB = FindMaxConsecutiveMatches(wordsA, wordsB);
            var maxConsecutiveBA = FindMaxConsecutiveMatches(wordsB, wordsA);
            var maxConsecutive = Math.Max(maxConsecutiveAB, maxConsecutiveBA);

            // Calculate ratio based on shorter array
            var minLength = Math.Min(wordsA.Length, wordsB.Length);
            var ratio = (double)maxConsecutive / minLength;

            // Threshold: need at least 50% match to consider it good
            if (ratio < 0.5)
            {
                return 0.0;
            }

            // Exponential scaling for better matches (1.5 power rewards longer sequences)
            return Math.Pow(ratio, 1.5);
        }

        private static int FindMaxConsecutiveMatches(string[] source, string[] target)
        {
            var maxConsecutive = 0;

            // Try starting from each position in source
            for (var i = 0; i <= source.Length - target.Length; i++)
            {
                var consecutive = 0;

                // Count consecutive matches
                for (var j = 0; j < target.Length && i + j < source.Length; j++)
                {
                    if (source[i + j].Equals(target[j], StringComparison.OrdinalIgnoreCase))
                    {
                        consecutive++;
                    }
                    else
                    {
                        break; // Stop at first non-match
                    }
                }

                maxConsecutive = Math.Max(maxConsecutive, consecutive);
            }

            return maxConsecutive;
        }

        private static double FuzzyMatchComponents(string[] a, string[] b)
        {
            double weightDenom = Math.Max(a.Length, b.Length);
            double sum = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var high = 0.0;
                var indexDistance = 0;
                for (var x = 0; x < b.Length; x++)
                {
                    // Obsolete method - returning 0 as this whole fuzzy matching system is being replaced
                    var coef = 0.0; // LevenshteinCoefficient(a[i], b[x]);
                    if (coef > high)
                    {
                        high = coef;
                        indexDistance = Math.Abs(i - x);
                    }
                }

                sum += (1.0 - (indexDistance / weightDenom)) * high;
            }

            return sum;
        }

        /// <summary>
        /// DEPRECATED: This Levenshtein distance method is being phased out in favor of deterministic matching.
        /// Use IDeterministicMatcher or IExactStringMatcher instead.
        /// </summary>
        [Obsolete("Use IDeterministicMatcher or IExactStringMatcher for matching logic")]
        public static double LevenshteinCoefficient(this string a, string b)
        {
            return 1.0 - ((double)a.LevenshteinDistance(b) / Math.Max(a.Length, b.Length));
        }

        private static readonly HashSet<string> Copywords = new HashSet<string>
        {
            "agency", "corporation", "company", "co.", "council",
            "committee", "inc.", "institute", "national",
            "society", "club", "team"
        };

        private static readonly HashSet<string> SurnamePrefixes = new HashSet<string>
        {
            "da", "de", "di", "la", "le", "van", "von"
        };

        private static readonly HashSet<string> Prefixes = new HashSet<string>
        {
            "mr", "mr.", "mrs", "mrs.", "ms", "ms.", "dr", "dr.", "prof", "prof."
        };

        private static readonly HashSet<string> Suffixes = new HashSet<string>
        {
            "jr", "sr", "inc", "ph.d", "phd",
            "md", "m.d", "i", "ii", "iii", "iv",
            "junior", "senior"
        };

        private static readonly Dictionary<char, char> Brackets = new Dictionary<char, char>
        {
            { '(', ')' },
            { '[', ']' },
            { '{', '}' }
        };

        private static readonly Dictionary<char, char> RMap = Brackets.ToDictionary(x => x.Value, x => x.Key);

        public static string RemoveBracketedText(this string input)
        {
            var counts = Brackets.ToDictionary(x => x.Key, y => 0);
            var total = 0;
            var buf = new List<char>(input.Length);

            foreach (var c in input)
            {
                if (Brackets.ContainsKey(c))
                {
                    counts[c] += 1;
                    total += 1;
                }
                else if (RMap.ContainsKey(c))
                {
                    var idx = RMap[c];
                    if (counts[idx] > 0)
                    {
                        counts[idx] -= 1;
                        total -= 1;
                    }
                }
                else if (total < 1)
                {
                    buf.Add(c);
                }
            }

            return new string(buf.ToArray());
        }

        public static string ToLastFirst(this string author)
        {
            // ported from https://github.com/kovidgoyal/calibre/blob/master/src/calibre/ebooks/metadata/__init__.py
            if (author == null)
            {
                return null;
            }

            var sauthor = author.RemoveBracketedText().Trim();

            var tokens = sauthor.Split();

            if (tokens.Length < 2)
            {
                return author;
            }

            var ltoks = tokens.Select(x => x.ToLowerInvariant()).ToHashSet();

            if (ltoks.Intersect(Copywords).Any())
            {
                return author;
            }

            if (tokens.Length == 2 && SurnamePrefixes.Contains(tokens[0].ToLowerInvariant()))
            {
                return author;
            }

            int first;
            for (first = 0; first < tokens.Length; first++)
            {
                if (!Prefixes.Contains(tokens[first].ToLowerInvariant()))
                {
                    break;
                }
            }

            if (first == tokens.Length)
            {
                return author;
            }

            int last;
            for (last = tokens.Length - 1; last >= first; last--)
            {
                if (!Suffixes.Contains(tokens[last].ToLowerInvariant()))
                {
                    break;
                }
            }

            if (last < first)
            {
                return author;
            }

            var suffix = tokens.TakeLast(tokens.Length - last - 1).ConcatToString(" ");

            if (last > first && SurnamePrefixes.Contains(tokens[last - 1].ToLowerInvariant()))
            {
                tokens[last - 1] += ' ' + tokens[last];
                last -= 1;
            }

            var atokens = new[] { tokens[last] }.Concat(tokens.Skip(first).Take(last - first)).ToList();
            var addComma = atokens.Count > 1;

            if (suffix.IsNotNullOrWhiteSpace())
            {
                atokens.Add(suffix);
            }

            if (addComma)
            {
                atokens[0] += ',';
            }

            return atokens.ConcatToString(" ");
        }

        public static string EncodeRFC3986(this string value)
        {
            // From Twitterizer http://www.twitterizer.net/
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var encoded = Uri.EscapeDataString(value);

            return Regex
                .Replace(encoded, "(%[0-9a-f][0-9a-f])", c => c.Value.ToUpper())
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("$", "%24")
                .Replace("!", "%21")
                .Replace("*", "%2A")
                .Replace("'", "%27")
                .Replace("%7E", "~");
        }

        public static bool IsValidIpAddress(this string value)
        {
            if (!IPAddress.TryParse(value, out var parsedAddress))
            {
                return false;
            }

            if (parsedAddress.Equals(IPAddress.Parse("255.255.255.255")))
            {
                return false;
            }

            if (parsedAddress.IsIPv6Multicast)
            {
                return false;
            }

            return parsedAddress.AddressFamily == AddressFamily.InterNetwork || parsedAddress.AddressFamily == AddressFamily.InterNetworkV6;
        }

        public static string ToUrlHost(this string input)
        {
            return input.Contains(":") ? $"[{input}]" : input;
        }

        public static string RegexReplace(this string input, string pattern, string replacement)
        {
            if (input == null)
            {
                return null;
            }

            return System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement);
        }

        public static string NormalizeAuthorNameForComparison(this string authorName)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return string.Empty;
            }

            // Remove all spaces, periods, hyphens, and apostrophes
            // Convert to lowercase for case-insensitive comparison
            // This makes "A.F. Kay", "A. F. Kay", "AF Kay", "A F Kay" all normalize to "afkay"
            return Regex.Replace(authorName, @"[\s\.\-\']", "", RegexOptions.Compiled)
                .ToLowerInvariant();
        }
    }
}
