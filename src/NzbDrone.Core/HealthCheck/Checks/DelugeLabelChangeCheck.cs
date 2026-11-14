using System.Linq;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(TrackedDownloadRefreshedEvent))]
    [CheckOn(typeof(ProviderUpdatedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderDeletedEvent<IDownloadClient>))]
    public class DelugeLabelChangeCheck : HealthCheckBase
    {
        private readonly IDownloadClientFactory _clients;

        public DelugeLabelChangeCheck(IDownloadClientFactory clients, ILocalizationService localizationService)
            : base(localizationService)
        {
            _clients = clients;
        }

        public override HealthCheck Check()
        {
            var failures = _clients.GetAvailableProviders().OfType<Deluge>()
                .SelectMany(client => client.GetLabelChangeFailures().Select(failure =>
                    $"{client.Definition.Name} — {failure.Name}: {failure.Reason}"))
                .OrderBy(message => message).ToList();
            if (failures.Count == 0) return new HealthCheck(GetType());

            var details = FormatFailures(failures);
            return new HealthCheck(GetType(), HealthCheckResult.Warning,
                string.Format(_localizationService.GetLocalizedString("DelugeLabelChangeWarning"), details));
        }
    }
}
