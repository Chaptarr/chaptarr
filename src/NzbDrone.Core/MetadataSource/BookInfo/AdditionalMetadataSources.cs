using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MetadataSource.BookInfo
{
    /// <summary>
    /// Catalog source identifiers and compatibility rules for the runtime
    /// metadata catalog selection.
    /// </summary>
    public static class AdditionalMetadataSources
    {
        public const string ProviderIdPrefix = "meta";
        public const int MaxProviderIdLength = 2048;
        public const string OpenLibrary = "openlibrary";
        public const string Hardcover = "hardcover";
        public const string MetadataApi = "metadata-api";

        public static string GetEnvironmentSourceOverride()
        {
            var configured = Environment.GetEnvironmentVariable("CHAPTARR_METADATA_SOURCES");
            return !configured.IsNullOrWhiteSpace()
                ? configured
                : Environment.GetEnvironmentVariable("BOOKSHELF_METADATA_SOURCES");
        }

        private static readonly HashSet<string> AdditionalSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "googlebooks",
            "loc",
            "gutendex",
            "internetarchive",
            "ndl",
            "europeana",
            "apify-goodreads",
            OpenLibrary
        };

        // The configured metadata server participates in catalog search, but it
        // does not provide supplemental per-field metadata through this proxy.
        private static readonly HashSet<string> CatalogSources = new HashSet<string>(AdditionalSources, StringComparer.OrdinalIgnoreCase)
        {
            Hardcover,
            MetadataApi
        };

        // These sources can be fetched by AdditionalBookMetadataProxy and can
        // therefore be used for per-field ISBN-matched metadata preferences.
        public static bool IsSupportedSource(string source) =>
            !string.IsNullOrWhiteSpace(source) && AdditionalSources.Contains(source.Trim());

        public static bool IsSupportedCatalogSource(string source) =>
            !string.IsNullOrWhiteSpace(source) && CatalogSources.Contains(source.Trim());

        public static bool IsValidFieldPreference(string source) =>
            string.IsNullOrWhiteSpace(source) || IsSupportedSource(source);

        public static string CreateProviderId(string source, string sourceId)
        {
            var provider = (source ?? string.Empty).Trim().ToLowerInvariant();
            var id = (sourceId ?? string.Empty).Trim();
            var baseProvider = provider.EndsWith("-author", StringComparison.Ordinal)
                ? provider.Substring(0, provider.Length - "-author".Length)
                : provider;

            if (!IsSupportedSource(baseProvider) || id.IsNullOrWhiteSpace())
            {
                return null;
            }

            var payload = Encoding.UTF8.GetBytes(provider + "\n" + id);
            var encoded = Convert.ToBase64String(payload)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var providerId = ProviderIdPrefix + ":" + encoded;
            return providerId.Length <= MaxProviderIdLength ? providerId : null;
        }

        public static string CanonicalizeQualifiedProviderId(string providerId)
        {
            if (TryParseProviderId(providerId, out _, out _))
            {
                return providerId.Trim();
            }

            var separator = providerId?.IndexOf(':') ?? -1;
            if (separator <= 0 || separator >= providerId.Length - 1)
            {
                return null;
            }

            return CreateProviderId(providerId.Substring(0, separator), providerId.Substring(separator + 1));
        }

        public static bool TryParseProviderId(string providerId, out string source, out string sourceId)
        {
            source = null;
            sourceId = null;
            var separator = providerId?.IndexOf(':') ?? -1;
            if (separator <= 0 ||
                providerId.Length > MaxProviderIdLength ||
                !providerId.Substring(0, separator).Equals(ProviderIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var encoded = providerId.Substring(separator + 1).Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var valueSeparator = decoded.IndexOf('\n');
                if (valueSeparator <= 0 || valueSeparator >= decoded.Length - 1)
                {
                    return false;
                }

                var parsedSource = decoded.Substring(0, valueSeparator).Trim().ToLowerInvariant();
                var parsedId = decoded.Substring(valueSeparator + 1).Trim();
                var baseProvider = parsedSource.EndsWith("-author", StringComparison.Ordinal)
                    ? parsedSource.Substring(0, parsedSource.Length - "-author".Length)
                    : parsedSource;
                if (!IsSupportedSource(baseProvider) || parsedId.IsNullOrWhiteSpace())
                {
                    return false;
                }

                source = parsedSource;
                sourceId = parsedId;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static readonly HashSet<string> ManagedDefaultLists = new HashSet<string>(StringComparer.Ordinal)
        {
            "europeana,googlebooks",
            "europeana,googlebooks,gutendex",
            "europeana,googlebooks,loc",
            "europeana,googlebooks,gutendex,loc",
            "gutendex,loc",
            "loc,gutendex"
        };

        /// <summary>
        /// Resolves source selection, retaining the legacy additional-source
        /// setting and adding the previously implicit primary provider when
        /// no runtime catalog selection has been saved yet.
        /// </summary>
        public static HashSet<string> GetConfiguredRuntimeSources(
            string environmentSources,
            string runtimeSources,
            bool hasRuntimeSources,
            string legacyAdditionalSources,
            bool hasLegacyAdditionalSources,
            string googleBooksApiKey,
            string europeanaApiKey,
            string apifyActor,
            string apifyToken,
            string defaultPrimarySource)
        {
            string sourceList;

            if (IsEnvironmentOverride(environmentSources))
            {
                sourceList = environmentSources;
            }
            else if (hasRuntimeSources)
            {
                sourceList = runtimeSources ?? string.Empty;
            }
            else if (hasLegacyAdditionalSources)
            {
                sourceList = legacyAdditionalSources ?? string.Empty;
                sourceList = AddSource(sourceList, defaultPrimarySource);
            }
            else
            {
                sourceList = "loc,gutendex";
                sourceList = AddSource(sourceList, defaultPrimarySource);

                if (!googleBooksApiKey.IsNullOrWhiteSpace())
                {
                    sourceList = AddSource(sourceList, "googlebooks");
                }

                if (!europeanaApiKey.IsNullOrWhiteSpace())
                {
                    sourceList = AddSource(sourceList, "europeana");
                }
            }

            return Parse(sourceList);
        }

        public static HashSet<string> GetEnabledRuntimeSources(
            string environmentSources,
            string runtimeSources,
            bool hasRuntimeSources,
            string legacyAdditionalSources,
            bool hasLegacyAdditionalSources,
            string googleBooksApiKey,
            string europeanaApiKey,
            string apifyActor,
            string apifyToken,
            string defaultPrimarySource,
            bool hardcoverConfigured)
        {
            var enabled = GetConfiguredRuntimeSources(
                environmentSources,
                runtimeSources,
                hasRuntimeSources,
                legacyAdditionalSources,
                hasLegacyAdditionalSources,
                googleBooksApiKey,
                europeanaApiKey,
                apifyActor,
                apifyToken,
                defaultPrimarySource);

            if (googleBooksApiKey.IsNullOrWhiteSpace())
            {
                enabled.Remove("googlebooks");
            }

            if (europeanaApiKey.IsNullOrWhiteSpace())
            {
                enabled.Remove("europeana");
            }

            if (apifyActor.IsNullOrWhiteSpace() || apifyToken.IsNullOrWhiteSpace())
            {
                enabled.Remove("apify-goodreads");
            }

            if (!hardcoverConfigured)
            {
                enabled.Remove(Hardcover);
            }

            return enabled;
        }

        public static string GetEffectiveRuntimeSourceList(
            string environmentSources,
            string runtimeSources,
            bool hasRuntimeSources,
            string legacyAdditionalSources,
            bool hasLegacyAdditionalSources,
            string googleBooksApiKey,
            string europeanaApiKey,
            string apifyActor,
            string apifyToken,
            string defaultPrimarySource,
            bool hardcoverConfigured)
        {
            return string.Join(",", GetEnabledRuntimeSources(
                    environmentSources,
                    runtimeSources,
                    hasRuntimeSources,
                    legacyAdditionalSources,
                    hasLegacyAdditionalSources,
                    googleBooksApiKey,
                    europeanaApiKey,
                    apifyActor,
                    apifyToken,
                    defaultPrimarySource,
                    hardcoverConfigured)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        // Kept for callers and configuration created by earlier BookshelfNG
        // versions. These methods retain supplemental-provider semantics.
        public static HashSet<string> GetEnabledSources(
            string environmentSources,
            string savedSources,
            bool hasSavedSources,
            string googleBooksApiKey,
            string europeanaApiKey,
            string apifyActor,
            string apifyToken)
        {
            var enabled = GetConfiguredSources(
                environmentSources,
                savedSources,
                hasSavedSources,
                googleBooksApiKey,
                europeanaApiKey,
                apifyActor,
                apifyToken);

            if (googleBooksApiKey.IsNullOrWhiteSpace())
            {
                enabled.Remove("googlebooks");
            }

            if (europeanaApiKey.IsNullOrWhiteSpace())
            {
                enabled.Remove("europeana");
            }

            if (apifyActor.IsNullOrWhiteSpace() || apifyToken.IsNullOrWhiteSpace())
            {
                enabled.Remove("apify-goodreads");
            }

            return enabled;
        }

        public static HashSet<string> GetConfiguredSources(
            string environmentSources,
            string savedSources,
            bool hasSavedSources,
            string googleBooksApiKey,
            string europeanaApiKey,
            string apifyActor,
            string apifyToken)
        {
            var sourceList = ResolveSourceList(environmentSources, savedSources, hasSavedSources);

            if (sourceList == null)
            {
                sourceList = "loc,gutendex";

                if (!googleBooksApiKey.IsNullOrWhiteSpace())
                {
                    sourceList += ",googlebooks";
                }

                if (!europeanaApiKey.IsNullOrWhiteSpace())
                {
                    sourceList += ",europeana";
                }
            }

            return Parse(sourceList).Where(IsSupportedSource).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static string GetEffectiveSourceList(
            string environmentSources,
            string savedSources,
            bool hasSavedSources,
            string googleBooksApiKey,
            string europeanaApiKey,
            string apifyActor,
            string apifyToken)
        {
            return string.Join(",", GetEnabledSources(
                    environmentSources,
                    savedSources,
                    hasSavedSources,
                    googleBooksApiKey,
                    europeanaApiKey,
                    apifyActor,
                    apifyToken)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        public static bool IsEnvironmentOverride(string environmentSources) =>
            environmentSources != null && !ManagedDefaultLists.Contains(NormalizeSourceList(environmentSources));

        private static HashSet<string> Parse(string sourceList) =>
            new HashSet<string>(
                (sourceList ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Where(IsSupportedCatalogSource),
                StringComparer.OrdinalIgnoreCase);

        private static string AddSource(string sourceList, string source)
        {
            if (!IsSupportedCatalogSource(source))
            {
                return sourceList;
            }

            return Parse(sourceList).Contains(source)
                ? sourceList
                : string.IsNullOrWhiteSpace(sourceList) ? source : sourceList + "," + source;
        }

        private static string ResolveSourceList(string environmentSources, string savedSources, bool hasSavedSources)
        {
            if (IsEnvironmentOverride(environmentSources))
            {
                return environmentSources;
            }

            if (hasSavedSources)
            {
                return savedSources ?? string.Empty;
            }

            return environmentSources;
        }

        private static string NormalizeSourceList(string sourceList) =>
            string.Join(",", (sourceList ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => !x.IsNullOrWhiteSpace())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal));
    }
}
