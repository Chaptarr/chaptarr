using System.Linq;
using NLog;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class UpgradeAllowedSpecification : IDecisionEngineSpecification
    {
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly Logger _logger;

        public UpgradeAllowedSpecification(UpgradableSpecification upgradableSpecification,
                                           Logger logger,
                                           ICustomFormatCalculationService formatService)
        {
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (InteractiveBookSearchSpecificationHelper.IsResolvedInteractiveBookSearch(subject, searchCriteria))
            {
                _logger.Trace("Skipping upgrade-allowed rejection for resolved interactive book search");
                return Decision.Accept();
            }

            var qualityProfile = subject.GetQualityProfile();
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            foreach (var book in subject.GetBooksMatchingReleaseMediaType())
            {
                foreach (var recording in RecordingCustomFormatHelper.GroupStoredForScoring(book.BookFiles, book: book))
                {
                    var input = RecordingCustomFormatHelper.CreateStoredInput(recording, author: subject.Author, book: book);
                    var fileCustomFormats = _formatService.ParseCustomFormat(input, subject.Author);
                    foreach (var quality in recording.Select(file => file.Quality).Distinct())
                    {
                        _logger.Trace("Comparing file quality with report. Existing files contain {0}", quality);

                        if (!_upgradableSpecification.IsUpgradeAllowed(qualityProfile,
                                                                       quality,
                                                                       fileCustomFormats,
                                                                       subject.ParsedBookInfo.Quality,
                                                                       subject.CustomFormats))
                        {
                            _logger.Trace("Upgrading is not allowed by the quality profile");

                            return Decision.Reject("Existing files and the Quality profile does not allow upgrades");
                        }
                    }
                }
            }

            return Decision.Accept();
        }
    }
}
