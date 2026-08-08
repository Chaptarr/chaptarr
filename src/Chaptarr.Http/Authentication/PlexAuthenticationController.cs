using System.Text.RegularExpressions;
using Diacritical;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Notifications.Plex.PlexTv;

namespace Chaptarr.Http.Authentication
{
    [AllowAnonymous]
    [ApiController]
    public class PlexAuthenticationController : Controller
    {
        private static readonly TimeSpan PendingLoginLifetime = TimeSpan.FromMinutes(10);
        private const string InitialBindingRequiresTrustedNetwork = "Initial Plex account binding must be completed from localhost or a trusted local network. Connect through a private LAN, trusted VPN, or SSH tunnel and try again.";
        private static readonly Regex CookieNameRegex = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly object PlexBindingLock = new object();

        private readonly IPlexTvService _plexTvService;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly ICached<PlexPendingAuth> _pendingAuth;
        private readonly Logger _logger;

        public PlexAuthenticationController(IPlexTvService plexTvService,
                                            IConfigFileProvider configFileProvider,
                                            ICacheManager cacheManager,
                                            Logger logger)
        {
            _plexTvService = plexTvService;
            _configFileProvider = configFileProvider;
            _pendingAuth = cacheManager.GetCache<PlexPendingAuth>(GetType());
            _logger = logger;
        }

        [HttpGet("auth/plex")]
        public IActionResult Start([FromQuery] string returnUrl = null)
        {
            if (_configFileProvider.AuthenticationMethod != AuthenticationType.Plex)
            {
                return NotFound();
            }

            if (_configFileProvider.PlexAuthUserId.IsNullOrWhiteSpace() &&
                !TrustedNetworkPolicy.IsLocalOrTrusted(HttpContext, _configFileProvider))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = InitialBindingRequiresTrustedNetwork });
            }

            var finalReturnUrl = NormalizeReturnUrl(returnUrl);

            var state = Guid.NewGuid().ToString("N");
            var stateCookieName = GetStateCookieName();
            Response.Cookies.Append(
                stateCookieName,
                state,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.Add(PendingLoginLifetime)
                });

            PlexTvPinResponse pin;
            try
            {
                pin = _plexTvService.CreatePin();
            }
            catch (NzbDroneClientException ex)
            {
                _logger.Warn(ex, "[PLEX-AUTH] Failed to create PIN");
                return BadRequest(new { message = ex.Message });
            }

            if (pin == null || pin.Id <= 0 || pin.Code.IsNullOrWhiteSpace())
            {
                return BadRequest(new { message = "Unable to create Plex OAuth PIN" });
            }

            _pendingAuth.Set(state, new PlexPendingAuth
            {
                PinId = pin.Id,
                ReturnUrl = finalReturnUrl
            }, PendingLoginLifetime);

            var callbackUrl = BuildCallbackUrl(state);
            var signIn = _plexTvService.GetSignInUrl(callbackUrl, pin.Id, pin.Code);

            if (signIn?.OauthUrl.IsNullOrWhiteSpace() == true)
            {
                return BadRequest(new { message = "Unable to build Plex sign-in URL" });
            }

            return Redirect(signIn.OauthUrl);
        }

        [HttpGet("auth/plex/callback")]
        public async Task<IActionResult> Callback([FromQuery] string state)
        {
            if (_configFileProvider.AuthenticationMethod != AuthenticationType.Plex)
            {
                return NotFound();
            }

            if (_configFileProvider.PlexAuthUserId.IsNullOrWhiteSpace() &&
                !TrustedNetworkPolicy.IsLocalOrTrusted(HttpContext, _configFileProvider))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = InitialBindingRequiresTrustedNetwork });
            }

            if (state.IsNullOrWhiteSpace())
            {
                return BadRequest(new { message = "Missing state" });
            }

            var stateCookieName = GetStateCookieName();
            if (!Request.Cookies.TryGetValue(stateCookieName, out var cookieState) ||
                cookieState.IsNullOrWhiteSpace() ||
                !string.Equals(cookieState, state, StringComparison.Ordinal))
            {
                return BadRequest(new { message = "Invalid state" });
            }

            // One-time use.
            Response.Cookies.Delete(stateCookieName);

            var pending = _pendingAuth.Find(state);
            _pendingAuth.Remove(state);

            if (pending == null)
            {
                return BadRequest(new { message = "Plex login session expired, please try again" });
            }

            string authToken = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                authToken = _plexTvService.GetAuthToken(pending.PinId);
                if (authToken.IsNotNullOrWhiteSpace())
                {
                    break;
                }

                await Task.Delay(500);
            }

            if (authToken.IsNullOrWhiteSpace())
            {
                return BadRequest(new { message = "Plex authorization did not complete in time, please try again" });
            }

            PlexTvUserResponse user;
            try
            {
                user = _plexTvService.GetUser(authToken);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[PLEX-AUTH] Failed to fetch Plex user details");
                return BadRequest(new { message = "Unable to fetch Plex user details" });
            }

            if (user == null || user.Id <= 0)
            {
                return BadRequest(new { message = "Unable to verify Plex user" });
            }

            var configuredUserId = _configFileProvider.PlexAuthUserId;
            if (configuredUserId.IsNullOrWhiteSpace())
            {
                lock (PlexBindingLock)
                {
                    configuredUserId = _configFileProvider.PlexAuthUserId;
                    if (configuredUserId.IsNullOrWhiteSpace())
                    {
                        try
                        {
                            _configFileProvider.SaveConfigDictionary(new Dictionary<string, object>
                            {
                                { nameof(IConfigFileProvider.PlexAuthUserId), user.Id.ToString() },
                                { nameof(IConfigFileProvider.PlexAuthUsername), GetDisplayName(user) }
                            });

                            configuredUserId = user.Id.ToString();
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "[PLEX-AUTH] Failed to persist Plex user binding");
                            return BadRequest(new { message = "Unable to persist Plex user binding" });
                        }
                    }
                }
            }
            if (!string.Equals(configuredUserId, user.Id.ToString(), StringComparison.Ordinal))
            {
                return Forbid();
            }

            var claims = new List<Claim>
            {
                new Claim("user", GetDisplayName(user)),
                new Claim("identifier", user.Id.ToString()),
                new Claim("AuthType", AuthenticationType.Plex.ToString()),
                new Claim(AuthenticationBuilderExtensions.AuthStampClaim, _configFileProvider.AuthCookieStamp)
            };

            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true
            };

            await HttpContext.SignInAsync(
                AuthenticationType.Plex.ToString(),
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", "user", "identifier")),
                authProperties);

            return Redirect(pending.ReturnUrl);
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

        private string BuildCallbackUrl(string state)
        {
            var urlBase = _configFileProvider.UrlBase ?? string.Empty;
            var callbackPath = $"{urlBase}/auth/plex/callback";

            return $"{Request.Scheme}://{Request.Host}{callbackPath}?state={Uri.EscapeDataString(state)}";
        }

        private string GetStateCookieName()
        {
            var instanceName = _configFileProvider.InstanceName;
            instanceName = instanceName.RemoveDiacritics();
            instanceName = CookieNameRegex.Replace(instanceName, string.Empty);
            return $"{instanceName}PlexAuthState";
        }

        private static string GetDisplayName(PlexTvUserResponse user)
        {
            return user.Title.IsNotNullOrWhiteSpace()
                ? user.Title
                : user.Username.IsNotNullOrWhiteSpace()
                    ? user.Username
                    : user.Email.IsNotNullOrWhiteSpace()
                        ? user.Email
                        : user.Id.ToString();
        }

        private class PlexPendingAuth
        {
            public int PinId { get; set; }
            public string ReturnUrl { get; set; }
        }
    }
}
