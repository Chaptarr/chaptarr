using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Diacritical;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using HttpUri = NzbDrone.Common.Http.HttpUri;

namespace Chaptarr.Http.Authentication
{
    public static class AuthenticationBuilderExtensions
    {
        internal const string OidcOpenIdConnectScheme = "OidcOpenIdConnect";
        internal const string UiAuthScheme = "UIAuth";
        internal const string AuthStampClaim = "auth_stamp";
        private const string OidcEmailRequiredFailure = "Email claim is required to sign in.";
        private const string OidcVerifiedEmailRequiredFailure = "Verified email claim is required to sign in.";
        private const string OidcUserNotAllowedFailure = "User is not allowed to sign in.";
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly Regex CookieNameRegex = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder authenticationBuilder, string name, Action<ApiKeyAuthenticationOptions> options)
        {
            return authenticationBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(name, options);
        }

        public static AuthenticationBuilder AddBasic(this AuthenticationBuilder authenticationBuilder, string name)
        {
            return authenticationBuilder.AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(name, options => { });
        }

        public static AuthenticationBuilder AddNone(this AuthenticationBuilder authenticationBuilder, string name)
        {
            return authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(name, options => { });
        }

        public static AuthenticationBuilder AddExternal(this AuthenticationBuilder authenticationBuilder, string name)
        {
            return authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(name, options => { });
        }

        public static AuthenticationBuilder AddAppAuthentication(this IServiceCollection services)
        {
            services.AddOptions<CookieAuthenticationOptions>(AuthenticationType.Forms.ToString())
                .Configure<IConfigFileProvider>((options, configFileProvider) =>
                {
                    // Replace diacritics and replace non-word characters to ensure cookie name doesn't contain any valid URL characters not allowed in cookie names
                    var instanceName = configFileProvider.InstanceName;
                    instanceName = instanceName.RemoveDiacritics();
                    instanceName = CookieNameRegex.Replace(instanceName, string.Empty);

                    options.Cookie.Name = $"{instanceName}Auth";
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                    options.AccessDeniedPath = "/login?loginFailed=true";
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromDays(7);
                    options.SlidingExpiration = true;
                    options.ReturnUrlParameter = "returnUrl";

                    // ASP.NET Core 10 disables cookie redirects for known API endpoints (eg [ApiController]),
                    // which breaks our browser login flow for the UI controllers. Restore the previous behavior:
                    // - XHR requests: 401/403 with Location header
                    // - Browser requests: 302 redirect to /login
                    options.Events.OnRedirectToLogin = context =>
                    {
                        if (IsXhr(context.Request))
                        {
                            context.Response.Headers.Location = context.RedirectUri;
                            context.Response.StatusCode = 401;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }

                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        if (IsXhr(context.Request))
                        {
                            context.Response.Headers.Location = context.RedirectUri;
                            context.Response.StatusCode = 403;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }

                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    options.Events.OnValidatePrincipal = async context =>
                    {
                        var principal = context.Principal;
                        var stamp = principal?.FindFirst(AuthStampClaim)?.Value;
                        if (stamp.IsNullOrWhiteSpace() ||
                            !string.Equals(stamp, configFileProvider.AuthCookieStamp, StringComparison.Ordinal))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(AuthenticationType.Forms.ToString());
                        }
                    };
                });

            services.AddOptions<CookieAuthenticationOptions>(AuthenticationType.Oidc.ToString())
                .Configure<IConfigFileProvider>((options, configFileProvider) =>
                {
                    var instanceName = configFileProvider.InstanceName;
                    instanceName = instanceName.RemoveDiacritics();
                    instanceName = CookieNameRegex.Replace(instanceName, string.Empty);

                    options.Cookie.Name = $"{instanceName}OidcAuth";
                    options.AccessDeniedPath = "/login?loginFailed=true";
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromDays(7);
                    options.SlidingExpiration = true;
                    options.ReturnUrlParameter = "returnUrl";

                    // Hardening: OIDC cookies should only be used over HTTPS (reverse-proxy TLS is fine as long as X-Forwarded-Proto is correct).
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.HttpOnly = true;
                    // The session cookie does not need cross-site semantics (correlation/nonce cookies do).
                    // Prefer Lax to reduce CSRF surface while still working with normal OIDC redirects.
                    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;

                    // See Forms cookie configuration for why this is required on ASP.NET Core 10+.
                    options.Events.OnRedirectToLogin = context =>
                    {
                        if (IsXhr(context.Request))
                        {
                            context.Response.Headers.Location = context.RedirectUri;
                            context.Response.StatusCode = 401;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }

                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        if (IsXhr(context.Request))
                        {
                            context.Response.Headers.Location = context.RedirectUri;
                            context.Response.StatusCode = 403;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }

                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    options.Events.OnValidatePrincipal = async context =>
                    {
                        var principal = context.Principal;
                        var stamp = principal?.FindFirst(AuthStampClaim)?.Value;
                        if (stamp.IsNullOrWhiteSpace() ||
                            !string.Equals(stamp, configFileProvider.AuthCookieStamp, StringComparison.Ordinal))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(AuthenticationType.Oidc.ToString());
                        }
                    };
                });

            services.AddOptions<CookieAuthenticationOptions>(AuthenticationType.Plex.ToString())
                .Configure<IConfigFileProvider>((options, configFileProvider) =>
                {
                    var instanceName = configFileProvider.InstanceName;
                    instanceName = instanceName.RemoveDiacritics();
                    instanceName = CookieNameRegex.Replace(instanceName, string.Empty);

                    options.Cookie.Name = $"{instanceName}PlexAuth";
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                    options.AccessDeniedPath = "/login?loginFailed=true";
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromDays(7);
                    options.SlidingExpiration = true;
                    options.ReturnUrlParameter = "returnUrl";

                    // See Forms cookie configuration for why this is required on ASP.NET Core 10+.
                    options.Events.OnRedirectToLogin = context =>
                    {
                        if (IsXhr(context.Request))
                        {
                            context.Response.Headers.Location = context.RedirectUri;
                            context.Response.StatusCode = 401;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }

                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        if (IsXhr(context.Request))
                        {
                            context.Response.Headers.Location = context.RedirectUri;
                            context.Response.StatusCode = 403;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }

                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    options.Events.OnValidatePrincipal = async context =>
                    {
                        var principal = context.Principal;
                        var stamp = principal?.FindFirst(AuthStampClaim)?.Value;
                        if (stamp.IsNullOrWhiteSpace() ||
                            !string.Equals(stamp, configFileProvider.AuthCookieStamp, StringComparison.Ordinal))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(AuthenticationType.Plex.ToString());
                            return;
                        }

                        var configuredUserId = configFileProvider.PlexAuthUserId;
                        if (configuredUserId.IsNotNullOrWhiteSpace())
                        {
                            var identifier = principal?.FindFirst("identifier")?.Value;
                            if (identifier.IsNullOrWhiteSpace() ||
                                !string.Equals(identifier, configuredUserId, StringComparison.Ordinal))
                            {
                                context.RejectPrincipal();
                                await context.HttpContext.SignOutAsync(AuthenticationType.Plex.ToString());
                            }
                        }
                    };
                });

            services.AddOptions<OpenIdConnectOptions>(OidcOpenIdConnectScheme)
                .Configure<IConfigFileProvider, IHttpProxySettingsProvider, ICreateManagedSocketsHttpHandler>((options, configFileProvider, proxySettingsProvider, socketsHttpHandlerFactory) =>
                {
                    options.SignInScheme = AuthenticationType.Oidc.ToString();
                    // The OpenIdConnect handler is registered as a request-handler scheme, so ASP.NET will
                    // instantiate/initialize it on every request to check callback paths.
                    // OpenIdConnectOptions are validated during initialization and MUST be valid even when
                    // OIDC is not enabled/configured yet, otherwise the entire app breaks on startup.
                    //
                    // Use a safe placeholder configuration when OIDC isn't configured. The scheme will
                    // not be challenged unless the user selects OIDC as the active auth method.
                    var oidcAuthority = configFileProvider.OidcAuthority;
                    var isAppleIssuer = IsAppleIssuer(oidcAuthority);
                    var oidcClientId = configFileProvider.OidcClientId;
                    var oidcClientSecret = configFileProvider.OidcClientSecret;

                    // Be tolerant of users pasting the OIDC discovery URL into the Authority field.
                    // If the Authority ends with '/.well-known/openid-configuration', treat it as the metadata address
                    // and derive the issuer base URL from it.
                    var metadataAddress = string.Empty;
                    if (!string.IsNullOrWhiteSpace(oidcAuthority))
                    {
                        const string wellKnownSuffix = "/.well-known/openid-configuration";
                        var normalized = oidcAuthority.Trim().TrimEnd('/');
                        if (normalized.EndsWith(wellKnownSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            metadataAddress = normalized;
                            oidcAuthority = normalized[..^wellKnownSuffix.Length].TrimEnd('/');
                        }
                    }

                    var isConfigured = !string.IsNullOrWhiteSpace(oidcClientId) &&
                                       (!string.IsNullOrWhiteSpace(oidcAuthority) || !string.IsNullOrWhiteSpace(metadataAddress));

                    options.Authority = isConfigured ? oidcAuthority : "https://example.invalid";
                    options.MetadataAddress = isConfigured && !string.IsNullOrWhiteSpace(metadataAddress) ? metadataAddress : null;
                    options.ClientId = isConfigured ? oidcClientId : "unconfigured";
                    options.ClientSecret = isConfigured ? oidcClientSecret : string.Empty;

                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.UsePkce = !isAppleIssuer;
                    options.SaveTokens = false;
                    options.GetClaimsFromUserInfoEndpoint = !isAppleIssuer;
                    options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");

                    options.Scope.Clear();
                    var oidcScopes = isAppleIssuer
                        ? new[] { "openid", "email" }
                        : ParseOidcScopes(configFileProvider.OidcScopes);
                    foreach (var scope in oidcScopes)
                    {
                        options.Scope.Add(scope);
                    }

                    // Ensure Identity.Name resolves consistently (the rest of the app uses the "user" claim as NameClaimType).
                    options.TokenValidationParameters.NameClaimType = "user";
                    options.TokenValidationParameters.RoleClaimType = "identifier";

                    // Support providers that use form_post response_mode (POST callback) by allowing correlation cookies cross-site.
                    options.CorrelationCookie.SameSite = isAppleIssuer
                        ? Microsoft.AspNetCore.Http.SameSiteMode.None
                        : Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                    options.NonceCookie.SameSite = isAppleIssuer
                        ? Microsoft.AspNetCore.Http.SameSiteMode.None
                        : Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

                    var discoveryUrl = options.MetadataAddress ?? options.Authority;
                    var allowPrivateIssuer = BookshelfOidcEnvironmentSettings.AllowPrivateIssuer;
                    options.RequireHttpsMetadata = !allowPrivateIssuer;

                    HttpProxySettings proxySettings = null;
                    Exception proxyConfigurationError = null;
                    if (isConfigured && !string.IsNullOrWhiteSpace(discoveryUrl))
                    {
                        try
                        {
                            proxySettings = proxySettingsProvider.GetProxySettings(new HttpUri(discoveryUrl));
                        }
                        catch (Exception e)
                        {
                            proxyConfigurationError = e;
                            LogManager.GetCurrentClassLogger()
                                .Error(e, "Failed to determine proxy settings for OIDC discovery URL: {0}", discoveryUrl);
                        }
                    }

                    // Ensure OIDC backchannel traffic (discovery, token exchange, userinfo) uses the same
                    // proxy/certificate validation and IPv4/IPv6 fallback behavior as the rest of the app.
                    options.BackchannelTimeout = TimeSpan.FromSeconds(15);
                    options.BackchannelHttpHandler = proxyConfigurationError == null
                        ? OidcBackchannelHandlerFactory.Create(socketsHttpHandlerFactory, proxySettings, allowPrivateIssuer)
                        : new ConfigurationErrorHttpMessageHandler(proxyConfigurationError);

                    var legacyRedirectUri = BookshelfOidcEnvironmentSettings.GetRedirectUri();
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = context =>
                        {
                            if (!string.IsNullOrWhiteSpace(legacyRedirectUri))
                            {
                                context.ProtocolMessage.RedirectUri = legacyRedirectUri;
                            }
                            if (isAppleIssuer)
                            {
                                context.ProtocolMessage.ResponseMode = OpenIdConnectResponseMode.FormPost;
                            }

                            return System.Threading.Tasks.Task.CompletedTask;
                        },
                        OnTicketReceived = context => HandleOidcTicketReceived(context, configFileProvider),
                        OnRemoteFailure = context =>
                        {
                            var baseException = context.Failure?.GetBaseException();
                            var exceptionMessage = baseException?.Message ?? context.Failure?.Message ?? string.Empty;

                            var errorCode = "sso_failed";
                            if (!string.IsNullOrWhiteSpace(exceptionMessage) &&
                                (exceptionMessage.IndexOf("IDX20803", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 exceptionMessage.IndexOf("IDX20804", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                errorCode = "oidc_discovery_unreachable";
                            }
                            else if (baseException is TimeoutException || baseException is System.Threading.Tasks.TaskCanceledException ||
                                     (!string.IsNullOrWhiteSpace(exceptionMessage) &&
                                      exceptionMessage.IndexOf("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                errorCode = "oidc_backchannel_timeout";
                            }
                            else if (!string.IsNullOrWhiteSpace(exceptionMessage) &&
                                exceptionMessage.IndexOf("Unknown response type", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // This commonly happens when the callback request is missing query/form parameters
                                // (e.g., implicit flow putting tokens in the URL fragment, or a proxy stripping params).
                                errorCode = "missing_callback_parameters";
                            }
                            else if (!string.IsNullOrWhiteSpace(exceptionMessage) &&
                                     (exceptionMessage.IndexOf("correlation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      exceptionMessage.IndexOf("nonce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      exceptionMessage.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                errorCode = "cookie_or_proxy_issue";
                            }

                            LogManager.GetCurrentClassLogger()
                                .Warn(context.Failure, "OIDC remote login failed: {0}", exceptionMessage);

                            context.HandleResponse();
                            context.Response.Redirect(BuildSsoFailedLoginUrl(configFileProvider, context.Properties?.RedirectUri, errorCode));
                            return System.Threading.Tasks.Task.CompletedTask;
                        }
                    };
                });

            return services.AddAuthentication()
                .AddNone(AuthenticationType.None.ToString())
                .AddExternal(AuthenticationType.External.ToString())
                .AddBasic(AuthenticationType.Basic.ToString())
                .AddCookie(AuthenticationType.Forms.ToString())
                .AddCookie(AuthenticationType.Oidc.ToString())
                .AddCookie(AuthenticationType.Plex.ToString())
                .AddOpenIdConnect(OidcOpenIdConnectScheme, options =>
                {
                    // Safety net: OpenIdConnect is a request-handler scheme, so ASP.NET initializes it on
                    // every request (to check callback paths). Ensure options always validate even before
                    // the user has configured OIDC, otherwise the entire app fails to start.
                    if (string.IsNullOrWhiteSpace(options.SignInScheme))
                    {
                        options.SignInScheme = AuthenticationType.Oidc.ToString();
                    }

                    if (string.IsNullOrWhiteSpace(options.ClientId))
                    {
                        options.ClientId = "unconfigured";
                    }

                    if (string.IsNullOrWhiteSpace(options.Authority) && string.IsNullOrWhiteSpace(options.MetadataAddress))
                    {
                        options.Authority = "https://example.invalid";
                        options.RequireHttpsMetadata = false;
                    }

                    if (string.IsNullOrWhiteSpace(options.ResponseType))
                    {
                        options.ResponseType = OpenIdConnectResponseType.Code;
                    }

                    options.UsePkce = !IsAppleIssuer(options.Authority);
                })
                // Route UI auth dynamically based on the current config so switching auth methods does not require a restart.
                .AddPolicyScheme(UiAuthScheme, UiAuthScheme, options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var config = context.RequestServices.GetService<IConfigFileProvider>();
                        var method = config?.AuthenticationMethod ?? AuthenticationType.None;

                        if (config == null)
                        {
                            return AuthenticationType.None.ToString();
                        }

                        if (config.AuthenticationMethodIsMisconfigured)
                        {
                            return GetMisconfiguredAuthenticationScheme(context, config);
                        }

                        // Allow local fallback auth (Forms cookie) even when Plex/OIDC is the primary method.
                        // This is intentionally "either cookie works" so users can recover from an SSO misconfig.
                        var instanceName = config.InstanceName;
                        instanceName = instanceName.RemoveDiacritics();
                        instanceName = CookieNameRegex.Replace(instanceName, string.Empty);

                        var hasFormsCookie = context.Request.Cookies.ContainsKey($"{instanceName}Auth");
                        var hasOidcCookie = context.Request.Cookies.ContainsKey($"{instanceName}OidcAuth");
                        var hasPlexCookie = context.Request.Cookies.ContainsKey($"{instanceName}PlexAuth");
                        var allowLocalFallback = TrustedNetworkPolicy.IsLocalOrTrusted(context, config);

                        if (method == AuthenticationType.Oidc)
                        {
                            if (hasOidcCookie) return AuthenticationType.Oidc.ToString();
                            if (hasFormsCookie && allowLocalFallback) return AuthenticationType.Forms.ToString();
                            return AuthenticationType.Oidc.ToString();
                        }

                        if (method == AuthenticationType.Plex)
                        {
                            if (hasPlexCookie) return AuthenticationType.Plex.ToString();
                            if (hasFormsCookie && allowLocalFallback) return AuthenticationType.Forms.ToString();
                            return AuthenticationType.Plex.ToString();
                        }

                        return method.ToString();
                    };
                })
                .AddApiKey("API", options =>
                {
                    options.HeaderName = "X-Api-Key";
                    options.QueryName = "apikey";
                })
                .AddApiKey("SignalR", options =>
                {
                    options.HeaderName = "X-Api-Key";
                    options.QueryName = "access_token";
                });
        }

        private static System.Threading.Tasks.Task HandleOidcTicketReceived(TicketReceivedContext context, IConfigFileProvider configFileProvider)
        {
            var failure = ValidateAndNormalizeOidcPrincipal(context.Principal, configFileProvider);
            if (failure.IsNullOrWhiteSpace())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            // TicketReceivedContext.Fail() does not stop the remote handler: ASP.NET only honours
            // HandleResponse()/SkipHandler() here and otherwise signs the ticket in regardless. That
            // issued a cookie without the auth_stamp claim, which the cookie validator then rejected,
            // so a policy rejection surfaced as a silent redirect loop back to /login.
            var errorCode = GetOidcPolicyErrorCode(failure);
            Logger.WarnSafe("OIDC sign-in rejected by Chaptarr policy: {0}", errorCode);

            context.HandleResponse();
            context.Response.Redirect(BuildSsoFailedLoginUrl(configFileProvider, context.ReturnUri, errorCode));
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static string GetOidcPolicyErrorCode(string failure)
        {
            return failure switch
            {
                OidcEmailRequiredFailure => "oidc_email_missing",
                OidcVerifiedEmailRequiredFailure => "oidc_email_not_verified",
                OidcUserNotAllowedFailure => "oidc_user_not_allowed",
                _ => "sso_failed"
            };
        }

        private static string BuildSsoFailedLoginUrl(IConfigFileProvider configFileProvider, string returnUrl, string errorCode)
        {
            var urlBase = configFileProvider.UrlBase ?? string.Empty;

            if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith("/"))
            {
                returnUrl = urlBase + "/";
            }

            return $"{urlBase}/login?ssoFailed=true&ssoError={Uri.EscapeDataString(errorCode)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        private static string ValidateAndNormalizeOidcPrincipal(ClaimsPrincipal principal, IConfigFileProvider configFileProvider)
        {
            var allowedEmails = ParseCsvToSet(configFileProvider.OidcAllowedEmails);
            var allowedDomains = ParseCsvToSet(configFileProvider.OidcAllowedEmailDomains);
            var requiresTrustedEmail = allowedEmails.Count > 0 || allowedDomains.Count > 0;
            var allowAnyVerifiedUser = configFileProvider.OidcAllowAnyVerifiedUser;
            const bool requiresVerifiedEmail = true;

            var identity = principal?.Identity as ClaimsIdentity;
            var email = GetOidcEmail(principal);

            if ((requiresTrustedEmail || requiresVerifiedEmail) && string.IsNullOrWhiteSpace(email))
            {
                return OidcEmailRequiredFailure;
            }

            var emailVerified = GetEmailVerifiedClaimValue(principal);

            if ((requiresTrustedEmail || requiresVerifiedEmail) && IsEmailExplicitlyUnverified(emailVerified))
            {
                return OidcVerifiedEmailRequiredFailure;
            }

            if (string.IsNullOrWhiteSpace(emailVerified))
            {
                if (requiresVerifiedEmail)
                {
                    return OidcVerifiedEmailRequiredFailure;
                }

            }

            if (!allowAnyVerifiedUser)
            {
                if (!requiresTrustedEmail)
                {
                    return OidcUserNotAllowedFailure;
                }

                var normalizedEmail = email.ToLowerInvariant();
                var at = normalizedEmail.LastIndexOf('@');
                var domain = at >= 0 && at < normalizedEmail.Length - 1
                    ? normalizedEmail[(at + 1)..]
                    : string.Empty;
                var emailAllowed = allowedEmails.Contains(normalizedEmail);
                var domainAllowed = !string.IsNullOrWhiteSpace(domain) && allowedDomains.Contains(domain);
                if (!emailAllowed && !domainAllowed)
                {
                    return OidcUserNotAllowedFailure;
                }
            }

            if (identity == null)
            {
                return null;
            }

            if (!identity.HasClaim(c => c.Type == "user"))
            {
                var name = email;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                           principal?.FindFirst("sub")?.Value ??
                           "Unknown";
                }

                identity.AddClaim(new Claim("user", name));
            }

            if (!identity.HasClaim(c => c.Type == "identifier"))
            {
                var identifier = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                                 principal?.FindFirst("sub")?.Value ??
                                 "0";
                identity.AddClaim(new Claim("identifier", identifier));
            }

            var issuer = principal?.FindFirst("iss")?.Value;
            var subject = principal?.FindFirst("sub")?.Value ??
                          principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject))
            {
                if (!identity.HasClaim(c => c.Type == "oidc_issuer"))
                {
                    identity.AddClaim(new Claim("oidc_issuer", issuer));
                }

                if (!identity.HasClaim(c => c.Type == "oidc_subject"))
                {
                    identity.AddClaim(new Claim("oidc_subject", subject));
                }
            }

            if (!identity.HasClaim(c => c.Type == "AuthType"))
            {
                identity.AddClaim(new Claim("AuthType", AuthenticationType.Oidc.ToString()));
            }

            if (!identity.HasClaim(c => c.Type == AuthStampClaim))
            {
                identity.AddClaim(new Claim(AuthStampClaim, configFileProvider.AuthCookieStamp));
            }

            return null;
        }

        private static string GetOidcEmail(ClaimsPrincipal principal)
        {
            return principal?.FindFirst(ClaimTypes.Email)?.Value ??
                   principal?.FindFirst("email")?.Value;
        }

        private static string GetEmailVerifiedClaimValue(ClaimsPrincipal principal)
        {
            return principal?.FindFirst("email_verified")?.Value;
        }

        private static bool IsEmailExplicitlyUnverified(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsXhr(HttpRequest request)
        {
            if (request == null)
            {
                return false;
            }

            if (string.Equals(request.Query[HeaderNames.XRequestedWith], "XMLHttpRequest", StringComparison.Ordinal) ||
                string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.Ordinal))
            {
                return true;
            }

            // Treat API-like requests as XHR to avoid returning 302 + HTML login pages to JSON/API callers.
            // Prefer safe heuristics that work for fetch/SignalR and external API clients.
            if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                request.Path.StartsWithSegments("/signalr", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (request.Headers.ContainsKey("X-Api-Key") ||
                request.Query.ContainsKey("apikey") ||
                request.Query.ContainsKey("access_token"))
            {
                return true;
            }

            var accept = request.Headers.Accept.ToString();
            if (accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                accept.IndexOf("text/json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static string GetMisconfiguredAuthenticationScheme(HttpContext context, IConfigFileProvider configFileProvider)
        {
            return TrustedNetworkPolicy.IsLocalOrTrusted(context, configFileProvider)
                ? AuthenticationType.None.ToString()
                : "API";
        }

        private static HashSet<string> ParseCsvToSet(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return raw
                .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsAppleIssuer(string issuer)
        {
            return string.Equals(issuer?.Trim(), "https://appleid.apple.com", StringComparison.Ordinal);
        }

        private static IEnumerable<string> ParseOidcScopes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new[] { "openid", "profile", "email" };
            }

            var scopes = raw
                .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!scopes.Any())
            {
                return new[] { "openid", "profile", "email" };
            }

            // Ensure required OIDC scope is present.
            if (!scopes.Contains("openid", StringComparer.OrdinalIgnoreCase))
            {
                scopes.Insert(0, "openid");
            }

            // BookshelfNG always requested email because its admission policy is
            // based on verified email addresses, even when operators supplied a
            // custom scope list.
            if (!scopes.Contains("email", StringComparer.OrdinalIgnoreCase))
            {
                scopes.Add("email");
            }

            return scopes;
        }

        private sealed class ConfigurationErrorHttpMessageHandler : HttpMessageHandler
        {
            private readonly Exception _configurationError;

            public ConfigurationErrorHttpMessageHandler(Exception configurationError)
            {
                _configurationError = configurationError;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(new InvalidOperationException(
                    "OIDC backchannel requests are blocked because proxy configuration could not be resolved.",
                    _configurationError));
            }
        }
    }
}
