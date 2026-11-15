using System;
using System.Security.Cryptography;

namespace NzbDrone.Common.Http
{
    public static class ExternalImageRequestHeaders
    {
        private static readonly string[] UserAgents = new[]
        {
            // Desktop browsers
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:134.0) Gecko/20100101 Firefox/134.0",

            // Mobile browsers
            "Mozilla/5.0 (iPhone; CPU iPhone OS 19_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.2 Mobile/15E148 Safari/604.1",
            "Mozilla/5.0 (iPad; CPU OS 19_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.2 Mobile/15E148 Safari/604.1",

            // Generic Android app identifiers
            "Dalvik/2.1.0 (Linux; U; Android 13; SM-S908U Build/TP1A.220624.014)",
            "Dalvik/2.1.0 (Linux; U; Android 14; SM-S918U Build/UP1A.231005.007)",
            "Dalvik/2.1.0 (Linux; U; Android 14; SM-S928U Build/UP1A.231005.007)",
            "Dalvik/2.1.0 (Linux; U; Android 14; Pixel 8 Pro Build/UD1A.230803.041)",
            "Dalvik/2.1.0 (Linux; U; Android 13; CPH2451 Build/TP1A.220905.001)"
        };

        public static string GetRandomUserAgent()
        {
            return UserAgents[RandomNumberGenerator.GetInt32(UserAgents.Length)];
        }

        public static bool ShouldSendBrowserLikeHeaders(string userAgent)
        {
            return userAgent?.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase) == true;
        }

        public static bool IsGoodreadsUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.IdnHost.TrimEnd('.');
            return host.Equals("goodreads.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".goodreads.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("gr-assets.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".gr-assets.com", StringComparison.OrdinalIgnoreCase);
        }

        public static void UpdateReferer(HttpRequest request)
        {
            request.Headers.Remove("Referer");
            if (IsGoodreadsUrl(request.Url.FullUri))
            {
                request.Headers["Referer"] = "https://www.goodreads.com/";
            }
        }

        public static void ApplyExternalImageRequestHeaders(HttpRequest request, string userAgent, bool rangeRequest)
        {
            request.Headers["User-Agent"] = userAgent;
            request.Headers["Accept"] = "image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8";

            if (ShouldSendBrowserLikeHeaders(userAgent))
            {
                request.Headers["Accept-Language"] = "en-US,en;q=0.9";
                request.Headers["Cache-Control"] = "no-cache";
                request.Headers["Pragma"] = "no-cache";
                request.Headers["Sec-Fetch-Dest"] = "image";
                request.Headers["Sec-Fetch-Mode"] = "no-cors";
                request.Headers["Sec-Fetch-Site"] = "cross-site";
            }
            else
            {
                request.Headers.Remove("Accept-Language");
                request.Headers.Remove("Cache-Control");
                request.Headers.Remove("Pragma");
                request.Headers.Remove("Sec-Fetch-Dest");
                request.Headers.Remove("Sec-Fetch-Mode");
                request.Headers.Remove("Sec-Fetch-Site");
            }

            UpdateReferer(request);

            if (rangeRequest)
            {
                request.Headers["Range"] = "bytes=0-0";
            }
        }
    }
}
