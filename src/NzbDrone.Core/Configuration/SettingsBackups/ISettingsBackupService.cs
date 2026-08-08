using System.Collections.Generic;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public interface ISettingsBackupService
    {
        List<SettingsBackupLocation> GetLocations();
        List<SettingsBackupFileInfo> GetFiles(string rootFolder);
        SettingsBackupResult CreateBackup(SettingsBackupCreateRequest request);
        SettingsBackupRestoreResult RestoreBackup(SettingsBackupRestoreRequest request);
    }
}

