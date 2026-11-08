using System;
using System.Linq;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(DownloadCleanupStatusChangedEvent))]
    [CheckOn(typeof(TrackedDownloadsRemovedEvent), CheckOnCondition.FailedOnly)]
    public class DownloadCleanupCheck : HealthCheckBase
    {
        private readonly ITrackedDownloadService _trackedDownloads;

        public DownloadCleanupCheck(ITrackedDownloadService trackedDownloads, ILocalizationService localizationService)
            : base(localizationService)
        {
            _trackedDownloads = trackedDownloads;
        }

        public override HealthCheck Check()
        {
            // Reuse the cleanup pass's evidence. A health check must not poll the client
            // or retry the same inaccessible filesystem independently.
            var failures = _trackedDownloads.GetTrackedDownloads()
                .Where(download => download.CleanupCheckFailed &&
                    download.State == TrackedDownloadState.Imported && !download.DownloadItem.Removed)
                .OrderBy(download => download.DownloadItem.Title, StringComparer.Ordinal)
                .ToList();
            if (failures.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            var details = FormatFailures(failures.Select(download =>
                $"{download.DownloadItem.Title} ({(download.ImportItem ?? download.DownloadItem).OutputPath}): " +
                string.Join("; ", download.StatusMessages.SelectMany(message => message.Messages))).ToList());

            return new HealthCheck(GetType(), HealthCheckResult.Warning,
                string.Format(_localizationService.GetLocalizedString("DownloadCleanupCheckWarning"), details));
        }
    }
}
