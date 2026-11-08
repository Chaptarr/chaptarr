using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
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
                _logger.Trace("Skipping cutoff rejection for resolved interactive book search");
                return Decision.Accept();
            }

            var qualityProfile = subject.GetQualityProfile();
            var candidateQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(qualityProfile, subject.ParsedBookInfo.Quality);
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            foreach (var book in subject.GetBooksMatchingReleaseMediaType())
            {
                foreach (var recording in RecordingCustomFormatHelper.GroupStoredForScoring(book.BookFiles, book: book))
                {
                    var input = RecordingCustomFormatHelper.CreateStoredInput(recording, author: subject.Author, book: book);
                    var customFormats = _formatService.ParseCustomFormat(input);
                    foreach (var quality in recording.Select(file => file.Quality).Distinct())
                    {
                        var currentQualities = new List<QualityModel> { quality };

                        if (_logger.IsTraceEnabled)
                        {
                            _logger.Trace("Comparing file quality with report. Existing files contain {0}", currentQualities.ConcatToString());
                        }

                        if (!_upgradableSpecification.CutoffNotMet(qualityProfile,
                                                                   currentQualities,
                                                                   customFormats,
                                                                   candidateQuality))
                        {
                            _logger.Trace("Cutoff already met by existing files, rejecting.");

                            var qualityCutoffIndex = qualityProfile.GetIndex(qualityProfile.Cutoff);
                            var qualityCutoff = qualityProfile.Items[qualityCutoffIndex.Index];

                            return Decision.Reject("Existing files meets cutoff: {0}", qualityCutoff);
                        }
                    }
                }
            }

            return Decision.Accept();
        }
    }
}
