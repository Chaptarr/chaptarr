using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public static class AuthorIdentity
    {
        public static bool MatchesByProviderIdIntersection(Author local, Author remote)
        {
            if (local == null || remote == null)
            {
                return false;
            }

            var localTokens = GetProviderIdentityTokens(local);
            if (localTokens.Count == 0)
            {
                return false;
            }

            var remoteTokens = GetProviderIdentityTokens(remote);
            if (remoteTokens.Count == 0)
            {
                return false;
            }

            return localTokens.Overlaps(remoteTokens);
        }

        public static List<string> GetProviderIdentityTokenList(Author author)
        {
            var tokens = new List<string>();
            if (author == null)
            {
                return tokens;
            }

            Add(tokens, author.HardcoverAuthorId, "hc");
            Add(tokens, author.GoodreadsAuthorId, "gr");
            Add(tokens, author.OpenLibraryAuthorId, "ol");
            Add(tokens, author.GoogleBooksAuthorId, "gb");
            Add(tokens, author.AudnexusAuthorId, "az");

            foreach (var providerId in author.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                Add(tokens, providerId, null);
            }

            return tokens
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static HashSet<string> GetProviderIdentityTokens(Author author)
        {
            return new HashSet<string>(GetProviderIdentityTokenList(author), StringComparer.OrdinalIgnoreCase);
        }

        public static string GetPreferredProviderId(Author author)
        {
            return GetProviderIdentityTokenList(author).FirstOrDefault();
        }

        public static string GetProviderIdentityTokenForPrefix(Author author, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return null;
            }

            var expectedPrefix = prefix.Trim().ToLowerInvariant();
            return GetProviderIdentityTokenList(author)
                .FirstOrDefault(id => id.StartsWith(expectedPrefix + ":", StringComparison.OrdinalIgnoreCase));
        }

        public static string GetWorkLookupAuthorHintForProviderId(Author author, string providerId)
        {
            var prefix = GetSupportedWorkLookupHintPrefix(providerId);
            return prefix == null ? null : GetProviderIdentityTokenForPrefix(author, prefix);
        }

        public static string NormalizeWorkLookupAuthorHint(string providerId, string authorProviderId)
        {
            var providerPrefix = GetSupportedWorkLookupHintPrefix(providerId);
            if (providerPrefix == null ||
                !ProviderIdHelper.TryNormalize(authorProviderId, defaultPrefix: null, out var normalizedAuthorProviderId))
            {
                return null;
            }

            var authorPrefix = GetProviderPrefix(normalizedAuthorProviderId);
            return providerPrefix.Equals(authorPrefix, StringComparison.OrdinalIgnoreCase)
                ? normalizedAuthorProviderId
                : null;
        }

        public static HashSet<string> MergeProviderIdentityTokens(params Author[] authors)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var author in authors ?? Array.Empty<Author>())
            {
                foreach (var providerId in GetProviderIdentityTokenList(author))
                {
                    tokens.Add(providerId);
                }
            }

            return tokens;
        }

        private static string GetSupportedWorkLookupHintPrefix(string providerId)
        {
            if (!ProviderIdHelper.TryNormalize(providerId, defaultPrefix: null, out var normalizedProviderId))
            {
                return null;
            }

            var prefix = GetProviderPrefix(normalizedProviderId);
            return prefix is "gr" or "hc" ? prefix : null;
        }

        private static string GetProviderPrefix(string providerId)
        {
            var idx = providerId?.IndexOf(':') ?? -1;
            return idx > 0 ? providerId.Substring(0, idx).ToLowerInvariant() : null;
        }

        private static void Add(List<string> tokens, string rawId, string defaultPrefix)
        {
            var normalized = NormalizeProviderId(rawId, defaultPrefix);
            if (normalized == null)
            {
                return;
            }

            tokens.Add(normalized);
        }

        private static string NormalizeProviderId(string rawId, string defaultPrefix)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(rawId.Trim(), defaultPrefix);
            }
            catch
            {
                return null;
            }
        }
    }
}
