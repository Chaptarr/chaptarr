using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Backup
{
    public class BackupStatus
    {
        internal ScheduledBackupFailure ScheduledBackupFailure { get; set; }
        public bool PendingRestore { get; set; }
        public bool CanCancelRestore { get; set; }
        public bool RestoreFailed { get; set; }
        public bool ScheduleDisabled { get; set; }
        public string DatabaseBackupUnavailableReason { get; set; }
    }

    internal class ScheduledBackupFailure
    {
        public DateTime FailedAt { get; set; }
        public string Reason { get; set; }
    }

    public class Backup
    {
        public string Name { get; set; }
        public BackupType Type { get; set; }
        public long Size { get; set; }
        public DateTime Time { get; set; }
        public List<string> IncludedComponents { get; set; } = new();
        public List<string> OmittedComponents { get; set; } = new();
    }
}
