using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class DelaySpecification : IDecisionEngineSpecification
    {
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly IDelayProfileService _delayProfileService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public DelaySpecification(IPendingReleaseService pendingReleaseService,
                                  IUpgradableSpecification qualityUpgradableSpecification,
                                  IDelayProfileService delayProfileService,
                                  IMediaFileService mediaFileService,
                                  Logger logger)
        {
            _pendingReleaseService = pendingReleaseService;
            _upgradableSpecification = qualityUpgradableSpecification;
            _delayProfileService = delayProfileService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Database;
        public RejectionType Type => RejectionType.Temporary;

        public IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> GetOldestPendingPublishDates()
        {
            _pendingReleaseService.GetPending(out var oldestPublishDates);
            return oldestPublishDates;
        }

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (searchCriteria != null && searchCriteria.UserInvokedSearch)
            {
                _logger.Trace("Ignoring delay for user invoked search");
                return Decision.Accept();
            }

            var qualityProfile = subject.GetQualityProfile();
            var mediaType = subject.GetMediaType();
            if (qualityProfile == null)
            {
                return mediaType.HasValue
                    ? Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name)
                    : Decision.Reject("Unable to determine release media type");
            }

            var tags = subject.Author.GetTagsForMediaType(mediaType.Value);
            var delayProfile = _delayProfileService.BestForTags(tags);
            var delay = delayProfile.GetProtocolDelay(subject.Release.DownloadProtocol);
            var isPreferredProtocol = subject.Release.DownloadProtocol == delayProfile.PreferredProtocol;

            if (delay == 0)
            {
                _logger.Trace("Profile does not require a waiting period before download for {0}.", subject.Release.DownloadProtocol);
                return Decision.Accept();
            }

            var qualityComparer = new QualityModelComparer(qualityProfile);

            if (isPreferredProtocol)
            {
                foreach (var book in subject.GetBooksMatchingReleaseMediaType())
                {
                    var bookFiles = _mediaFileService.GetFilesByBook(book.Id);

                    foreach (var file in bookFiles)
                    {
                        var currentQuality = file.Quality;
                        var newQuality = subject.ParsedBookInfo.Quality;
                        var qualityCompare = qualityComparer.Compare(newQuality?.Quality, currentQuality.Quality);

                        if (qualityCompare == 0 && newQuality?.Revision.CompareTo(currentQuality.Revision) > 0)
                        {
                            _logger.Trace("New quality is a better revision for existing quality, skipping delay");
                            return Decision.Accept();
                        }
                    }
                }
            }

            // If quality meets or exceeds the best allowed quality in the profile accept it immediately
            if (delayProfile.BypassIfHighestQuality)
            {
                var bestQualityInProfile = qualityProfile.LastAllowedQuality();
                var isBestInProfile = qualityComparer.Compare(subject.ParsedBookInfo.Quality.Quality, bestQualityInProfile) >= 0;

                if (isBestInProfile && isPreferredProtocol)
                {
                    _logger.Trace("Quality is highest in profile for preferred protocol, will not delay");
                    return Decision.Accept();
                }
            }

            // If quality meets or exceeds the best allowed quality in the profile accept it immediately
            if (delayProfile.BypassIfAboveCustomFormatScore)
            {
                var score = subject.CustomFormatScore;
                var minimum = delayProfile.MinimumCustomFormatScore;

                if (score >= minimum && isPreferredProtocol)
                {
                    _logger.Trace("Custom format score ({0}) meets minimum ({1}) for preferred protocol, will not delay", score, minimum);
                    return Decision.Accept();
                }
            }

            var bookIds = subject.Books.Select(e => e.Id);

            DateTime? oldestDate;
            if (subject.PreparedPendingReleaseDates != null)
            {
                oldestDate = bookIds.Select(id => subject.PreparedPendingReleaseDates.TryGetValue((subject.Author.Id, id), out var date)
                    ? (DateTime?)date : null).Min();
            }
            else
            {
                oldestDate = _pendingReleaseService.OldestPendingRelease(subject.Author.Id, bookIds.ToArray())?.Release.PublishDate;
            }

            if (oldestDate.HasValue && (DateTime.UtcNow - oldestDate.Value).TotalMinutes > delay)
            {
                return Decision.Accept();
            }

            if (subject.Release.AgeMinutes < delay)
            {
                _logger.Trace("Waiting for better quality release, There is a {0} minute delay on {1}", delay, subject.Release.DownloadProtocol);
                return Decision.RejectHardFilter("Waiting for better quality release", "Delay");
            }

            return Decision.Accept();
        }
    }
}
