using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    internal static class TitleHomogenizer
    {
        // Compute common title tokens between two titles with threshold rule: loss ≤ min(2, 50%)
        public static string ComputeCommonTitle(string title1, string title2)
        {
            if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2)) return null;

            var tokens1 = MatchingTextUtils.TokenizeToList(MatchingTextUtils.NormalizeBasic(title1));
            var tokens2 = MatchingTextUtils.TokenizeToList(MatchingTextUtils.NormalizeBasic(title2));

            if (tokens1.Count == 0 || tokens2.Count == 0) return null;

            // Intersection preserving order from tokens1
            var set2 = new HashSet<string>(tokens2, StringComparer.Ordinal);
            var intersection = tokens1.Where(x => set2.Contains(x)).ToList();

            var minCount = Math.Min(tokens1.Count, tokens2.Count);
            var maxLoss = Math.Min(2, minCount / 2);
            var minRequired = Math.Max(1, minCount - maxLoss);

            if (intersection.Count >= minRequired)
            {
                return string.Join(" ", intersection);
            }

            return null;
        }

        // Detailed variant for callers that want to log kept/dropped tokens (from the perspective of title1)
        public static string ComputeCommonTitleDetailed(string title1, string title2, out List<string> keptTokens, out List<string> droppedTokens)
        {
            keptTokens = new List<string>();
            droppedTokens = new List<string>();

            if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2)) return null;

            var tokens1 = MatchingTextUtils.TokenizeToList(MatchingTextUtils.NormalizeBasic(title1));
            var tokens2 = MatchingTextUtils.TokenizeToList(MatchingTextUtils.NormalizeBasic(title2));
            if (tokens1.Count == 0 || tokens2.Count == 0) return null;

            var set2 = new HashSet<string>(tokens2, StringComparer.Ordinal);
            keptTokens = tokens1.Where(x => set2.Contains(x)).ToList();
            droppedTokens = tokens1.Where(x => !set2.Contains(x)).ToList();

            var minCount = Math.Min(tokens1.Count, tokens2.Count);
            var maxLoss = Math.Min(2, minCount / 2);
            var minRequired = Math.Max(1, minCount - maxLoss);

            if (keptTokens.Count >= minRequired)
            {
                return string.Join(" ", keptTokens);
            }

            return null;
        }
    }
}
