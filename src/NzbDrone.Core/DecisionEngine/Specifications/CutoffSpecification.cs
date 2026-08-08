using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class CutoffSpecification : IDecisionEngineSpecification
    {
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly Logger _logger;
        private readonly ICustomFormatCalculationService _formatService;

        public CutoffSpecification(UpgradableSpecification upgradableSpecification,
                                   ICustomFormatCalculationService formatService,
                                   Logger logger)
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
                _logger.Debug("Skipping cutoff rejection for resolved interactive book search");
                return Decision.Accept();
            }

            var candidateQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(subject.Author, subject.ParsedBookInfo.Quality);
            var qualityProfile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            foreach (var file in subject.Books.SelectMany(b => b.BookFiles))
            {
                var currentQualities = new List<QualityModel> { file.Quality };

                _logger.Debug("Comparing file quality with report. Existing files contain {0}", currentQualities.ConcatToString());

                var customFormats = _formatService.ParseCustomFormat(file);

                if (!_upgradableSpecification.CutoffNotMet(qualityProfile,
                                                           currentQualities,
                                                           customFormats,
                                                           candidateQuality))
                {
                    _logger.Debug("Cutoff already met by existing files, rejecting.");

                    var qualityCutoffIndex = qualityProfile.GetIndex(qualityProfile.Cutoff);
                    var qualityCutoff = qualityProfile.Items[qualityCutoffIndex.Index];

                    return Decision.Reject("Existing files meets cutoff: {0}", qualityCutoff);
                }
            }

            return Decision.Accept();
        }
    }
}
