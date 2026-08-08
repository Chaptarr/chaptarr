using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
            // family-name component. This catches "Marster"/"Marsters" without turning a shared
            // surname into identity proof for a different person.
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

        private static string[] NormalizeTokens(string value)
        {
            return NormalizeWords(value)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static string NormalizeWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            var pendingSeparator = false;
            var previousBaseWasLatin = false;

            foreach (var rune in value.Normalize(NormalizationForm.FormD).EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark)
                {
                    // Accent-insensitive matching is useful for Latin names (Jose/José), but
                    // combining marks are identity-bearing in many other scripts. For example,
                    // dropping the Japanese dakuten would make か and が compare as equal.
                    if (!previousBaseWasLatin && builder.Length > 0 && !pendingSeparator)
                    {
                        builder.Append(rune);
                    }

                    continue;
                }

                if (Rune.IsLetterOrDigit(rune))
                {
                    if (pendingSeparator && builder.Length > 0 && builder[^1] != ' ')
                    {
                        builder.Append(' ');
                    }

                    builder.Append(Rune.ToLowerInvariant(rune));
                    pendingSeparator = false;
                    previousBaseWasLatin = IsLatin(rune);
                }
                else if (builder.Length > 0)
                {
                    pendingSeparator = true;
                    previousBaseWasLatin = false;
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC).Trim();
        }

        private static bool IsLatin(Rune rune)
        {
            var value = rune.Value;

            return (value >= 'A' && value <= 'Z') ||
                   (value >= 'a' && value <= 'z') ||
                   (value >= 0x00C0 && value <= 0x02AF) ||
                   (value >= 0x1D00 && value <= 0x1D7F) ||
                   (value >= 0x1D80 && value <= 0x1DBF) ||
                   (value >= 0x1E00 && value <= 0x1EFF) ||
                   (value >= 0x2C60 && value <= 0x2C7F) ||
                   (value >= 0xA720 && value <= 0xA7FF) ||
                   (value >= 0xAB30 && value <= 0xAB6F) ||
                   (value >= 0x10780 && value <= 0x107BF) ||
                   (value >= 0x1DF00 && value <= 0x1DFFF);
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
