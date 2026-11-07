using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class UpgradeAllowedSpecification : IDecisionEngineSpecification
    {
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly Logger _logger;
        private readonly IConfigService _configService;

        public UpgradeAllowedSpecification(UpgradableSpecification upgradableSpecification,
                                           Logger logger,
                                           ICustomFormatCalculationService formatService,
                                           IConfigService configService)
        {
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _logger = logger;
            _configService = configService;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (_configService?.BookshelfMatchCompatibility != true &&
                InteractiveBookSearchSpecificationHelper.IsResolvedInteractiveBookSearch(subject, searchCriteria))
            {
                _logger.Trace("Skipping upgrade-allowed rejection for resolved interactive book search");
                return Decision.Accept();
            }

            var qualityProfile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            if (_configService?.BookshelfMatchCompatibility == true)
            {
                foreach (var file in subject.Books.SelectMany(book => book.BookFiles))
                {
                    if (file?.Quality == null ||
                        !MediaFileExtensions.AreCompatibleQualities(file.Quality.Quality, subject.ParsedBookInfo.Quality.Quality))
                    {
                        continue;
                    }

                    var customFormats = _formatService.ParseCustomFormat(file, subject.Author);
                    if (!_upgradableSpecification.IsUpgradeAllowed(
                            qualityProfile,
                            file.Quality,
                            customFormats,
                            subject.ParsedBookInfo.Quality,
                            subject.CustomFormats))
                    {
                        return Decision.Reject("Existing files and the Quality profile does not allow upgrades");
                    }
                }

                return Decision.Accept();
            }

            foreach (var file in SplitPartFileFilter.ComparableFiles(subject, subject.Books.SelectMany(b => b.BookFiles)))
            {
                if (file == null)
                {
                    _logger.Trace("File is no longer available, skipping this file.");
                    continue;
                }

                var fileCustomFormats = _formatService.ParseCustomFormat(file, subject.Author);
                _logger.Trace("Comparing file quality with report. Existing files contain {0}", file.Quality);

                if (!_upgradableSpecification.IsUpgradeAllowed(qualityProfile,
                                                               file.Quality,
                                                               fileCustomFormats,
                                                               subject.ParsedBookInfo.Quality,
                                                               subject.CustomFormats))
                {
                    _logger.Trace("Upgrading is not allowed by the quality profile");

                    return Decision.Reject("Existing files and the Quality profile does not allow upgrades");
                }
            }

            return Decision.Accept();
        }
    }
}
