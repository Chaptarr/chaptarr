using System.Linq;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class QualityAllowedByProfileSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public QualityAllowedByProfileSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            _logger.Debug("[QUALITY_PROFILE_CHECK] ===== QUALITY PROFILE CHECK STARTED =====");
            _logger.Debug("[QUALITY_PROFILE_CHECK] Release: '{0}'", subject.Release?.Title ?? "NULL");
            _logger.Debug("[QUALITY_PROFILE_CHECK] Author: '{0}' (ID: {1})", subject.Author?.Name ?? "NULL", subject.Author?.Id ?? -1);
            _logger.Debug("[QUALITY_PROFILE_CHECK] Quality being checked: '{0}' (ID: {1})",
                subject.ParsedBookInfo.Quality.Quality.Name,
                subject.ParsedBookInfo.Quality.Quality.Id);

            _logger.Debug("[QUALITY_PROFILE_CHECK] Author profile IDs:");
            _logger.Debug("  - AudiobookQualityProfileId: {0}", subject.Author?.AudiobookQualityProfileId ?? -999);
            _logger.Debug("  - EbookQualityProfileId: {0}", subject.Author?.EbookQualityProfileId ?? -999);

            _logger.Debug("[QUALITY_PROFILE_CHECK] Author profile objects:");
            _logger.Debug("  - AudiobookQualityProfile: {0}",
                subject.Author?.AudiobookQualityProfileId.HasValue == true ?
                    $"LOADED - '{subject.Author.AudiobookQualityProfile?.Value?.Name ?? "Unknown"}' (ID: {subject.Author.AudiobookQualityProfileId.Value})" :
                    "NULL");
            _logger.Debug("  - EbookQualityProfile: {0}",
                subject.Author?.EbookQualityProfileId.HasValue == true ?
                    $"LOADED - '{subject.Author.EbookQualityProfile?.Value?.Name ?? "Unknown"}' (ID: {subject.Author.EbookQualityProfileId.Value})" :
                    "NULL");

            _logger.Debug("[QUALITY_PROFILE_CHECK] Calling GetQualityProfileForQuality for quality ID {0}...",
                subject.ParsedBookInfo.Quality.Quality.Id);
            var profile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);

            if (profile == null)
            {
                _logger.Debug("[QUALITY_PROFILE_CHECK] GetQualityProfileForQuality returned NULL!");
                _logger.Debug("[QUALITY_PROFILE_CHECK] This means no profile is configured for quality '{0}' (ID: {1})",
                    subject.ParsedBookInfo.Quality.Quality.Name,
                    subject.ParsedBookInfo.Quality.Quality.Id);
                _logger.Debug("[QUALITY_PROFILE_CHECK] Creating SOFT FILTER rejection");

                // Make this a soft filter so users can bypass it and see what's available
                return Decision.RejectSoftFilter("No quality profile configured for {0} files", "Quality", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            _logger.Debug("[QUALITY_PROFILE_CHECK] Found profile: '{0}' (ID: {1})", profile.Name, profile.Id);
            _logger.Debug("[QUALITY_PROFILE_CHECK] Profile Items Count: {0}", profile.Items?.Count ?? 0);

            if (profile.Items != null)
            {
                var allowedQualities = profile.Items
                    .Where(i => i.Allowed)
                    .SelectMany(i => i.GetQualities())
                    .ToList();
                _logger.Debug("[QUALITY_PROFILE_CHECK] Allowed qualities in profile: [{0}]",
                    string.Join(", ", allowedQualities.Select(q => $"{q.Name} (ID: {q.Id})")));
            }

            var qualityIndex = profile.GetIndex(subject.ParsedBookInfo.Quality.Quality);
            var qualityOrGroup = profile.Items[qualityIndex.Index];

            if (!qualityOrGroup.Allowed)
            {
                _logger.Debug("[QUALITY_PROFILE_CHECK] Quality {0} REJECTED - not allowed in profile", subject.ParsedBookInfo.Quality);
                _logger.Debug("[QUALITY_PROFILE_CHECK] QualityOrGroup at index {0}: Allowed={1}, Name={2}", qualityIndex.Index, qualityOrGroup.Allowed, qualityOrGroup.Name ?? qualityOrGroup.Quality?.Name ?? "Unknown");

                // Soft filter: Quality profile restrictions can be bypassed
                return Decision.RejectSoftFilter("{0} is not wanted in profile", "Quality", subject.ParsedBookInfo.Quality.Quality);
            }

            _logger.Debug("[QUALITY_PROFILE_CHECK] Quality {0} ACCEPTED - allowed in profile", subject.ParsedBookInfo.Quality);
            return Decision.Accept();
        }
    }
}
