using System;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using AspNetAuthenticationService = Microsoft.AspNetCore.Authentication.IAuthenticationService;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class OidcPrincipalValidationFixture
    {
        [TestCase("alice@example.com", "true", "alice@example.com", "example.com", true, false, null)]
        [TestCase("alice@example.com", "1", "", "example.com", true, false, null)]
        [TestCase("alice@example.com", "True", "", "example.com", true, false, null)]
        [TestCase("alice@example.com", "0", "", "example.com", true, false, "oidc_email_not_verified")]
        [TestCase("alice@example.com", "yes", "", "example.com", true, false, "oidc_email_not_verified")]
        [TestCase("alice@example.com", "true", "", "@example.com", true, false, "oidc_domain_not_allowed")]
        [TestCase("alice@example.com", null, "alice@example.com", "", false, false, null)]
        [TestCase("alice@example.com", "true", "", "", true, true, null)]
        [TestCase("alice@example.com", "true", "", ",", true, false, null)]
        [TestCase("alice@example.com", null, "alice@example.com", "", true, false, "oidc_email_verified_missing")]
        [TestCase("alice@example.com", "false", "alice@example.com", "", false, false, "oidc_email_not_verified")]
        [TestCase("alice@example.com", "false", "", "", false, true, "oidc_email_not_verified")]
        [TestCase("alice@example.com", null, "", "", false, true, "oidc_email_verified_missing")]
        [TestCase(null, null, "", "", true, true, "oidc_email_missing")]
        [TestCase("bob@example.com", "true", "alice@example.com", "example.com", true, false, "oidc_email_not_allowed")]
        [TestCase("alice@example.com", "true", "alice@example.com", "family.org", true, false, "oidc_domain_not_allowed")]
        public async Task registered_ticket_callback_applies_access_rules(string email, string verified, string emails, string domains, bool requireVerified, bool allowAny, string expectedError)
        {
            var context = await ReceiveTicket(email, verified, emails, domains, requireVerified, allowAny);

            if (expectedError == null)
            {
                Assert.That(context.Result, Is.Null);
                Assert.That(context.Principal.FindFirst("auth_stamp")?.Value, Is.EqualTo("test-stamp"));
            }
            else
            {
                Assert.That(context.Result?.Handled, Is.True, "a rejection must stop the remote handler before sign-in");
                Assert.That(context.Response.Headers.Location.ToString(),
                    Is.EqualTo($"/chaptarr/login?ssoFailed=true&ssoError={expectedError}&returnUrl=%2Fchaptarr%2Fbooks"));
                Assert.That(context.Principal.FindFirst("auth_stamp"), Is.Null);
            }
        }

        [TestCase("admin@example.org", "", "oidc_email_not_allowed")]
        [TestCase("", "example.org", "oidc_domain_not_allowed")]
        [NonParallelizable]
        public async Task list_rejection_logs_only_the_domain_and_policy_reason(string emails, string domains, string reason)
        {
            var domain = "mail-2.\u202Egoogle\u200Bmail.\r\nco_m" + new string('x', 260);
            var messages = await CaptureAuthLogs(LogLevel.Warn, () => ReceiveTicket("private-name@" + domain, "true", emails, domains, true, false));

            var expectedDomain = ("mail-2.googlemail.com" + new string('x', 260))[..253];
            Assert.That(messages, Is.EqualTo(new[] { $"OIDC sign-in rejected: {reason}, email_verified=true, domain={expectedDomain}" }));
        }

        [TestCase("भारत.in")]
        [TestCase("ดี.th")]
        [TestCase("𐐀.example")]
        [NonParallelizable]
        public async Task list_rejection_keeps_the_domain_spelling_in_the_log(string domain)
        {
            var messages = await CaptureAuthLogs(LogLevel.Warn, () => ReceiveTicket("reader@" + domain, "true", "", "example.org", true, false));
            Assert.That(messages, Is.EqualTo(new[] { $"OIDC sign-in rejected: oidc_domain_not_allowed, email_verified=true, domain={domain}" }));
        }

        [Test]
        [NonParallelizable]
        public async Task malformed_verification_logs_only_its_classification()
        {
            var messages = await CaptureAuthLogs(LogLevel.Warn, () => ReceiveTicket("alice@example.org", "unrecognized claim", "", "example.org", true, false));
            Assert.That(messages, Is.EqualTo(new[] { "OIDC sign-in rejected: oidc_email_not_verified, email_verified=invalid" }));
        }

        [TestCase("Forms", null, "member", false)]
        [TestCase("Forms", "current", "member", true)]
        [TestCase("Oidc", "old", "member", false)]
        [TestCase("Oidc", "current", "member", true)]
        [TestCase("Plex", "old", "member", false)]
        [TestCase("Plex", "current", "other", false)]
        [TestCase("Plex", "current", "member", true)]
        [NonParallelizable]
        public async Task cookie_validation_requires_the_current_stamp_and_keeps_the_plex_user_check(string schemeName, string stamp, string identifier, bool valid)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(InterfaceProxy.Proxy<IConfigFileProvider>((method, _) => method.Name switch
            {
                "get_InstanceName" => "Chaptarr",
                "get_AuthCookieStamp" => "current",
                "get_PlexAuthUserId" => "member",
                _ => throw new NotImplementedException(method.Name)
            }));
            services.AddAppAuthentication();
            string signedOutScheme = null;
            services.AddSingleton(InterfaceProxy.Proxy<AspNetAuthenticationService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(AspNetAuthenticationService.SignOutAsync)));
                signedOutScheme = (string)args[1];
                return Task.CompletedTask;
            }));
            using var provider = services.BuildServiceProvider();
            var scheme = new AuthenticationScheme(schemeName, null, typeof(CookieAuthenticationHandler));
            var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme.Name);
            var identity = new ClaimsIdentity(scheme.Name);
            if (stamp != null) identity.AddClaim(new Claim("auth_stamp", stamp));
            identity.AddClaim(new Claim("identifier", identifier));
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), scheme.Name);
            var context = new CookieValidatePrincipalContext(new DefaultHttpContext { RequestServices = provider }, scheme, options, ticket);

            var messages = await CaptureAuthLogs(LogLevel.Debug, () => options.Events.OnValidatePrincipal(context));
            var expectedMessages = stamp == "current"
                ? Array.Empty<string>()
                : new[] { $"Rejected {schemeName} session because its authentication stamp was missing or changed." };
            Assert.That(messages, Is.EqualTo(expectedMessages));

            Assert.That(context.Principal != null, Is.EqualTo(valid));
            Assert.That(signedOutScheme, Is.EqualTo(valid ? null : schemeName));
        }

        [Test]
        public async Task rejected_sign_in_with_a_nonlocal_return_url_returns_to_the_application_root()
        {
            var context = await ReceiveTicket("reader@example.org", "false", "", "example.org", true, false, "//other.example");
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo(
                "/chaptarr/login?ssoFailed=true&ssoError=oidc_email_not_verified&returnUrl=%2Fchaptarr%2F"));
        }

        private static async Task<string[]> CaptureAuthLogs(LogLevel level, Func<Task> action)
        {
            var original = LogManager.Configuration;
            var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRule(level, level, target, "*AuthenticationBuilderExtensions");
            try
            {
                LogManager.Configuration = logging;
                await action();
                return target.Logs.ToArray();
            }
            finally
            {
                LogManager.Configuration = original;
            }
        }

        private static async Task<TicketReceivedContext> ReceiveTicket(string email, string verified, string emails, string domains, bool requireVerified, bool allowAny, string returnUrl = "/chaptarr/books")
        {
            var config = InterfaceProxy.Proxy<IConfigFileProvider>((method, _) => method.Name switch
            {
                "get_OidcAllowedEmails" => emails,
                "get_OidcAllowedEmailDomains" => domains,
                "get_OidcRequireEmailVerified" => requireVerified,
                "get_OidcAllowAnyVerifiedUser" => allowAny,
                "get_AuthCookieStamp" => "test-stamp",
                "get_UrlBase" => "/chaptarr",
                "get_OidcScopes" => "openid profile email",
                "get_OidcAuthority" => "https://login.example",
                "get_OidcClientId" or "get_OidcClientSecret" => "test",
                _ => throw new NotImplementedException(method.Name)
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(config);
            services.AddSingleton(InterfaceProxy.Proxy<IHttpProxySettingsProvider>((_, _) => null));
            using var handler = new SocketsHttpHandler();
            services.AddSingleton(InterfaceProxy.Proxy<ICreateManagedSocketsHttpHandler>((_, _) => handler));
            services.AddAppAuthentication();
            using var provider = services.BuildServiceProvider();
            var scheme = new AuthenticationScheme("OidcOpenIdConnect", null, typeof(OpenIdConnectHandler));
            var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(scheme.Name);

            var identity = new ClaimsIdentity("oidc");
            identity.AddClaim(new Claim("sub", "subject-1"));
            if (email != null) identity.AddClaim(new Claim(ClaimTypes.Email, email));
            if (verified != null) identity.AddClaim(new Claim("email_verified", verified));
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), scheme.Name);
            var context = new TicketReceivedContext(new DefaultHttpContext(), scheme, options, ticket) { ReturnUri = returnUrl };

            await options.Events.OnTicketReceived(context);

            return context;
        }
    }
}
