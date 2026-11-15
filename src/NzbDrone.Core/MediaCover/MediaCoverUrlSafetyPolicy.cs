using System;
using System.Net;
using System.Net.Sockets;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaCover
{
    internal sealed class UnsafeImageUrlException : WebException
    {
        public string Url { get; }
        public string Reason { get; }

        public UnsafeImageUrlException(string url, string reason, string noun)
            : base($"Blocked unsafe image {noun} '{CleanseLogMessage.Cleanse(url)}': {reason}", WebExceptionStatus.ProtocolError)
        {
            Url = url;
            Reason = reason;
        }
    }

    internal static class MediaCoverUrlSafetyPolicy
    {
        internal static HttpRequestTargetPolicy CreateTargetPolicy(IConfigService configService)
        {
            var metadataHost = GetMetadataServerHost(configService);
            return new HttpRequestTargetPolicy("image:" + metadataHost?.ToLowerInvariant(),
                uri =>
                {
                    if (!TryGetSafeImageUrl(uri.ToString(), metadataHost, out _, out var reason))
                    {
                        throw BlockedUrl(uri.ToString(), reason);
                    }
                },
                (uri, address) =>
                {
                    if (CloudMetadataTargetPolicy.IsBlocked(address) ||
                        (!IsTrustedCoverHost(uri.Host, metadataHost) && IsPrivateOrLocalAddress(address)))
                    {
                        throw BlockedUrl(uri.ToString(), "private_or_local_address");
                    }
                });
        }

        internal static UnsafeImageUrlException BlockedUrl(string url, string reason, string noun = "URL")
        {
            return new UnsafeImageUrlException(url, reason, noun);
        }

        // Structural checks only. DNS belongs to the selected HTTP route, not
        // cache reuse or a separate preflight disconnected from the actual socket.
        internal static bool TryGetSafeImageUrl(string url, IConfigService configService, out string safeUrl, out string reason)
        {
            return TryGetSafeImageUrl(url, GetMetadataServerHost(configService), out safeUrl, out reason);
        }

        private static bool TryGetSafeImageUrl(string url, string metadataHost, out string safeUrl, out string reason)
        {
            safeUrl = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                reason = "empty_url";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                reason = "invalid_url";
                return false;
            }

            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = "invalid_scheme";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                reason = "userinfo_not_allowed";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                reason = "missing_host";
                return false;
            }

            if (CloudMetadataTargetPolicy.IsBlocked(uri.Host, resolve: false))
            {
                reason = IPAddress.TryParse(uri.Host.Trim('[', ']'), out _) ? "private_or_local_address" : "cloud_metadata_host";
                return false;
            }

            if (!IsTrustedCoverHost(uri.Host, metadataHost) &&
                ((IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IsPrivateOrLocalAddress(address)) || IsLoopbackHost(uri.Host)))
            {
                reason = "private_or_local_address";
                return false;
            }

            safeUrl = uri.ToString();
            return true;
        }

        private static bool IsTrustedCoverHost(string host, string metadataHost)
        {
            if (string.IsNullOrWhiteSpace(metadataHost) || string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (host.Equals(metadataHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // If the user configured the metadata server on loopback, allow loopback cover URLs as well.
            return IsLoopbackHost(metadataHost) && IsLoopbackHost(host);
        }

        private static string GetMetadataServerHost(IConfigService configService)
        {
            try
            {
                var url = configService?.MetadataServerUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                return new Uri(url).Host;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            host = host.Trim().Trim('[', ']');

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        }

        internal static bool IsPrivateOrLocalAddress(IPAddress ip)
        {
            if (ip == null)
            {
                return true;
            }

            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();

                // 0.0.0.0/8 is "this network" and can behave unexpectedly.
                if (bytes[0] == 0)
                {
                    return true;
                }

                // 10.0.0.0/8
                if (bytes[0] == 10)
                {
                    return true;
                }

                // 127.0.0.0/8
                if (bytes[0] == 127)
                {
                    return true;
                }

                // 169.254.0.0/16 (link-local)
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    return true;
                }

                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                {
                    return true;
                }

                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168)
                {
                    return true;
                }

                // 100.64.0.0/10 (carrier-grade NAT)
                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                {
                    return true;
                }

                // Multicast/broadcast/reserved (224.0.0.0/4 + 240.0.0.0/4).
                if (bytes[0] >= 224)
                {
                    return true;
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                {
                    return true;
                }

                // Unique local addresses (fc00::/7)
                var bytes = ip.GetAddressBytes();
                if ((bytes[0] & 0xFE) == 0xFC)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
