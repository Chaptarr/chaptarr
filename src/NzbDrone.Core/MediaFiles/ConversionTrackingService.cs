using System;
using System.Collections.Concurrent;
using System.Threading;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.MediaFiles
{
    public interface IConversionTrackingService
    {
        void Start(string downloadId, int targetQualityId, string targetQualityName, string message = null);
        void Progress(string downloadId, decimal? progress, string message = null);
        void RegisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource);
        void UnregisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource);
        bool Cancel(string downloadId, CancellationTokenSource expectedSource = null);
        void Cancelled(string downloadId, string message = null);
        void Complete(string downloadId);
        void Fail(string downloadId, string errorMessage);
        void Clear(string downloadId);
        ConversionQueueStatus Get(string downloadId);
    }

    public class ConversionTrackingService : IConversionTrackingService
    {
        private static readonly TimeSpan TerminalStatusRetention = TimeSpan.FromDays(1);
        private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(15);
        private static readonly ConcurrentDictionary<string, ConversionQueueStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastPruneUtc = DateTime.MinValue;
        private readonly IEventAggregator _eventAggregator;

        public ConversionTrackingService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }

        public void Start(string downloadId, int targetQualityId, string targetQualityName, string message = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            PruneExpiredTerminalStatuses();

            _statuses[downloadId] = new ConversionQueueStatus
            {
                Status = "converting",
                TargetQualityId = targetQualityId,
                TargetQualityName = targetQualityName,
                Progress = null,
                Message = message,
                CanCancel = false,
                Updated = DateTime.UtcNow
            };

            PublishQueueUpdate();
        }

        public void Progress(string downloadId, decimal? progress, string message = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || !_statuses.TryGetValue(downloadId, out var status))
            {
                return;
            }

            if (string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, "cancelling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (progress.HasValue)
            {
                progress = Math.Min(100m, Math.Max(0m, progress.Value));
            }

            status.Progress = progress;
            status.Message = message ?? status.Message;
            status.Updated = DateTime.UtcNow;

            PublishQueueUpdate();
        }

        public void RegisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || cancellationTokenSource == null)
            {
                return;
            }

            // Sources are borrowed. Owners unregister before disposing; the same gate
            // keeps cancellation from using a source after its owner detaches it.
            lock (_cancellations)
            {
                _cancellations[downloadId] = cancellationTokenSource;
                if (_statuses.TryGetValue(downloadId, out var status))
                {
                    status.CanCancel = true;
                    status.Updated = DateTime.UtcNow;
                }
            }

            PublishQueueUpdate();
        }

        public void UnregisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || cancellationTokenSource == null)
            {
                return;
            }

            lock (_cancellations)
            {
                // An earlier attempt finishing must not detach a replacement attempt.
                if (!_cancellations.TryGetValue(downloadId, out var current) || !ReferenceEquals(current, cancellationTokenSource))
                {
                    return;
                }

                ClearCancellation(downloadId);
                if (_statuses.TryGetValue(downloadId, out var status))
                {
                    status.CanCancel = false;
                    status.Updated = DateTime.UtcNow;
                }
            }

            PublishQueueUpdate();
        }

        public bool Cancel(string downloadId, CancellationTokenSource expectedSource = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return false;
            }

            lock (_cancellations)
            {
                if (!_cancellations.TryGetValue(downloadId, out var cancellationTokenSource) ||
                    (expectedSource != null && !ReferenceEquals(cancellationTokenSource, expectedSource)))
                {
                    return false;
                }
                cancellationTokenSource.Cancel();

                _statuses.AddOrUpdate(
                    downloadId,
                    _ => new ConversionQueueStatus
                    {
                        Status = "cancelling",
                        Message = "Cancelling conversion",
                        CanCancel = false,
                        Updated = DateTime.UtcNow
                    },
                    (_, status) =>
                    {
                        status.Status = "cancelling";
                        status.Message = "Cancelling conversion";
                        status.CanCancel = false;
                        status.Updated = DateTime.UtcNow;
                        return status;
                    });
            }

            PublishQueueUpdate();
            return true;
        }

        public void Cancelled(string downloadId, string message = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            PruneExpiredTerminalStatuses();

            lock (_cancellations)
            {
                _statuses.AddOrUpdate(
                    downloadId,
                    _ => new ConversionQueueStatus
                    {
                        Status = "cancelled",
                        Progress = null,
                        Message = string.IsNullOrWhiteSpace(message) ? "Conversion cancelled" : message,
                        CanCancel = false,
                        Updated = DateTime.UtcNow
                    },
                    (_, status) =>
                    {
                        status.Status = "cancelled";
                        status.Progress = null;
                        status.Message = string.IsNullOrWhiteSpace(message) ? "Conversion cancelled" : message;
                        status.CanCancel = false;
                        status.Updated = DateTime.UtcNow;
                        return status;
                    });

                ClearCancellation(downloadId);
            }
            PublishQueueUpdate();
        }

        public void Complete(string downloadId)
        {
            Clear(downloadId);
        }

        public void Fail(string downloadId, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            PruneExpiredTerminalStatuses();

            lock (_cancellations)
            {
                _statuses[downloadId] = new ConversionQueueStatus
                {
                    Status = "failed",
                    Progress = null,
                    Message = errorMessage,
                    Updated = DateTime.UtcNow
                };
                ClearCancellation(downloadId);
            }
            PublishQueueUpdate();
        }

        public void Clear(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            lock (_cancellations)
            {
                _statuses.TryRemove(downloadId, out _);
                ClearCancellation(downloadId);
            }
            PublishQueueUpdate();
        }

        public ConversionQueueStatus Get(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return null;
            }

            PruneExpiredTerminalStatuses();

            return _statuses.TryGetValue(downloadId, out var status) ? status : null;
        }

        private static void ClearCancellation(string downloadId)
        {
            // Called under the cancellation gate. Detach only; source disposal belongs
            // to the owner after all tracker cancellation calls have finished.
            _cancellations.TryRemove(downloadId, out _);
        }

        private void PublishQueueUpdate()
        {
            _eventAggregator.PublishEvent(new QueueUpdatedEvent());
        }

        private static void PruneExpiredTerminalStatuses()
        {
            var now = DateTime.UtcNow;
            if (now - _lastPruneUtc < PruneInterval)
            {
                return;
            }

            _lastPruneUtc = now;
            foreach (var item in _statuses)
            {
                var status = item.Value;
                if (status == null ||
                    !IsTerminalStatus(status.Status) ||
                    now - status.Updated <= TerminalStatusRetention)
                {
                    continue;
                }

                _statuses.TryRemove(item.Key, out _);
            }
        }

        private static bool IsTerminalStatus(string status)
        {
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class ConversionQueueStatus
    {
        public string Status { get; set; }
        public int? TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public decimal? Progress { get; set; }
        public string Message { get; set; }
        public bool CanCancel { get; set; }
        public DateTime Updated { get; set; }
    }
}
