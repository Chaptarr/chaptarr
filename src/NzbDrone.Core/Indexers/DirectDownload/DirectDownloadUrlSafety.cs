using System;
using System.Linq;
using System.Net;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public static class DirectDownloadUrlSafety
    {
        public static Uri NormalizeAndValidate(string rawUrl)
        {
            if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri))
            {
                throw Unsafe(rawUrl, "Source URL must be an absolute http or https URL.");
            }

            ValidateAbsoluteHttpOrHttpsUri(uri, rawUrl);

            return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
        }

        public static Uri ValidateAbsoluteHttpOrHttpsUri(Uri uri, string rawUrl = null)
        {
            if (uri == null)
            {
                throw Unsafe(rawUrl, "Source URL is required.");
            }

            if (!uri.IsAbsoluteUri)
            {
                throw Unsafe(rawUrl ?? uri.ToString(), "Source URL must be absolute.");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Unsupported source URL scheme '{uri.Scheme}'. Only http and https are supported.");
            }

            if (uri.UserInfo.IsNotNullOrWhiteSpace())
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' must not include embedded credentials.");
            }

            EnsureSafeHost(uri, rawUrl ?? uri.AbsoluteUri);
            return uri;
        }

        public static void EnsureSafeHost(Uri uri, string rawUrl = null)
        {
            if (uri == null)
            {
                throw Unsafe(rawUrl, "Source URL is required.");
            }

            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' is not allowed.");
            }

            if (IPAddress.TryParse(uri.Host, out var parsedAddress))
            {
                if (IsUnsafeAddress(parsedAddress))
                {
                    throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' is not allowed.");
                }

                return;
            }

            try
            {
                var resolved = Dns.GetHostAddresses(uri.Host);
                if (resolved.Any(IsUnsafeAddress))
                {
                    throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' is not allowed.");
                }
            }
            catch (DirectDownloadProbeException)
            {
                throw;
            }
            catch
            {
            }
        }

        private static bool IsUnsafeAddress(IPAddress address)
        {
            if (address == null)
            {
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            return IPAddress.IsLoopback(address) ||
                   address.IsLocalAddress() ||
                   address.IsCgnatIpAddress() ||
                   CloudMetadataTargetPolicy.IsBlocked(address);
        }

        private static DirectDownloadProbeException Unsafe(string rawUrl, string message)
        {
            return new DirectDownloadProbeException(CleanseLogMessage.Cleanse(message ?? rawUrl ?? "Source URL is not allowed."));
        }
    }
}
