using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Notifications.Plex.PlexTv;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class ExternalAuthenticationSecurityFixture
    {
        private static readonly MethodInfo MisconfiguredSchemeMethod = typeof(AuthenticationBuilderExtensions).GetMethod(
            "GetMisconfiguredAuthenticationScheme",
            BindingFlags.NonPublic | BindingFlags.Static);

        private class ConfigFileProviderProxy : DispatchProxy
        {
            public AuthenticationType AuthenticationMethod { get; set; } = AuthenticationType.Plex;
            public AuthenticationType ConfiguredAuthenticationMethod { get; set; } = AuthenticationType.Plex;
            public bool AuthenticationMethodIsMisconfigured { get; set; }
            public string PlexAuthUserId { get; set; } = string.Empty;
            public Queue<string> PlexAuthUserIdResponses { get; } = new Queue<string>();
            public bool TrustCgnatIpAddresses { get; set; }
            public string UrlBase { get; set; } = string.Empty;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_AuthenticationMethod" => AuthenticationMethod,
                    "get_ConfiguredAuthenticationMethod" => ConfiguredAuthenticationMethod,
                    "get_AuthenticationMethodIsMisconfigured" => AuthenticationMethodIsMisconfigured,
                    "get_PlexAuthUserId" => PlexAuthUserIdResponses.Count > 0 ? PlexAuthUserIdResponses.Dequeue() : PlexAuthUserId,
                    "get_PlexAuthUsername" => string.Empty,
                    "get_TrustCgnatIpAddresses" => TrustCgnatIpAddresses,
                    "get_UrlBase" => UrlBase,
                    "get_InstanceName" => "Chaptarr",
                    "get_AllowedHosts" => "chaptarr.example",
                    "get_OidcAllowedEmails" => string.Empty,
                    "get_OidcAllowedEmailDomains" => string.Empty,
                    "get_OidcAllowAnyVerifiedUser" => false,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            public static IConfigFileProvider Create()
            {
                return DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            }
        }

        private class PlexTvServiceStub : IPlexTvService
        {
            public PlexTvUserResponse User { get; set; } = new PlexTvUserResponse { Id = 5678, Username = "second-user" };

            public string CallbackUrl { get; private set; }

            public PlexTvPinResponse CreatePin() => new PlexTvPinResponse { Id = 10, Code = "pin-code" };
            public PlexTvSignInUrlResponse GetSignInUrl(string callbackUrl, int pinId, string pinCode)
            {
                CallbackUrl = callbackUrl;
                return new PlexTvSignInUrlResponse { OauthUrl = "https://app.plex.tv/auth" };
            }
            public string GetAuthToken(int pinId) => "plex-token";
            public PlexTvUserResponse GetUser(string authToken) => User;
            public List<PlexTvResourceResponse> GetResources(string authToken) => throw new NotImplementedException();
            public void Ping(string authToken) => throw new NotImplementedException();
        }

        [Test]
        public void unbound_plex_login_should_be_denied_from_remote_networks()
        {
            var controller = CreatePlexController(ConfigFileProviderProxy.Create(), IPAddress.Parse("203.0.113.20"));

            var start = controller.Start() as ObjectResult;
            var callback = controller.Callback("state").GetAwaiter().GetResult() as ObjectResult;

            Assert.That(start?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(callback?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(start?.Value?.ToString(), Does.Contain("localhost or a trusted local network"));
            Assert.That(callback?.Value?.ToString(), Does.Contain("localhost or a trusted local network"));
        }

        [Test]
        public void unbound_plex_login_should_allow_local_binding_flow()
        {
            var controller = CreatePlexController(ConfigFileProviderProxy.Create(), IPAddress.Parse("10.0.0.10"));

            var result = controller.Start();

            Assert.That(result, Is.TypeOf<RedirectResult>());
        }

        [TestCase("X-Forwarded-For", "127.0.0.1")]
        [TestCase("X-Forwarded-Host", "localhost")]
        [TestCase("X-Forwarded-Proto", "https")]
        [TestCase("Forwarded", "for=127.0.0.1")]
        public async Task first_plex_binding_allows_local_forwarders(string header, string value)
        {
            var controller = CreatePlexController(ConfigFileProviderProxy.Create(), IPAddress.Parse("10.0.0.10"));
            controller.Request.Headers[header] = value;
            Assert.That(controller.Start(), Is.TypeOf<RedirectResult>());
            Assert.That((await controller.Callback("state") as ObjectResult)?.StatusCode, Is.Not.EqualTo(403));
        }

        [TestCase("X-Forwarded-For")]
        [TestCase("X-Real-IP")]
        public async Task public_clients_behind_a_local_proxy_cannot_bind_plex_or_recover_oidc(string header)
        {
            var config = ConfigFileProviderProxy.Create();
            var controller = CreatePlexController(config, IPAddress.Parse("10.0.0.10"));
            controller.Request.Headers[header] = "203.0.113.9";
            Assert.That((controller.Start() as ObjectResult)?.StatusCode, Is.EqualTo(403));
            Assert.That((await controller.Callback("state") as ObjectResult)?.StatusCode, Is.EqualTo(403));

            var state = (ConfigFileProviderProxy)(object)config;
            state.ConfiguredAuthenticationMethod = AuthenticationType.Oidc;
            state.AuthenticationMethod = AuthenticationType.None;
            state.AuthenticationMethodIsMisconfigured = true;
            Assert.That(GetMisconfiguredScheme(controller.HttpContext, config), Is.EqualTo("API"));
        }

        [Test]
        public void already_bound_plex_login_should_remain_available_remotely()
        {
            var config = ConfigFileProviderProxy.Create();
            ((ConfigFileProviderProxy)(object)config).PlexAuthUserId = "1234";
            var controller = CreatePlexController(config, IPAddress.Parse("203.0.113.20"));

            var result = controller.Start();

            Assert.That(result, Is.TypeOf<RedirectResult>());
        }

        [Test]
        public async Task concurrent_plex_first_bind_loser_should_be_rejected_immediately()
        {
            var config = ConfigFileProviderProxy.Create();
            var state = (ConfigFileProviderProxy)(object)config;
            state.PlexAuthUserIdResponses.Enqueue(string.Empty);
            state.PlexAuthUserIdResponses.Enqueue(string.Empty);
            state.PlexAuthUserIdResponses.Enqueue(string.Empty);
            state.PlexAuthUserIdResponses.Enqueue("1234");

            var controller = CreatePlexController(config, IPAddress.Parse("10.0.0.10"));
            Assert.That(controller.Start(), Is.TypeOf<RedirectResult>());

            var setCookie = controller.Response.Headers.SetCookie.ToString();
            var cookiePair = setCookie.Split(';')[0];
            var callbackState = cookiePair.Substring(cookiePair.IndexOf('=') + 1);
            controller.Request.Headers.Cookie = cookiePair;

            var result = await controller.Callback(callbackState);

            Assert.That(result, Is.TypeOf<ForbidResult>());
        }

        [Test]
        public void incomplete_oidc_should_allow_local_recovery_and_require_api_authentication_remotely()
        {
            var config = ConfigFileProviderProxy.Create();
            var state = (ConfigFileProviderProxy)(object)config;
            state.ConfiguredAuthenticationMethod = AuthenticationType.Oidc;
            state.AuthenticationMethod = AuthenticationType.None;
            state.AuthenticationMethodIsMisconfigured = true;

            var localScheme = GetMisconfiguredScheme(CreateHttpContext("10.0.0.10"), config);
            var remoteScheme = GetMisconfiguredScheme(CreateHttpContext("203.0.113.20"), config);

            Assert.That(localScheme, Is.EqualTo(AuthenticationType.None.ToString()));
            Assert.That(remoteScheme, Is.EqualTo("API"));
        }

        [Test]
        public void incomplete_oidc_should_raise_an_error_health_check()
        {
            var config = ConfigFileProviderProxy.Create();
            var state = (ConfigFileProviderProxy)(object)config;
            state.ConfiguredAuthenticationMethod = AuthenticationType.Oidc;
            state.AuthenticationMethod = AuthenticationType.None;
            state.AuthenticationMethodIsMisconfigured = true;

            var result = new OidcSecurityCheck(config, null).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Error));
        }

        [TestCase("https", "public.example", "/chaptarr")]
        [TestCase("https", "public.example:8443", "")]
        [TestCase("http", "localhost:8787", "/chaptarr")]
        public void plex_callback_returns_to_the_origin_that_received_the_state_cookie(string scheme, string host, string urlBase)
        {
            var config = ConfigFileProviderProxy.Create();
            ((ConfigFileProviderProxy)(object)config).UrlBase = urlBase;
            ((ConfigFileProviderProxy)(object)config).PlexAuthUserId = "1234";
            var plex = new PlexTvServiceStub();
            var controller = CreatePlexController(config, IPAddress.Loopback, plex);
            controller.Request.Host = new HostString(host);
            controller.Request.Scheme = scheme;
            Assert.That(controller.Start(), Is.TypeOf<RedirectResult>());
            Assert.That(plex.CallbackUrl, Does.StartWith($"{scheme}://{host}{urlBase}/auth/plex/callback?state="));
            Assert.That(new Uri(plex.CallbackUrl).Query.Length, Is.GreaterThan("?state=".Length));
            Assert.That(controller.Response.Headers.SetCookie.ToString(), Does.Not.Contain("domain="));
        }

        private static PlexAuthenticationController CreatePlexController(IConfigFileProvider config, IPAddress remoteIpAddress, PlexTvServiceStub plex = null)
        {
            var controller = new PlexAuthenticationController(
                plex ?? new PlexTvServiceStub(),
                config,
                new CacheManager(),
                LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = CreateHttpContext(remoteIpAddress.ToString())
                }
            };

            controller.HttpContext.Request.Scheme = "https";
            controller.HttpContext.Request.Host = new HostString("chaptarr.example");
            return controller;
        }

        private static DefaultHttpContext CreateHttpContext(string remoteIpAddress)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString("localhost");
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIpAddress);
            return context;
        }

        private static string GetMisconfiguredScheme(HttpContext context, IConfigFileProvider config)
        {
            Assert.That(MisconfiguredSchemeMethod, Is.Not.Null);
            return (string)MisconfiguredSchemeMethod.Invoke(null, new object[] { context, config });
        }
    }
}
