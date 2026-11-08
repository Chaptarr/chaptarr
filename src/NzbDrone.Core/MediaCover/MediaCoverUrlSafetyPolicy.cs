using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaCover
{
    internal static class MediaCoverUrlSafetyPolicy
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        internal static bool TryGetSafeImageUrl(string url, IConfigService configService, out string safeUrl, out string reason,
            CancellationToken cancellationToken = default, Func<string, CancellationToken, Task<IPAddress[]>> resolve = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            if (!IsTrustedCoverHost(uri.Host, configService) && HostResolvesToPrivateOrLocalAddress(uri.Host, out var privateReason, cancellationToken, resolve))
            {
                reason = privateReason;
                return false;
            }

            safeUrl = uri.ToString();
            return true;
        }

        private static bool IsTrustedCoverHost(string host, IConfigService configService)
        {
            var metadataHost = GetMetadataServerHost(configService);
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

        private static bool HostResolvesToPrivateOrLocalAddress(string host, out string reason,
            CancellationToken cancellationToken, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(host))
            {
                reason = "missing_host";
                return true;
            }

            host = host.Trim().Trim('[', ']');

            if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("metadata.google.internal.", StringComparison.OrdinalIgnoreCase))
            {
                reason = "cloud_metadata_host";
                return true;
            }

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var ip))
            {
                addresses = new[] { ip };
            }
            else
            {
                var elapsed = Stopwatch.StartNew();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var pending = resolve == null
                        ? Dns.GetHostAddressesAsync(host, deadline.Token)
                        : resolve(host, deadline.Token);
                    addresses = pending.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    reason = "dns_lookup_timed_out";
                    Logger.Debug("Cover DNS lookup for {0} timed out after {1} ms", host, elapsed.ElapsedMilliseconds);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reason = "dns_lookup_failed";
                    Logger.Debug(ex, "Cover DNS lookup for {0} failed after {1} ms", host, elapsed.ElapsedMilliseconds);
                    return true;
                }
            }

            if (addresses == null || addresses.Length == 0)
            {
                reason = "dns_no_records";
                return true;
            }

            foreach (var address in addresses)
            {
                if (IsPrivateOrLocalAddress(address))
                {
                    reason = "private_or_local_address";
                    return true;
                }
            }

            return false;
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
