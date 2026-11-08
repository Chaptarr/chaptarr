using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ApplicationStartedEvent))]
    [CheckOn(typeof(ConfigFileSavedEvent))]
    public class AllowedHostsCheck : HealthCheckBase
    {
        private readonly IConfigFileProvider _config;

        public AllowedHostsCheck(IConfigFileProvider config, ILocalizationService localizationService)
            : base(localizationService)
        {
            _config = config;
        }

        public override HealthCheck Check()
        {
            if (AllowedHostsParser.IsUnrestricted(_config.AllowedHosts) && (_config.AuthenticationMethod == AuthenticationType.None ||
                                 _config.AuthenticationMethod == AuthenticationType.External ||
                                 _config.AuthenticationRequired == AuthenticationRequiredType.DisabledForLocalAddresses))
            {
                return new HealthCheck(GetType(), HealthCheckResult.Warning,
                    _localizationService.GetLocalizedString("AllowedHostsNotConfiguredHealthCheckMessage"), "#allowed-hosts-not-configured");
            }

            return new HealthCheck(GetType());
        }
    }
}
