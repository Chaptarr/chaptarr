using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListSyncHistory : ModelBase
    {
        public int ImportListId { get; set; }
        public string ImportListName { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public long DurationMilliseconds { get; set; }
        public string Result { get; set; }
        public string FailureStage { get; set; }
        public bool DryRun { get; set; }
        public int ProcessedCount { get; set; }
        public int NewCount { get; set; }
        public int ExistingCount { get; set; }
        public int ChangedCount { get; set; }
        public int ExcludedCount { get; set; }
        public int UnresolvedCount { get; set; }
        public int SearchEligibleCount { get; set; }
        public bool ProviderStateCommitted { get; set; }
    }

    public class ImportListSyncCounts
    {
        public int ProcessedCount { get; set; }
        public int NewCount { get; set; }
        public int ExistingCount { get; set; }
        public int ChangedCount { get; set; }
        public int ExcludedCount { get; set; }
        public int UnresolvedCount { get; set; }
        public int SearchEligibleCount { get; set; }
    }
}
