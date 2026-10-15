using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.CustomFormats
{
    internal static class NarratorNameMatcher
    {
        private static readonly Regex RegexSyntax = new Regex(@"[\\\^\$\*\+\?\[\]\(\)\{\}\|]", RegexOptions.Compiled);

        public static bool LooksLikeLiteralName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !RegexSyntax.IsMatch(value);
        }

        public static bool IsMatch(string observedName, string configuredName)
        {
            if (AuthorNameMatcher.AuthorNamesMatch(configuredName, observedName))
            {
                return true;
            }

            var observedTokens = NormalizeTokens(observedName);
            var configuredTokens = NormalizeTokens(configuredName);

            if (observedTokens.Length == 0 || configuredTokens.Length == 0)
            {
                return false;
            }

            if (observedTokens.SequenceEqual(configuredTokens, StringComparer.Ordinal))
            {
                return true;
            }

            // Typo tolerance is deliberately narrow: keep every given/middle-name component exact
            // and allow one inserted, removed, or substituted code point only in a reasonably long
            // family-name component. A shared surname cannot establish a different person's identity.
            if (observedTokens.Length != configuredTokens.Length || configuredTokens.Length < 2)
            {
                return false;
            }

            for (var i = 0; i < configuredTokens.Length - 1; i++)
            {
                if (!string.Equals(observedTokens[i], configuredTokens[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var observedFamilyName = observedTokens[^1];
            var configuredFamilyName = configuredTokens[^1];

            return Math.Min(observedFamilyName.EnumerateRunes().Count(), configuredFamilyName.EnumerateRunes().Count()) >= 4 &&
                   IsWithinOneEdit(observedFamilyName, configuredFamilyName);
        }

        // Used only after provider identity has established the same book. Acquisition
        // preferences retain IsMatch's stricter given-name policy.
        public static bool IsSamePersonWithinBook(string left, string right)
        {
            var a = NormalizeTokens(left?.Replace("'", string.Empty).Replace("’", string.Empty).Replace("‘", string.Empty));
            var b = NormalizeTokens(right?.Replace("'", string.Empty).Replace("’", string.Empty).Replace("‘", string.Empty));
            if (a.Length == 0 || a.Length != b.Length)
            {
                return false;
            }

            if (a.SequenceEqual(b))
            {
                return true;
            }

            var differences = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
            if (a.Length < 2 || differences.Count != 1 ||
                Math.Max(a[differences[0]].EnumerateRunes().Count(), b[differences[0]].EnumerateRunes().Count()) < 4)
            {
                return false;
            }

            // Keep other name components exact and limit spelling tolerance so a long
            // shared surname cannot hide an entirely different given name.
            var normalizedLeft = string.Join(" ", a);
            var normalizedRight = string.Join(" ", b);
            var edits = normalizedLeft.LevenshteinDistance(normalizedRight);
            return edits <= 2 && 1.0 - (double)edits / Math.Max(normalizedLeft.Length, normalizedRight.Length) >= 0.8;
        }

        private static string[] NormalizeTokens(string value)
        {
            return UnicodeComparisonNormalizer.NormalizeWords(value)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsWithinOneEdit(string left, string right)
        {
            var leftRunes = left.EnumerateRunes().ToArray();
            var rightRunes = right.EnumerateRunes().ToArray();

            if (Math.Abs(leftRunes.Length - rightRunes.Length) > 1)
            {
                return false;
            }

            var leftIndex = 0;
            var rightIndex = 0;
            var edits = 0;

            while (leftIndex < leftRunes.Length && rightIndex < rightRunes.Length)
            {
                if (leftRunes[leftIndex] == rightRunes[rightIndex])
                {
                    leftIndex++;
                    rightIndex++;
                    continue;
                }

                edits++;
                if (edits > 1)
                {
                    return false;
                }

                if (leftRunes.Length > rightRunes.Length)
                {
                    leftIndex++;
                }
                else if (rightRunes.Length > leftRunes.Length)
                {
                    rightIndex++;
                }
                else
                {
                    leftIndex++;
                    rightIndex++;
                }
            }

            if (leftIndex < leftRunes.Length || rightIndex < rightRunes.Length)
            {
                edits++;
            }

            return edits <= 1;
        }
    }
}
