using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace NzbDrone.Core.Configuration
{
    internal static class LegacyConfigurationCompatibility
    {
        private static readonly string[] LegacySectionNames = { "Bookshelf", "Audioarr", "Readarr" };

        public static IConfiguration AddFallbackSections(IConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var fallbackValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var legacySection in LegacySectionNames)
            {
                AddSectionFallback(configuration, fallbackValues, legacySection);
            }

            AddRootFallback(configuration, fallbackValues, "BOOKSHELF_PORT", "Chaptarr:Server:Port");
            AddRootFallback(configuration, fallbackValues, "BOOKSHELF_URLBASE", "Chaptarr:Server:UrlBase");

            var missingFallbackValues = fallbackValues
                .Where(item => configuration[item.Key] == null)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

            // Chained providers can surface an explicit empty value as null in the derived
            // configuration. Re-apply native empty values so clearing a secret still wins.
            var explicitEmptyValues = configuration.AsEnumerable()
                .Where(item => item.Value == string.Empty && item.Key.StartsWith("Chaptarr:", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var item in explicitEmptyValues)
            {
                missingFallbackValues[item.Key] = item.Value;
            }

            return new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection(missingFallbackValues)
                .Build();
        }

        private static void AddSectionFallback(
            IConfiguration configuration,
            IDictionary<string, string> fallbackValues,
            string legacySection)
        {
            var legacyPrefix = legacySection + ":";
            foreach (var setting in configuration.AsEnumerable())
            {
                // Deployment templates may declare optional legacy values as
                // empty placeholders. Treat those as unset so a later empty
                // alias cannot mask a configured value from another section
                // or the persisted config file. Native Chaptarr empty values
                // are reapplied below and still support explicit clearing.
                if (string.IsNullOrEmpty(setting.Value) ||
                    !setting.Key.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var chaptarrKey = "Chaptarr:" + setting.Key.Substring(legacyPrefix.Length);
                if (configuration[chaptarrKey] == null)
                {
                    // Later legacy sections take precedence when an installation retained
                    // multiple application-prefixed environment values.
                    fallbackValues[chaptarrKey] = setting.Value;
                }
            }
        }

        private static void AddRootFallback(
            IConfiguration configuration,
            IDictionary<string, string> fallbackValues,
            string legacyKey,
            string chaptarrKey)
        {
            // Empty root aliases are placeholders too; they must not shadow
            // the persisted server settings.
            if (configuration[chaptarrKey] == null &&
                !fallbackValues.ContainsKey(chaptarrKey) &&
                !string.IsNullOrEmpty(configuration[legacyKey]))
            {
                fallbackValues[chaptarrKey] = configuration[legacyKey];
            }
        }
    }
}
