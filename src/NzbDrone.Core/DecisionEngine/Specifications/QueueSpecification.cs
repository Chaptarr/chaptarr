using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class QueueSpecification : IDecisionEngineSpecification
    {
        private readonly IQueueService _queueService;
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public QueueSpecification(IQueueService queueService,
                                  UpgradableSpecification upgradableSpecification,
                                  ICustomFormatCalculationService formatService,
                                  IConfigService configService,
                                  Logger logger)
        {
            _queueService = queueService;
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            return CheckQueue(subject, _queueService.GetQueue(includeConversionStatus: false));
        }

        public Func<RemoteBook, Decision> PrepareCheck()
        {
            var queue = _queueService.GetQueue(includeConversionStatus: false);
            return subject => CheckQueue(subject, queue);
        }

        private Decision CheckQueue(RemoteBook subject, List<Queue.Queue> queue)
        {
            var subjectBookIds = GetSubjectBookIds(subject);
            if (!subjectBookIds.Any())
            {
                return Decision.Accept();
            }

            var matchingBook = queue.Where(q => q.TrackedDownloadState != TrackedDownloadState.Imported &&
                                        q.GetTargetBookIds().Intersect(subjectBookIds).Any())
                                    .ToList();

            foreach (var queueItem in matchingBook)
            {
                var remoteBook = queueItem.RemoteBook;
                if (remoteBook?.AlternativeCopies == true)
                {
                    remoteBook = remoteBook.ForBooks(remoteBook.Books.Where(book => subjectBookIds.Contains(book.Id)));
                }
                var queuedSourceQuality = remoteBook?.ParsedBookInfo?.Quality ?? queueItem.Quality;
                var candidateSourceQuality = subject.ParsedBookInfo.Quality;
                var qualityProfile = subject.GetQualityProfile();
                var queuedEffectiveQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(qualityProfile, queuedSourceQuality);
                var candidateEffectiveQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(qualityProfile, candidateSourceQuality);

                // To avoid a race make sure it's not FailedPending (failed awaiting removal/search).
                // Failed items (already searching for a replacement) won't be part of the queue since
                // it's a copy, of the tracked download, not a reference.
                if (queueItem.TrackedDownloadState == TrackedDownloadState.DownloadFailedPending)
                {
                    continue;
                }

                if (qualityProfile == null || queuedSourceQuality?.Quality == null)
                {
                    _logger.Trace("Rejecting release because a matching download is already queued and Chaptarr cannot prove the new release is a profile-approved upgrade. Queue item: {0}", queueItem.Title);
                    return Decision.Reject("A download is already queued for this book");
                }

                _logger.Trace("Checking if existing release in queue meets cutoff. Effective queued quality is: {0}", queuedEffectiveQuality);

                var queuedItemCustomFormats = remoteBook?.ParsedBookInfo != null
                    ? _formatService.ParseCustomFormat(remoteBook, (long)queueItem.Size)
                    : new List<CustomFormat>();

                if (!_upgradableSpecification.CutoffNotMet(qualityProfile,
                                                           new List<QualityModel> { queuedEffectiveQuality },
                                                           queuedItemCustomFormats,
                                                           candidateEffectiveQuality))
                {
                    return Decision.Reject("Release in queue already meets cutoff: {0}", queuedEffectiveQuality);
                }

                _logger.Trace("Checking if release has a higher profile preference than queued release. Queued source: {0}", queuedSourceQuality);

                if (!_upgradableSpecification.IsReleaseUpgradable(qualityProfile,
                                                           queuedSourceQuality,
                                                           queuedItemCustomFormats,
                                                           candidateSourceQuality,
                                                           subject.CustomFormats))
                {
                    return Decision.Reject("Release in queue is of equal or higher preference: {0}", queuedSourceQuality);
                }

                _logger.Trace("Checking if profiles allow upgrading. Queued source: {0}", queuedSourceQuality);

                if (!_upgradableSpecification.IsReleaseUpgradeAllowed(qualityProfile,
                                                               queuedSourceQuality,
                                                               queuedItemCustomFormats,
                                                               candidateSourceQuality,
                                                               subject.CustomFormats))
                {
                    return Decision.Reject("Another release is queued and the Quality profile does not allow upgrades");
                }

                if (_upgradableSpecification.IsRevisionUpgrade(queuedSourceQuality, candidateSourceQuality))
                {
                    if (_configService.DownloadPropersAndRepacks == ProperDownloadTypes.DoNotUpgrade)
                    {
                        _logger.Trace("Auto downloading of propers is disabled");
                        return Decision.Reject("Proper downloading is disabled");
                    }
                }
            }

            return Decision.Accept();
        }

        private static List<int> GetSubjectBookIds(RemoteBook remoteBook)
        {
            return GetBookIds(remoteBook?.GetBooksMatchingReleaseMediaType());
        }

        private static List<int> GetBookIds(IEnumerable<Book> books)
        {
            return books?
                .Where(book => book?.Id > 0)
                .Select(book => book.Id)
                .Distinct()
                .ToList() ?? new List<int>();
        }
    }
}
