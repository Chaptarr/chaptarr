using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace NzbDrone.Host
{
    public static class HttpSecuritySettings
    {
        public const int DefaultRequestsPerMinute = 600;
        public const int MinimumRequestsPerMinute = 60;
        public const int MaximumRequestsPerMinute = 6000;
        public const long DefaultMaxRequestBodySize = 16 * 1024 * 1024;
        public const long MinimumMaxRequestBodySize = 1024 * 1024;
        public const long MaximumMaxRequestBodySize = 128 * 1024 * 1024;

        public static string[] GetAllowedOrigins(IConfiguration configuration)
        {
            return GetListValue(configuration, "AllowedOrigins");
        }

        public static string[] GetTrustedProxies(IConfiguration configuration)
        {
            return GetListValue(configuration, "TrustedProxies");
        }

        public static int GetRequestsPerMinute(IConfiguration configuration)
        {
            var configuredValue = GetValue<int?>(configuration, "RequestsPerMinute");
            return Math.Clamp(configuredValue ?? DefaultRequestsPerMinute, MinimumRequestsPerMinute, MaximumRequestsPerMinute);
        }

        public static long GetMaxRequestBodySize(IConfiguration configuration)
        {
            var configuredValue = GetValue<long?>(configuration, "MaxRequestBodySize");
            return Math.Clamp(configuredValue ?? DefaultMaxRequestBodySize, MinimumMaxRequestBodySize, MaximumMaxRequestBodySize);
        }

        private static T GetValue<T>(IConfiguration configuration, string key)
        {
            var chaptarrKey = $"Chaptarr:Server:{key}";

            if (configuration[chaptarrKey] != null)
            {
                return configuration.GetValue<T>(chaptarrKey);
            }

            return configuration.GetValue<T>($"Readarr:Server:{key}");
        }

        private static string[] GetListValue(IConfiguration configuration, string key)
        {
            var value = configuration[$"Chaptarr:Server:{key}"] ?? configuration[$"Readarr:Server:{key}"] ?? string.Empty;

            return value
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }
    }
}
