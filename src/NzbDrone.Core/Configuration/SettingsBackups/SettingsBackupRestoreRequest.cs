using System.Collections.Generic;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupRestoreRequest
    {
        public string FilePath { get; set; }
        public string Passphrase { get; set; }
        public HashSet<SettingsBackupCategory> Categories { get; set; } = new();
        public SettingsBackupRestoreMode Mode { get; set; } = SettingsBackupRestoreMode.Overwrite;
    }
}

