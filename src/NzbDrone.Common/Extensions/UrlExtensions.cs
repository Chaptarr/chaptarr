using System;

namespace NzbDrone.Common.Extensions
{
    public static class UrlExtensions
    {
        public static bool IsValidUrl(this string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith(" ") || path.EndsWith(" "))
            {
                return false;
            }

            return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsWellFormedOriginalString();
        }

        public static bool IsValidHttpUrl(this string path)
        {
            if (!path.IsValidUrl())
            {
                return false;
            }

            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
