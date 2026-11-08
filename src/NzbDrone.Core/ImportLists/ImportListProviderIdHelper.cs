using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.ImportLists
{
    public static class ImportListProviderIdHelper
    {
        public static List<string> GetExclusionLookupIds(IEnumerable<string> providerIds) =>
            (providerIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .SelectMany(id => id.StartsWith("gr:", StringComparison.OrdinalIgnoreCase) ? new[] { id, id.Substring(3) } : new[] { id })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static bool MatchesExclusionId(string providerId, string exclusionId)
        {
            if (providerId.IsNullOrWhiteSpace() || exclusionId.IsNullOrWhiteSpace())
            {
                return false;
            }

            providerId = providerId.Trim();
            exclusionId = exclusionId.Trim();
            if (exclusionId.Contains(":") || !providerId.Contains(":"))
            {
                return providerId.Equals(exclusionId, StringComparison.OrdinalIgnoreCase);
            }

            // Historical unqualified provider exclusions came from Goodreads. Preserve that
            // provenance so a same-numbered identifier from another provider cannot match.
            return providerId.StartsWith("gr:", StringComparison.OrdinalIgnoreCase) &&
                   providerId.Substring(3).Equals(exclusionId, StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string value, string defaultPrefix)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            value = value.Trim();

            if (value.Contains(":"))
            {
                try
                {
                    return ProviderIdHelper.Normalize(value, defaultPrefix: null);
                }
                catch
                {
                    return value;
                }
            }

            if (defaultPrefix.IsNullOrWhiteSpace())
            {
                return value;
            }

            return ProviderIdHelper.WithPrefix(defaultPrefix, value);
        }
    }
}
