using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Authentication
{
    [AllowAnonymous]
    [ApiController]
    public class AuthenticationController : Controller
    {
        private readonly IAuthenticationService _authService;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;

        public AuthenticationController(IAuthenticationService authService, IConfigFileProvider configFileProvider, IAppFolderInfo appFolderInfo, Logger logger)
        {
            _authService = authService;
            _configFileProvider = configFileProvider;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromForm] LoginResource resource, [FromQuery] string returnUrl = null)
        {
            var user = _authService.Login(HttpContext.Request, resource.Username, resource.Password);

            if (user == null)
            {
                var encodedReturnUrl = returnUrl == null ? string.Empty : Uri.EscapeDataString(returnUrl);
                return Redirect($"~/login?returnUrl={encodedReturnUrl}&loginFailed=true");
            }

            var claims = new List<Claim>
            {
                new Claim("user", user.Username),
                new Claim("identifier", user.Identifier.ToString()),
                new Claim("AuthType", AuthenticationType.Forms.ToString()),
                new Claim(AuthenticationBuilderExtensions.AuthStampClaim, _configFileProvider.AuthCookieStamp)
            };

            var authProperties = new AuthenticationProperties
            {
                IsPersistent = resource.RememberMe == "on"
            };

            try
            {
                await HttpContext.SignInAsync(AuthenticationType.Forms.ToString(), new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", "user", "identifier")), authProperties);
            }
            catch (CryptographicException e)
            {
                if (e.InnerException is XmlException)
                {
                    _logger.Error(e, "Failed to authenticate user due to corrupt XML. Please remove all XML files from {0} and restart Chaptarr", Path.Combine(_appFolderInfo.AppDataFolder, "asp"));
                }
                else
                {
                    _logger.Error(e, "Failed to authenticate user. {0}", e.Message);
                }

                return Unauthorized();
            }

            if (returnUrl.IsNullOrWhiteSpace() || !Url.IsLocalUrl(returnUrl))
            {
                return Redirect(_configFileProvider.UrlBase + "/");
            }

            if (_configFileProvider.UrlBase.IsNullOrWhiteSpace() || returnUrl.StartsWith(_configFileProvider.UrlBase))
            {
                return Redirect(returnUrl);
            }

            return Redirect(_configFileProvider.UrlBase + returnUrl);
        }

        [HttpGet("logout")]
        public async Task<IActionResult> Logout()
        {
            _authService.Logout(HttpContext);
            // If multiple UI auth cookies exist (e.g., local fallback while Plex/OIDC is primary),
            // sign out of all cookie schemes so "Logout" consistently clears access.
            await HttpContext.SignOutAsync(AuthenticationType.Forms.ToString());
            await HttpContext.SignOutAsync(AuthenticationType.Oidc.ToString());
            await HttpContext.SignOutAsync(AuthenticationType.Plex.ToString());
            return Redirect(_configFileProvider.UrlBase + "/");
        }
    }
}
