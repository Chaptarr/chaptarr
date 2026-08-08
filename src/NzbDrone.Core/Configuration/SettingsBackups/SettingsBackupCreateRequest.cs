using System.Collections.Generic;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupCreateRequest
    {
        public string RootFolder { get; set; }
        public string FileName { get; set; }
        public string Passphrase { get; set; }
        public HashSet<SettingsBackupCategory> Categories { get; set; } = new();
        public bool OverwriteExistingFile { get; set; }
    }
}

