using System;
using System.Collections.Generic;

namespace NzbDrone.Core.MetadataSource
{
    public static class ProviderIdHelper
    {
        private static readonly IReadOnlyList<string> CanonicalPrefixesOrdered = Array.AsReadOnly(new[]
        {
            "hc",
            "gr",
            "ol",
            "gb",
            "az"
        });

        private static readonly HashSet<string> CanonicalPrefixSet = new HashSet<string>(CanonicalPrefixesOrdered, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> CanonicalPrefixes => CanonicalPrefixesOrdered;
        public static string CanonicalPrefixesDisplay => string.Join("/", CanonicalPrefixesOrdered);

        public static bool IsCanonicalPrefix(string prefix)
        {
            return !string.IsNullOrWhiteSpace(prefix) && CanonicalPrefixSet.Contains(prefix.Trim());
        }

        public static string StripPrefix(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return providerId;
            }

            providerId = providerId.Trim();
            var idx = providerId.IndexOf(':');
            return idx > 0 ? providerId.Substring(idx + 1) : providerId;
        }

        /// <summary>
        /// Validates and normalizes a provider ID to canonical <c>{prefix}:{id}</c> form.
        /// <para/>
        /// Contract: provider IDs must already include a canonical prefix from <see cref="CanonicalPrefixes"/>.
        /// Any other prefix (or missing prefix) is treated as a contract violation and will throw.
        /// </summary>
        public static string Normalize(string providerId, string defaultPrefix)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            providerId = providerId.Trim();
            if (ContainsProviderIdArtifact(providerId))
            {
                throw new InvalidOperationException($"Provider ID '{providerId}' contains an invalid provider-id artifact.");
            }

            var colonIndex = providerId.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= providerId.Length - 1)
            {
                throw new InvalidOperationException($"Provider ID '{providerId}' is not in canonical '<prefix>:<id>' form.");
            }

            if (providerId.IndexOf(':', colonIndex + 1) != -1)
            {
                throw new InvalidOperationException($"Provider ID '{providerId}' contains multiple ':' separators; expected a single canonical '<prefix>:<id>'.");
            }

            var prefix = providerId.Substring(0, colonIndex).Trim().ToLowerInvariant();
            var id = providerId.Substring(colonIndex + 1).Trim();

            if (id.Length == 0)
            {
                return null;
            }

            if (ContainsProviderIdArtifact(id))
            {
                throw new InvalidOperationException($"Provider ID '{providerId}' contains an invalid provider-id artifact.");
            }

            if (!IsCanonicalPrefix(prefix))
            {
                throw new InvalidOperationException($"Provider ID '{providerId}' uses unsupported prefix '{prefix}'. Expected one of {CanonicalPrefixesDisplay}.");
            }

            if (!string.IsNullOrWhiteSpace(defaultPrefix))
            {
                var expectedPrefix = defaultPrefix.Trim().ToLowerInvariant();
                if (!IsCanonicalPrefix(expectedPrefix))
                {
                    throw new InvalidOperationException($"Expected provider prefix '{defaultPrefix}' is not a canonical prefix.");
                }

                if (!prefix.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Provider ID '{providerId}' has prefix '{prefix}' but expected '{expectedPrefix}'.");
                }
            }

            // Canonicalize AZ IDs to uppercase for deterministic comparisons.
            if (prefix.Equals("az", StringComparison.OrdinalIgnoreCase))
            {
                id = id.ToUpperInvariant();
            }

            return $"{prefix}:{id}";
        }

        public static bool TryNormalize(string providerId, string defaultPrefix, out string normalizedProviderId)
        {
            normalizedProviderId = null;

            try
            {
                normalizedProviderId = Normalize(providerId, defaultPrefix);
                return !string.IsNullOrWhiteSpace(normalizedProviderId);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Converts a raw or canonical provider ID into canonical <c>{prefix}:{id}</c> form.
        /// Use this on ingress or when repairing legacy local data. Use <see cref="Normalize"/>
        /// when the caller expects the value to already be canonical.
        /// </summary>
        public static string Canonicalize(string rawOrPrefixed, string expectedPrefix)
        {
            if (string.IsNullOrWhiteSpace(rawOrPrefixed))
            {
                return null;
            }

            rawOrPrefixed = rawOrPrefixed.Trim();
            if (ContainsProviderIdArtifact(rawOrPrefixed))
            {
                throw new InvalidOperationException($"Provider ID '{rawOrPrefixed}' contains an invalid provider-id artifact.");
            }

            if (rawOrPrefixed.Contains(":"))
            {
                return Normalize(rawOrPrefixed, expectedPrefix);
            }

            if (string.IsNullOrWhiteSpace(expectedPrefix))
            {
                throw new InvalidOperationException($"Provider ID '{rawOrPrefixed}' is raw/unprefixed and cannot be canonicalized without an expected prefix.");
            }

            return WithPrefix(expectedPrefix, rawOrPrefixed);
        }

        public static string WithPrefix(string prefix, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            prefix = prefix?.Trim().ToLowerInvariant();
            if (!IsCanonicalPrefix(prefix))
            {
                throw new InvalidOperationException($"Provider prefix '{prefix}' is not supported. Expected one of {CanonicalPrefixesDisplay}.");
            }

            id = id.Trim();
            if (id.Length == 0)
            {
                return null;
            }

            if (ContainsProviderIdArtifact(id))
            {
                throw new InvalidOperationException($"Provider ID '{id}' contains an invalid provider-id artifact.");
            }

            if (id.Contains(":"))
            {
                throw new InvalidOperationException($"Provider ID '{id}' contains ':' but was expected to be the raw id portion for prefix '{prefix}'.");
            }

            if (prefix == "az")
            {
                id = id.ToUpperInvariant();
            }

            return $"{prefix}:{id}";
        }

        public static bool ContainsProviderIdArtifact(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("System.Collections", StringComparison.Ordinal) >= 0 ||
                   value.IndexOf('`') >= 0 ||
                   value.IndexOf('[') >= 0 ||
                   value.IndexOf(']') >= 0;
        }

        public static bool ContainsUnsafeProviderAliasValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (ContainsProviderIdArtifact(value))
            {
                return true;
            }

            foreach (var ch in value)
            {
                if (char.IsWhiteSpace(ch))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
