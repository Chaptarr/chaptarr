using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public enum SyncQueueStatus
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3
    }

    public class AuthorSyncQueue : ModelBase
    {
        public string PrefixedAuthorId { get; set; }
        public SyncQueueStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public string LastError { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }

        // Helper methods
        public bool IsActive()
        {
            return Status == SyncQueueStatus.Pending || Status == SyncQueueStatus.Processing;
        }

        public bool IsComplete()
        {
            return Status == SyncQueueStatus.Completed || Status == SyncQueueStatus.Failed;
        }

        public bool ShouldRetry()
        {
            return Status == SyncQueueStatus.Failed && AttemptCount < 3;
        }
    }
}