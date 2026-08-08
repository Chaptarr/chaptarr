using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Update;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/host")]
    public class HostConfigController : RestController<HostConfigResource>
    {
        private const string UiAuthScheme = "UIAuth";
        private const string AuthStampClaim = "auth_stamp";

        private readonly IConfigFileProvider _configFileProvider;
        private readonly IConfigService _configService;
        private readonly IUserService _userService;
        private readonly IProxyTestService _proxyTestService;
        private readonly IProxyService _proxyService;
        private readonly Logger _logger;

        public HostConfigController(IConfigFileProvider configFileProvider,
                                    IConfigService configService,
                                    IUserService userService,
                                    IProxyTestService proxyTestService,
                                    IProxyService proxyService,
                                    FileExistsValidator fileExistsValidator)
        {
            _configFileProvider = configFileProvider;
            _configService = configService;
            _userService = userService;
            _proxyTestService = proxyTestService;
            _proxyService = proxyService;
            _logger = LogManager.GetCurrentClassLogger();

            SharedValidator.RuleFor(c => c.BindAddress)
                           .ValidIpAddress()
                           .When(c => c.BindAddress != "*" && c.BindAddress != "localhost");

            SharedValidator.RuleFor(c => c.Port).ValidPort();

            SharedValidator.RuleFor(c => c.UrlBase).ValidUrlBase();
            SharedValidator.RuleFor(c => c.InstanceName).ContainsChaptarr().When(c => c.InstanceName.IsNotNullOrWhiteSpace());

            SharedValidator.RuleFor(c => c.Username).NotEmpty().When(c => c.AuthenticationMethod == AuthenticationType.Basic ||
                                                                          c.AuthenticationMethod == AuthenticationType.Forms);
            SharedValidator.RuleFor(c => c.Password)
                           .NotEmpty()
                           .When(c => (c.AuthenticationMethod == AuthenticationType.Basic || c.AuthenticationMethod == AuthenticationType.Forms) &&
                                      _userService.FindUser() == null);

            // Allow (optional) local recovery credentials even when Plex/OIDC is the primary auth method.
            // Require a non-empty password when setting credentials for the first time.
            SharedValidator.RuleFor(c => c.Password)
                           .NotEmpty()
                           .When(c => (c.AuthenticationMethod == AuthenticationType.Plex || c.AuthenticationMethod == AuthenticationType.Oidc) &&
                                      c.Username.IsNotNullOrWhiteSpace() &&
                                      _userService.FindUser() == null);

            // Prevent accidental "password only" submissions when configuring recovery credentials.
            SharedValidator.RuleFor(c => c.Username)
                           .NotEmpty()
                           .When(c => (c.AuthenticationMethod == AuthenticationType.Plex || c.AuthenticationMethod == AuthenticationType.Oidc) &&
                                      c.Password.IsNotNullOrWhiteSpace());

            SharedValidator.RuleFor(c => c.OidcAuthority)
                           .NotEmpty()
                           .When(c => c.AuthenticationMethod == AuthenticationType.Oidc);
            SharedValidator.RuleFor(c => c.OidcAuthority)
                           .Must(IsValidAbsoluteUrl)
                           .WithMessage("Must be a valid absolute URL")
                           .When(c => c.AuthenticationMethod == AuthenticationType.Oidc && c.OidcAuthority.IsNotNullOrWhiteSpace());
            SharedValidator.RuleFor(c => c.OidcAuthority)
                           .Must(IsSecureOidcAuthority)
                           .WithMessage("Must use https:// unless the provider is running on localhost")
                           .When(c => c.AuthenticationMethod == AuthenticationType.Oidc && c.OidcAuthority.IsNotNullOrWhiteSpace());

            SharedValidator.RuleFor(c => c.OidcClientId).NotEmpty().When(c => c.AuthenticationMethod == AuthenticationType.Oidc);
            SharedValidator.RuleFor(c => c.OidcClientSecret)
                           .NotEmpty()
                           .When(c => c.AuthenticationMethod == AuthenticationType.Oidc && _configFileProvider.OidcClientSecret.IsNullOrWhiteSpace());

            SharedValidator.RuleFor(c => c.PasswordConfirmation)
                .Equal(c => c.Password)
                .When(c => c.Password.IsNotNullOrWhiteSpace())
                .WithMessage("Must match Password");

            SharedValidator.RuleFor(c => c.SslPort).ValidPort().When(c => c.EnableSsl);
            SharedValidator.RuleFor(c => c.SslPort).NotEqual(c => c.Port).When(c => c.EnableSsl);

            SharedValidator.RuleFor(c => c.SslCertPath)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .IsValidPath()
                .SetValidator(fileExistsValidator)
                .Must((resource, path) => IsValidSslCertificate(resource)).WithMessage("Invalid SSL certificate file or password")
                .When(c => c.EnableSsl);

            SharedValidator.RuleFor(c => c.Branch).NotEmpty().WithMessage("Branch name is required, 'master' is the default");
            SharedValidator.RuleFor(c => c.UpdateScriptPath).IsValidPath().When(c => c.UpdateMechanism == UpdateMechanism.Script);
            SharedValidator.RuleFor(c => c.ProxyMode)
                           .Must(x => Enum.IsDefined(typeof(ProxyMode), x))
                           .WithMessage("Proxy mode must be Disabled, IndexerOnly, or ProxyEverything");
            SharedValidator.RuleFor(c => c.ProxyType)
                           .Must(x => Enum.IsDefined(typeof(ProxyType), x))
                           .WithMessage("Proxy type must be Http, Socks4, or Socks5");

            SharedValidator.RuleFor(c => c.BackupFolder).IsValidPath().When(c => Path.IsPathRooted(c.BackupFolder));
            SharedValidator.RuleFor(c => c.BackupInterval).InclusiveBetween(1, 7);
            SharedValidator.RuleFor(c => c.BackupRetention).InclusiveBetween(1, 90);
        }

        private bool IsValidSslCertificate(HostConfigResource resource)
        {
            try
            {
                var password = resource.SslCertPassword;
                if (password.IsNullOrWhiteSpace() && !_configFileProvider.SslCertPassword.IsNullOrWhiteSpace())
                {
                    password = _configFileProvider.SslCertPassword;
                }

                using var cert = X509CertificateLoader.LoadPkcs12FromFile(resource.SslCertPath, password, X509KeyStorageFlags.DefaultKeySet);
                return cert != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidAbsoluteUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }

        private static bool IsSecureOidcAuthority(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttps ||
                    (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
        }

        protected override HostConfigResource GetResourceById(int id)
        {
            return GetHostConfig();
        }

        [HttpGet]
        public HostConfigResource GetHostConfig()
        {
            var resource = HostConfigResourceMapper.ToResource(_configFileProvider, _configService);
            resource.Id = 1;

            var user = _userService.FindUser();

            resource.Username = user?.Username ?? string.Empty;
            // Password is write-only. Never return the stored hash to the client.
            resource.Password = string.Empty;
            resource.PasswordConfirmation = string.Empty;

            // Attach proxy name for display using stored GlobalProxyId
            try
            {
                var globalId = _configService.GlobalProxyId;
                if (globalId.HasValue)
                {
                    var proxy = _proxyService.Get(globalId.Value);
                    resource.ProxyName = proxy?.Name;
                }
            }
            catch { }

            RedactSensitiveFields(resource);
            return resource;
        }

        [RestPutById]
        public async Task<ActionResult<HostConfigResource>> SaveHostConfig([FromBody] HostConfigResource resource)
        {
            EnsureValidProxyEnums(resource.ProxyMode, resource.ProxyType);

            var current = HostConfigResourceMapper.ToResource(_configFileProvider, _configService);
            var currentUser = _userService.FindUser();

            var existingUiAuth = await TryAuthenticateUiAsync();

            // Preserve write-only secrets if the client doesn't send a value.
            if (resource.OidcClientSecret.IsNullOrWhiteSpace())
            {
                resource.OidcClientSecret = current.OidcClientSecret;
            }

            if (resource.ProxyPassword.IsNullOrWhiteSpace())
            {
                resource.ProxyPassword = current.ProxyPassword;
            }

            if (resource.SslCertPassword.IsNullOrWhiteSpace())
            {
                resource.SslCertPassword = current.SslCertPassword;
            }

            var legacyProxyFieldsChanged = LegacyProxyFieldsChanged(resource, current);

            var shouldRotateAuthStamp =
                resource.AuthenticationMethod != current.AuthenticationMethod ||
                resource.AuthenticationRequired != current.AuthenticationRequired ||
                !string.Equals(resource.OidcAuthority ?? string.Empty, current.OidcAuthority ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(resource.OidcClientId ?? string.Empty, current.OidcClientId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(resource.OidcClientSecret ?? string.Empty, current.OidcClientSecret ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(resource.OidcScopes ?? string.Empty, current.OidcScopes ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(resource.OidcAllowedEmails ?? string.Empty, current.OidcAllowedEmails ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(resource.OidcAllowedEmailDomains ?? string.Empty, current.OidcAllowedEmailDomains ?? string.Empty, StringComparison.Ordinal) ||
                resource.OidcRequireEmailVerified != current.OidcRequireEmailVerified ||
                resource.OidcAllowAnyVerifiedUser != current.OidcAllowAnyVerifiedUser ||
                resource.Password.IsNotNullOrWhiteSpace() ||
                ((resource.AuthenticationMethod == AuthenticationType.Basic || resource.AuthenticationMethod == AuthenticationType.Forms) &&
                 resource.Username.IsNotNullOrWhiteSpace() &&
                 currentUser != null &&
                 !string.Equals(resource.Username.Trim().ToLowerInvariant(), currentUser.Username ?? string.Empty, StringComparison.Ordinal));

            if (resource.AuthenticationMethod == AuthenticationType.Oidc &&
                resource.OidcAllowedEmails.IsNullOrWhiteSpace() &&
                resource.OidcAllowedEmailDomains.IsNullOrWhiteSpace() &&
                !resource.OidcAllowAnyVerifiedUser)
            {
                throw new ValidationException(new[]
                {
                    new FluentValidation.Results.ValidationFailure(
                        nameof(resource.OidcAllowedEmails),
                        "OIDC requires allowed emails, allowed email domains, or the explicit allow-any-verified-user option.")
                });
            }

            var dictionary = resource.GetType()
                                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                     .ToDictionary(prop => prop.Name, prop => prop.GetValue(resource, null));

            _configFileProvider.SaveConfigDictionary(dictionary);
            _configService.SaveConfigDictionary(dictionary);
            SyncProxyBypassSettings(resource.ProxyBypassLocalAddresses, resource.ProxyBypassFilter);

            User upsertedUser = null;
            if (resource.AuthenticationMethod == AuthenticationType.Basic ||
                resource.AuthenticationMethod == AuthenticationType.Forms ||
                resource.AuthenticationMethod == AuthenticationType.Plex ||
                resource.AuthenticationMethod == AuthenticationType.Oidc)
            {
                if (resource.Username.IsNotNullOrWhiteSpace())
                {
                    upsertedUser = _userService.Upsert(resource.Username, resource.Password);
                }
            }

            string newAuthStamp = null;
            if (shouldRotateAuthStamp)
            {
                newAuthStamp = Guid.NewGuid().ToString("N");
                _configFileProvider.SaveConfigDictionary(new Dictionary<string, object>
                {
                    { nameof(IConfigFileProvider.AuthCookieStamp), newAuthStamp }
                });

                // Avoid a jarring UX during initial setup or auth updates:
                // rotate the stamp to invalidate other sessions, but preserve this browser session when possible.
                await TryPreserveUiSessionAfterAuthStampRotation(resource, existingUiAuth, newAuthStamp, upsertedUser);
            }

            // Handle proxy configuration - create/update proxy by name and store its ID
            if (resource.ProxyMode != ProxyMode.Disabled &&
                legacyProxyFieldsChanged &&
                resource.ProxyHostname.IsNotNullOrWhiteSpace() &&
                resource.ProxyPort > 0)
            {
                try
                {
                    ProxyDefinition target = null;
                    if (_configService.GlobalProxyId.HasValue)
                    {
                        target = _proxyService.Get(_configService.GlobalProxyId.Value);
                    }
                    if (target == null && resource.ProxyName.IsNotNullOrWhiteSpace())
                    {
                        target = _proxyService.All().FirstOrDefault(p => p.Name == resource.ProxyName);
                    }
                    if (target == null)
                    {
                        target = new ProxyDefinition
                        {
                            Name = resource.ProxyName.IsNotNullOrWhiteSpace() ? resource.ProxyName : "Default Proxy"
                        };
                        _proxyService.Add(target);
                        _logger.Info("Created proxy '{0}' from quickstart configuration", target.Name);
                    }

                    target.ProxyType = resource.ProxyType;
                    target.Hostname = resource.ProxyHostname;
                    target.Port = resource.ProxyPort;
                    target.Username = resource.ProxyUsername;
                    target.Password = resource.ProxyPassword;
                    _proxyService.Update(target);

                    _configService.GlobalProxyId = target.Id;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to create/update proxy entry from quickstart");

                    // Don't fail the entire save operation if proxy creation fails
                }
            }

            // Return the updated configuration to show any normalization that occurred
            var updatedResource = HostConfigResourceMapper.ToResource(_configFileProvider, _configService);
            updatedResource.Id = 1;

            var user = _userService.FindUser();
            updatedResource.Username = user?.Username ?? string.Empty;
            updatedResource.Password = string.Empty;
            updatedResource.PasswordConfirmation = string.Empty;
            try
            {
                var gid = _configService.GlobalProxyId;
                if (gid.HasValue)
                {
                    var proxy = _proxyService.Get(gid.Value);
                    updatedResource.ProxyName = proxy?.Name;
                }
            }
            catch { }

            RedactSensitiveFields(updatedResource);
            return Accepted(updatedResource);
        }

        private void SyncProxyBypassSettings(bool bypassLocalAddresses, string bypassFilter)
        {
            var normalizedFilter = bypassFilter ?? string.Empty;

            foreach (var proxy in _proxyService.All())
            {
                if (proxy.BypassLocalAddresses == bypassLocalAddresses &&
                    string.Equals(proxy.BypassFilter ?? string.Empty, normalizedFilter, StringComparison.Ordinal))
                {
                    continue;
                }

                proxy.BypassLocalAddresses = bypassLocalAddresses;
                proxy.BypassFilter = normalizedFilter;
                _proxyService.Update(proxy);
            }
        }

        internal static bool LegacyProxyFieldsChanged(HostConfigResource resource, HostConfigResource current)
        {
            return resource.ProxyType != current.ProxyType ||
                   !string.Equals(resource.ProxyHostname ?? string.Empty, current.ProxyHostname ?? string.Empty, StringComparison.Ordinal) ||
                   resource.ProxyPort != current.ProxyPort ||
                   !string.Equals(resource.ProxyUsername ?? string.Empty, current.ProxyUsername ?? string.Empty, StringComparison.Ordinal) ||
                   !string.Equals(resource.ProxyPassword ?? string.Empty, current.ProxyPassword ?? string.Empty, StringComparison.Ordinal);
        }

        private async Task<AuthenticateResult> TryAuthenticateUiAsync()
        {
            try
            {
                return await HttpContext.AuthenticateAsync(UiAuthScheme);
            }
            catch
            {
                return null;
            }
        }

        private async Task TryPreserveUiSessionAfterAuthStampRotation(HostConfigResource resource, AuthenticateResult existingUiAuth, string newStamp, User upsertedUser)
        {
            if (string.IsNullOrWhiteSpace(newStamp))
            {
                return;
            }

            try
            {
                var principal = existingUiAuth?.Succeeded == true ? existingUiAuth.Principal : null;
                var existingAuthType = GetAuthType(principal);

                // If switching to Forms auth, we can keep the user in-session without forcing a login prompt
                // only when we can validate the new credentials (fresh setup) or when they already had a Forms cookie.
                if (resource.AuthenticationMethod == AuthenticationType.Forms)
                {
                    if (existingAuthType == AuthenticationType.Forms)
                    {
                        await SignInWithUpdatedStamp(AuthenticationType.Forms.ToString(), principal, existingUiAuth?.Properties, newStamp);
                        return;
                    }

                    // Fresh setup: credentials were just provided; authenticate and issue a Forms cookie immediately.
                    if (resource.Username.IsNotNullOrWhiteSpace() && resource.Password.IsNotNullOrWhiteSpace())
                    {
                        var user = _userService.FindUser(resource.Username, resource.Password) ?? upsertedUser;
                        if (user != null)
                        {
                            await HttpContext.SignInAsync(AuthenticationType.Forms.ToString(), BuildFormsPrincipal(user, newStamp), new AuthenticationProperties());
                        }
                    }

                    return;
                }

                // When Plex/OIDC is the configured primary auth, keep any existing Plex/OIDC or local fallback Forms cookie working.
                if (resource.AuthenticationMethod == AuthenticationType.Plex)
                {
                    if (existingAuthType == AuthenticationType.Plex)
                    {
                        await SignInWithUpdatedStamp(AuthenticationType.Plex.ToString(), principal, existingUiAuth?.Properties, newStamp);
                    }
                    else if (existingAuthType == AuthenticationType.Forms)
                    {
                        await SignInWithUpdatedStamp(AuthenticationType.Forms.ToString(), principal, existingUiAuth?.Properties, newStamp);
                    }

                    return;
                }

                if (resource.AuthenticationMethod == AuthenticationType.Oidc)
                {
                    if (existingAuthType == AuthenticationType.Oidc)
                    {
                        await SignInWithUpdatedStamp(AuthenticationType.Oidc.ToString(), principal, existingUiAuth?.Properties, newStamp);
                    }
                    else if (existingAuthType == AuthenticationType.Forms)
                    {
                        await SignInWithUpdatedStamp(AuthenticationType.Forms.ToString(), principal, existingUiAuth?.Properties, newStamp);
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to preserve UI session after auth stamp rotation");
            }
        }

        private static AuthenticationType GetAuthType(ClaimsPrincipal principal)
        {
            var raw = principal?.FindFirst("AuthType")?.Value;
            return Enum.TryParse(raw, out AuthenticationType parsed) ? parsed : AuthenticationType.None;
        }

        private static ClaimsPrincipal WithAuthStamp(ClaimsPrincipal principal, string stamp)
        {
            if (principal == null)
            {
                return null;
            }

                var identities = principal.Identities.Select(identity =>
                {
                    var claims = identity.Claims
                    .Where(c => !string.Equals(c.Type, AuthStampClaim, StringComparison.Ordinal))
                    .ToList();

                claims.Add(new Claim(AuthStampClaim, stamp));

                return new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
            });

            return new ClaimsPrincipal(identities);
        }

        private static ClaimsPrincipal BuildFormsPrincipal(User user, string stamp)
        {
            var claims = new List<Claim>
            {
                new Claim("user", user.Username),
                new Claim("identifier", user.Identifier.ToString()),
                new Claim("AuthType", AuthenticationType.Forms.ToString()),
                new Claim(AuthStampClaim, stamp)
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", "user", "identifier"));
        }

        private async Task SignInWithUpdatedStamp(string scheme, ClaimsPrincipal principal, AuthenticationProperties properties, string newStamp)
        {
            var updatedPrincipal = WithAuthStamp(principal, newStamp);
            if (updatedPrincipal == null)
            {
                return;
            }

            await HttpContext.SignInAsync(scheme, updatedPrincipal, properties ?? new AuthenticationProperties());
        }

        private static void RedactSensitiveFields(HostConfigResource resource)
        {
            resource.OidcClientSecret = string.Empty;
            resource.ProxyPassword = string.Empty;
            resource.SslCertPassword = string.Empty;
        }

        [HttpPost("test/proxy")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public async Task<ActionResult<ProxyTestResult>> TestProxy([FromBody] ProxyTestRequest request)
        {
            try
            {
                // TestProxy only accepts a ProxyType. Use a known-valid mode to reuse the
                // shared enum guard without implying this endpoint changes proxy routing mode.
                EnsureValidProxyEnums(ProxyMode.ProxyEverything, request.ProxyType);

                _logger.Debug("Proxy test endpoint called - Host: {0}, Port: {1}, Type: {2}", request.ProxyHostname, request.ProxyPort, request.ProxyType);

                var result = await _proxyTestService.TestProxy(
                    request.ProxyHostname,
                    request.ProxyPort,
                    request.ProxyType,
                    request.ProxyUsername,
                    request.ProxyPassword);

                _logger.Debug("Proxy test completed - Valid: {0}, Message: {1}", result.IsValid, result.Message);

                return Ok(result);
            }
            catch (global::System.Exception ex)
            {
                _logger.Error(ex, "Exception in proxy test endpoint");
                return BadRequest(new ProxyTestResult
                {
                    IsValid = false,
                    Message = $"Test failed: {ex.Message}"
                });
            }
        }

        private static void EnsureValidProxyEnums(ProxyMode proxyMode, ProxyType proxyType)
        {
            if (!Enum.IsDefined(typeof(ProxyMode), proxyMode))
            {
                throw new BadRequestException("Proxy mode must be Disabled, IndexerOnly, or ProxyEverything");
            }

            if (!Enum.IsDefined(typeof(ProxyType), proxyType))
            {
                throw new BadRequestException("Proxy type must be Http, Socks4, or Socks5");
            }
        }
    }

    public class ProxyTestRequest
    {
        public string ProxyHostname { get; set; }
        public int ProxyPort { get; set; }
        public ProxyType ProxyType { get; set; }
        public string ProxyUsername { get; set; }
        public string ProxyPassword { get; set; }
    }
}
