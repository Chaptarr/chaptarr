using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ApplicationStartedEvent))]
    [CheckOn(typeof(ConfigFileSavedEvent))]
    public class ApiKeySetupCheck : HealthCheckBase
    {
        private readonly IConfigFileProvider _config;

        public ApiKeySetupCheck(IConfigFileProvider config, ILocalizationService localizationService)
            : base(localizationService)
        {
            _config = config;
        }

        public override HealthCheck Check() => _config.ApiKeyNeedsManualRotation
            ? new HealthCheck(GetType(), HealthCheckResult.Warning,
                _localizationService.GetLocalizedString("ApiKeyPinnedAfterSetupHealthCheckMessage"))
            : new HealthCheck(GetType());
    }
}
