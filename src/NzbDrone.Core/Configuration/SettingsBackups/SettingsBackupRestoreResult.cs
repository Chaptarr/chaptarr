using System.Collections.Generic;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupRestoreResult
    {
        public List<string> WouldRemove { get; set; } = new();
        internal HashSet<RemotePathMappingBackup> SkippedRemotePathMappings { get; } = new();
        public List<string> Applied { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}

