using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class ReleaseLanguageSpecification : IDecisionEngineSpecification
    {
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

        // Full names and common 3-letter markers are safe to treat as explicit language signals.
        // Short 2-letter markers are only used near the end of the title to reduce false positives.
        private static readonly Dictionary<string, string> TitleLanguageTokens = BuildTitleLanguageTokens();
        private static readonly Dictionary<string, string> TitleLanguageTailTokens = BuildTitleLanguageTailTokens();

        private readonly IMetadataProfileService _metadataProfileService;
        private readonly Logger _logger;

        public ReleaseLanguageSpecification(IMetadataProfileService metadataProfileService, Logger logger)
        {
            _metadataProfileService = metadataProfileService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var release = subject?.Release;
            var author = subject?.Author;
            var quality = subject?.ParsedBookInfo?.Quality?.Quality;

            if (release == null || author == null || quality == null)
            {
                return Decision.Accept();
            }

            var mediaType = ResolveMediaType(subject, searchCriteria);
            if (!mediaType.HasValue)
            {
                _logger.Trace("[LANGUAGE_FILTER] Unable to determine media type for '{0}', skipping language filter", release.Title);
                return Decision.Accept();
            }

            var profile = GetMetadataProfile(author, mediaType.Value);
            if (profile == null)
            {
                return Decision.Accept();
            }

            EditionMetadataProfileFilter.ParseAllowedLanguages(
                profile.AllowedLanguages,
                out var allowedLanguages,
                out var allowUnknownLanguage,
                out var languageFilterConfigured,
                out _);

            if (!languageFilterConfigured)
            {
                return Decision.Accept();
            }

            var detectedLanguages = GetDetectedReleaseLanguages(subject);
            if (detectedLanguages.Count == 0)
            {
                _logger.Trace("[LANGUAGE_FILTER] No explicit language detected for '{0}', allowing", release.Title);
                return Decision.Accept();
            }

            if (detectedLanguages.Any(allowedLanguages.Contains))
            {
                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("[LANGUAGE_FILTER] Release '{0}' allowed by profile '{1}' with detected languages [{2}]",
                        release.Title,
                        profile.Name,
                        string.Join(", ", detectedLanguages));
                }

                return Decision.Accept();
            }

            var detectedLanguageSummary = string.Join(", ", detectedLanguages);
            if (allowUnknownLanguage)
            {
                _logger.Trace("[LANGUAGE_FILTER] Release '{0}' languages [{1}] are not allowed, but profile '{2}' permits unknowns only. Rejecting known foreign language.",
                    release.Title,
                    detectedLanguageSummary,
                    profile.Name);
            }

            return Decision.RejectSoftFilter(
                "Release language [{0}] is not allowed by metadata profile '{1}'",
                "Language",
                detectedLanguageSummary,
                profile.Name);
        }

        private MetadataProfile GetMetadataProfile(Author author, BookMediaType mediaType)
        {
            MetadataProfile loaded = mediaType == BookMediaType.Ebook
                ? author.EbookMetadataProfile?.Value
                : author.AudiobookMetadataProfile?.Value;

            if (loaded != null)
            {
                return loaded;
            }

            var profileId = mediaType == BookMediaType.Ebook
                ? author.EbookMetadataProfileId
                : author.AudiobookMetadataProfileId;

            if (!profileId.HasValue || profileId.Value <= 0 || !_metadataProfileService.Exists(profileId.Value))
            {
                return null;
            }

            return _metadataProfileService.Get(profileId.Value);
        }

        private static BookMediaType? ResolveMediaType(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var quality = subject?.ParsedBookInfo?.Quality?.Quality;
            var release = subject?.Release;

            var detected = QualityMediaTypeHelper.DetectMediaType(quality, release);
            if (detected.HasValue)
            {
                return detected;
            }

            if (!HasResolvedIdentity(subject))
            {
                return null;
            }

            return GetSingleMediaType(subject?.Books) ?? GetSingleMediaType(searchCriteria?.Books);
        }

        private static bool HasResolvedIdentity(RemoteBook subject)
        {
            if (subject?.SearchCriteriaMatch?.IsMatch == true)
            {
                return true;
            }

            var releaseTitle = subject?.Release?.Title?.Trim();
            var parsedBookTitle = subject?.ParsedBookInfo?.BookTitle?.Trim();
            var parsedAuthor = subject?.ParsedBookInfo?.AuthorName?.Trim();

            if (string.IsNullOrWhiteSpace(parsedBookTitle) || string.IsNullOrWhiteSpace(parsedAuthor))
            {
                return false;
            }

            return !string.Equals(parsedBookTitle, releaseTitle, StringComparison.OrdinalIgnoreCase);
        }

        private static BookMediaType? GetSingleMediaType(IEnumerable<Book> books)
        {
            var mediaTypes = (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .Select(book => book.MediaType)
                .Distinct()
                .Take(2)
                .ToList();

            return mediaTypes.Count == 1 ? mediaTypes[0] : null;
        }

        private static HashSet<string> GetDetectedReleaseLanguages(RemoteBook subject)
        {
            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var release = subject?.Release;
            if (release == null)
            {
                return detected;
            }

            foreach (var language in release.Languages ?? Enumerable.Empty<Language>())
            {
                var code = IsoLanguages.Get(language)?.ThreeLetterCode?.CanonicalizeLanguage();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    detected.Add(code);
                }
            }

            if (detected.Count > 0)
            {
                return detected;
            }

            foreach (var code in DetectLanguagesFromTitle(subject))
            {
                detected.Add(code);
            }

            return detected;
        }

        private static IEnumerable<string> DetectLanguagesFromTitle(RemoteBook subject)
        {
            var title = subject?.Release?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            var tokens = TokenRegex.Matches(Parser.Parser.CleanReleaseTitleForParsing(title))
                .Select(m => m.Value?.Trim().ToLowerInvariant())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (tokens.Count == 0)
            {
                yield break;
            }

            var protectedTokens = new bool[tokens.Count];
            foreach (var variant in GetProtectedVariants(subject))
            {
                ProtectMatchingTokenSpans(tokens, protectedTokens, variant);
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                if (protectedTokens[i])
                {
                    continue;
                }

                var token = tokens[i];

                if (TitleLanguageTokens.TryGetValue(token, out var code))
                {
                    yield return code;
                    continue;
                }

                if (i >= tokens.Count - 3 && TitleLanguageTailTokens.TryGetValue(token, out code))
                {
                    yield return code;
                }
            }
        }

        private static IEnumerable<string> GetProtectedVariants(RemoteBook subject)
        {
            var protectedVariants = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var releaseTitle = subject?.Release?.Title;

            void Add(string value)
            {
                var trimmed = value?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) &&
                    !string.Equals(trimmed, releaseTitle, StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(trimmed))
                {
                    protectedVariants.Add(trimmed);
                }
            }

            Add(subject?.SearchCriteriaMatch?.MatchedVariant);
            Add(subject?.SearchCriteriaMatch?.PrimaryTitle);
            Add(subject?.Release?.Book);
            Add(subject?.ParsedBookInfo?.BookTitle);
            Add(subject?.ParsedBookInfo?.AuthorName);
            Add(subject?.Release?.Author);
            Add(subject?.Author?.Name);

            foreach (var book in subject?.Books ?? Enumerable.Empty<Book>())
            {
                Add(book?.Title);
                Add(book?.OriginalTitle);
                Add(book?.Subtitle);

                foreach (var edition in book?.Editions ?? Enumerable.Empty<Edition>())
                {
                    Add(edition?.Title);
                    Add(edition?.Subtitle);
                }
            }

            return protectedVariants
                .OrderByDescending(GetTokenCount)
                .ThenByDescending(v => v.Length);
        }

        private static void ProtectMatchingTokenSpans(IReadOnlyList<string> releaseTokens, bool[] protectedTokens, string variant)
        {
            var variantTokens = TokenRegex.Matches(variant ?? string.Empty)
                .Select(m => m.Value?.Trim().ToLowerInvariant())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (variantTokens.Count == 0 || variantTokens.Count > releaseTokens.Count)
            {
                return;
            }

            for (var start = 0; start <= releaseTokens.Count - variantTokens.Count; start++)
            {
                if (!MatchesSpan(releaseTokens, start, variantTokens))
                {
                    continue;
                }

                for (var offset = 0; offset < variantTokens.Count; offset++)
                {
                    protectedTokens[start + offset] = true;
                }
            }
        }

        private static bool MatchesSpan(IReadOnlyList<string> releaseTokens, int start, IReadOnlyList<string> variantTokens)
        {
            for (var i = 0; i < variantTokens.Count; i++)
            {
                if (!string.Equals(releaseTokens[start + i], variantTokens[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static int GetTokenCount(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? 0 : TokenRegex.Matches(value).Count;
        }

        private static Dictionary<string, string> BuildTitleLanguageTokens()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var language in Language.All)
            {
                var iso = IsoLanguages.Get(language);
                var code = iso?.ThreeLetterCode?.CanonicalizeLanguage();
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                map[language.Name.ToLowerInvariant()] = code;
                map[code] = code;

                if (!string.IsNullOrWhiteSpace(iso.EnglishName))
                {
                    map[iso.EnglishName.ToLowerInvariant()] = code;
                }
            }

            // Common scene/indexer tokens and alternate names.
            map["cze"] = "ces";
            map["cz"] = "ces";
            map["ger"] = "deu";
            map["dut"] = "nld";
            map["gre"] = "ell";
            map["rum"] = "ron";
            map["swe"] = "swe";
            map["swedish"] = "swe";
            map["svenska"] = "swe";
            map["italian"] = "ita";
            map["italiano"] = "ita";
            map["dutch"] = "nld";
            map["nederlands"] = "nld";
            map["german"] = "deu";
            map["deutsch"] = "deu";
            map["czech"] = "ces";
            map["cesky"] = "ces";
            map["česky"] = "ces";
            map["spanish"] = "spa";
            map["espanol"] = "spa";
            map["español"] = "spa";
            map["castellano"] = "spa";
            map["french"] = "fra";
            map["francais"] = "fra";
            map["français"] = "fra";
            map["portuguese"] = "por";
            map["portugues"] = "por";
            map["português"] = "por";
            map["brazilian"] = "por";

            return map;
        }

        private static Dictionary<string, string> BuildTitleLanguageTailTokens()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = "eng",
                ["es"] = "spa",
                ["fr"] = "fra",
                ["de"] = "deu",
                ["it"] = "ita",
                ["nl"] = "nld",
                ["sv"] = "swe",
                ["cs"] = "ces",
                ["pt"] = "por"
            };
        }
    }
}
