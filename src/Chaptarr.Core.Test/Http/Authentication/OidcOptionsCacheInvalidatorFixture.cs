using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class OidcOptionsCacheInvalidatorFixture
    {
        private const string Scheme = "OidcOpenIdConnect";

        [Test]
        public void all_proxy_configuration_events_should_invalidate_cached_oidc_transport()
        {
            var cache = new OptionsCache<OpenIdConnectOptions>();
            var subject = new OidcOptionsCacheInvalidator(cache);

            AssertInvalidated(cache, () => subject.Handle(new ConfigFileSavedEvent()));
            AssertInvalidated(cache, () => subject.Handle(new ConfigSavedEvent()));
            AssertInvalidated(cache, () => subject.Handle(new ProxyUpdatedEvent(new ProxyDefinition { Id = 1 })));
            AssertInvalidated(cache, () => subject.Handle(new ProxyDeletedEvent(new ProxyDefinition { Id = 1 })));
        }

        private static void AssertInvalidated(IOptionsMonitorCache<OpenIdConnectOptions> cache, System.Action invalidate)
        {
            cache.TryAdd(Scheme, new OpenIdConnectOptions());
            invalidate();
            Assert.That(cache.TryAdd(Scheme, new OpenIdConnectOptions()), Is.True);
        }
    }
}
