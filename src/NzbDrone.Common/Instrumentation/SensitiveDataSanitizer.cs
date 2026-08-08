using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Common.Instrumentation
{
    public static class SensitiveDataSanitizer
    {
        private static readonly Regex ApiKeyRegex = new Regex(@"(apikey|api_key|api-key|access_token|refresh_token|id_token|token|client_secret|authorization_code|code)=([^&\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AuthorizationHeaderRegex = new Regex(@"(authorization|x-api-key):\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CookieRegex = new Regex(@"(mam_id|session|auth|token)=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CookiePairRegex = new Regex(@"(?<name>(?:^|;\s*)[^=;\s]+)=[^;]*", RegexOptions.Compiled);
        private static readonly Regex PasswordRegex = new Regex(@"(password|passwd|pwd)=([^&\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex JsonSecretRegex = new Regex(@"(""[^""]*(?:apikey|token|secret|password|passwd|pwd|authorization|cookie)[^""]*""\s*:\s*"")([^""]+)("")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Sanitizes a URL by masking sensitive query parameters
        /// </summary>
        public static string SanitizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            url = ApiKeyRegex.Replace(url, "$1=<REDACTED>");
            url = PasswordRegex.Replace(url, "$1=<REDACTED>");

            return url;
        }

        /// <summary>
        /// Sanitizes HTTP headers by masking sensitive values
        /// </summary>
        public static Dictionary<string, string> SanitizeHeaders(Dictionary<string, string> headers)
        {
            if (headers == null)
            {
                return null;
            }

            var sanitized = new Dictionary<string, string>(headers);
            foreach (var key in headers.Keys.ToList())
            {
                var lowerKey = key.ToLowerInvariant();

                if (lowerKey.Contains("authorization") ||
                    lowerKey.Contains("x-api-key") ||
                    lowerKey.Contains("apikey") ||
                    lowerKey.Contains("token") ||
                    lowerKey.Contains("secret") ||
                    lowerKey.Contains("password"))
                {
                    sanitized[key] = "<REDACTED>";
                }
                else if (lowerKey.Contains("cookie"))
                {
                    sanitized[key] = SanitizeCookie(headers[key]);
                }
            }

            return sanitized;
        }

        /// <summary>
        /// Sanitizes cookie strings by redacting sensitive cookies
        /// </summary>
        public static string SanitizeCookie(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie))
            {
                return cookie;
            }

            return CookiePairRegex.Replace(cookie, "${name}=<REDACTED>");
        }

        /// <summary>
        /// Sanitizes an object's string representation
        /// </summary>
        public static string SanitizeObject(object obj)
        {
            if (obj == null)
            {
                return "null";
            }

            var str = obj.ToString();

            // Sanitize common patterns
            str = SanitizeUrl(str);
            str = AuthorizationHeaderRegex.Replace(str, "$1: <REDACTED>");
            str = CookieRegex.Replace(str, "$1=<REDACTED>");
            str = JsonSecretRegex.Replace(str, "$1<REDACTED>$3");

            return str;
        }

        /// <summary>
        /// Creates a sanitized log message from a format string and arguments
        /// </summary>
        public static string SanitizeLogMessage(string format, params object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return SanitizeObject(format);
            }

            var sanitizedArgs = args.Select(arg =>
            {
                return arg is string str ? SanitizeObject(str) : arg;
            }).ToArray();

            return string.Format(format, sanitizedArgs);
        }
    }
}
