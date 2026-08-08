using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Http.Authentication
{
    public class OidcOptionsCacheInvalidator : IHandle<ConfigFileSavedEvent>,
                                               IHandle<ConfigSavedEvent>,
                                               IHandle<ProxyUpdatedEvent>,
                                               IHandle<ProxyDeletedEvent>
    {
        private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptionsCache;

        public OidcOptionsCacheInvalidator(IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache)
        {
            _oidcOptionsCache = oidcOptionsCache;
        }

        public void Handle(ConfigFileSavedEvent message)
        {
            Invalidate();
        }

        public void Handle(ConfigSavedEvent message)
        {
            Invalidate();
        }

        public void Handle(ProxyUpdatedEvent message)
        {
            Invalidate();
        }

        public void Handle(ProxyDeletedEvent message)
        {
            Invalidate();
        }

        private void Invalidate()
        {
            _oidcOptionsCache.TryRemove(AuthenticationBuilderExtensions.OidcOpenIdConnectScheme);
        }
    }
}
