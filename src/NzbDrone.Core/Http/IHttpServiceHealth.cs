using System;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Http
{
    public enum HttpServiceResult
    {
        Response,
        Inconclusive,
        RateLimited,
        ConnectionFailure,
        Outage,
        AccessDenied,
        InvalidResponse
    }

    public interface IHttpServiceHealth
    {
        /// <summary>Classifies HTTP and transport failures; never-connected failures, including proxy tunnels,
        /// take precedence over attached statuses except rate limits. responseFailure may only specify
        /// AccessDenied or InvalidResponse for a service-specific response.</summary>
        HttpServiceResult ReportResult(string sourceName, HttpResponse response = null, Exception exception = null, bool serverErrorsAreOutages = false, string context = null, string label = null, string failureMessage = null, HttpServiceResult? responseFailure = null);
        HttpServiceStatus GetStatus(string sourceName);
        void ReportSuccess(string sourceName, string label = null);
        void ReportFailure(string sourceName, Exception exception);
        void ReportRateLimited(string sourceName, TimeSpan? retryAfter = null);
        void ReportInconclusive(string sourceName);
        void Reset(string sourceName);
        bool CanAttemptWithoutProbe(string sourceName, out TimeSpan retryAfter);
        bool CanBeginRequest(string sourceName, out TimeSpan retryAfter, string label = null);
        bool TryBeginRequest(string sourceName, out TimeSpan retryAfter, string label = null);
    }

    public class HttpServiceStatus
    {
        public string FailureContext { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public bool IsHealthy { get; set; }
        public DateTime? LastSuccess { get; set; }
        public DateTime? LastFailure { get; set; }
        public string LastErrorMessage { get; set; }
        public bool IsRateLimited { get; set; }
        public DateTime? RateLimitedUntil { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int EscalationLevel { get; set; }
        public bool ProbeInProgress { get; set; }
        public DateTime? ProbeStartedAt { get; set; }
    }
}
