using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class HistorySpecification : IDecisionEngineSpecification
    {
        private readonly IHistoryService _historyService;
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public HistorySpecification(IHistoryService historyService,
                                    UpgradableSpecification upgradableSpecification,
                                    ICustomFormatCalculationService formatService,
                                    IConfigService configService,
                                    Logger logger)
        {
            _historyService = historyService;
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Database;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (searchCriteria != null)
            {
                _logger.Trace("Skipping history check during search");
                return Decision.Accept();
            }

            var cdhEnabled = _configService.EnableCompletedDownloadHandling;

            _logger.Trace("Performing history status check on report");
            foreach (var book in subject.Books)
            {
                _logger.Trace("Checking current status of book [{0}] in history", book.Id);
                var mostRecent = _historyService.MostRecentForBook(book.Id);

                if (mostRecent != null && mostRecent.EventType == EntityHistoryEventType.Grabbed)
                {
                    var recent = mostRecent.Date.After(DateTime.UtcNow.AddHours(-12));

                    if (!recent && cdhEnabled)
                    {
                        continue;
                    }

                    mostRecent.Book ??= book;
                    var customFormats = _formatService.ParseCustomFormat(mostRecent, subject.Author);

                    var qualityProfile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);
                    if (qualityProfile == null)
                    {
                        return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
                    }

                    var currentEffectiveQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(subject.Author, mostRecent.Quality);
                    var candidateEffectiveQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(subject.Author, subject.ParsedBookInfo.Quality);

                    // The series will be the same as the one in history since it's the same episode.
                    // Instead of fetching the series from the DB reuse the known series.
                    var cutoffUnmet = _upgradableSpecification.CutoffNotMet(
                        qualityProfile,
                        new List<QualityModel> { currentEffectiveQuality },
                        customFormats,
                        candidateEffectiveQuality);

                    var upgradeable = _upgradableSpecification.IsReleaseUpgradable(
                        qualityProfile,
                        mostRecent.Quality,
                        customFormats,
                        subject.ParsedBookInfo.Quality,
                        subject.CustomFormats);

                    if (!cutoffUnmet)
                    {
                        if (recent)
                        {
                            return Decision.Reject("Recent grab event in history already meets cutoff: {0}", currentEffectiveQuality);
                        }

                        return Decision.Reject("CDH is disabled and grab event in history already meets cutoff: {0}", currentEffectiveQuality);
                    }

                    if (!upgradeable)
                    {
                        if (recent)
                        {
                            return Decision.Reject("Recent grab event in history is of equal or higher preference: {0}", mostRecent.Quality);
                        }

                        return Decision.Reject("CDH is disabled and grab event in history is of equal or higher preference: {0}", mostRecent.Quality);
                    }
                }
            }

            return Decision.Accept();
        }
    }
}
