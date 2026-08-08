using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMamUnsatisfiedSlotGuard
    {
        MamUnsatisfiedSlotAvailability Check(RemoteBook remoteBook);
        MamUnsatisfiedSlotAvailability TryReserve(RemoteBook remoteBook);
        void Reconcile(MyAnonaMouse indexer, MyAnonaMouseAccountStatus status);
    }

    public sealed class MamUnsatisfiedSlotAvailability
    {
        private MamUnsatisfiedSlotAvailability(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        public bool Accepted { get; }
        public string Reason { get; }

        public static MamUnsatisfiedSlotAvailability Accept()
        {
            return new MamUnsatisfiedSlotAvailability(true, null);
        }

        public static MamUnsatisfiedSlotAvailability Reject(string reason)
        {
            return new MamUnsatisfiedSlotAvailability(false, reason);
        }
    }

    public class MamUnsatisfiedSlotGuard : IMamUnsatisfiedSlotGuard
    {
        private static readonly TimeSpan MaximumStatusAge = TimeSpan.FromHours(2);
        // MAM documents a 5-20 minute lag before snatches reach account summaries. Keep counting locally through the full window.
        private static readonly TimeSpan SummaryAccountingLag = TimeSpan.FromMinutes(20);

        private readonly IIndexerFactory _indexerFactory;
        private readonly IMamUnsatisfiedSlotReservationRepository _repository;
        private readonly Logger _logger;
        private readonly object _reservationLock = new object();

        public MamUnsatisfiedSlotGuard(IIndexerFactory indexerFactory,
                                       IMamUnsatisfiedSlotReservationRepository repository,
                                       Logger logger)
        {
            _indexerFactory = indexerFactory;
            _repository = repository;
            _logger = logger;
        }

        public MamUnsatisfiedSlotAvailability Check(RemoteBook remoteBook)
        {
            lock (_reservationLock)
            {
                return Evaluate(remoteBook, false);
            }
        }

        public MamUnsatisfiedSlotAvailability TryReserve(RemoteBook remoteBook)
        {
            lock (_reservationLock)
            {
                return Evaluate(remoteBook, true);
            }
        }

        public void Reconcile(MyAnonaMouse indexer, MyAnonaMouseAccountStatus status)
        {
            if (indexer?.Definition is not IndexerDefinition definition ||
                definition.Settings is not MyAnonaMouseSettings settings ||
                !settings.ProtectUnsatisfiedSlots ||
                status == null)
            {
                return;
            }

            var reservations = GetAccountReservations(definition, settings);

            // This does not infer that a torrent became satisfied. Once MAM's own snapshot covers the
            // latest attempt plus its documented accounting lag, a served torrent is in the reported
            // count and an unserved attempt is a phantom; either way, retaining the row would double-count.
            foreach (var reservation in reservations.Where(r =>
                         status.SnapshotCreatedUtc >= (r.ConfirmedUtc ?? r.ReservedUtc).Add(SummaryAccountingLag)))
            {
                lock (_reservationLock)
                {
                    var current = _repository.Find(reservation.IndexerId, reservation.TorrentId);
                    if (current != null &&
                        status.SnapshotCreatedUtc >= (current.ConfirmedUtc ?? current.ReservedUtc).Add(SummaryAccountingLag))
                    {
                        var accountingAnchor = current.ConfirmedUtc ?? current.ReservedUtc;
                        _repository.Delete(current.Id);
                        _logger.Debug(
                            "Retired MAM slot reservation for torrent {0} on indexer '{1}': provider snapshot {2:u} covers {3:u} plus the {4}-minute accounting window",
                            current.TorrentId,
                            definition.Name,
                            status.SnapshotCreatedUtc,
                            accountingAnchor,
                            SummaryAccountingLag.TotalMinutes);
                    }
                }
            }
        }

        internal static bool TryGetTorrentId(ReleaseInfo release, out string torrentId)
        {
            torrentId = null;
            var guid = release?.Guid;
            if (string.IsNullOrWhiteSpace(guid) || !guid.StartsWith("MAM-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = guid.Substring(4);
            if (!long.TryParse(candidate, out var numericId) || numericId <= 0)
            {
                return false;
            }

            torrentId = candidate;
            return true;
        }

        private MamUnsatisfiedSlotAvailability Evaluate(RemoteBook remoteBook, bool reserve)
        {
            var release = remoteBook?.Release;
            var definition = GetMamDefinition(release);
            if (definition == null)
            {
                return MamUnsatisfiedSlotAvailability.Accept();
            }

            if (definition.Settings is not MyAnonaMouseSettings settings || !settings.ProtectUnsatisfiedSlots)
            {
                return MamUnsatisfiedSlotAvailability.Accept();
            }

            if (!TryGetTorrentId(release, out var torrentId))
            {
                return MamUnsatisfiedSlotAvailability.Reject("MAM safety pause: this release has no valid MAM torrent identity");
            }

            var reservations = GetAccountReservations(definition, settings);
            var existingReservation = reservations.FirstOrDefault(r => string.Equals(r.TorrentId, torrentId, StringComparison.Ordinal));
            if (existingReservation != null)
            {
                if (reserve && !existingReservation.ConfirmedUtc.HasValue)
                {
                    existingReservation.ReservedUtc = DateTime.UtcNow;
                    _repository.Update(existingReservation);
                }

                return MamUnsatisfiedSlotAvailability.Accept();
            }

            var now = DateTime.UtcNow;
            if (!HasFreshStatus(settings, now))
            {
                return MamUnsatisfiedSlotAvailability.Reject("MAM safety pause: the unsatisfied-slot status is unavailable or stale; test the indexer and try again");
            }

            var manualGrab = remoteBook.ReleaseSource == ReleaseSourceType.InteractiveSearch;
            var safetyReserve = Math.Max(0, settings.UnsatisfiedSlotReserve);
            var manualGrabBuffer = Math.Max(0, settings.ManualGrabBuffer);
            var reservedSlots = safetyReserve + (manualGrab ? 0 : manualGrabBuffer);
            var usableSlots = Math.Max(0, settings.UnsatisfiedLimit.Value - reservedSlots);
            var effectiveUnsatisfied = settings.UnsatisfiedCount.Value + reservations.Count;

            if (effectiveUnsatisfied >= usableSlots)
            {
                var reason = manualGrab
                    ? $"{safetyReserve} safety slot(s) are always kept free"
                    : $"{safetyReserve} safety slot(s) and {manualGrabBuffer} user slot(s) are being kept free";

                return MamUnsatisfiedSlotAvailability.Reject(
                    $"MAM safety pause: {effectiveUnsatisfied} of {settings.UnsatisfiedLimit.Value} unsatisfied slots are reported or reserved; {reason}");
            }

            if (reserve)
            {
                _repository.Insert(new MamUnsatisfiedSlotReservation
                {
                    IndexerId = definition.Id,
                    TorrentId = torrentId,
                    ReservedUtc = now
                });
            }

            return MamUnsatisfiedSlotAvailability.Accept();
        }

        private IndexerDefinition GetMamDefinition(ReleaseInfo release)
        {
            if (release?.IndexerId <= 0)
            {
                return null;
            }

            var definition = _indexerFactory.Find(release.IndexerId);
            return definition?.Settings is MyAnonaMouseSettings ? definition : null;
        }

        // MAM reports one unsatisfied limit per account. Native indexers sharing the same mam_id
        // must also share in-flight reservations or each can spend the same apparent free slots.
        private List<MamUnsatisfiedSlotReservation> GetAccountReservations(
            IndexerDefinition definition,
            MyAnonaMouseSettings settings)
        {
            var accountIndexerIds = _indexerFactory.All()
                .Where(candidate =>
                    candidate.Id > 0 &&
                    candidate.Settings is MyAnonaMouseSettings candidateSettings &&
                    !string.IsNullOrWhiteSpace(settings.MamId) &&
                    string.Equals(candidateSettings.MamId, settings.MamId, StringComparison.Ordinal))
                .Select(candidate => candidate.Id)
                .ToHashSet();

            accountIndexerIds.Add(definition.Id);

            return _repository.All()
                .Where(reservation => accountIndexerIds.Contains(reservation.IndexerId))
                .ToList();
        }

        private static bool HasFreshStatus(MyAnonaMouseSettings settings, DateTime now)
        {
            if (!settings.UnsatisfiedCount.HasValue ||
                !settings.UnsatisfiedLimit.HasValue ||
                settings.UnsatisfiedCount.Value < 0 ||
                settings.UnsatisfiedLimit.Value <= 0 ||
                !settings.UnsatisfiedSnapshotUtc.HasValue ||
                !settings.UnsatisfiedStatusRefreshedUtc.HasValue)
            {
                return false;
            }

            return settings.UnsatisfiedSnapshotUtc.Value <= now.AddMinutes(5) &&
                   settings.UnsatisfiedStatusRefreshedUtc.Value <= now.AddMinutes(5) &&
                   now - settings.UnsatisfiedSnapshotUtc.Value <= MaximumStatusAge &&
                   now - settings.UnsatisfiedStatusRefreshedUtc.Value <= MaximumStatusAge;
        }

    }
}
