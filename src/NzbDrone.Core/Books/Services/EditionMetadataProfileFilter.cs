using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
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
        List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile, IReadOnlySet<Edition> protectedEditions);
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
            return Apply(editions, metadataProfile, (IReadOnlySet<Edition>)null);
        }

        public List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile, IReadOnlySet<string> protectedForeignEditionIds)
        {
            var remoteEditions = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null).ToList();
            var protectedEditions = ResolveProtectedEditions(remoteEditions, null, NormalizeProtectionIds(protectedForeignEditionIds));
            return Apply(remoteEditions, metadataProfile, protectedEditions);
        }

        public List<Edition> Apply(IEnumerable<Edition> editions, MetadataProfile metadataProfile, IReadOnlySet<Edition> protectedEditions)
        {
            var filtered = (editions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .ToList();

            if (metadataProfile == null)
            {
                return filtered;
            }

            ParseAllowedLanguages(
                metadataProfile.AllowedLanguages,
                out var allowedLanguages,
                out var allowUnknownLanguage,
                out var languageFilterConfigured,
                out _);

            filtered = filtered.Where(e =>
                    IsProtectedEdition(e, protectedEditions) ||
                    MeetsIdentifierRequirements(e, metadataProfile))
                .ToList();

            filtered = filtered.Where(e =>
                    IsProtectedEdition(e, protectedEditions) ||
                    IsAllowedLanguage(e, allowedLanguages, allowUnknownLanguage, languageFilterConfigured))
                .ToList();

            if (metadataProfile.Ignored != null && metadataProfile.Ignored.Any())
            {
                var ignoredTerms = ExpandIgnoredTerms(metadataProfile.Ignored);
                filtered = filtered.Where(e =>
                    IsProtectedEdition(e, protectedEditions) ||
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

            return (!metadataProfile.SkipMissingIsbn || hasIsbn || hasAsin) &&
                (!metadataProfile.SkipMissingAsin || hasAsin);
        }

        public static string NormalizeLanguageBucket(string language)
        {
            return language?.CanonicalizeLanguage()?.Trim();
        }

        public static IReadOnlySet<string> NormalizeProtectionIds(IReadOnlySet<string> ids)
        {
            return ids?.Where(id => id.IsNotNullOrWhiteSpace()).Select(id => id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static List<string> GetProtectionTokens(Edition edition)
        {
            var tokens = BookEditionIdentity.GetEditionRehomeTokens(edition);
            if (tokens.Count == 0 && edition?.ForeignEditionId.IsNotNullOrWhiteSpace() == true)
            {
                tokens.Add(edition.ForeignEditionId.Trim());
            }

            return tokens;
        }

        public static HashSet<Edition> ResolveProtectedEditions(IEnumerable<Edition> remoteEditions, IEnumerable<Edition> localProtectedEditions, IReadOnlySet<string> requestedEditionIds = null, Logger logger = null)
        {
            var identities = (localProtectedEditions ?? Enumerable.Empty<Edition>()).Where(edition => edition != null)
                .Select(edition => (Edition: edition, Tokens: GetProtectionTokens(edition)))
                .Where(identity => identity.Tokens.Count > 0)
                .ToList();
            var protectedEditions = new HashSet<Edition>(ReferenceEqualityComparer.Instance);
            if (identities.Count == 0 && requestedEditionIds?.Count is not > 0)
            {
                return protectedEditions;
            }

            var aliases = new Dictionary<string, HashSet<Edition>>(StringComparer.OrdinalIgnoreCase);
            var foreignIds = new Dictionary<string, HashSet<Edition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var edition in remoteEditions ?? Enumerable.Empty<Edition>())
            {
                if (edition == null)
                {
                    continue;
                }

                foreach (var token in BookEditionIdentity.GetRemoteEditionRehomeTokens(edition))
                {
                    AddIndexValue(aliases, token, edition);
                }

                if (edition.ForeignEditionId.IsNotNullOrWhiteSpace())
                {
                    AddIndexValue(foreignIds, edition.ForeignEditionId.Trim(), edition);
                }
            }

            foreach (var identity in identities)
            {
                if (identity.Edition.ForeignEditionId.IsNotNullOrWhiteSpace() && foreignIds.TryGetValue(identity.Edition.ForeignEditionId.Trim(), out var exact))
                {
                    protectedEditions.UnionWith(exact);
                    continue;
                }

                ProtectMatches(ResolveAliases(identity.Tokens), identity.Tokens);
            }

            if (requestedEditionIds?.Count > 0)
            {
                foreach (var entry in foreignIds.Where(entry => requestedEditionIds.Contains(entry.Key)))
                {
                    protectedEditions.UnionWith(entry.Value);
                }

                foreach (var entry in aliases.Where(entry => requestedEditionIds.Contains(entry.Key) && !foreignIds.ContainsKey(entry.Key)))
                {
                    ProtectMatches(entry.Value, new[] { entry.Key });
                }
            }

            return protectedEditions;

            static void AddIndexValue(Dictionary<string, HashSet<Edition>> index, string token, Edition edition)
            {
                if (!index.TryGetValue(token, out var matches))
                {
                    matches = new HashSet<Edition>(ReferenceEqualityComparer.Instance);
                    index[token] = matches;
                }

                matches.Add(edition);
            }

            HashSet<Edition> ResolveAliases(IEnumerable<string> tokens)
            {
                var matches = new HashSet<Edition>(ReferenceEqualityComparer.Instance);
                foreach (var token in tokens)
                {
                    if (aliases.TryGetValue(token, out var editions))
                    {
                        matches.UnionWith(editions);
                    }
                }

                return matches;
            }

            void ProtectMatches(HashSet<Edition> matches, IEnumerable<string> identityTokens)
            {
                if (matches.Count == 1)
                {
                    protectedEditions.UnionWith(matches);
                }
                else if (matches.Count > 1)
                {
                    var first = matches.First();
                    var sameForeignId = first.ForeignEditionId.IsNotNullOrWhiteSpace() && matches.All(edition =>
                        string.Equals(edition.ForeignEditionId?.Trim(), first.ForeignEditionId.Trim(), StringComparison.OrdinalIgnoreCase));
                    var tokens = BookEditionIdentity.GetEditionRehomeTokens(first).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var sameCanonicalIds = matches.All(edition => edition.ForeignEditionId.IsNullOrWhiteSpace()) && tokens.Count > 0 &&
                        matches.All(edition => tokens.SetEquals(BookEditionIdentity.GetEditionRehomeTokens(edition)));

                    // Known identity duplicates retain their protection for the existing refresh coalescing path.
                    if (sameForeignId || sameCanonicalIds)
                    {
                        protectedEditions.UnionWith(matches);
                    }
                    else
                    {
                        logger?.Warn("Skipping metadata-profile owned-edition protection: edition tokens [{0}] point to multiple remote editions [{1}] in the current work and media side.",
                            string.Join(", ", identityTokens),
                            string.Join(", ", matches.Select(edition => edition.ForeignEditionId.IsNotNullOrWhiteSpace() ? edition.ForeignEditionId : string.Join("|", GetProtectionTokens(edition))).OrderBy(id => id, StringComparer.OrdinalIgnoreCase)));
                    }
                }
            }
        }

        public static bool IsProtectedEdition(Edition edition, IReadOnlySet<Edition> protectedEditions)
        {
            return edition != null && protectedEditions?.Contains(edition) == true;
        }
    }
}
