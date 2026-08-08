using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Authentication
{
    [AllowAnonymous]
    [ApiController]
    public class OidcAuthenticationController : Controller
    {
        private readonly IConfigFileProvider _configFileProvider;

        public OidcAuthenticationController(IConfigFileProvider configFileProvider)
        {
            _configFileProvider = configFileProvider;
        }

        [HttpGet("auth/oidc")]
        public IActionResult Start([FromQuery] string returnUrl = null)
        {
            if (_configFileProvider.AuthenticationMethod != AuthenticationType.Oidc)
            {
                return NotFound();
            }

            var finalReturnUrl = NormalizeReturnUrl(returnUrl);
            var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                RedirectUri = finalReturnUrl
            };

            return Challenge(properties, AuthenticationBuilderExtensions.OidcOpenIdConnectScheme);
        }

        private string NormalizeReturnUrl(string returnUrl)
        {
            if (returnUrl.IsNullOrWhiteSpace() || !Url.IsLocalUrl(returnUrl))
            {
                return _configFileProvider.UrlBase + "/";
            }

            if (_configFileProvider.UrlBase.IsNullOrWhiteSpace() || returnUrl.StartsWith(_configFileProvider.UrlBase))
            {
                return returnUrl;
            }

            return _configFileProvider.UrlBase + returnUrl;
        }
    }
}

