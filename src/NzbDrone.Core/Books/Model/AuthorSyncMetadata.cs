using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class AuthorSyncMetadata : ModelBase
    {
        public int AuthorId { get; set; }

        // External ID like "hc:123" for matching with API responses (max 128 chars)
        public string ExternalAuthorId { get; set; }

        // Opaque ETag from the metadata server response header (max 128 chars).
        public string ETag { get; set; }

        // ISO timestamp from server response body (for future bulk sync)
        public string ServerVersion { get; set; }

        // Stored as UTC DateTime to match the app-wide datastore mapper.
        public DateTime? LastSyncAttempt { get; set; }
        public DateTime? LastSuccessfulSync { get; set; }

        // Sync status tracking
        public SyncStatus LastSyncStatus { get; set; }
        public int? LastHttpStatus { get; set; }
        public int SyncFailureCount { get; set; }
        public string LastError { get; set; } // Max 1024 chars
        public int LastSyncDurationMs { get; set; }
        public DateTime? NextSyncNotBefore { get; set; }

        // Navigation property
        public Author Author { get; set; }
    }

    public enum SyncStatus
    {
        Unknown = 0,
        Success = 1,
        NotModified = 2,
        Failed = 3,
        RateLimited = 4
    }
}
