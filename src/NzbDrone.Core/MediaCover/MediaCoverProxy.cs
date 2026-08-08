using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.Goodreads;

namespace NzbDrone.Core.MediaCover
{
    public interface IMediaCoverProxy
    {
        string RegisterUrl(string url);
        void ProxyRemoteUrls(IEnumerable<MediaCover> covers);

        string GetUrl(string hash);
        byte[] GetImage(string hash);
    }

    public class MediaCoverProxy : IMediaCoverProxy
    {
        private const int MaxCacheEntries = 20000;
        private const int MaxImageRedirects = 5;
        private const int MaxImageBytes = 20 * 1024 * 1024;
        private static readonly TimeSpan ImageRequestTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClient _httpClient;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IConfigService _configService;
        private readonly ICached<string> _cache;

        public MediaCoverProxy(IHttpClient httpClient, IConfigFileProvider configFileProvider, IConfigService configService, ICacheManager cacheManager)
        {
            _httpClient = httpClient;
            _configFileProvider = configFileProvider;
            _configService = configService;
            _cache = cacheManager.GetCache<string>(GetType());
        }

        public string RegisterUrl(string url)
        {
            if (url.IsNullOrWhiteSpace() || MediaCoverRendition.IsKnownPlaceholderImageUrl(url))
            {
                return null;
            }

            var hash = url.SHA256Hash();

            _cache.ClearExpired();

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache.Set(hash, url, TimeSpan.FromHours(24));

            var fileName = GetProxyFileName(url);
            return _configFileProvider.UrlBase + @"/MediaCoverProxy/" + hash + "/" + fileName;
        }

        private static string GetProxyFileName(string url)
        {
            var fileName = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Path.GetFileName(uri.AbsolutePath)
                : Path.GetFileName(url);

            return MediaCoverRendition.IsSupportedImagePath(fileName)
                ? Uri.EscapeDataString(fileName)
                : "cover.jpg";
        }

        public void ProxyRemoteUrls(IEnumerable<MediaCover> covers)
        {
            if (covers == null)
            {
                return;
            }

            foreach (var cover in covers.Where(cover => cover?.Url.IsNotNullOrWhiteSpace() == true))
            {
                if (cover.Url.StartsWith(_configFileProvider.UrlBase + "/MediaCoverProxy/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Uri.TryCreate(cover.Url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    cover.Url = RegisterUrl(cover.Url);
                }
            }
        }

        public string GetUrl(string hash)
        {
            var result = _cache.Find(hash);

            if (result == null)
            {
                throw new KeyNotFoundException("Url no longer in cache");
            }

            return result;
        }

        public byte[] GetImage(string hash)
        {
            var url = GetUrl(hash);

            if (!TryGetSafeImageUrl(url, out var currentUrl, out var reason))
            {
                throw new WebException($"Blocked unsafe image URL '{url}': {reason}", WebExceptionStatus.ProtocolError);
            }

            // Never leak app identity via default User-Agent when proxying remote images.
            // Goodreads in particular is sensitive to non-browser headers.
            var userAgent = ExternalImageRequestHeaders.IsGoodreadsUrl(currentUrl)
                ? GoodreadsAndroidUserAgent.GetRandom()
                : ExternalImageRequestHeaders.GetRandomUserAgent();

            for (var i = 0; i <= MaxImageRedirects; i++)
            {
                using var responseStream = new SizeLimitedMemoryStream(MaxImageBytes);
                var request = new HttpRequest(currentUrl)
                {
                    AllowAutoRedirect = false,
                    RequestTimeout = ImageRequestTimeout,
                    ResponseStream = responseStream
                };

                ExternalImageRequestHeaders.ApplyExternalImageRequestHeaders(request, currentUrl, userAgent, rangeRequest: false);

                var response = _httpClient.Get(request);

                if (!response.HasHttpRedirect)
                {
                    var imageData = responseStream.Length > 0
                        ? responseStream.ToArray()
                        : response.ResponseData ?? Array.Empty<byte>();

                    if (MediaCoverRendition.InspectDownloadedImage(url, imageData, out var contentHash))
                    {
                        throw new PlaceholderImageException(url, contentHash);
                    }

                    return imageData;
                }

                var location = response.Headers.GetSingleValue("Location");
                if (location.IsNullOrWhiteSpace())
                {
                    throw new WebException($"Redirect response from {currentUrl} is missing a Location header.", WebExceptionStatus.ProtocolError);
                }

                Uri nextUri;
                try
                {
                    nextUri = new Uri(new Uri(currentUrl), location);
                }
                catch (Exception ex)
                {
                    throw new WebException($"Invalid redirect location '{location}' from '{currentUrl}'.", ex, WebExceptionStatus.ProtocolError, null);
                }

                if (!TryGetSafeImageUrl(nextUri.ToString(), out currentUrl, out var nextReason))
                {
                    throw new WebException($"Blocked unsafe image redirect target '{nextUri}': {nextReason}", WebExceptionStatus.ProtocolError);
                }
            }

            throw new WebException($"Too many redirects while downloading image from '{url}'.", WebExceptionStatus.ProtocolError);
        }

        private sealed class SizeLimitedMemoryStream : MemoryStream
        {
            private readonly long _maximumLength;

            public SizeLimitedMemoryStream(long maximumLength)
            {
                _maximumLength = maximumLength;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                EnsureCapacityFor(count);
                base.Write(buffer, offset, count);
            }

            public override System.Threading.Tasks.Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
            {
                EnsureCapacityFor(count);
                return base.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override System.Threading.Tasks.ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            {
                EnsureCapacityFor(buffer.Length);
                return base.WriteAsync(buffer, cancellationToken);
            }

            private void EnsureCapacityFor(int count)
            {
                if (Length + count > _maximumLength)
                {
                    throw new InvalidDataException($"Remote image exceeds the {_maximumLength} byte size limit.");
                }
            }
        }

        private bool TryGetSafeImageUrl(string url, out string safeUrl, out string reason)
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

            if (!IsTrustedCoverHost(uri.Host) && HostResolvesToPrivateOrLocalAddress(uri.Host, out var privateReason))
            {
                reason = privateReason;
                return false;
            }

            safeUrl = uri.ToString();
            return true;
        }

        private bool IsTrustedCoverHost(string host)
        {
            var metadataHost = GetMetadataServerHost();
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

        private string GetMetadataServerHost()
        {
            try
            {
                var url = _configService?.MetadataServerUrl;
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

        private static bool HostResolvesToPrivateOrLocalAddress(string host, out string reason)
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
                try
                {
                    addresses = Dns.GetHostAddresses(host);
                }
                catch
                {
                    reason = "dns_lookup_failed";
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

        private static bool IsPrivateOrLocalAddress(IPAddress ip)
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
                if (ip.Equals(IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
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
