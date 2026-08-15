using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class UpgradeDiskSpecification : IDecisionEngineSpecification
    {
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly Logger _logger;

        public UpgradeDiskSpecification(UpgradableSpecification qualityUpgradableSpecification,
                                        ICacheManager cacheManager,
                                        ICustomFormatCalculationService formatService,
                                        Logger logger)
        {
            _upgradableSpecification = qualityUpgradableSpecification;
            _formatService = formatService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (InteractiveBookSearchSpecificationHelper.IsResolvedInteractiveBookSearch(subject, searchCriteria))
            {
                _logger.Trace("Skipping on-disk upgrade rejection for resolved interactive book search");
                return Decision.Accept();
            }

            // Handle null subject
            if (subject == null)
            {
                return Decision.Accept();
            }

            if (subject.Books == null || !subject.Books.Any())
            {
                return Decision.Accept();
            }

            // Check if any books have files
            var hasFiles = subject.Books.Any(b => b.BookFiles != null && b.BookFiles.Any());
            if (!hasFiles)
            {
                return Decision.Accept();
            }

            foreach (var file in subject.Books.Where(b => b.BookFiles != null).SelectMany(c => c.BookFiles))
            {
                if (file == null)
                {
                    return Decision.Accept();
                }

                var customFormats = _formatService.ParseCustomFormat(file);

                var qualityProfile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);
                if (qualityProfile == null)
                {
                    return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
                }

                if (!_upgradableSpecification.IsUpgradable(qualityProfile,
                                                           file.Quality,
                                                           customFormats,
                                                           subject.ParsedBookInfo.Quality,
                                                           subject.CustomFormats))
                {
                    return Decision.Reject("Existing files on disk is of equal or higher preference: {0}", file.Quality.Quality.Name);
                }
            }

            return Decision.Accept();
        }
    }
}
