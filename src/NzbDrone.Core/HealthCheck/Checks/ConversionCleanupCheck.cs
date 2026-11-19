using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(TrackedDownloadRefreshedEvent))]
    [CheckOn(typeof(TrackedDownloadsRemovedEvent))]
    [CheckOn(typeof(DownloadCompletedEvent))]
    [CheckOn(typeof(DownloadFailedEvent))]
    [CheckOn(typeof(DownloadIgnoredEvent))]
    [CheckOn(typeof(ProviderDeletedEvent<IDownloadClient>))]
    public class ConversionCleanupCheck : HealthCheckBase
    {
        private readonly IConversionJobRepository _jobs;
        public ConversionCleanupCheck(IConversionJobRepository jobs, ILocalizationService localizationService)
            : base(localizationService) => _jobs = jobs;

        public override HealthCheck Check()
        {
            var warnings = new List<string>();
            foreach (var job in _jobs.CleanupWarnings())
            {
                ConversionJobRequest request;
                try { request = JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson); }
                catch (System.Exception ex) when (ex is JsonException or System.ArgumentNullException) { request = null; }
                warnings.Add(_localizationService.GetLocalizedString("ConversionCleanupCheckMessage", new Dictionary<string, object>
                {
                    ["title"] = request?.BookTitle ?? job.DownloadId,
                    ["path"] = job.WorkFolder,
                    ["reason"] = request?.CleanupError ?? _localizationService.GetLocalizedString(request?.HistoryIdentityAmbiguous == true ? "ConversionDownloadIdentityAmbiguous" : "ConversionRequestUnreadable")
                }));
            }
            return warnings.Count == 0 ? new HealthCheck(GetType()) :
                new HealthCheck(GetType(), HealthCheckResult.Warning, FormatFailures(warnings, _jobs.CleanupWarningCount()));
        }
    }
}
