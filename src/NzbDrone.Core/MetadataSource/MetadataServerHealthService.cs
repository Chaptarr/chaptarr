using System;
using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Core.MetadataSource
{
    public class MetadataServerHealthService : IMetadataServerHealthService
    {
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, MetadataServerStatus> _serverStatuses;
        private readonly object _syncRoot = new object();
        private static readonly TimeSpan _probeTimeout = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan DefaultRateLimitRetryAfter = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan MaxRateLimitRetryAfter = TimeSpan.FromMinutes(15);

        private static readonly TimeSpan[] _metadataServerBackoff =
        {
            DefaultRateLimitRetryAfter,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            MaxRateLimitRetryAfter
        };

        public MetadataServerHealthService(Logger logger)
        {
            _logger = logger;
            _serverStatuses = new ConcurrentDictionary<string, MetadataServerStatus>();
        }

        public MetadataServerStatus GetStatus(string sourceName)
        {
            return GetOrCreateStatus(sourceName);
        }

        public void ReportSuccess(string sourceName)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);
                var wasUnavailable = !status.IsHealthy || status.IsRateLimited || status.ConsecutiveFailures > 0;

                status.LastSuccess = DateTime.UtcNow;
                status.ConsecutiveFailures = 0;
                status.EscalationLevel = 0;
                status.IsHealthy = true;
                status.IsRateLimited = false;
                status.RateLimitedUntil = null;
                status.LastErrorMessage = null;
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;

                if (wasUnavailable)
                {
                    _logger.Info("Metadata server {0} reported success and is available again", sourceName);
                }
                else
                {
                    _logger.Debug("Metadata server {0} reported success", sourceName);
                }
            }
        }

        public void ReportFailure(string sourceName, Exception exception)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);

                status.LastFailure = DateTime.UtcNow;
                status.ConsecutiveFailures++;
                status.LastErrorMessage = exception?.Message;
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;

                OpenCircuit(status, sourceName, exception?.Message);
            }
        }

        public void ReportRateLimited(string sourceName, TimeSpan? retryAfter = null)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);
                var backoff = retryAfter ?? DefaultRateLimitRetryAfter;

                // Clamp at the sink: TooManyRequestsException parses Retry-After itself and
                // bypasses MetadataServerHealthGate.GetRetryAfter, so bound the window here too.
                if (backoff > MaxRateLimitRetryAfter)
                {
                    backoff = MaxRateLimitRetryAfter;
                }

                status.IsRateLimited = true;
                status.IsHealthy = false;
                status.RateLimitedUntil = DateTime.UtcNow.Add(backoff);
                status.LastFailure = DateTime.UtcNow;
                status.LastErrorMessage = "Rate limited";
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;

                _logger.Info("Metadata server {0} is rate limited until {1}",
                             sourceName,
                             status.RateLimitedUntil?.ToString("yyyy-MM-dd HH:mm:ss UTC"));
            }
        }

        public void ReportInconclusive(string sourceName)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;
            }
        }

        public bool TryBeginRequest(string sourceName, out TimeSpan retryAfter)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);
                var now = DateTime.UtcNow;
                retryAfter = TimeSpan.Zero;

                if (status.IsHealthy && !status.IsRateLimited)
                {
                    return true;
                }

                if (status.RateLimitedUntil.HasValue && status.RateLimitedUntil.Value > now)
                {
                    retryAfter = status.RateLimitedUntil.Value - now;
                    return false;
                }

                if (status.ProbeInProgress &&
                    status.ProbeStartedAt.HasValue &&
                    now - status.ProbeStartedAt.Value < _probeTimeout)
                {
                    retryAfter = TimeSpan.FromSeconds(30);
                    return false;
                }

                status.ProbeInProgress = true;
                status.ProbeStartedAt = now;
                _logger.Debug("Metadata server {0} cooldown expired; allowing one recovery probe", sourceName);
                return true;
            }
        }

        public void Reset(string sourceName)
        {
            lock (_syncRoot)
            {
                if (_serverStatuses.TryRemove(sourceName, out _))
                {
                    _logger.Info("Metadata server {0} circuit manually reset; the next request will re-test availability", sourceName);
                }
            }
        }

        private MetadataServerStatus GetOrCreateStatus(string sourceName)
        {
            return _serverStatuses.GetOrAdd(sourceName, name => new MetadataServerStatus
            {
                Name = name,
                IsHealthy = true,
                ConsecutiveFailures = 0
            });
        }

        private void OpenCircuit(MetadataServerStatus status, string sourceName, string message)
        {
            status.IsHealthy = false;
            status.IsRateLimited = false;
            status.EscalationLevel = Math.Min(status.EscalationLevel + 1, _metadataServerBackoff.Length);

            var retryAfter = _metadataServerBackoff[status.EscalationLevel - 1];
            status.RateLimitedUntil = DateTime.UtcNow.Add(retryAfter);

            _logger.Warn("Metadata server {0} is unavailable after {1} consecutive failure(s). Retrying in {2}. Last error: {3}",
                         sourceName,
                         status.ConsecutiveFailures,
                         MetadataServerHealthGate.FormatRetryAfter(retryAfter),
                         message);
        }
    }
}
