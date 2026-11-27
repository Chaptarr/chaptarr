using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Http
{
    public class HttpServiceHealth : IHttpServiceHealth
    {
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, HttpServiceStatus> _serverStatuses;
        private readonly object _syncRoot = new object();
        private static readonly TimeSpan _probeTimeout = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan DefaultRateLimitRetryAfter = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan MaxRateLimitRetryAfter = TimeSpan.FromMinutes(15);

        private static readonly TimeSpan[] _serviceBackoff =
        {
            DefaultRateLimitRetryAfter,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            MaxRateLimitRetryAfter
        };

        public HttpServiceHealth(Logger logger)
        {
            _logger = logger;
            _serverStatuses = new ConcurrentDictionary<string, HttpServiceStatus>();
        }

        public HttpServiceResult ReportResult(string sourceName, HttpResponse response = null, Exception exception = null,
            bool serverErrorsAreOutages = false, string context = null, string label = null, string failureMessage = null, HttpServiceResult? responseFailure = null)
        {
            if (responseFailure.HasValue && responseFailure is not (HttpServiceResult.AccessDenied or HttpServiceResult.InvalidResponse))
            {
                throw new ArgumentOutOfRangeException(nameof(responseFailure));
            }
            lock (_syncRoot)
            {
                GetOrCreateStatus(sourceName).Label = label ?? sourceName;
                response ??= (exception as HttpException)?.Response;
                var statusCode = HttpTransportErrors.GetStatusCode(response, exception);
                var connectionFailure = HttpTransportErrors.IsConnectionNotEstablished(exception) || statusCode == HttpStatusCode.ProxyAuthenticationRequired;
                if (!responseFailure.HasValue && exception != null && (int?)statusCode is >= 200 and < 300)
                {
                    ReportInconclusive(sourceName);
                    return HttpServiceResult.Inconclusive;
                }
                HttpServiceResult result;
                if (exception is TooManyRequestsException || statusCode == HttpStatusCode.TooManyRequests)
                {
                    ReportRateLimited(sourceName, GetRetryAfter(response));
                    result = HttpServiceResult.RateLimited;
                }
                else if (responseFailure.HasValue || connectionFailure || (statusCode.HasValue ? HttpTransportErrors.IsUnavailableStatus(statusCode.Value) ||
                    (serverErrorsAreOutages && statusCode == HttpStatusCode.InternalServerError)
                    : HttpTransportErrors.IsTransientTransportException(exception)))
                {
                    ReportFailure(sourceName, failureMessage != null ? new WebException(failureMessage) : exception ?? new WebException(HttpTransportErrors.GetStatusMessage(response, exception)));
                    result = responseFailure ?? (connectionFailure
                        ? HttpServiceResult.ConnectionFailure : HttpServiceResult.Outage);
                }
                else
                {
                    if (statusCode.HasValue)
                    {
                        ReportSuccess(sourceName);
                    }
                    else
                    {
                        ReportInconclusive(sourceName);
                    }
                    return statusCode.HasValue ? HttpServiceResult.Response : HttpServiceResult.Inconclusive;
                }
                GetOrCreateStatus(sourceName).FailureContext = context;
                return result;
            }
        }

        public static bool OpensBackoff(HttpServiceResult result) => result is not (HttpServiceResult.Response or HttpServiceResult.Inconclusive);

        public static TimeSpan? GetRetryAfter(HttpResponse response)
        {
            var delay = RetryAfterParser.GetDelay(response);
            return delay.HasValue ? ClampRetryAfter(delay.Value) : null;
        }

        private static TimeSpan ClampRetryAfter(TimeSpan retryAfter)
        {
            return retryAfter > MaxRateLimitRetryAfter
                ? MaxRateLimitRetryAfter
                : retryAfter;
        }

        public static string FormatRetryAfter(TimeSpan retryAfter)
        {
            if (retryAfter.TotalHours >= 1)
            {
                return $"{retryAfter.TotalHours:0.#}h";
            }

            if (retryAfter.TotalMinutes >= 1)
            {
                return $"{retryAfter.TotalMinutes:0.#}m";
            }

            return $"{Math.Max(1, retryAfter.TotalSeconds):0}s";
        }

        public HttpServiceStatus GetStatus(string sourceName)
        {
            return GetOrCreateStatus(sourceName);
        }

        public void ReportSuccess(string sourceName, string label = null)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName, label);
                var wasUnavailable = !status.IsHealthy || status.IsRateLimited || status.ConsecutiveFailures > 0;

                status.LastSuccess = DateTime.UtcNow;
                status.ConsecutiveFailures = 0;
                status.EscalationLevel = 0;
                status.IsHealthy = true;
                status.IsRateLimited = false;
                status.RateLimitedUntil = null;
                status.LastErrorMessage = null;
                status.FailureContext = null;
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;

                if (wasUnavailable)
                {
                    _logger.Info("{0} reported success and is available again", status.Label ?? sourceName);
                }
                else
                {
                    _logger.Debug("{0} reported success", status.Label ?? sourceName);
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
                status.LastErrorMessage = HttpTransportErrors.GetFailureReason(exception);
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;

                OpenCircuit(status, sourceName, status.LastErrorMessage);
            }
        }

        public void ReportRateLimited(string sourceName, TimeSpan? retryAfter = null)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);
                var backoff = retryAfter ?? DefaultRateLimitRetryAfter;

                // Bound direct callers as well as parsed HTTP responses.
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

                _logger.Info("{0} is rate limited until {1}",
                             status.Label ?? sourceName,
                             status.RateLimitedUntil?.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture));
            }
        }

        // Releases the service probe without an owner check; callers report only their completed request.
        public void ReportInconclusive(string sourceName)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName);
                status.ProbeInProgress = false;
                status.ProbeStartedAt = null;
            }
        }

        public bool TryBeginRequest(string sourceName, out TimeSpan retryAfter, string label = null)
        {
            lock (_syncRoot)
            {
                var status = GetOrCreateStatus(sourceName, label);
                if (!CanBeginRequest(status, out retryAfter))
                {
                    return false;
                }

                if (!status.IsHealthy || status.IsRateLimited)
                {
                    status.ProbeInProgress = true;
                    status.ProbeStartedAt = DateTime.UtcNow;
                    _logger.Debug("{0} cooldown expired; allowing one recovery probe", status.Label ?? sourceName);
                }
                return true;
            }
        }

        // A scheduling check may avoid local preparation, but must not reserve the
        // probe. Only the actual HTTP request claims it and reports its outcome.
        public bool CanBeginRequest(string sourceName, out TimeSpan retryAfter, string label = null)
        {
            lock (_syncRoot)
            {
                return CanBeginRequest(GetOrCreateStatus(sourceName, label), out retryAfter);
            }
        }

        private static bool CanBeginRequest(HttpServiceStatus status, out TimeSpan retryAfter)
        {
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
            if (status.ProbeInProgress && status.ProbeStartedAt.HasValue && now - status.ProbeStartedAt.Value < _probeTimeout)
            {
                retryAfter = TimeSpan.FromSeconds(30);
                return false;
            }
            return true;
        }

        public bool CanAttemptWithoutProbe(string sourceName, out TimeSpan retryAfter)
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
                }

                // Optional callers must never claim the single half-open probe.
                // A load-bearing metadata request owns recovery and reports its result.
                return false;
            }
        }

        public void Reset(string sourceName)
        {
            lock (_syncRoot)
            {
                if (_serverStatuses.TryRemove(sourceName, out var status))
                {
                    _logger.Info("{0} circuit manually reset; the next request will re-test availability", status.Label ?? sourceName);
                }
            }
        }

        private HttpServiceStatus GetOrCreateStatus(string sourceName, string label = null)
        {
            var status = _serverStatuses.GetOrAdd(sourceName, name => new HttpServiceStatus
            {
                Name = name,
                IsHealthy = true,
                ConsecutiveFailures = 0
            });
            if (label != null)
            {
                status.Label = label;
            }
            return status;
        }

        private void OpenCircuit(HttpServiceStatus status, string sourceName, string message)
        {
            status.IsHealthy = false;
            status.IsRateLimited = false;
            status.EscalationLevel = Math.Min(status.EscalationLevel + 1, _serviceBackoff.Length);

            var retryAfter = _serviceBackoff[status.EscalationLevel - 1];
            status.RateLimitedUntil = DateTime.UtcNow.Add(retryAfter);

            _logger.Warn("{0} is unavailable after {1} consecutive failure(s). Retrying in {2}. Last error: {3}",
                         status.Label ?? sourceName,
                         status.ConsecutiveFailures,
                         FormatRetryAfter(retryAfter),
                         message);
        }
    }
}
