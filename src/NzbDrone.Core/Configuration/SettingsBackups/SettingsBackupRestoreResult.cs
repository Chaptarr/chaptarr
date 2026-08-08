using System.Collections.Generic;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupRestoreResult
    {
        public List<string> Applied { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}

