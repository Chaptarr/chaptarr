using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class UnitTagConsensusBuilder
    {
        private const double ConsensusRatio = 0.6d;

        internal static Dictionary<string, List<string>> BuildConsensus(IEnumerable<Dictionary<string, List<string>>> tagSets, int? totalFileCount = null)
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

                if (selectedValues.Count > 0)
                {
                    consensus[key] = selectedValues;
                }
            }

            if (consensus.Count > 0)
            {
                return consensus;
            }

            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
