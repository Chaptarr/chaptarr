using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Releases;

namespace NzbDrone.Core.Books.Services
{
    public interface IEditionMetadataProfileFilter
    {
        List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile);
        List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile, IReadOnlySet<string> protectedForeignEditionIds);
    }

    public class EditionMetadataProfileFilter : IEditionMetadataProfileFilter
    {
        private readonly ITermMatcherService _termMatcherService;

        public EditionMetadataProfileFilter(ITermMatcherService termMatcherService)
        {
            _termMatcherService = termMatcherService;
        }

        public List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile)
        {
            return Apply(editions, metadataProfile, null);
        }

        public List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile, IReadOnlySet<string> protectedForeignEditionIds)
        {
            var filtered = (editions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .ToList();

            if (metadataProfile == null)
            {
                return filtered;
            }

            var protectedIds = protectedForeignEditionIds?
                .Where(id => id.IsNotNullOrWhiteSpace())
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ParseAllowedLanguages(
                metadataProfile.AllowedLanguages,
                out var allowedLanguages,
                out var allowUnknownLanguage,
                out var languageFilterConfigured,
                out _);

            filtered = filtered.Where(e =>
                    IsProtectedEdition(e, protectedIds) ||
                    MeetsIdentifierRequirements(e, metadataProfile))
                .ToList();

            filtered = filtered.Where(e =>
                    IsProtectedEdition(e, protectedIds) ||
                    IsAllowedLanguage(e, allowedLanguages, allowUnknownLanguage, languageFilterConfigured))
                .ToList();

            if (metadataProfile.Ignored != null && metadataProfile.Ignored.Any())
            {
                var ignoredTerms = ExpandIgnoredTerms(metadataProfile.Ignored);
                filtered = filtered.Where(e =>
                    IsProtectedEdition(e, protectedIds) ||
                    !FindMatchingIgnoredTerms(ignoredTerms, e.Title, _termMatcherService.IsMatch).Any()).ToList();
            }

            return filtered;
        }

        public static List<string> ExpandIgnoredTerms(IEnumerable<string> ignoredTerms)
        {
            return (ignoredTerms ?? Enumerable.Empty<string>())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .SelectMany(t => t.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> FindMatchingIgnoredTerms(IEnumerable<string> ignoredTerms, string value, Func<string, string, bool> isMatch)
        {
            if (value.IsNullOrWhiteSpace() || isMatch == null)
            {
                return new List<string>();
            }

            var expandedTerms = ignoredTerms?.Where(t => t.IsNotNullOrWhiteSpace()).ToList() ?? new List<string>();
            if (!expandedTerms.Any())
            {
                return new List<string>();
            }

            if (expandedTerms.Count > 50)
            {
                return expandedTerms.AsParallel()
                    .Where(t => isMatch(t, value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return expandedTerms
                .Where(t => isMatch(t, value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void ParseAllowedLanguages(
            string allowedLanguagesRaw,
            out HashSet<string> allowedLanguages,
            out bool allowUnknownLanguage,
            out bool configured,
            out List<string> unknownTokens)
        {
            allowedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allowUnknownLanguage = false;
            configured = false;
            unknownTokens = new List<string>();

            if (allowedLanguagesRaw.IsNullOrWhiteSpace())
            {
                return;
            }

            foreach (var raw in allowedLanguagesRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (token.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (token.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    allowUnknownLanguage = true;
                    continue;
                }

                var canonical = token.CanonicalizeLanguage();
                if (canonical.IsNullOrWhiteSpace())
                {
                    unknownTokens.Add(token);
                    continue;
                }

                allowedLanguages.Add(canonical);
            }

            configured = allowedLanguages.Any() || allowUnknownLanguage;
        }

        public static bool IsAllowedLanguage(
            Edition edition,
            IReadOnlySet<string> allowedLanguages,
            bool allowUnknownLanguage,
            bool languageFilterConfigured)
        {
            if (!languageFilterConfigured)
            {
                return true;
            }

            var language = NormalizeLanguageBucket(edition?.Language);
            return language == null ? allowUnknownLanguage : allowedLanguages.Contains(language);
        }

        public static bool MeetsIdentifierRequirements(Edition edition, MetadataProfile metadataProfile)
        {
            if (edition == null || metadataProfile == null)
            {
                return true;
            }

            var hasIsbn = edition.Isbn13.IsNotNullOrWhiteSpace() || edition.Isbn10.IsNotNullOrWhiteSpace();
            var hasAsin = edition.Asin.IsNotNullOrWhiteSpace();

            if (!metadataProfile.SkipMissingIsbn && !metadataProfile.SkipMissingAsin)
            {
                return true;
            }

            if (metadataProfile.SkipMissingIsbn && !metadataProfile.SkipMissingAsin)
            {
                return hasIsbn || hasAsin;
            }

            if (!metadataProfile.SkipMissingIsbn && metadataProfile.SkipMissingAsin)
            {
                return hasAsin;
            }

            return hasIsbn && hasAsin;
        }

        public static string NormalizeLanguageBucket(string language)
        {
            return language?.CanonicalizeLanguage()?.Trim();
        }

        private static bool IsProtectedEdition(Edition edition, IReadOnlySet<string> protectedForeignEditionIds)
        {
            if (edition?.ForeignEditionId.IsNullOrWhiteSpace() != false || protectedForeignEditionIds == null || protectedForeignEditionIds.Count == 0)
            {
                return false;
            }

            return protectedForeignEditionIds.Contains(edition.ForeignEditionId.Trim());
        }
    }
}
