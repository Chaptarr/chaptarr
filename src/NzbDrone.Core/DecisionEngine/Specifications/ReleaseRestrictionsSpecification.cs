using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class ReleaseRestrictionsSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly IReleaseProfileService _releaseProfileService;
        private readonly ITermMatcherService _termMatcherService;
        private readonly IConfigService _configService;

        // Enhanced title cleaning regex for consistent term matching
        private static readonly Regex CleanTitleCruft = new Regex(@"\((?:unabridged|abridged|audiobook|audio\s*book)\)|\[(?:MP3|M4B|AAC|FLAC|OGG)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ReleaseRestrictionsSpecification(
            ITermMatcherService termMatcherService,
            IReleaseProfileService releaseProfileService,
            IConfigService configService,
            Logger logger)
        {
            _logger = logger;
            _releaseProfileService = releaseProfileService;
            _termMatcherService = termMatcherService;
            _configService = configService;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            _logger.Trace("Checking if release meets restrictions: {0}", subject);

            var title = subject.Release.Title;
            if (_configService?.BookshelfMatchCompatibility == true)
            {
                return IsSatisfiedByBookshelfReleaseProfile(title, subject);
            }

            var mediaType = subject.GetPreferredMediaType();
            var tags = subject.Author.GetTagsForMediaType(mediaType);
            var releaseProfiles = _releaseProfileService.EnabledForTags(tags, subject.Release.IndexerId);

            // Enhanced title processing with smart cleaning for more accurate term matching
            var originalTitle = title;
            var cleanedTitle = CleanTitleCruft.Replace(title, "").Trim();
            var normalizedTitle = NormalizeForTermMatching(cleanedTitle);

            var titleVariants = new[] { originalTitle, cleanedTitle, normalizedTitle }
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            if (_logger.IsTraceEnabled)
            {
                _logger.TraceSafe("Term matching against title variants: {0}", string.Join(" | ", titleVariants));
            }

            var required = releaseProfiles.Where(r => r.Required.Any());
            var ignored = releaseProfiles.Where(r => r.Ignored.Any());

            foreach (var r in required)
            {
                var requiredTerms = r.Required;

                var foundTerms = ContainsAnyEnhanced(requiredTerms, titleVariants);
                if (foundTerms.Empty())
                {
                    var terms = string.Join(", ", requiredTerms);
                    _logger.TraceSafe("[{0}] does not contain one of the required terms: {1}", title, terms);
                    return RejectProfileRestriction("Does not contain one of the required terms: {0}", terms);
                }
                else
                {
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.TraceSafe("[{0}] matched required terms: {1}", title, string.Join(", ", foundTerms));
                    }
                }
            }

            foreach (var r in ignored)
            {
                var ignoredTerms = r.Ignored;

                var foundTerms = ContainsAnyEnhanced(ignoredTerms, titleVariants);
                if (foundTerms.Any())
                {
                    var terms = string.Join(", ", foundTerms);
                    _logger.TraceSafe("[{0}] contains these ignored terms: {1}", title, terms);
                    return RejectProfileRestriction("Contains these ignored terms: {0}", terms);
                }
            }

            _logger.Trace("[{0}] No restrictions apply, allowing", subject);
            return Decision.Accept();
        }

        private Decision IsSatisfiedByBookshelfReleaseProfile(string title, RemoteBook subject)
        {
            // BookshelfNG matched its configured terms against the unmodified release
            // title and the author's single tag set. Keep that contract for migrated
            // installs; normalization and split-format tags can change the result.
            var releaseProfiles = _releaseProfileService.EnabledForTags(subject.Author.Tags, subject.Release.IndexerId);

            foreach (var profile in releaseProfiles.Where(item => item.Required.Any()))
            {
                var foundTerms = ContainsAny(profile.Required, title);
                if (foundTerms.Empty())
                {
                    var terms = string.Join(", ", profile.Required);
                    _logger.TraceSafe("[{0}] does not contain one of the required terms: {1}", title, terms);
                    return Decision.Reject("Does not contain one of the required terms: {0}", terms);
                }
            }

            foreach (var profile in releaseProfiles.Where(item => item.Ignored.Any()))
            {
                var foundTerms = ContainsAny(profile.Ignored, title);
                if (foundTerms.Any())
                {
                    var terms = string.Join(", ", foundTerms);
                    _logger.TraceSafe("[{0}] contains these ignored terms: {1}", title, terms);
                    return Decision.Reject("Contains these ignored terms: {0}", terms);
                }
            }

            return Decision.Accept();
        }

        private List<string> ContainsAny(List<string> terms, string title)
        {
            return terms.Where(t => _termMatcherService.IsMatch(t, title)).ToList();
        }

        /// <summary>
        /// Enhanced term matching against multiple title variants
        /// </summary>
        private List<string> ContainsAnyEnhanced(List<string> terms, List<string> titleVariants)
        {
            var foundTerms = new List<string>();

            foreach (var term in terms)
            {
                foreach (var titleVariant in titleVariants)
                {
                    if (_termMatcherService.IsMatch(term, titleVariant))
                    {
                        foundTerms.Add(term);
                        break; // Found match for this term, move to next term
                    }
                }
            }

            return foundTerms;
        }

        private Decision RejectProfileRestriction(string reason, params object[] args)
        {
            if (_configService?.BookshelfMatchCompatibility == true)
            {
                return Decision.Reject(reason, args);
            }

            return Decision.RejectSoftFilter(reason, "Release Profile", args);
        }

        /// <summary>
        /// Normalize title for more accurate term matching
        /// </summary>
        private string NormalizeForTermMatching(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var normalized = title;

            // Remove common audiobook artifacts that might interfere with term matching
            normalized = Regex.Replace(normalized, @"\b(?:unabridged|abridged|audiobook|audio\s*book|complete)\b", "", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\[(?:mp3|m4b|aac|flac|ogg)\]", "", RegexOptions.IgnoreCase);

            // Standardize spacing for better term matching
            normalized = Regex.Replace(normalized, @"[\._]+", " "); // Convert dots and underscores to spaces
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim(); // Collapse multiple spaces

            return normalized;
        }
    }
}
