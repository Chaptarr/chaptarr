using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Profiles.Metadata
{
    public static class PreferredEditionTermMatcher
    {
        public static double Score(string preferences, string title, string format, string publisher)
        {
            if (string.IsNullOrWhiteSpace(preferences))
            {
                return 0;
            }

            var terms = Parse(preferences);
            if (terms.Count == 0)
            {
                return 0;
            }

            var editionText = string.Join(" ", new[] { title, format, publisher }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

            var maxWeight = terms.Max(term => term.Weight);
            var matchingWeight = terms
                .Where(term => editionText.IndexOf(term.Term, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(term => term.Weight)
                .DefaultIfEmpty(0)
                .Max();

            return matchingWeight / (double)maxWeight;
        }

        private static List<WeightedTerm> Parse(string preferences)
        {
            var terms = new List<WeightedTerm>();
            foreach (var value in preferences.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = value.IndexOf('|');
                if (separator <= 0 || separator == value.Length - 1 ||
                    !int.TryParse(value.Substring(0, separator).Trim(), out var weight) || weight <= 0)
                {
                    continue;
                }

                var term = value.Substring(separator + 1).Trim();
                if (!string.IsNullOrWhiteSpace(term))
                {
                    terms.Add(new WeightedTerm(weight, term));
                }
            }

            return terms;
        }

        private sealed class WeightedTerm
        {
            public WeightedTerm(int weight, string term)
            {
                Weight = weight;
                Term = term;
            }

            public int Weight { get; }
            public string Term { get; }
        }
    }
}
