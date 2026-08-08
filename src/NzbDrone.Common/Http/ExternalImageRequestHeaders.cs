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
            return url.Contains("goodreads.com", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("gr-assets.com", StringComparison.OrdinalIgnoreCase);
        }

        public static void ApplyExternalImageRequestHeaders(HttpRequest request, string url, string userAgent, bool rangeRequest)
        {
            request.Headers.Add("User-Agent", userAgent);
            request.Headers.Add("Accept", "image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");

            if (ShouldSendBrowserLikeHeaders(userAgent))
            {
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Cache-Control", "no-cache");
                request.Headers.Add("Pragma", "no-cache");
                request.Headers.Add("Sec-Fetch-Dest", "image");
                request.Headers.Add("Sec-Fetch-Mode", "no-cors");
                request.Headers.Add("Sec-Fetch-Site", "cross-site");
            }

            if (IsGoodreadsUrl(url))
            {
                request.Headers.Add("Referer", "https://www.goodreads.com/");
            }

            if (rangeRequest)
            {
                request.Headers.Add("Range", "bytes=0-0");
            }
        }
    }
}
