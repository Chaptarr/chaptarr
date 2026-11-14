using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class UnitTagConsensusBuilder
    {
        private const double ConsensusRatio = 0.6d;

        internal static Dictionary<string, List<string>> BuildConsensus(IEnumerable<Dictionary<string, List<string>>> tagSets, int? totalFileCount = null, bool includeSharedText = false)
        {
            var nonEmptyTagSets = (tagSets ?? Enumerable.Empty<Dictionary<string, List<string>>>())
                .Where(tags => tags != null && tags.Count > 0)
                .ToList();

            if (nonEmptyTagSets.Count == 0)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var population = totalFileCount.GetValueOrDefault(nonEmptyTagSets.Count);
            if (population <= 0)
            {
                population = nonEmptyTagSets.Count;
            }

            var supportThreshold = Math.Max(1, (int)Math.Ceiling(population * ConsensusRatio));

            if (nonEmptyTagSets.Count == 1)
            {
                return supportThreshold <= 1
                    ? CloneTags(nonEmptyTagSets[0])
                    : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var valueSupportByKey = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var tagSet in nonEmptyTagSets)
            {
                foreach (var kv in tagSet)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0)
                    {
                        continue;
                    }

                    var distinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawValue in kv.Value)
                    {
                        var value = rawValue?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            distinctValues.Add(value);
                        }
                    }

                    if (distinctValues.Count == 0)
                    {
                        continue;
                    }

                    if (!valueSupportByKey.TryGetValue(kv.Key, out var valueSupport))
                    {
                        valueSupport = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        valueSupportByKey[kv.Key] = valueSupport;
                    }

                    foreach (var value in distinctValues)
                    {
                        valueSupport[value] = valueSupport.TryGetValue(value, out var count) ? count + 1 : 1;
                    }
                }
            }

            var consensus = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyEntry in valueSupportByKey)
            {
                var key = keyEntry.Key;
                var selectedValues = keyEntry.Value
                    .Where(valueEntry => valueEntry.Value >= supportThreshold)
                    .OrderByDescending(valueEntry => valueEntry.Value)
                    .ThenBy(valueEntry => valueEntry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(valueEntry => valueEntry.Key)
                    .ToList();

                if (includeSharedText && !FileMatchingService.IsExcludedFromMatching(key))
                {
                    if (nonEmptyTagSets.Count != population || !nonEmptyTagSets.All(tags => tags.ContainsKey(key)))
                    {
                        selectedValues.Clear();
                    }
                    else if (keyEntry.Value.Any(value => value.Value < population))
                    {
                        selectedValues = FindSharedText(nonEmptyTagSets.Select(tags => tags[key]));
                    }
                }

                if (selectedValues.Count > 0)
                {
                    consensus[key] = selectedValues;
                }
            }

            return consensus;
        }

        // These spans seed matching only. Stored conversion tags keep complete source values,
        // and group membership still requires identity proof against each original field.
        private static List<string> FindSharedText(IEnumerable<List<string>> fields)
        {
            List<string[]> shared = null;
            List<(string Value, List<TitleTokenSpan> Spans)> originals = null;
            foreach (var values in fields)
            {
                var prepared = (values ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= ContainmentValidator.MaxTitleEvidenceValueLength)
                    .Distinct(StringComparer.Ordinal)
                    .Select(value => (Value: value, Spans: ReleaseTitleMatchScorer.TokenizeWithSpans(value)))
                    .Where(value => value.Spans.Count > 0).ToList();
                originals ??= prepared;
                var tokens = prepared.Select(value => value.Spans.Select(span => span.Value).ToArray()).ToList();
                shared = shared == null ? tokens : CommonSpans(shared, tokens);
                if (shared.Count == 0)
                {
                    break;
                }
            }

            return (shared ?? new List<string[]>()).Select(tokens => originals.SelectMany(source =>
                    ReleaseTitleMatchScorer.FindExactTitleSpans(source.Spans.Select(span => span.Value).ToList(), tokens)
                        .Select(span => source.Value.Substring(source.Spans[span.Start].Start,
                            source.Spans[span.End].End - source.Spans[span.Start].Start))).First())
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        private static List<string[]> CommonSpans(List<string[]> left, List<string[]> right)
        {
            var spans = new HashSet<string>(StringComparer.Ordinal);
            foreach (var first in left)
            {
                foreach (var second in right)
                {
                    for (var i = 0; i < first.Length; i++)
                    {
                        for (var j = 0; j < second.Length; j++)
                        {
                            if (first[i] != second[j] || (i > 0 && j > 0 && first[i - 1] == second[j - 1]))
                            {
                                continue;
                            }

                            var length = 1;
                            while (i + length < first.Length && j + length < second.Length && first[i + length] == second[j + length])
                            {
                                length++;
                            }

                            spans.Add(string.Join(" ", first, i, length));
                        }
                    }
                }
            }

            return spans.Where(span => !spans.Any(other => other.Length > span.Length &&
                    (" " + other + " ").Contains(" " + span + " ", StringComparison.Ordinal)))
                .Select(span => span.Split(' ')).ToList();
        }

        private static Dictionary<string, List<string>> CloneTags(Dictionary<string, List<string>> source)
        {
            var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (source == null || source.Count == 0)
            {
                return clone;
            }

            foreach (var kv in source)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    continue;
                }

                var values = (kv.Value ?? new List<string>())
                    .Select(value => value?.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (values.Count > 0)
                {
                    clone[kv.Key] = values;
                }
            }

            return clone;
        }
    }
}
