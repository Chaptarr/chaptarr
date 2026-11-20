using System;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Books.Calibre
{
    public static class CalibreLibraryIdentity
    {
        public static string GetKey(CalibreSettings settings) => settings == null ? null
            : GetKey(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase, settings.Library);

        public static string GetKey(bool ssl, string host, int port, string urlBase, string library)
        {
            if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            {
                return null;
            }
            if (!Uri.TryCreate(HttpRequestBuilder.BuildBaseUrl(ssl, host.Trim(), port, urlBase).TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }
            return $"{uri}" + (string.IsNullOrEmpty(library) ? string.Empty : $" [{library}]");
        }
    }
}
