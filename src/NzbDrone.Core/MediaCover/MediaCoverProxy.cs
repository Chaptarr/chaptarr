using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        byte[] GetImageFromUrl(string url);
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

        internal static string GetProxyFileName(string url)
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

        public byte[] GetImageFromUrl(string url)
        {
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
                    LogHttpError = false,
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

                    MediaCoverRendition.ValidateImageResponse(imageData, response.Headers?.ContentType);

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
            return MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(url, _configService, out safeUrl, out reason);
        }


    }
}
