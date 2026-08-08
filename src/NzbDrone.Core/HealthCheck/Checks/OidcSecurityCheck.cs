using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ConfigFileSavedEvent))]
    public class OidcSecurityCheck : HealthCheckBase
    {
        private readonly IConfigFileProvider _configFileProvider;

        public OidcSecurityCheck(IConfigFileProvider configFileProvider, ILocalizationService localizationService)
            : base(localizationService)
        {
            _configFileProvider = configFileProvider;
        }

        public override HealthCheck Check()
        {
            if (_configFileProvider.ConfiguredAuthenticationMethod != AuthenticationType.Oidc)
            {
                return new HealthCheck(GetType());
            }

            if (_configFileProvider.AuthenticationMethodIsMisconfigured)
            {
                return new HealthCheck(
                    GetType(),
                    HealthCheckResult.Error,
                    "OIDC authentication is incomplete. Remote UI access is blocked until the authority, client ID, and client secret are configured.",
                    "#oidc-configuration-incomplete");
            }

            if (_configFileProvider.OidcAllowedEmails.IsNullOrWhiteSpace() &&
                _configFileProvider.OidcAllowedEmailDomains.IsNullOrWhiteSpace() &&
                !_configFileProvider.OidcAllowAnyVerifiedUser)
            {
                return new HealthCheck(
                    GetType(),
                    HealthCheckResult.Warning,
                    "OIDC is enabled without an email or domain allow-list. Configure allowed emails/domains or explicitly enable Allow Any Verified OIDC User.",
                    "#oidc-allowlist-missing");
            }

            return new HealthCheck(GetType());
        }
    }
}
