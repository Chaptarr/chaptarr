using System;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class LoginHtmlMapper : HtmlMapperBase
    {
        private readonly IConfigFileProvider _configFileProvider;

        public LoginHtmlMapper(IAppFolderInfo appFolderInfo,
                               IDiskProvider diskProvider,
                               Lazy<ICacheBreakerProvider> cacheBreakProviderFactory,
                               IConfigFileProvider configFileProvider,
                               Logger logger)
            : base(diskProvider, cacheBreakProviderFactory, logger)
        {
            _configFileProvider = configFileProvider;
            HtmlPath = Path.Combine(appFolderInfo.StartUpFolder, configFileProvider.UiFolder, "login.html");
            UrlBase = configFileProvider.UrlBase;
        }

        public override string Map(string resourceUrl)
        {
            return HtmlPath;
        }

        protected override Stream GetContentStream(string filePath)
        {
            var text = GetHtmlText();
            text = text.Replace("__AUTH_METHOD__", _configFileProvider.AuthenticationMethod.ToString().ToLowerInvariant());
            text = text.Replace("__OIDC_PROVIDER__", GetOidcProviderHint());

            var stream = new MemoryStream();
            var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(text);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        public override bool CanHandle(string resourceUrl)
        {
            return resourceUrl.StartsWith("/login");
        }

        private string GetOidcProviderHint()
        {
            var explicitHint = Environment.GetEnvironmentVariable("CHAPTARR_OIDC_PROVIDER")?
                .Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(explicitHint))
            {
                explicitHint = Environment.GetEnvironmentVariable("BOOKSHELF_OIDC_PROVIDER")?
                    .Trim().ToLowerInvariant();
            }
            var known = new[]
            {
                "google", "microsoft-entra", "okta", "auth0", "keycloak",
                "authentik", "zitadel", "amazon-cognito", "apple-sign-in", "gitlab", "fusionauth", "onelogin", "pingone", "jumpcloud"
            };
            if (!string.IsNullOrWhiteSpace(explicitHint))
            {
                return known.Contains(explicitHint) ? explicitHint : "generic";
            }

            if (!Uri.TryCreate(_configFileProvider.OidcAuthority, UriKind.Absolute, out var issuer))
            {
                return "generic";
            }
            var host = issuer.Host.ToLowerInvariant();
            var path = issuer.AbsolutePath.ToLowerInvariant();
            if (host == "accounts.google.com") return "google";
            if (host.EndsWith(".microsoftonline.com", StringComparison.Ordinal) ||
                host == "login.microsoftonline.com") return "microsoft-entra";
            if (host.Contains("okta", StringComparison.Ordinal)) return "okta";
            if (host.EndsWith(".auth0.com", StringComparison.Ordinal)) return "auth0";
            if (host.StartsWith("cognito-idp.", StringComparison.Ordinal)) return "amazon-cognito";
            if (host == "gitlab.com") return "gitlab";
            if (host.Contains("zitadel", StringComparison.Ordinal)) return "zitadel";
            if (path.Contains("/application/o/", StringComparison.Ordinal)) return "authentik";
            if (path.Contains("/realms/", StringComparison.Ordinal)) return "keycloak";
            return "generic";
        }
    }
}
