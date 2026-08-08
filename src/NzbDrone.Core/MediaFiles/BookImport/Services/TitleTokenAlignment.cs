using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    internal sealed class TitleTokenAlignmentResult
    {
        public TitleTokenAlignmentResult(IReadOnlyList<int> consumedFieldIndexes, bool usedNearExact, bool usedTransposition)
        {
            ConsumedFieldIndexes = consumedFieldIndexes;
            UsedNearExact = usedNearExact;
            UsedTransposition = usedTransposition;
        }

        public IReadOnlyList<int> ConsumedFieldIndexes { get; }
        public bool UsedNearExact { get; }
        public bool UsedTransposition { get; }
    }

    internal static class TitleTokenAlignment
    {
        private const int CompactSplitMinimumTokenLength = 5;
        private const int CompactSplitMinimumPartLength = 2;

        private static readonly HashSet<string> StructuralGlueTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "or", "the", "of", "in", "on", "at", "by", "for", "with", "to", "from", "as"
        };

        private static readonly HashSet<string> OptionalLeadingArticles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the"
        };

        public static bool IsStructuralGlueToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && StructuralGlueTokens.Contains(token.Trim());
        }

        public static bool TryAlignStructural(
            IReadOnlyList<string> requiredTokens,
            IReadOnlyList<string> fieldTokens,
            bool allowNearExact,
            bool allowTransposition,
            out TitleTokenAlignmentResult result)
        {
            result = null;

            if (requiredTokens == null || requiredTokens.Count == 0)
            {
                return false;
            }

            var identityTokens = requiredTokens
                .Where((token, index) =>
                    !string.IsNullOrWhiteSpace(token) &&
                    !(index > 0 && index < requiredTokens.Count - 1 && IsStructuralGlueToken(token)))
                .ToList();

            if (!TryAlignOrdered(
                    identityTokens,
                    fieldTokens,
                    allowNearExact,
                    allowTransposition,
                    out result))
            {
                if (!allowNearExact ||
                    identityTokens.Count <= 1 ||
                    !OptionalLeadingArticles.Contains(identityTokens[0]) ||
                    !TryAlignOrdered(
                        identityTokens.Skip(1).ToList(),
                        fieldTokens,
                        allowNearExact: true,
                        allowTransposition,
                        out var tolerantResult))
                {
                    return false;
                }

                result = new TitleTokenAlignmentResult(
                    tolerantResult.ConsumedFieldIndexes,
                    usedNearExact: true,
                    tolerantResult.UsedTransposition);
            }

            var consumed = new HashSet<int>(result.ConsumedFieldIndexes);
            var first = result.ConsumedFieldIndexes.Min();
            var last = result.ConsumedFieldIndexes.Max();
            for (var index = first + 1; index < last; index++)
            {
                if (!consumed.Contains(index) && !IsStructuralGlueToken(fieldTokens[index]))
                {
                    result = null;
                    return false;
                }
            }

            return true;
        }

        public static bool TokensMatchExactOrSynonym(string requiredToken, string fieldToken)
        {
            if (string.IsNullOrWhiteSpace(requiredToken) || string.IsNullOrWhiteSpace(fieldToken))
            {
                return false;
            }

            return string.Equals(requiredToken, fieldToken, StringComparison.OrdinalIgnoreCase) ||
                   TokenSynonyms.AreSynonyms(requiredToken, fieldToken);
        }

        public static bool TokensMatchCompactSplit(string compactToken, string firstPart, string secondPart)
        {
            if (string.IsNullOrWhiteSpace(compactToken) ||
                string.IsNullOrWhiteSpace(firstPart) ||
                string.IsNullOrWhiteSpace(secondPart))
            {
                return false;
            }

            var compact = compactToken.Trim();
            var first = firstPart.Trim();
            var second = secondPart.Trim();

            if (compact.Length < CompactSplitMinimumTokenLength ||
                first.Length < CompactSplitMinimumPartLength ||
                second.Length < CompactSplitMinimumPartLength)
            {
                return false;
            }

            return string.Equals(compact, first + second, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TokensMatchNearExact(string requiredToken, string fieldToken, bool allowTransposition)
        {
            return TryMatchNearExact(requiredToken, fieldToken, allowTransposition, out _);
        }

        private static bool TryMatchNearExact(string requiredToken, string fieldToken, bool allowTransposition, out bool isTransposition)
        {
            isTransposition = false;

            if (TokensMatchExactOrSynonym(requiredToken, fieldToken))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(requiredToken) || string.IsNullOrWhiteSpace(fieldToken))
            {
                return false;
            }

            var required = requiredToken.Trim().ToLowerInvariant();
            var field = fieldToken.Trim().ToLowerInvariant();

            // Series/book numbers are deliberate disambiguators: 5 must never match 6.
            if (required.Any(char.IsDigit) || field.Any(char.IsDigit))
            {
                return false;
            }

            if (!required.All(char.IsLetter) || !field.All(char.IsLetter))
            {
                return false;
            }

            if (IsTrailingCharInsertOrDelete(required, field))
            {
                return true;
            }

            if (allowTransposition && IsAdjacentTransposition(required, field))
            {
                isTransposition = true;
                return true;
            }

            return false;
        }

        public static bool TryAlignOrdered(
            IReadOnlyList<string> requiredTokens,
            IReadOnlyList<string> fieldTokens,
            bool allowNearExact,
            bool allowTransposition,
            out TitleTokenAlignmentResult result)
        {
            result = null;

            if (requiredTokens == null || fieldTokens == null || requiredTokens.Count == 0 || fieldTokens.Count == 0)
            {
                return false;
            }

            var consumed = new List<int>();
            var nextFieldIndex = 0;
            var nearExactCount = 0;
            var usedTransposition = false;

            var required = requiredTokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            for (var requiredIndex = 0; requiredIndex < required.Count;)
            {
                var requiredToken = required[requiredIndex];
                var exactIndex = -1;
                for (var i = nextFieldIndex; i < fieldTokens.Count; i++)
                {
                    if (TokensMatchExactOrSynonym(requiredToken, fieldTokens[i]))
                    {
                        exactIndex = i;
                        break;
                    }
                }

                if (exactIndex >= 0)
                {
                    consumed.Add(exactIndex);
                    nextFieldIndex = exactIndex + 1;
                    requiredIndex++;
                    continue;
                }

                if (requiredIndex + 1 < required.Count)
                {
                    var compactFieldIndex = -1;
                    for (var i = nextFieldIndex; i < fieldTokens.Count; i++)
                    {
                        if (TokensMatchCompactSplit(fieldTokens[i], requiredToken, required[requiredIndex + 1]))
                        {
                            compactFieldIndex = i;
                            break;
                        }
                    }

                    if (compactFieldIndex >= 0)
                    {
                        consumed.Add(compactFieldIndex);
                        nextFieldIndex = compactFieldIndex + 1;
                        requiredIndex += 2;
                        continue;
                    }
                }

                var splitFieldIndex = -1;
                for (var i = nextFieldIndex; i + 1 < fieldTokens.Count; i++)
                {
                    if (TokensMatchCompactSplit(requiredToken, fieldTokens[i], fieldTokens[i + 1]))
                    {
                        splitFieldIndex = i;
                        break;
                    }
                }

                if (splitFieldIndex >= 0)
                {
                    consumed.Add(splitFieldIndex);
                    consumed.Add(splitFieldIndex + 1);
                    nextFieldIndex = splitFieldIndex + 2;
                    requiredIndex++;
                    continue;
                }

                if (!allowNearExact)
                {
                    return false;
                }

                var nearIndex = -1;
                var nearIsTransposition = false;
                for (var i = nextFieldIndex; i < fieldTokens.Count; i++)
                {
                    if (TryMatchNearExact(requiredToken, fieldTokens[i], allowTransposition, out var isTransposition))
                    {
                        nearIndex = i;
                        nearIsTransposition = isTransposition;
                        break;
                    }
                }

                if (nearIndex < 0)
                {
                    return false;
                }

                nearExactCount++;
                if (nearExactCount > 1)
                {
                    return false;
                }

                consumed.Add(nearIndex);
                usedTransposition |= nearIsTransposition;
                nextFieldIndex = nearIndex + 1;
                requiredIndex++;
            }

            if (consumed.Count == 0)
            {
                return false;
            }

            result = new TitleTokenAlignmentResult(consumed, nearExactCount > 0, usedTransposition);
            return true;
        }

        private static bool IsTrailingCharInsertOrDelete(string required, string field)
        {
            if (Math.Abs(required.Length - field.Length) != 1)
            {
                return false;
            }

            var longer = required.Length > field.Length ? required : field;
            var shorter = required.Length > field.Length ? field : required;
            return longer.StartsWith(shorter, StringComparison.Ordinal);
        }

        private static bool IsAdjacentTransposition(string required, string field)
        {
            if (required.Length != field.Length || required.Length < 3)
            {
                return false;
            }

            var diffs = new List<int>();
            for (var i = 0; i < required.Length; i++)
            {
                if (required[i] == field[i])
                {
                    continue;
                }

                diffs.Add(i);
                if (diffs.Count > 2)
                {
                    return false;
                }
            }

            return diffs.Count == 2 &&
                   diffs[1] == diffs[0] + 1 &&
                   required[diffs[0]] == field[diffs[1]] &&
                   required[diffs[1]] == field[diffs[0]];
        }
    }
}
