using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Authentication;

namespace Chaptarr.Http.Authentication
{
    public class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IAuthenticationService _authService;

        public BasicAuthenticationHandler(IAuthenticationService authService,
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
            _authService = authService;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                // No Authorization header is not an authentication failure; it just means the request
                // did not attempt Basic auth. Returning NoResult avoids noisy logs when auth is optional
                // (eg "DisabledForLocalAddresses") or when another auth scheme is in use.
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // Get authorization key
            var authorizationHeader = Request.Headers["Authorization"].ToString();

            if (!authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                // Authorization header is present but not Basic; allow other schemes to handle it.
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var encoded = authorizationHeader.Substring("Basic ".Length).Trim();
            if (encoded.Length == 0)
            {
                return Task.FromResult(AuthenticateResult.Fail("Authorization code not formatted properly."));
            }

            string authBase64;
            try
            {
                authBase64 = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return Task.FromResult(AuthenticateResult.Fail("Authorization code not formatted properly."));
            }

            var authSplit = authBase64.Split(':', 2);
            if (authSplit.Length != 2)
            {
                return Task.FromResult(AuthenticateResult.Fail("Authorization code not formatted properly."));
            }

            var authUsername = authSplit[0];
            var authPassword = authSplit[1];

            var user = _authService.Login(Request, authUsername, authPassword);

            if (user == null)
            {
                return Task.FromResult(AuthenticateResult.Fail("The username or password is not correct."));
            }

            var claims = new List<Claim>
            {
                new Claim("user", user.Username),
                new Claim("identifier", user.Identifier.ToString()),
                new Claim("AuthType", AuthenticationType.Basic.ToString())
            };

            var identity = new ClaimsIdentity(claims, "Basic", "user", "identifier");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Basic");

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{BuildInfo.AppName}\"";
            Response.StatusCode = 401;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 403;
            return Task.CompletedTask;
        }
    }
}
