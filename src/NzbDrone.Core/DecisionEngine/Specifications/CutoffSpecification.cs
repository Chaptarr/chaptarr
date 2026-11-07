using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class CutoffSpecification : IDecisionEngineSpecification
    {
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly Logger _logger;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IConfigService _configService;

        public CutoffSpecification(UpgradableSpecification upgradableSpecification,
                                   ICustomFormatCalculationService formatService,
                                   IConfigService configService,
                                   Logger logger)
        {
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (_configService?.BookshelfMatchCompatibility != true &&
                InteractiveBookSearchSpecificationHelper.IsResolvedInteractiveBookSearch(subject, searchCriteria))
            {
                _logger.Trace("Skipping cutoff rejection for resolved interactive book search");
                return Decision.Accept();
            }

            if (_configService?.BookshelfMatchCompatibility == true)
            {
                return IsBookshelfCutoffNotMet(subject);
            }

            var candidateQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(subject.Author, subject.ParsedBookInfo.Quality);
            var qualityProfile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            foreach (var file in SplitPartFileFilter.ComparableFiles(subject, subject.Books.SelectMany(b => b.BookFiles)))
            {
                var currentQualities = new List<QualityModel> { file.Quality };

                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("Comparing file quality with report. Existing files contain {0}", currentQualities.ConcatToString());
                }

                var customFormats = _formatService.ParseCustomFormat(file);

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

            return Decision.Accept();
        }

        private Decision IsBookshelfCutoffNotMet(RemoteBook subject)
        {
            var candidateQuality = subject.ParsedBookInfo.Quality;
            var qualityProfile = subject.Author.GetQualityProfileForQuality(candidateQuality?.Quality);
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", candidateQuality?.Quality?.Name ?? "unknown");
            }

            foreach (var file in subject.Books.SelectMany(book => book.BookFiles ?? new List<BookFile>()))
            {
                if (file?.Quality == null ||
                    !MediaFileExtensions.AreCompatibleQualities(file.Quality.Quality, candidateQuality?.Quality))
                {
                    continue;
                }

                var customFormats = _formatService.ParseCustomFormat(file);
                if (!_upgradableSpecification.CutoffNotMet(
                        qualityProfile,
                        new List<QualityModel> { file.Quality },
                        customFormats,
                        candidateQuality))
                {
                    var cutoffIndex = qualityProfile.GetIndex(qualityProfile.Cutoff);
                    var cutoff = qualityProfile.Items[cutoffIndex.Index];
                    return Decision.Reject("Existing files meets cutoff: {0}", cutoff);
                }
            }

            return Decision.Accept();
        }
    }
}
