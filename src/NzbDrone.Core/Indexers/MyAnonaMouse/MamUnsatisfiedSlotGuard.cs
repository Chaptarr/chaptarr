using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMamUnsatisfiedSlotGuard
    {
        MamUnsatisfiedSlotAvailability Check(RemoteBook remoteBook);
        MamUnsatisfiedSlotAvailability TryReserve(RemoteBook remoteBook);
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
        // Covers delayed accounting and clock skew. Revisit 5 minutes after more timing tests.
        internal static readonly TimeSpan SummaryAccountingLag = TimeSpan.FromMinutes(10);

        private static readonly TimeSpan MaximumStatusAge = TimeSpan.FromHours(2);

        private readonly IIndexerFactory _indexerFactory;
        private readonly IMamUnsatisfiedSlotReservationRepository _repository;
        private readonly Logger _logger;
        private readonly object _reservationLock = new object();

        public MamUnsatisfiedSlotGuard(IIndexerFactory indexerFactory, IMamUnsatisfiedSlotReservationRepository repository, Logger logger)
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

        private static bool TryGetTorrentId(ReleaseInfo release, out string torrentId)
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
            var started = Stopwatch.GetTimestamp();
            var release = remoteBook?.Release;
            var definition = release?.IndexerId > 0 ? _indexerFactory.Find(release.IndexerId) : null;
            if (definition?.Settings is not MyAnonaMouseSettings settings || !settings.ProtectUnsatisfiedSlots)
            {
                return MamUnsatisfiedSlotAvailability.Accept();
            }

            if (!TryGetTorrentId(release, out var torrentId))
            {
                return MamUnsatisfiedSlotAvailability.Reject("MAM safety pause: this release has no valid MAM torrent identity");
            }

            var now = DateTime.UtcNow;
            if (!HasFreshStatus(settings, now))
            {
                return MamUnsatisfiedSlotAvailability.Reject("MAM safety pause: the unsatisfied-slot status is unavailable or stale; test the indexer and try again");
            }

            // MAM's count at the last check plus the other torrents grabbed on that account since then.
            var reservations = _repository.All().ToList();
            var countedSince = settings.UnsatisfiedSnapshotUtc.Value - SummaryAccountingLag;
            var recent = reservations.Where(r => r.ReservedUtc > countedSince && r.TorrentId != torrentId).ToList();
            var otherAccountIndexerIds = GetOtherAccountIndexerIds(recent, definition.Id, settings.UserId);
            var grabsSince = recent
                .Where(r => !otherAccountIndexerIds.Contains(r.IndexerId))
                .Select(r => r.TorrentId)
                .Distinct()
                .Count();
            var effectiveUnsatisfied = settings.UnsatisfiedCount.Value + grabsSince;

            var manualGrab = remoteBook.ReleaseSource == ReleaseSourceType.InteractiveSearch;
            var safetyReserve = Math.Max(0, settings.UnsatisfiedSlotReserve);
            var manualGrabBuffer = Math.Max(0, settings.ManualGrabBuffer);
            var reservedSlots = safetyReserve + (manualGrab ? 0 : manualGrabBuffer);
            var usableSlots = Math.Max(0, settings.UnsatisfiedLimit.Value - reservedSlots);

            if (!reserve)
            {
                _logger.Trace(
                    "Checked MAM torrent {0} on indexer '{1}': {2} reported at the last check plus {3} grabbed since, {4} usable, read {5} reservation rows in {6:F1} ms",
                    torrentId, definition.Name, settings.UnsatisfiedCount.Value, grabsSince, usableSlots, reservations.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

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
                var reservation = reservations.FirstOrDefault(r => r.IndexerId == definition.Id && r.TorrentId == torrentId) ??
                                  new MamUnsatisfiedSlotReservation { IndexerId = definition.Id, TorrentId = torrentId };
                // A retry may be the first request MAM counts, so it restarts this torrent's window.
                reservation.ReservedUtc = now;
                _repository.Upsert(reservation);

                // Status older than two hours is never used, so no usable check can still count these grabs.
                var expired = reservations.Where(r => r.ReservedUtc <= now - MaximumStatusAge - SummaryAccountingLag).Select(r => r.Id).ToList();
                _repository.DeleteMany(expired);

                _logger.Debug(
                    "Reserved MAM torrent {0} on indexer '{1}': {2} reported at the last check plus {3} grabbed since, {4} usable; read {5} reservation rows and removed {6} old records in {7:F1} ms",
                    torrentId,
                    definition.Name,
                    settings.UnsatisfiedCount.Value,
                    grabsSince,
                    usableSlots,
                    reservations.Count,
                    expired.Count,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return MamUnsatisfiedSlotAvailability.Accept();
        }

        // MAM tracks unsatisfied slots per user ID, not per indexer, so every MAM indexer on one user ID shares the count.
        private HashSet<int> GetOtherAccountIndexerIds(List<MamUnsatisfiedSlotReservation> recent, int indexerId, string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return new HashSet<int>();
            }

            return recent.Select(r => r.IndexerId)
                .Where(id => id != indexerId)
                .Distinct()
                .Where(id => _indexerFactory.Find(id)?.Settings is MyAnonaMouseSettings other &&
                             !string.IsNullOrWhiteSpace(other.UserId) &&
                             other.UserId != userId)
                .ToHashSet();
        }

        internal static bool HasFreshStatus(MyAnonaMouseSettings settings, DateTime now)
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
