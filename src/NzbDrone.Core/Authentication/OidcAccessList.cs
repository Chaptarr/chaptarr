using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Authentication
{
    public static class OidcAccessList
    {
        public static HashSet<string> Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return raw
                .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static bool HasNoAccessRule(string emails, string domains, bool allowAnyVerifiedUser)
        {
            return !allowAnyVerifiedUser && Parse(emails).Count == 0 && Parse(domains).Count == 0;
        }

        public static bool HasValidDomains(string raw)
        {
            return Parse(raw).All(domain => Uri.CheckHostName(domain) == UriHostNameType.Dns &&
                                            !domain.EndsWith(".", StringComparison.Ordinal));
        }
    }
}
