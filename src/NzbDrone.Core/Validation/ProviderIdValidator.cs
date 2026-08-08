using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Validation
{
    public static class ProviderIdValidator
    {
        public static readonly HashSet<string> ValidPrefixes = new HashSet<string>(ProviderIdHelper.CanonicalPrefixes, StringComparer.OrdinalIgnoreCase);

        private static readonly Regex ProviderIdRegex = new Regex(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

        public static string ValidPrefixesDisplay => ProviderIdHelper.CanonicalPrefixesDisplay;

        private static string CanonicalizePrefix(string prefix)
        {
            if (prefix.IsNullOrWhiteSpace())
            {
                return prefix;
            }

            return prefix.Trim().ToLowerInvariant();
        }

        public static bool TryNormalize(string raw, out string normalizedProviderId, out string prefix, out string id, out string errorMessage)
        {
            normalizedProviderId = null;
            prefix = null;
            id = null;
            errorMessage = null;

            var decoded = WebUtility.UrlDecode(raw);
            decoded = decoded?.Trim().Trim('{', '}');

            var parts = decoded?.Split(new[] { ':' }, 2) ?? Array.Empty<string>();
            if (parts.Length != 2 ||
                parts[0].IsNullOrWhiteSpace() ||
                parts[1].IsNullOrWhiteSpace())
            {
                errorMessage = "Invalid provider ID format. Expected 'provider:id'.";
                return false;
            }

            prefix = CanonicalizePrefix(parts[0]);
            id = parts[1].Trim();

            if (!ValidPrefixes.Contains(prefix) || !ProviderIdRegex.IsMatch(id))
            {
                errorMessage = $"Invalid provider ID. Expected one of {ValidPrefixesDisplay} with an alphanumeric id.";
                return false;
            }

            normalizedProviderId = $"{prefix}:{id}";
            return true;
        }

        public static bool IsValidId(string id)
        {
            return id.IsNotNullOrWhiteSpace() && ProviderIdRegex.IsMatch(id.Trim());
        }
    }
}
