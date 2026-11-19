using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// Candidate-relative account of the exact token positions consumed from one observed value.
    /// OccurrenceKey intentionally omits the tag label: raw/canonical aliases carrying the same
    /// normalized value are one conservative occurrence, not independent votes.
    /// </summary>
    internal sealed class TitleOccurrenceExplanation
    {
        public string OccurrenceKey { get; set; }
        public string FieldName { get; set; }
        public bool IsNearExact { get; set; }
        public bool IsSeriesContext { get; set; }
        public HashSet<int> MeaningfulConsumedIndexes { get; set; } = new HashSet<int>();
        public IReadOnlyList<string> NearbyUnexplainedTokens { get; set; } = Array.Empty<string>();
    }

    internal static class TitleOccurrenceComparison
    {
        internal static bool ExplainsTitle(TitleOccurrenceExplanation title, IReadOnlyList<TitleOccurrenceExplanation> explanations)
        {
            var remaining = new HashSet<int>(title.MeaningfulConsumedIndexes);
            foreach (var explanation in explanations.Where(item =>
                string.Equals(item.OccurrenceKey, title.OccurrenceKey, StringComparison.Ordinal) &&
                (!item.IsNearExact || title.IsNearExact)))
            {
                remaining.ExceptWith(explanation.MeaningfulConsumedIndexes);
            }

            return remaining.Count == 0;
        }

        internal static bool StrictlyExplainsMoreOfSameOccurrence(
            TitleOccurrenceExplanation left,
            TitleOccurrenceExplanation right)
        {
            if (left == null || right == null ||
                string.IsNullOrWhiteSpace(left.OccurrenceKey) ||
                !string.Equals(left.OccurrenceKey, right.OccurrenceKey, StringComparison.Ordinal))
            {
                return false;
            }

            // A fuzzy explanation cannot displace an exact explanation merely by consuming more positions.
            if (left.IsNearExact && !right.IsNearExact)
            {
                return false;
            }

            return left.MeaningfulConsumedIndexes.IsProperSupersetOf(right.MeaningfulConsumedIndexes);
        }

        internal static int CompareWorkOccurrenceExplanations(
            IReadOnlyList<TitleOccurrenceExplanation> left,
            IReadOnlyList<TitleOccurrenceExplanation> right,
            bool allowSeriesContext)
        {
            var leftTitles = left.Where(item => !item.IsSeriesContext).ToList();
            var rightTitles = right.Where(item => !item.IsSeriesContext).ToList();
            bool Covers(TitleOccurrenceExplanation explanation, TitleOccurrenceExplanation title) =>
                string.Equals(explanation.OccurrenceKey, title.OccurrenceKey, StringComparison.Ordinal) &&
                (!explanation.IsNearExact || title.IsNearExact) &&
                explanation.MeaningfulConsumedIndexes.IsSupersetOf(title.MeaningfulConsumedIndexes);

            bool ExplainsConnectedEvidence(
                IReadOnlyList<TitleOccurrenceExplanation> explanations,
                IReadOnlyList<TitleOccurrenceExplanation> titles,
                IReadOnlyList<TitleOccurrenceExplanation> otherExplanations,
                IReadOnlyList<TitleOccurrenceExplanation> otherTitles) =>
                explanations.Any(item => item.IsSeriesContext) && otherTitles.Count > 0 &&
                otherTitles.All(title => explanations.Any(item => Covers(item, title))) &&
                titles.Any(title => !title.IsNearExact && !otherExplanations.Any(item => Covers(item, title)));

            var leftExplainsMore = allowSeriesContext && ExplainsConnectedEvidence(left, leftTitles, right, rightTitles);
            var rightExplainsMore = allowSeriesContext && ExplainsConnectedEvidence(right, rightTitles, left, leftTitles);
            if (leftExplainsMore != rightExplainsMore)
            {
                return leftExplainsMore ? 1 : -1;
            }

            // A book's own series can explain an otherwise competing title; without that
            // connection, preserve the strict title comparison within each physical value.
            leftExplainsMore = leftTitles.Any(item => rightTitles.Any(other => StrictlyExplainsMoreOfSameOccurrence(item, other)));
            rightExplainsMore = rightTitles.Any(item => leftTitles.Any(other => StrictlyExplainsMoreOfSameOccurrence(item, other)));
            return leftExplainsMore == rightExplainsMore ? 0 : leftExplainsMore ? 1 : -1;
        }
    }
}
