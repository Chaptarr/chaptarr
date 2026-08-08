using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Releases;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class ReleaseRestrictionsSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly IReleaseProfileService _releaseProfileService;
        private readonly ITermMatcherService _termMatcherService;

        // Enhanced title cleaning regex for consistent term matching
        private static readonly Regex CleanTitleCruft = new Regex(@"\((?:unabridged|abridged|audiobook|audio\s*book)\)|\[(?:MP3|M4B|AAC|FLAC|OGG)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ReleaseRestrictionsSpecification(ITermMatcherService termMatcherService, IReleaseProfileService releaseProfileService, Logger logger)
        {
            _logger = logger;
            _releaseProfileService = releaseProfileService;
            _termMatcherService = termMatcherService;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            _logger.Debug("Checking if release meets restrictions: {0}", subject);

            var title = subject.Release.Title;
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

            _logger.Debug("Term matching against title variants: {0}", string.Join(" | ", titleVariants));

            var required = releaseProfiles.Where(r => r.Required.Any());
            var ignored = releaseProfiles.Where(r => r.Ignored.Any());

            foreach (var r in required)
            {
                var requiredTerms = r.Required;

                var foundTerms = ContainsAnyEnhanced(requiredTerms, titleVariants);
                if (foundTerms.Empty())
                {
                    var terms = string.Join(", ", requiredTerms);
                    _logger.Debug("[{0}] does not contain one of the required terms: {1}", title, terms);
                    return Decision.RejectSoftFilter("Does not contain one of the required terms: {0}", "Release Profile", terms);
                }
                else
                {
                    var matchedTerms = string.Join(", ", foundTerms);
                    _logger.Debug("[{0}] matched required terms: {1}", title, matchedTerms);
                }
            }

            foreach (var r in ignored)
            {
                var ignoredTerms = r.Ignored;

                var foundTerms = ContainsAnyEnhanced(ignoredTerms, titleVariants);
                if (foundTerms.Any())
                {
                    var terms = string.Join(", ", foundTerms);
                    _logger.Debug("[{0}] contains these ignored terms: {1}", title, terms);
                    return Decision.RejectSoftFilter("Contains these ignored terms: {0}", "Release Profile", terms);
                }
            }

            _logger.Debug("[{0}] No restrictions apply, allowing", subject);
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
