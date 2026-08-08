using System.Collections.Generic;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupResult
    {
        public string Path { get; set; }
        public Dictionary<string, int> Counts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}

