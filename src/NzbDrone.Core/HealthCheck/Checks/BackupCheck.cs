using NzbDrone.Core.Backup;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(BackupRestoreStateChangedEvent))]
    public class BackupCheck : HealthCheckBase
    {
        private readonly IBackupService _backupService;

        public BackupCheck(IBackupService backupService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _backupService = backupService;
        }

        public override HealthCheck Check()
        {
            var status = _backupService.GetStatus();
            var key = status.RestoreFailed ? "BackupRestoreFailedWarning" :
                status.PendingRestore ? "BackupRestorePendingWarning" :
                status.DatabaseBackupUnavailableReason != null ? "BackupToolsUnavailableWarning" : null;
            if (key == null && status.ScheduledBackupFailure is { } failure)
                return new HealthCheck(GetType(), HealthCheckResult.Warning,
                    _localizationService.GetLocalizedString("ScheduledBackupFailedWarning", new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["time"] = failure.FailedAt.ToString("u"), ["reason"] = failure.Reason
                    }));
            return key == null ? new HealthCheck(GetType()) :
                new HealthCheck(GetType(), HealthCheckResult.Warning, _localizationService.GetLocalizedString(key));
        }
    }
}
