using System;

namespace NzbDrone.Core.Indexers.Newznab
{
    internal static class NewznabEndpointCompatibility
    {
        // The operator retired the .su endpoints without redirects. Preserve saved
        // settings and signed/encoded request data while routing only these hosts.
        internal static string Normalize(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !(uri.Host.Equals("api.nzb.su", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("nzb.su", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("www.nzb.su", StringComparison.OrdinalIgnoreCase)))
            {
                return url;
            }

            var prefix = uri.Scheme + "://";
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            var tailStart = url.IndexOfAny(new[] { '/', '?', '#' }, prefix.Length);
            if (tailStart < 0)
            {
                tailStart = url.Length;
            }

            var authority = url.Substring(prefix.Length, tailStart - prefix.Length);
            if (!authority.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) &&
                !authority.Equals(uri.Host + ":" + uri.Port, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            return "https://api.nzb.life" + url.Substring(tailStart);
        }
    }
}
