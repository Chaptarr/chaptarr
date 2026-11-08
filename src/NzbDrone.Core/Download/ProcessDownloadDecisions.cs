using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.DecisionEngine.Specifications;

namespace NzbDrone.Core.Download
{
    public interface IProcessDownloadDecisions
    {
        Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions);
        Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions, int? downloadClientId);
        Task ProcessSelectedDecisions(List<RemoteBook> selected, int? downloadClientId = null);
    }

    public class ProcessDownloadDecisions : IProcessDownloadDecisions
    {
        private readonly IDownloadService _downloadService;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly QueueSpecification _queueSpecification;
        private readonly Logger _logger;

        public ProcessDownloadDecisions(IDownloadService downloadService,
                                        IPrioritizeDownloadDecision prioritizeDownloadDecision,
                                        IPendingReleaseService pendingReleaseService,
                                        QueueSpecification queueSpecification,
                                        Logger logger)
        {
            _downloadService = downloadService;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _pendingReleaseService = pendingReleaseService;
            _queueSpecification = queueSpecification;
            _logger = logger;
        }

        public Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions) => ProcessDecisions(decisions, null);

        public async Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions, int? downloadClientId)
        {
            var qualifiedReports = GetQualifiedReports(decisions);
            var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(qualifiedReports);
            var grabbed = new List<DownloadDecision>();
            var pending = new List<DownloadDecision>();
            var rejected = decisions.Where(d => d.Rejected).ToList();

            var pendingAddQueue = new List<Tuple<DownloadDecision, PendingReleaseReason>>();
            var checkQueue = _queueSpecification.PrepareCheck();

            var usenetFailed = false;
            var torrentFailed = false;

            foreach (var report in CoalesceReports(prioritizedDecisions, grabbed))
            {
                void RemoveJoinedDelays()
                {
                    if (!report.RemoteBook.AlternativeCopies || report.RemoteBook.Books.Count < 2)
                    {
                        return;
                    }

                    var ids = report.RemoteBook.Books.Select(book => book.Id).ToHashSet();
                    bool JoinedDelay(DownloadDecision candidate) => IsDelayOnly(candidate) &&
                        Equals(GetAlternativeReleaseKey(candidate.RemoteBook.Release), GetAlternativeReleaseKey(report.RemoteBook.Release)) &&
                        candidate.RemoteBook.Books.All(book => ids.Contains(book.Id));
                    pendingAddQueue.RemoveAll(pair => JoinedDelay(pair.Item1));
                    pending.RemoveAll(JoinedDelay);
                }

                var downloadProtocol = report.RemoteBook.Release.DownloadProtocol;

                //Skip if already grabbed
                if (IsBookProcessed(grabbed, report))
                {
                    continue;
                }

                if (report.TemporarilyRejected)
                {
                    PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.Delay);
                    continue;
                }

                var queueDecision = checkQueue(report.RemoteBook);
                if (!queueDecision.Accepted)
                {
                    rejected.Add(new DownloadDecision(report.RemoteBook, new Rejection(queueDecision.Reason,
                        _queueSpecification.Type, queueDecision.CanBypass, queueDecision.Category, queueDecision.Severity)));
                    continue;
                }

                if ((downloadProtocol == DownloadProtocol.Usenet && usenetFailed) ||
                    (downloadProtocol == DownloadProtocol.Torrent && torrentFailed))
                {
                    RemoveJoinedDelays();
                    PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.DownloadClientUnavailable);
                    continue;
                }

                var result = await ProcessDecisionInternal(report, downloadClientId);
                if (result != ProcessedDecisionResult.Skipped)
                {
                    RemoveJoinedDelays();
                }

                switch (result)
                {
                    case ProcessedDecisionResult.Grabbed:
                        {
                            grabbed.Add(report);
                            break;
                        }

                    case ProcessedDecisionResult.Pending:
                        {
                            PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.Delay);
                            break;
                        }

                    case ProcessedDecisionResult.Rejected:
                        {
                            rejected.Add(report);
                            break;
                        }

                    case ProcessedDecisionResult.Failed:
                        {
                            PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.DownloadClientUnavailable);

                            if (downloadProtocol == DownloadProtocol.Usenet)
                            {
                                usenetFailed = true;
                            }
                            else if (downloadProtocol == DownloadProtocol.Torrent)
                            {
                                torrentFailed = true;
                            }

                            break;
                        }

                    case ProcessedDecisionResult.Skipped:
                        {
                            break;
                        }
                }
            }

            if (pendingAddQueue.Any())
            {
                _pendingReleaseService.AddMany(pendingAddQueue);
            }

            return new ProcessedDecisions(grabbed, pending, rejected);
        }

        public async Task ProcessSelectedDecisions(List<RemoteBook> selected, int? downloadClientId = null)
        {
            var decisions = selected.Select(remote => remote.AlternativeCopies ? remote.Release.PendingContext?.RemoteBook ?? remote : remote)
                .Distinct().Select(remote => new DownloadDecision(remote)).ToList();
            foreach (var report in CoalesceReports(decisions, new List<DownloadDecision>()))
            {
                await _downloadService.DownloadReport(report.RemoteBook, downloadClientId);
            }
        }

        private static bool IsDelayOnly(DownloadDecision decision) => decision.TemporarilyRejected &&
            decision.Rejections.All(rejection => rejection.Category == "Delay");

        private IEnumerable<DownloadDecision> CoalesceReports(List<DownloadDecision> prioritizedDecisions, List<DownloadDecision> grabbed)
        {
            var remainingByBook = prioritizedDecisions
                .SelectMany(decision => decision.RemoteBook.Books.Select(book => (book.Id, Decision: decision)))
                .GroupBy(pair => pair.Id).ToDictionary(group => group.Key, group => new Queue<DownloadDecision>(group.Select(pair => pair.Decision)));
            var alternativesByRelease = prioritizedDecisions.Where(decision => decision.RemoteBook.AlternativeCopies)
                .GroupBy(decision => GetAlternativeReleaseKey(decision.RemoteBook.Release)).ToDictionary(group => group.Key, group => group.ToList());
            var consumed = new HashSet<DownloadDecision>();

            foreach (var report in prioritizedDecisions)
            {
                if (!consumed.Contains(report))
                {
                    yield return CoalesceAlternatives(report, alternativesByRelease, remainingByBook, consumed, grabbed);
                }
            }
        }

        private static object GetAlternativeReleaseKey(ReleaseInfo release)
        {
            return release.IndexerId > 0 && !string.IsNullOrWhiteSpace(release.Guid) && release.DownloadProtocol != DownloadProtocol.Unknown
                ? (release.IndexerId, release.DownloadProtocol, release.Guid)
                : release;
        }

        private DownloadDecision CoalesceAlternatives(DownloadDecision report,
            Dictionary<object, List<DownloadDecision>> alternativesByRelease,
            Dictionary<int, Queue<DownloadDecision>> remainingByBook,
            HashSet<DownloadDecision> consumed, List<DownloadDecision> grabbed)
        {
            var source = report.RemoteBook;
            if (!source.AlternativeCopies || (!report.Approved && !IsDelayOnly(report)) || source.Books.Count == 0 || IsBookProcessed(grabbed, report))
            {
                // Keep a waiting-period-only view available if an eligible same-release copy is encountered later.
                if (!IsDelayOnly(report))
                {
                    consumed.Add(report);
                }

                return report;
            }

            var target = source.Books[0];
            var selected = alternativesByRelease[GetAlternativeReleaseKey(source.Release)].Where(candidate =>
            {
                if (consumed.Contains(candidate) || (!candidate.Approved && !IsDelayOnly(candidate)) || candidate.RemoteBook.Books.Count == 0 || IsBookProcessed(grabbed, candidate))
                {
                    return false;
                }

                // Waiting copies retain only their current mapped release's captured group.
                if (!report.Approved && !ReferenceEquals(candidate.RemoteBook.Release, source.Release))
                {
                    return false;
                }

                return candidate.RemoteBook.Books.All(book =>
                {
                    var remaining = remainingByBook[book.Id];
                    while (report.Approved && remaining.Count > 0 && consumed.Contains(remaining.Peek()))
                    {
                        remaining.Dequeue();
                    }

                    return remaining.Count > 0 && ReferenceEquals(remaining.Peek(), candidate) &&
                        book.AuthorId == target.AuthorId && book.MediaType == target.MediaType &&
                        (book.Id == target.Id || WorkIdMatcher.WorkProviderIdMatches(target, book));
                });
            }).ToList();
            if (selected.Count < 2 || !selected.Contains(report))
            {
                consumed.Add(report);
                return report;
            }

            foreach (var candidate in selected)
            {
                consumed.Add(candidate);
            }

            var merged = source.ForBooks(selected.SelectMany(candidate => candidate.RemoteBook.Books));
            merged.TargetEditionIds = selected.SelectMany(candidate => candidate.RemoteBook.TargetEditionIds)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            merged.TargetCustomFormatScores = selected.SelectMany(candidate => candidate.RemoteBook.Books.Select(book =>
                new KeyValuePair<int, int>(book.Id, candidate.RemoteBook.Books.Count == 1 ? candidate.RemoteBook.CustomFormatScore :
                    candidate.RemoteBook.TargetCustomFormatScores.GetValueOrDefault(book.Id, candidate.RemoteBook.CustomFormatScore))))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            return new DownloadDecision(merged, report.Rejections.ToArray()) { AutoPickReasons = report.AutoPickReasons };
        }

        internal List<DownloadDecision> GetQualifiedReports(IEnumerable<DownloadDecision> decisions)
        {
            return decisions.Where(IsQualifiedReport).ToList();
        }

        internal bool IsQualifiedReport(DownloadDecision decision)
        {
            // Process both approved and temporarily rejected
            return (decision.Approved || decision.TemporarilyRejected) && decision.RemoteBook.Books.Any();
        }

        private bool IsBookProcessed(List<DownloadDecision> decisions, DownloadDecision report)
        {
            var bookIds = report.RemoteBook.Books.Select(e => e.Id).ToList();

            return decisions.SelectMany(r => r.RemoteBook.Books)
                            .Select(e => e.Id)
                            .ToList()
                            .Intersect(bookIds)
                            .Any();
        }

        private void PreparePending(List<Tuple<DownloadDecision, PendingReleaseReason>> queue, List<DownloadDecision> grabbed, List<DownloadDecision> pending, DownloadDecision report, PendingReleaseReason reason)
        {
            // If a release was already grabbed with matching books we should store it as a fallback
            // and filter it out the next time it is processed.
            // If a higher quality release failed to add to the download client, but a lower quality release
            // was sent to another client we still list it normally so it apparent that it'll grab next time.
            // Delayed is treated the same, but only the first is listed the subsequent items as stored as Fallback.
            if (IsBookProcessed(grabbed, report) ||
                IsBookProcessed(pending, report))
            {
                reason = PendingReleaseReason.Fallback;
            }

            queue.Add(Tuple.Create(report, reason));
            pending.Add(report);
        }

        private async Task<ProcessedDecisionResult> ProcessDecisionInternal(DownloadDecision decision, int? downloadClientId = null)
        {
            var remoteBook = decision.RemoteBook;

            try
            {
                _logger.Trace("Grabbing from Indexer {0} at priority {1}.", remoteBook.Release.Indexer, remoteBook.Release.IndexerPriority);
                await _downloadService.DownloadReport(remoteBook, downloadClientId);

                return ProcessedDecisionResult.Grabbed;
            }
            catch (MamUnsatisfiedSlotsUnavailableException ex)
            {
                _logger.Debug(ex, "MAM has no safely available unsatisfied-torrent slot; storing release until later. " + remoteBook);

                return ProcessedDecisionResult.Pending;
            }
            catch (ReleaseUnavailableException)
            {
                _logger.Warn("Failed to download release from indexer, no longer available. " + remoteBook);
                return ProcessedDecisionResult.Rejected;
            }
            catch (Exception ex)
            {
                if (ex is DownloadClientUnavailableException || ex is DownloadClientAuthenticationException)
                {
                    _logger.Debug(ex,
                        "Failed to send release to download client, storing until later. " + remoteBook);

                    return ProcessedDecisionResult.Failed;
                }
                else
                {
                    _logger.Warn(ex, "Couldn't add report to download queue. " + remoteBook);
                    return ProcessedDecisionResult.Skipped;
                }
            }
        }
    }
}
