using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    internal sealed class MatchEvidenceValueBuilder
    {
        private const int MaxStoredValueLength = 500;
        private const int WindowContextLength = 40;
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}]+(?:['\u2018\u2019][sS])?", RegexOptions.Compiled);

        private sealed class EvidenceToken
        {
            public string Value { get; init; }
            public int Start { get; init; }
            public int End { get; init; }
        }

        private readonly List<MatchEvidenceValue> _values = new List<MatchEvidenceValue>();

        public void AddPhrase(
            string source,
            string field,
            string rawValue,
            string phrase,
            string disposition,
            string type,
            string scope,
            string detail,
            bool allowNearExact = false)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || string.IsNullOrWhiteSpace(phrase))
            {
                return;
            }

            var fieldTokens = Tokenize(rawValue);
            var requiredTokens = Tokenize(phrase).Select(token => token.Value).ToList();
            if (!TryFindBestMatches(fieldTokens, requiredTokens, allowNearExact, out var matchedIndexes))
            {
                return;
            }

            AddTokenIndexes(source, field, rawValue, fieldTokens, matchedIndexes, disposition, type, scope, detail);
        }

        public void AddMatchingTokens(
            string source,
            string field,
            string rawValue,
            IEnumerable<string> expectedTokens,
            string disposition,
            string type,
            string scope,
            string detail)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || expectedTokens == null)
            {
                return;
            }

            var expected = expectedTokens
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Select(NormalizeToken)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expected.Count == 0)
            {
                return;
            }

            var fieldTokens = Tokenize(rawValue);
            var matchedIndexes = fieldTokens
                .Select((token, index) => new { token, index })
                .Where(item => expected.Any(expectedToken =>
                    TitleTokenAlignment.TokensMatchExactOrSynonym(expectedToken, item.token.Value)))
                .Select(item => item.index)
                .ToList();

            AddTokenIndexes(source, field, rawValue, fieldTokens, matchedIndexes, disposition, type, scope, detail);
        }

        public void AddLiteral(
            string source,
            string field,
            string rawValue,
            string literal,
            string disposition,
            string type,
            string scope,
            string detail)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || string.IsNullOrWhiteSpace(literal))
            {
                return;
            }

            var start = rawValue.IndexOf(literal, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return;
            }

            AddRange(source, field, rawValue, new MatchEvidenceRange
            {
                Start = start,
                End = start + literal.Length,
                Disposition = disposition,
                Type = type,
                Scope = scope,
                Detail = detail
            });
        }

        public void AddNeutralRemainder(
            string source,
            string field,
            string rawValue,
            string scope,
            string detail)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            var value = FindValue(source, rawValue);
            if (value == null)
            {
                return;
            }

            var occupied = value.Ranges
                .Where(range => range != null && range.Start >= 0 && range.End > range.Start)
                .ToList();

            foreach (var token in Tokenize(rawValue))
            {
                if (occupied.Any(range => token.Start < range.End && token.End > range.Start))
                {
                    continue;
                }

                AddRange(source, field, rawValue, new MatchEvidenceRange
                {
                    Start = token.Start,
                    End = token.End,
                    Disposition = "neutral",
                    Type = "tolerated_metadata",
                    Scope = scope,
                    Detail = detail
                });
            }
        }

        public List<MatchEvidenceValue> Build()
        {
            return _values
                .Where(value => value.Ranges?.Count > 0)
                .Select(BuildStoredValue)
                .OrderBy(value => SourceOrder(value.Source))
                .ThenBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int SourceOrder(string source)
        {
            return string.Equals(source, "embedded_tag", StringComparison.OrdinalIgnoreCase) ? 0 :
                string.Equals(source, "path", StringComparison.OrdinalIgnoreCase) ? 1 :
                string.Equals(source, "filename", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        }

        private void AddTokenIndexes(
            string source,
            string field,
            string rawValue,
            IReadOnlyList<EvidenceToken> fieldTokens,
            IEnumerable<int> matchedIndexes,
            string disposition,
            string type,
            string scope,
            string detail)
        {
            var indexes = (matchedIndexes ?? Enumerable.Empty<int>())
                .Where(index => index >= 0 && index < fieldTokens.Count)
                .Distinct()
                .OrderBy(index => index)
                .ToList();
            if (indexes.Count == 0)
            {
                return;
            }

            var spans = new List<(int Start, int End)>();
            foreach (var index in indexes)
            {
                var token = fieldTokens[index];
                if (spans.Count > 0 &&
                    IsNonSemanticGap(rawValue, spans[^1].End, token.Start))
                {
                    var previous = spans[^1];
                    spans[^1] = (previous.Start, token.End);
                }
                else
                {
                    spans.Add((token.Start, token.End));
                }
            }

            foreach (var span in spans)
            {
                AddRange(source, field, rawValue, new MatchEvidenceRange
                {
                    Start = span.Start,
                    End = span.End,
                    Disposition = disposition,
                    Type = type,
                    Scope = scope,
                    Detail = detail
                });
            }
        }

        private void AddRange(string source, string field, string rawValue, MatchEvidenceRange range)
        {
            if (range == null || range.Start < 0 || range.End <= range.Start || range.End > rawValue.Length)
            {
                return;
            }

            var value = FindValue(source, rawValue);
            if (value == null)
            {
                value = new MatchEvidenceValue
                {
                    Source = source,
                    Value = rawValue
                };
                _values.Add(value);
            }

            if (!string.IsNullOrWhiteSpace(field) &&
                !value.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                value.Fields.Add(field);
            }

            if (!value.Ranges.Any(existing =>
                    existing.Start == range.Start &&
                    existing.End == range.End &&
                    string.Equals(existing.Disposition, range.Disposition, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Type, range.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Scope, range.Scope, StringComparison.OrdinalIgnoreCase)))
            {
                value.Ranges.Add(range);
            }
        }

        private MatchEvidenceValue FindValue(string source, string rawValue)
        {
            return _values.FirstOrDefault(value =>
                string.Equals(value.Source, source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.Value, rawValue, StringComparison.Ordinal));
        }

        private static MatchEvidenceValue BuildStoredValue(MatchEvidenceValue source)
        {
            var clone = source.Clone();
            clone.Fields = clone.Fields
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
                .ToList();
            clone.Ranges = clone.Ranges
                .Where(range => range.Start >= 0 && range.End > range.Start && range.End <= clone.Value.Length)
                .OrderBy(range => range.Start)
                .ThenBy(range => range.End)
                .ToList();

            if (clone.Value.Length <= MaxStoredValueLength || clone.Ranges.Count == 0)
            {
                return clone;
            }

            var first = clone.Ranges.Min(range => range.Start);
            var last = clone.Ranges.Max(range => range.End);
            if (last - first > MaxStoredValueLength - 2)
            {
                // Matchable title/person values are bounded upstream. If a pathological field has
                // annotations farther apart than the display budget, retain it intact rather than
                // corrupt or silently discard a decision-time range.
                return clone;
            }

            var availableContext = MaxStoredValueLength - (last - first) - 2;
            var before = Math.Min(first, Math.Min(WindowContextLength, availableContext / 2));
            var after = Math.Min(clone.Value.Length - last, Math.Min(WindowContextLength, availableContext - before));
            var windowStart = first - before;
            var windowEnd = last + after;
            var hasPrefix = windowStart > 0;
            var hasSuffix = windowEnd < clone.Value.Length;
            var prefix = hasPrefix ? "…" : string.Empty;
            var suffix = hasSuffix ? "…" : string.Empty;

            clone.Value = prefix + clone.Value.Substring(windowStart, windowEnd - windowStart) + suffix;
            foreach (var range in clone.Ranges)
            {
                range.Start = range.Start - windowStart + prefix.Length;
                range.End = range.End - windowStart + prefix.Length;
            }

            return clone;
        }

        private static bool TryFindBestMatches(
            IReadOnlyList<EvidenceToken> fieldTokens,
            IReadOnlyList<string> requiredTokens,
            bool allowNearExact,
            out List<int> matchedIndexes)
        {
            matchedIndexes = null;
            if (fieldTokens == null || requiredTokens == null || fieldTokens.Count == 0 || requiredTokens.Count == 0)
            {
                return false;
            }

            var fieldValues = fieldTokens.Select(token => token.Value).ToList();
            if (!TitleTokenAlignment.TryAlignStructural(
                    requiredTokens,
                    fieldValues,
                    allowNearExact,
                    allowTransposition: allowNearExact,
                    out var ordered))
            {
                return false;
            }

            var first = ordered.ConsumedFieldIndexes.Min();
            var last = ordered.ConsumedFieldIndexes.Max();
            matchedIndexes = Enumerable.Range(first, last - first + 1).ToList();
            return true;
        }

        private static List<EvidenceToken> Tokenize(string rawValue)
        {
            var tokens = new List<EvidenceToken>();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return tokens;
            }

            foreach (Match match in TokenRegex.Matches(rawValue))
            {
                var normalized = NormalizeToken(match.Value);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                tokens.Add(new EvidenceToken
                {
                    Value = normalized,
                    Start = match.Index,
                    End = match.Index + match.Length
                });
            }

            return tokens;
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = Regex.Replace(value, "['\u2018\u2019]s$", "s", RegexOptions.IgnoreCase);
            value = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var character in value)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(character);
                }
            }

            return builder.ToString().ToLowerInvariant();
        }

        private static bool IsNonSemanticGap(string value, int start, int end)
        {
            if (start < 0 || end < start || end > value.Length)
            {
                return false;
            }

            for (var index = start; index < end; index++)
            {
                if (char.IsLetterOrDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
