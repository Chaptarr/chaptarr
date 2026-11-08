using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.DecisionEngine
{
    // Interactive search hides identity and pack mismatches behind Show Hidden. Once author
    // and book identity pass, profile and preference rejections stay visible with their
    // reasons. The API uses this predicate to split visible and hidden results.
    public static class InteractiveSearchVisibility
    {
        private static readonly HashSet<string> MismatchCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Matching",
            "Author",
            "Title",
            "Parsing",
            "Pack"
        };

        public static (bool ShouldFilter, bool IsHardFilter) Evaluate(DownloadDecision decision)
        {
            if (decision == null || !decision.Rejections.Any())
            {
                return (false, false);
            }

            if (!decision.Rejections.Any(r => MismatchCategories.Contains(r.Category ?? "General")))
            {
                return (false, false);
            }

            return (true, decision.Rejections.Any(r => r.IsHardFilter));
        }

        public static bool IsHidden(DownloadDecision decision)
        {
            return Evaluate(decision).ShouldFilter;
        }
    }
}
