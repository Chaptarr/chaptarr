using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class HistorySpecification : IDecisionEngineSpecification
    {
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly UpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public HistorySpecification(IHistoryService historyService,
                                    IDownloadHistoryService downloadHistoryService,
                                    UpgradableSpecification upgradableSpecification,
                                    ICustomFormatCalculationService formatService,
                                    IConfigService configService,
                                    Logger logger)
        {
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Database;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (subject.PositivelyIncompatibleBookIds == null || subject.PreparedBookHistory == null)
            {
                PrepareAlternativeCopySuppression(new[] { subject }, checkGrabHistory: searchCriteria == null);
            }

            if (subject.GetBooksMatchingReleaseMediaType().Any(book => subject.PositivelyIncompatibleBookIds.Contains(book.Id)))
            {
                return Decision.RejectSoftFilter("This release was proved incompatible with the requested copy", "History");
            }

            if (searchCriteria != null)
            {
                _logger.Trace("Skipping history check during search");
                return Decision.Accept();
            }

            var qualityProfile = subject.GetQualityProfile();
            if (qualityProfile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            var cdhEnabled = _configService.EnableCompletedDownloadHandling;

            _logger.Trace("Performing history status check on report");
            foreach (var book in subject.GetBooksMatchingReleaseMediaType())
            {
                _logger.Trace("Checking current status of book [{0}] in history", book.Id);
                subject.PreparedBookHistory.TryGetValue(book.Id, out var mostRecent);

                if (mostRecent != null && mostRecent.EventType == EntityHistoryEventType.Grabbed)
                {
                    var recent = mostRecent.Date.After(DateTime.UtcNow.AddHours(-12));

                    if (!recent && cdhEnabled)
                    {
                        continue;
                    }

                    mostRecent.Book ??= book;
                    var customFormats = _formatService.ParseCustomFormat(mostRecent, subject.Author);

                    var currentEffectiveQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(qualityProfile, mostRecent.Quality);
                    var candidateEffectiveQuality = QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(qualityProfile, subject.ParsedBookInfo.Quality);

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

        public void PrepareAlternativeCopySuppression(IEnumerable<RemoteBook> remoteBooks, bool checkGrabHistory = true)
        {
            var subjects = (remoteBooks ?? Enumerable.Empty<RemoteBook>()).Where(subject => subject != null).ToList();
            var wantedCopies = subjects.SelectMany(subject => subject.GetBooksMatchingReleaseMediaType())
                .Where(book => book.Id > 0).GroupBy(book => book.Id).ToDictionary(group => group.Key, group =>
                {
                    var book = group.First();
                    return (AuthorId: book.AuthorId, EditionId: book.AnyEditionOk ? 0 : BookEditionIdentity.GetMonitoredEdition(book)?.Id ?? 0);
                });
            var history = _historyService.GetAcquisitionHistory(wantedCopies.Keys) ?? new List<EntityHistory>();
            var incompatible = new Dictionary<(int BookId, int AuthorId, int EditionId, string Guid, int IndexerId, int Protocol), HashSet<string>>();
            var ineligibleLatestGrabs = new HashSet<int>();
            foreach (var row in history.Where(row => row.EventType == EntityHistoryEventType.Grabbed &&
                bool.TryParse(row.GetDataValue(EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE), out var proved) && proved &&
                bool.TryParse(row.GetDataValue(EntityHistory.ALTERNATIVE_COPIES), out var alternatives) && alternatives))
            {
                var guid = row.GetDataValue("Guid");
                if (row.EditionId > 0 && guid.IsNotNullOrWhiteSpace() &&
                    int.TryParse(row.GetDataValue(EntityHistory.INDEXER_ID), out var indexerId) && indexerId > 0 &&
                    int.TryParse(row.GetDataValue("Protocol"), out var protocol) && protocol > 0)
                {
                    var key = (row.BookId, row.AuthorId, row.EditionId, guid, indexerId, protocol);
                    if (!incompatible.TryGetValue(key, out var hashes))
                    {
                        hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        incompatible.Add(key, hashes);
                    }

                    var hash = row.GetDataValue("TorrentInfoHash");
                    hashes.Add(hash.IsNullOrWhiteSpace() ? null : hash);
                    if (wantedCopies.TryGetValue(row.BookId, out var wanted) &&
                        wanted.AuthorId == row.AuthorId)
                    {
                        ineligibleLatestGrabs.Add(row.Id);
                    }
                }
            }

            // A disproved copy grab did not serve the wanted row; retain any earlier ordinary grab's protection.
            var latest = history.Where(row => !ineligibleLatestGrabs.Contains(row.Id)).GroupBy(row => row.BookId)
                .ToDictionary(group => group.Key, group => group.MaxBy(row => row.Date));
            if (checkGrabHistory)
            {
                var downloads = _downloadHistoryService.GetLatestDownloadHistoryItems(latest.Values
                        .Where(row => row.EventType == EntityHistoryEventType.Grabbed).Select(row => row.DownloadId))
                    .ToDictionary(row => row.DownloadId, StringComparer.OrdinalIgnoreCase);
                foreach (var row in latest.Values.ToArray())
                {
                    if (row.DownloadId.IsNotNullOrWhiteSpace() && downloads.TryGetValue(row.DownloadId, out var download) &&
                        download.Date >= row.Date && (download.EventType is DownloadHistoryEventType.DownloadImported or
                            DownloadHistoryEventType.DownloadFailed or DownloadHistoryEventType.DownloadIgnored))
                    {
                        latest.Remove(row.BookId);
                    }
                }
            }

            foreach (var subject in subjects)
            {
                subject.PreparedBookHistory = latest;
                var release = subject.Release;
                var hash = (release as TorrentInfo)?.InfoHash;
                subject.PositivelyIncompatibleBookIds = subject.GetBooksMatchingReleaseMediaType()
                    .Where(book => wantedCopies.TryGetValue(book.Id, out var wanted) && wanted.EditionId > 0 && release != null &&
                        incompatible.TryGetValue((book.Id, wanted.AuthorId, wanted.EditionId,
                            release.Guid, release.IndexerId, (int)release.DownloadProtocol), out var hashes) &&
                        (hash.IsNullOrWhiteSpace() || hashes.Contains(null) || hashes.Contains(hash)))
                    .Select(book => book.Id).ToHashSet();
            }
        }
    }
}
