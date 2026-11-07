using System;

namespace NzbDrone.Core.Configuration
{
    /// <summary>
    /// Reads BookshelfNG OIDC environment values as migration fallbacks. A non-empty
    /// ChaptarrNG setting always wins; legacy values are used only when it is unset.
    /// </summary>
    public static class BookshelfOidcEnvironmentSettings
    {
        public const string Authority = "BOOKSHELF_OIDC_ISSUER_URL";
        public const string ClientId = "BOOKSHELF_OIDC_CLIENT_ID";
        public const string ClientSecret = "BOOKSHELF_OIDC_CLIENT_SECRET";
        public const string RedirectUri = "BOOKSHELF_OIDC_REDIRECT_URI";
        public const string Scopes = "BOOKSHELF_OIDC_SCOPES";
        public const string AllowedEmails = "BOOKSHELF_OIDC_ALLOWED_EMAILS";
        public const string AllowedEmailDomains = "BOOKSHELF_OIDC_ALLOWED_EMAIL_DOMAINS";
        public const string AllowAnyVerifiedUser = "BOOKSHELF_OIDC_ALLOW_ANY_VERIFIED_USER";
        public const string AllowPrivateIssuerEnvironmentVariable = "BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER";

        public static string Resolve(string configuredValue, string environmentVariable, string defaultValue)
        {
            if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                return configuredValue;
            }

            var legacyValue = Environment.GetEnvironmentVariable(environmentVariable);
            return string.IsNullOrWhiteSpace(legacyValue) ? defaultValue : legacyValue;
        }

        public static bool ResolveBoolean(string configuredValue, string environmentVariable, bool defaultValue)
        {
            if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                return Convert.ToBoolean(configuredValue);
            }

            var legacyValue = Environment.GetEnvironmentVariable(environmentVariable);
            return bool.TryParse(legacyValue, out var parsed) ? parsed : defaultValue;
        }

        public static string GetRedirectUri()
        {
            var value = Environment.GetEnvironmentVariable(RedirectUri)?.Trim();
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                uri.UserInfo.Length > 0 ||
                uri.Query.Length > 0 ||
                uri.Fragment.Length > 0 ||
                !uri.AbsolutePath.EndsWith("/signin-oidc", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return value;
        }

        public static bool AllowPrivateIssuer =>
            bool.TryParse(Environment.GetEnvironmentVariable(AllowPrivateIssuerEnvironmentVariable), out var parsed) && parsed;
    }
}
