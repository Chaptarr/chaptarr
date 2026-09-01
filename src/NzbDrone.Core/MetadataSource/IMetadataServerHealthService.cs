using System;

namespace NzbDrone.Core.MetadataSource
{
    public interface IMetadataServerHealthService
    {
        MetadataServerStatus GetStatus(string sourceName);
        void ReportSuccess(string sourceName);
        void ReportFailure(string sourceName, Exception exception);
        void ReportRateLimited(string sourceName, TimeSpan? retryAfter = null);
        void ReportInconclusive(string sourceName);
        void Reset(string sourceName);
        bool CanAttemptWithoutProbe(string sourceName, out TimeSpan retryAfter);
        bool TryBeginRequest(string sourceName, out TimeSpan retryAfter);
    }

    public class MetadataServerStatus
    {
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
