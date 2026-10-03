using System;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class OidcPrincipalValidationFixture
    {
        private static readonly MethodInfo ValidateMethod = typeof(AuthenticationBuilderExtensions).GetMethod(
            "ValidateAndNormalizeOidcPrincipal",
            BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo TicketReceivedMethod = typeof(AuthenticationBuilderExtensions).GetMethod(
            "HandleOidcTicketReceived",
            BindingFlags.NonPublic | BindingFlags.Static);

        private class ConfigFileProviderProxy : DispatchProxy
        {
            public string AllowedEmails { get; set; } = string.Empty;
            public string AllowedEmailDomains { get; set; } = string.Empty;
            public bool RequireEmailVerified { get; set; }
            public bool AllowAnyVerifiedUser { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_OidcAllowedEmails" => AllowedEmails,
                    "get_OidcAllowedEmailDomains" => AllowedEmailDomains,
                    "get_OidcRequireEmailVerified" => RequireEmailVerified,
                    "get_OidcAllowAnyVerifiedUser" => AllowAnyVerifiedUser,
                    "get_AuthCookieStamp" => "test-stamp",
                    "get_UrlBase" => string.Empty,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }

            public static IConfigFileProvider Create(string allowedEmails = "", string allowedEmailDomains = "", bool requireEmailVerified = false, bool allowAnyVerifiedUser = false)
            {
                var proxy = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
                var state = (ConfigFileProviderProxy)(object)proxy;
                state.AllowedEmails = allowedEmails;
                state.AllowedEmailDomains = allowedEmailDomains;
                state.RequireEmailVerified = requireEmailVerified;
                state.AllowAnyVerifiedUser = allowAnyVerifiedUser;
                return proxy;
            }
        }

        [Test]
        public void should_allow_missing_email_verified_claim_by_default_when_allowlist_matches()
        {
            var failure = Validate(BuildPrincipal("alice@example.com"), ConfigFileProviderProxy.Create(allowedEmails: "alice@example.com"));

            Assert.That(failure, Is.Null);
        }

        [Test]
        public void should_reject_missing_email_verified_claim_when_strict_mode_is_enabled()
        {
            var failure = Validate(BuildPrincipal("alice@example.com"), ConfigFileProviderProxy.Create(allowedEmails: "alice@example.com", requireEmailVerified: true));

            Assert.That(failure, Is.EqualTo("Verified email claim is required to sign in."));
        }

        [Test]
        public void should_reject_explicitly_unverified_email_when_allowlist_is_configured()
        {
            var failure = Validate(BuildPrincipal("alice@example.com", "false"), ConfigFileProviderProxy.Create(allowedEmails: "alice@example.com"));

            Assert.That(failure, Is.EqualTo("Verified email claim is required to sign in."));
        }

        [Test]
        public void should_allow_verified_email_when_allowlist_matches()
        {
            var failure = Validate(BuildPrincipal("alice@example.com", "true"), ConfigFileProviderProxy.Create(allowedEmails: "alice@example.com", requireEmailVerified: true));

            Assert.That(failure, Is.Null);
        }

        [Test]
        public void should_allow_any_verified_user_when_explicitly_configured()
        {
            var failure = Validate(BuildPrincipal("alice@example.com", "true"), ConfigFileProviderProxy.Create(allowAnyVerifiedUser: true));

            Assert.That(failure, Is.Null);
        }

        [Test]
        public void should_reject_unverified_allow_any_user()
        {
            var failure = Validate(BuildPrincipal("alice@example.com", "false"), ConfigFileProviderProxy.Create(allowAnyVerifiedUser: true));

            Assert.That(failure, Is.EqualTo("Verified email claim is required to sign in."));
        }

        [TestCase("bob@example.com", null, "oidc_user_not_allowed")]
        [TestCase("alice@example.com", "false", "oidc_email_not_verified")]
        public void should_stop_sign_in_and_redirect_to_login_when_ticket_is_rejected(string email, string emailVerified, string expectedError)
        {
            var context = BuildTicketReceivedContext(BuildPrincipal(email, emailVerified), "/books");

            ReceiveTicket(context, ConfigFileProviderProxy.Create(allowedEmails: "alice@example.com"));

            Assert.That(context.Result, Is.Not.Null);
            Assert.That(context.Result.Handled, Is.True, "a rejected ticket must not fall through to SignInAsync");
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo($"/login?ssoFailed=true&ssoError={expectedError}&returnUrl=%2Fbooks"));
            Assert.That(context.Principal.HasClaim(c => c.Type == "auth_stamp"), Is.False);
        }

        [Test]
        public void should_let_accepted_ticket_sign_in_with_auth_stamp()
        {
            var context = BuildTicketReceivedContext(BuildPrincipal("alice@example.com", "true"), "/books");

            ReceiveTicket(context, ConfigFileProviderProxy.Create(allowedEmails: "alice@example.com"));

            Assert.That(context.Result, Is.Null);
            Assert.That(context.Principal.FindFirst("auth_stamp")?.Value, Is.EqualTo("test-stamp"));
        }

        private static void ReceiveTicket(TicketReceivedContext context, IConfigFileProvider configFileProvider)
        {
            ((Task)TicketReceivedMethod.Invoke(null, new object[] { context, configFileProvider })).GetAwaiter().GetResult();
        }

        private static TicketReceivedContext BuildTicketReceivedContext(ClaimsPrincipal principal, string returnUri)
        {
            var scheme = new AuthenticationScheme("OidcOpenIdConnect", null, typeof(OpenIdConnectHandler));
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), scheme.Name);

            return new TicketReceivedContext(new DefaultHttpContext(), scheme, new OpenIdConnectOptions(), ticket)
            {
                ReturnUri = returnUri
            };
        }

        private static string Validate(ClaimsPrincipal principal, IConfigFileProvider configFileProvider)
        {
            return (string)ValidateMethod.Invoke(null, new object[] { principal, configFileProvider });
        }

        private static ClaimsPrincipal BuildPrincipal(string email, string emailVerified = null)
        {
            var identity = new ClaimsIdentity("oidc");
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
            identity.AddClaim(new Claim("sub", "subject-1"));

            if (emailVerified != null)
            {
                identity.AddClaim(new Claim("email_verified", emailVerified));
            }

            return new ClaimsPrincipal(identity);
        }
    }
}
