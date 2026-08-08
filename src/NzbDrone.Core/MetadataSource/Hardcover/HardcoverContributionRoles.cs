using System;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MetadataSource.Hardcover
{
    internal static class HardcoverContributionRoles
    {
        public static bool IsPrimaryAuthor(string contribution)
        {
            return TryGetPrimaryPriority(contribution, out _);
        }

        public static bool TryGetPrimaryPriority(string contribution, out int priority)
        {
            // Mirrors SMS HardcoverContributionRolePrimaryPriority:
            // explicit persona roles outrank Hardcover's common NULL/blank marker.
            var normalized = Normalize(contribution);
            if (normalized.IsNullOrWhiteSpace())
            {
                priority = 1;
                return true;
            }

            switch (normalized)
            {
                case "author":
                case "pseudonym":
                case "house name":
                case "author's hebrew name":
                    priority = 0;
                    return true;
                default:
                    priority = 0;
                    return false;
            }
        }

        private static string Normalize(string contribution)
        {
            if (contribution.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return string.Join(" ", contribution.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
