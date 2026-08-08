using System;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupFileInfo
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }
}

