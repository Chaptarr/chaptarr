namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupLocation
    {
        public string Path { get; set; }
        public bool Exists { get; set; }
        public bool Writable { get; set; }
        public string Warning { get; set; }
    }
}

