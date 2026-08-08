using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    public interface IMapCoversToLocal
    {
        void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<MediaCover> covers, string selectedAuthorImageHash = null);
        string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null);
        void EnsureAuthorCovers(Author author);
        void EnsureBookCovers(Book book);
        Task<EnsureImageResult> EnsureAuthorImage(Author author, MediaCover cover);
    }

	    public class MediaCoverService :
	        IHandleAsync<AuthorRefreshCompleteEvent>,
	        IHandleAsync<AuthorDeletedEvent>,
	        IHandleAsync<BookDeletedEvent>,
	        IMapCoversToLocal
	    {

	        private readonly IMediaCoverProxy _mediaCoverProxy;
	        private readonly IImageResizer _resizer;
	        private readonly IBookService _bookService;
	        private readonly IAuthorService _authorService;
	        private readonly IHttpClient _httpClient;
	        private readonly IDiskProvider _diskProvider;
        private readonly ICoverExistsSpecification _coverExistsSpecification;
        private readonly IConfigService _configService;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeferredCoverService _deferredCoverService;
        private readonly Logger _logger;

        private readonly string _coverRootFolder;

        private const int MaxImageRedirects = 5;
        private static readonly TimeSpan ImageRequestTimeout = TimeSpan.FromSeconds(30);

        // ImageSharp is slow on ARM (no hardware acceleration on mono yet)
        // So limit the number of concurrent resizing tasks
        private static SemaphoreSlim _semaphore = new SemaphoreSlim((int)Math.Ceiling(Environment.ProcessorCount / 2.0));
        private readonly ConcurrentDictionary<string, Task<EnsureImageResult>> _inFlightDownloads = new();
        private readonly ConcurrentDictionary<int, byte> _inFlightBookCovers = new();
        private readonly ConcurrentDictionary<string, string> _bookCoverUrlToPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, BookCoverMetadata> _bookCoverMetadataByBookId = new();
        private readonly ConcurrentDictionary<string, string> _authorCoverUrlByAuthorAndType = new(StringComparer.OrdinalIgnoreCase);

        public MediaCoverService(IMediaCoverProxy mediaCoverProxy,
                                 IImageResizer resizer,
                                 IBookService bookService,
                                 IHttpClient httpClient,
                                 IDiskProvider diskProvider,
                                 IAppFolderInfo appFolderInfo,
                                 ICoverExistsSpecification coverExistsSpecification,
                                 IConfigService configService,
                                 IConfigFileProvider configFileProvider,
                                 IEventAggregator eventAggregator,
                                 IDeferredCoverService deferredCoverService,
                                 Logger logger,
                                 IAuthorService authorService = null)
        {
            _mediaCoverProxy = mediaCoverProxy;
            _resizer = resizer;
            _bookService = bookService;
            _authorService = authorService;
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _coverExistsSpecification = coverExistsSpecification;
            _configService = configService;
            _configFileProvider = configFileProvider;
            _eventAggregator = eventAggregator;
            _deferredCoverService = deferredCoverService;
            _logger = logger;

            _coverRootFolder = appFolderInfo.GetMediaCoverPath();
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
                var url = _configService.MetadataServerUrl;
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

        private bool HostResolvesToPrivateOrLocalAddress(string host, out string reason)
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

        private HttpResponse GetImageResponse(string url, bool rangeRequest, string userAgent = null)
        {
            if (!TryGetSafeImageUrl(url, out var currentUrl, out var reason))
            {
                throw new WebException($"Blocked unsafe image URL '{url}': {reason}", WebExceptionStatus.ProtocolError);
            }

            userAgent ??= ExternalImageRequestHeaders.GetRandomUserAgent();

            for (var i = 0; i <= MaxImageRedirects; i++)
            {
                var request = new HttpRequest(currentUrl)
                {
                    AllowAutoRedirect = false,
                    RequestTimeout = ImageRequestTimeout
                };

                ExternalImageRequestHeaders.ApplyExternalImageRequestHeaders(request, currentUrl, userAgent, rangeRequest);

                var response = _httpClient.Get(request);

                if (!response.HasHttpRedirect)
                {
                    return response;
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

        public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null)
        {
            var heightSuffix = height.HasValue ? "-" + height.ToString() : "";

            if (coverEntity == MediaCoverEntity.Book)
            {
                return Path.Combine(GetBookCoverPath(entityId), coverType.ToString().ToLower() + heightSuffix + GetExtension(coverType, extension));
            }

            return Path.Combine(GetAuthorCoverPath(entityId), coverType.ToString().ToLower() + heightSuffix + GetExtension(coverType, extension));
        }

        public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<MediaCover> covers, string selectedAuthorImageHash = null)
        {
            var coverList = covers?.Where(cover => cover != null).ToList() ?? new List<MediaCover>();

            if (entityId == 0)
            {
                // Author isn't in Chaptarr yet, map via a proxy to circument referrer issues
                foreach (var mediaCover in coverList)
                {
                    mediaCover.RemoteUrl = mediaCover.RemoteUrl ?? mediaCover.Url;
                    mediaCover.Url = _mediaCoverProxy.RegisterUrl(mediaCover.RemoteUrl);
                }

                return;
            }

            MediaCover selectedAuthorCover = null;
            if (coverEntity == MediaCoverEntity.Author && !string.IsNullOrWhiteSpace(selectedAuthorImageHash))
            {
                selectedAuthorCover = coverList.FirstOrDefault(cover =>
                    selectedAuthorImageHash.Equals(
                        MediaCoverRendition.ComputeStableAuthorImageHash(cover.RemoteUrl ?? cover.Url, cover.CoverType),
                        StringComparison.OrdinalIgnoreCase));
            }

            foreach (var mediaCover in coverList)
            {
                if (mediaCover.CoverType == MediaCoverTypes.Unknown)
                {
                    continue;
                }

                var remoteUrl = mediaCover.RemoteUrl ?? mediaCover.Url;
                mediaCover.RemoteUrl = remoteUrl;
                string filePath = null;
                string cacheToken = null;

                if (ReferenceEquals(mediaCover, selectedAuthorCover))
                {
                    var coverPath = GetCoverPath(entityId, coverEntity, mediaCover.CoverType, mediaCover.Extension);
                    filePath = GetVariantPath(coverPath, ComputeHash(remoteUrl));
                    cacheToken = selectedAuthorImageHash;
                }
                else if (coverEntity == MediaCoverEntity.Author)
                {
                    // A selected poster is the only local poster exposed. Other carousel
                    // candidates stay remote even if an old on-demand variant still exists.
                    var selectedCoverOwnsType = selectedAuthorCover != null &&
                                                selectedAuthorCover.CoverType == mediaCover.CoverType;

                    if (!selectedCoverOwnsType &&
                        TryReadAuthorCoverIdentity(entityId, mediaCover.CoverType, out var storedUrl, out var contentHash) &&
                        !string.IsNullOrWhiteSpace(remoteUrl) &&
                        storedUrl.Equals(remoteUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        filePath = GetDeterministicLocalCoverPath(entityId, coverEntity, mediaCover);
                        cacheToken = contentHash;
                    }
                }
                else if (coverEntity == MediaCoverEntity.Book && BookCoverMatchesRemoteUrl(entityId, remoteUrl, out cacheToken))
                {
                    filePath = GetDeterministicLocalCoverPath(entityId, coverEntity, mediaCover);
                }

                if (filePath != null)
                {
                    var fileName = Path.GetFileName(filePath);
                    var localPath = coverEntity == MediaCoverEntity.Book
                        ? @"/MediaCover/Books/" + entityId + "/" + fileName
                        : @"/MediaCover/" + entityId + "/" + fileName;

                    mediaCover.Url = _configFileProvider.UrlBase + localPath;
                    if (!string.IsNullOrWhiteSpace(cacheToken))
                    {
                        mediaCover.Url += "?v=" + cacheToken;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(remoteUrl) && remoteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    mediaCover.Url = remoteUrl;
                    _mediaCoverProxy.ProxyRemoteUrls(new[] { mediaCover });
                }
            }
        }

        private string GetDeterministicLocalCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCover mediaCover)
        {
            var height = coverEntity == MediaCoverEntity.Author
                ? MediaCoverRendition.GetHeights(mediaCover.CoverType).FirstOrDefault()
                : 0;

            return GetCoverPath(
                entityId,
                coverEntity,
                mediaCover.CoverType,
                mediaCover.Extension,
                height > 0 ? height : null);
        }

        private string GetAuthorCoverPath(int authorId)
        {
            return Path.Combine(_coverRootFolder, authorId.ToString());
        }

        private string GetBookCoverPath(int bookId)
        {
            return Path.Combine(_coverRootFolder, "Books", bookId.ToString());
        }

        private string GetAuthorCoverIdentityPath(int authorId, MediaCoverTypes coverType)
        {
            return Path.Combine(
                GetAuthorCoverPath(authorId),
                MediaCoverRendition.GetAuthorCoverIdentityFileName(coverType));
        }

        private static string GetAuthorCoverIdentityCacheKey(int authorId, MediaCoverTypes coverType)
        {
            return $"{authorId}:{(int)coverType}";
        }

        private bool TryReadAuthorCoverIdentity(int authorId, MediaCoverTypes coverType, out string remoteUrl, out string contentHash)
        {
            var cacheKey = GetAuthorCoverIdentityCacheKey(authorId, coverType);
            var storedIdentity = _authorCoverUrlByAuthorAndType.GetOrAdd(cacheKey, _ =>
            {
                var identityPath = GetAuthorCoverIdentityPath(authorId, coverType);
                if (!_diskProvider.FileExists(identityPath))
                {
                    return string.Empty;
                }

                try
                {
                    return _diskProvider.ReadAllText(identityPath)?.Trim() ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            });

            return MediaCoverRendition.TryParseAuthorCoverIdentity(storedIdentity, out remoteUrl, out contentHash);
        }

        private bool AuthorCoverMatchesRemoteUrl(int authorId, MediaCoverTypes coverType, string remoteUrl)
        {
            return TryReadAuthorCoverIdentity(authorId, coverType, out var storedUrl, out _) &&
                   !string.IsNullOrWhiteSpace(remoteUrl) &&
                   storedUrl.Equals(remoteUrl, StringComparison.OrdinalIgnoreCase);
        }

        private void StoreAuthorCoverIdentity(int authorId, MediaCover cover, string contentHash)
        {
            if (cover == null || string.IsNullOrWhiteSpace(cover.Url))
            {
                return;
            }

            try
            {
                var identityPath = GetAuthorCoverIdentityPath(authorId, cover.CoverType);
                var identity = MediaCoverRendition.BuildAuthorCoverIdentity(cover.Url, contentHash);
                _diskProvider.EnsureFolder(Path.GetDirectoryName(identityPath));
                _diskProvider.WriteAllText(identityPath, identity);
                _authorCoverUrlByAuthorAndType[GetAuthorCoverIdentityCacheKey(authorId, cover.CoverType)] = identity;
            }
            catch (Exception ex)
            {
                // Without the identity sidecar, mapping deliberately keeps using the
                // privacy proxy rather than risk labeling stale bytes as another photo.
                _logger.Debug(ex, "Failed to store author-cover identity for AuthorId={0}, CoverType={1}", authorId, cover.CoverType);
            }
        }

        public void EnsureAuthorCovers(Author author)
        {
            _logger.Debug("EnsureAuthorCovers called for author: {0} (ID: {1})", author.Name, author.Id);
            _logger.Debug("Author has {0} images in metadata", author.Images?.Count ?? 0);

            // Null guard to prevent NullReferenceException when Images is null
            if (author.Images == null || author.Images.Count == 0)
            {
                _logger.Debug("No images for author {0} (ID: {1}), skipping cover download", author.Name, author.Id);
                return;
            }

            var toResize = new List<(MediaCover Cover, bool AlreadyExists, string ContentHash)>();
            var rejectedCoverTypes = new HashSet<MediaCoverTypes>();
            var retainedCoverTypes = new HashSet<MediaCoverTypes>();

            // There is one canonical local filename per cover type. Try providers in
            // server order, but stop after the first successful local file so a broken
            // preferred URL cannot block repair and later alternatives cannot overwrite it.
            var coverGroups = MediaCoverRendition.SelectCandidates(author.Images)
                .GroupBy(cover => cover.CoverType)
                .ToList();

            foreach (var coverGroup in coverGroups)
            {
                foreach (var cover in coverGroup)
                {
                    if (!TryGetSafeImageUrl(cover.Url, out _, out var unsafeReason))
                    {
                        _logger.Debug("Skipping unsafe author cover URL for {0} (ID: {1}): {2} ({3})", author.Name, author.Id, cover.Url, unsafeReason);
                        continue;
                    }

                    var fileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);
                    var alreadyExists = false;
                    var selectedUrlAlreadyStored = AuthorCoverMatchesRemoteUrl(author.Id, cover.CoverType, cover.Url);
                    string contentHash = null;

                    _logger.Debug("Processing cover type {0} for author {1}, URL: {2}", cover.CoverType, author.Name, cover.Url);

                    try
                    {
                        if (selectedUrlAlreadyStored &&
                            MediaCoverRendition.HasAllGeneratedRenditions(
                                height => GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height),
                                _diskProvider,
                                cover.CoverType))
                        {
                            retainedCoverTypes.Add(cover.CoverType);
                            break;
                        }

                        if (selectedUrlAlreadyStored)
                        {
                            TryReadAuthorCoverIdentity(author.Id, cover.CoverType, out _, out contentHash);
                        }

                        var serverFileHeaders = GetServerHeaders(cover.Url);

                        alreadyExists = selectedUrlAlreadyStored &&
                                        _coverExistsSpecification != null &&
                                        _coverExistsSpecification.AlreadyExists(serverFileHeaders.LastModified, GetContentLength(serverFileHeaders), fileName);

                        if (!alreadyExists)
                        {
                            contentHash = DownloadCover(author, cover, serverFileHeaders.LastModified ?? DateTime.Now);

                            if (contentHash == null)
                            {
                                rejectedCoverTypes.Add(cover.CoverType);
                                RejectPlaceholderAuthorImage(author, cover.Url);
                                continue;
                            }
                        }
                    }
                    catch (HttpException e) when (e.Response?.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _logger.Warn("Author cover download blocked (403 Forbidden) for {0} from URL: {1}", author.Name, cover.Url);
                    }
                    catch (HttpException e) when (e.Response?.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.Warn("Author cover not found (404) for {0} from URL: {1}", author.Name, cover.Url);
                    }
                    catch (HttpException e)
                    {
                        _logger.Warn("HTTP error downloading author cover for {0}. Status: {1}, URL: {2}", author.Name, e.Response?.StatusCode, cover.Url);
                    }
                    catch (WebException e)
                    {
                        _logger.Warn("Network error downloading author cover for {0}. Error: {1}, URL: {2}", author.Name, e.Message, cover.Url);
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Unexpected error downloading author cover for {0} from URL: {1}", author.Name, cover.Url);
                    }

                    if (string.IsNullOrWhiteSpace(contentHash) ||
                        !MediaCoverRendition.IsUsable(fileName, _diskProvider))
                    {
                        continue;
                    }

                    toResize.Add((cover, alreadyExists, contentHash));
                    retainedCoverTypes.Add(cover.CoverType);
                    break;
                }
            }

            // A rejected preferred URL must not erase an already-verified real fallback.
            // Remove the canonical family only when no candidate for that cover type was
            // retained or successfully downloaded.
            foreach (var rejectedCoverType in rejectedCoverTypes.Except(retainedCoverTypes))
            {
                RemoveCanonicalAuthorCoverArtifacts(author.Id, rejectedCoverType);
            }

            try
            {
                _semaphore.Wait();

                foreach (var tuple in toResize)
                {
                    EnsureResizedCovers(author, tuple.Cover, !tuple.AlreadyExists);
                    StoreAuthorCoverIdentity(author.Id, tuple.Cover, tuple.ContentHash);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void RejectPlaceholderAuthorImage(Author author, string rejectedUrl)
        {
            if (author?.Images == null || string.IsNullOrWhiteSpace(rejectedUrl))
            {
                return;
            }

            var before = author.Images.Count;
            author.Images = author.Images
                .Where(image => image != null && !MediaCoverRendition.IsKnownPlaceholderImageUrl(image.Url))
                .ToList();

            if (author.Images.Count == before)
            {
                return;
            }

            _logger.Info("[AUTHOR-COVER-PLACEHOLDER] Removed provider placeholder image for {0} ({1}) after content verification", author.Name, author.Id);

            if (_authorService == null)
            {
                return;
            }

            try
            {
                _authorService.UpdateAuthor(author);
            }
            catch (Exception ex)
            {
                // Display remains safe because the process-wide content verdict is already
                // active. Startup/daily repair will retry the durable row scrub.
                _logger.Warn(ex, "[AUTHOR-COVER-PLACEHOLDER] Failed to persist placeholder removal for author {0}", author.Id);
            }
        }

        private void RemoveCanonicalAuthorCoverArtifacts(int authorId, MediaCoverTypes coverType)
        {
            var folder = GetAuthorCoverPath(authorId);
            var prefix = coverType.ToString().ToLowerInvariant();

            _authorCoverUrlByAuthorAndType.TryRemove(GetAuthorCoverIdentityCacheKey(authorId, coverType), out _);

            try
            {
                if (!_diskProvider.FolderExists(folder))
                {
                    return;
                }

                foreach (var file in _diskProvider.GetFiles(folder, recursive: false))
                {
                    var fileName = Path.GetFileName(file);
                    var stem = Path.GetFileNameWithoutExtension(fileName);
                    var isIdentity = fileName.Equals(MediaCoverRendition.GetAuthorCoverIdentityFileName(coverType), StringComparison.OrdinalIgnoreCase);
                    var isCanonicalImage = MediaCoverRendition.IsSupportedImagePath(fileName) &&
                                           (stem.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                                            MediaCoverRendition.GetHeights(coverType).Any(height =>
                                                stem.Equals($"{prefix}-{height}", StringComparison.OrdinalIgnoreCase)));

                    if (isIdentity || isCanonicalImage)
                    {
                        _diskProvider.DeleteFile(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to remove rejected canonical author-cover artifacts for AuthorId={0}, CoverType={1}", authorId, coverType);
            }
        }

        public async Task<EnsureImageResult> EnsureAuthorImage(Author author, MediaCover cover)
        {
            if (author == null || cover == null || string.IsNullOrWhiteSpace(cover.Url))
            {
                return new EnsureImageResult { State = "error", ErrorCode = "invalid_arguments" };
            }

            if (MediaCoverRendition.IsKnownPlaceholderImageUrl(cover.Url))
            {
                RejectPlaceholderAuthorImage(author, cover.Url);
                return new EnsureImageResult { State = "error", ErrorCode = "placeholder_image" };
            }

            if (!TryGetSafeImageUrl(cover.Url, out _, out var unsafeReason))
            {
                var code = unsafeReason is "invalid_url" or "invalid_scheme" or "empty_url" or "missing_host" or "userinfo_not_allowed"
                    ? "invalid_url"
                    : "unsafe_url";
                return new EnsureImageResult { State = "error", ErrorCode = code };
            }

            var hash = ComputeHash(cover.Url);
            if (string.IsNullOrWhiteSpace(hash))
            {
                return new EnsureImageResult { State = "error", ErrorCode = "invalid_url" };
            }

            var key = $"{author.Id}:{hash}";

            var downloadTask = _inFlightDownloads.GetOrAdd(key, _ => DoEnsureAuthorImage(author, cover, hash));

            try
            {
                return await downloadTask;
            }
            finally
            {
                _inFlightDownloads.TryRemove(new KeyValuePair<string, Task<EnsureImageResult>>(key, downloadTask));
            }
        }

        private async Task<EnsureImageResult> DoEnsureAuthorImage(Author author, MediaCover cover, string hash)
        {
            try
            {
                await _semaphore.WaitAsync();

                var coverPath = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);
                var variantPath = GetVariantPath(coverPath, hash);
                var verificationPath = GetVariantVerificationPath(variantPath);

                if (_diskProvider.FileExists(variantPath) && _diskProvider.GetFileSize(variantPath) > 0)
                {
                    if (MediaCoverRendition.StoredContentHashIsVerified(verificationPath, _diskProvider))
                    {
                        return new EnsureImageResult { State = "downloaded", Path = variantPath };
                    }

                    if (TryVerifyExistingAuthorVariant(variantPath, verificationPath, cover.Url, out var existingIsPlaceholder))
                    {
                        return new EnsureImageResult { State = "downloaded", Path = variantPath };
                    }

                    if (existingIsPlaceholder)
                    {
                        RejectPlaceholderAuthorImage(author, cover.Url);
                        return new EnsureImageResult { State = "error", ErrorCode = "placeholder_image" };
                    }
                }

                var directory = Path.GetDirectoryName(variantPath);
                _diskProvider.EnsureFolder(directory);

                HttpResponse response;

                try
                {
                    response = GetImageResponse(cover.Url, rangeRequest: false);
                }
                catch (HttpException e) when (e.Response?.StatusCode == HttpStatusCode.Forbidden || e.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Warn("On-demand author image download failed ({0}) for {1} from URL: {2}", e.Response?.StatusCode, author.Name, cover.Url);
                    return new EnsureImageResult { State = "error", ErrorCode = "download_failed" };
                }

                if (response?.ResponseData == null || response.ResponseData.Length == 0)
                {
                    return new EnsureImageResult { State = "error", ErrorCode = "empty_response" };
                }

                if (MediaCoverRendition.InspectDownloadedImage(cover.Url, response.ResponseData, out var contentHash))
                {
                    DeleteAuthorVariant(variantPath, verificationPath);
                    RejectPlaceholderAuthorImage(author, cover.Url);
                    return new EnsureImageResult { State = "error", ErrorCode = "placeholder_image" };
                }

                using (var stream = new MemoryStream(response.ResponseData))
                {
                    _diskProvider.SaveStream(stream, variantPath);
                }

                _diskProvider.WriteAllText(verificationPath, contentHash);

                try
                {
                    _diskProvider.FileSetLastWriteTime(variantPath, DateTime.Now);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to set modified date for on-demand author image for {0}", author.Name);
                }

                return new EnsureImageResult { State = "downloaded", Path = variantPath };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to ensure author image for {0}", author.Name);
                return new EnsureImageResult { State = "error", ErrorCode = ex.GetType().Name };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private bool TryVerifyExistingAuthorVariant(string variantPath, string verificationPath, string remoteUrl, out bool isPlaceholder)
        {
            isPlaceholder = false;

            try
            {
                using var stream = _diskProvider.OpenReadStream(variantPath);
                using var sha256 = SHA256.Create();
                var contentHash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
                isPlaceholder = MediaCoverRendition.RegisterKnownPlaceholderImage(remoteUrl, contentHash);

                if (isPlaceholder)
                {
                    DeleteAuthorVariant(variantPath, verificationPath);
                    return false;
                }

                _diskProvider.WriteAllText(verificationPath, contentHash);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to verify existing on-demand author image {0}; it will be downloaded again", variantPath);
                return false;
            }
        }

        private void DeleteAuthorVariant(string variantPath, string verificationPath)
        {
            try
            {
                if (_diskProvider.FileExists(variantPath))
                {
                    _diskProvider.DeleteFile(variantPath);
                }

                if (_diskProvider.FileExists(verificationPath))
                {
                    _diskProvider.DeleteFile(verificationPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to remove rejected on-demand author image {0}", variantPath);
            }
        }

        public void EnsureBookCovers(Book book)
        {
            if (book == null || book.Id == 0)
            {
                return;
            }

            // Atomically queue the cover when downloads are deferred. Import callers can
            // legitimately hand us a null/unsaved book during rollback.
            if (_deferredCoverService.MarkBookForCoverDownload(book.Id))
            {
                _logger.Debug("Cover download deferred for book {0}: {1}", book.Id, book.Title);
                return;
            }

            // Avoid duplicate in-flight downloads for the same book (can be triggered by multiple events/UI requests).
            if (!_inFlightBookCovers.TryAdd(book.Id, 0))
            {
                _logger.Trace("Book cover download already in-flight for book {0}: {1}", book.Id, book.Title);
                return;
            }

            try
            {
                if (book.Editions == null)
                {
                    // A caller that did not hydrate editions cannot prove which cover
                    // is selected and therefore has no authority to replace or remove it.
                    _logger.Debug("Book {0} ({1}) has no hydrated editions; skipping cover reconciliation", book.Title, book.Id);
                    return;
                }

                var monitoredEditionCount = book.Editions?.Count(edition => edition?.Monitored == true) ?? 0;
                if (monitoredEditionCount > 1)
                {
                    _logger.Warn("Book {0} ({1}) has {2} monitored editions; cover selection will mirror the display path's deterministic monitored-edition choice", book.Title, book.Id, monitoredEditionCount);
                }

                // One persistent cover per book, sourced only from the monitored edition.
                // Multiple image URLs on that edition are provider fallbacks, not alternate editions.
                var candidates = MediaCoverRendition.SelectMonitoredBookCovers(book);
                if (candidates.Count == 0)
                {
                    _logger.Debug("No monitored-edition cover found for book {0} ({1})", book.Title, book.Id);
                    RemoveBookCoverArtifactsForProvenEditionChange(book);
                    return;
                }

                foreach (var candidate in candidates)
                {
                    if (!TryGetSafeImageUrl(candidate.Cover.Url, out _, out var unsafeReason))
                    {
                        _logger.Debug("Skipping unsafe monitored-edition cover URL for book {0}: {1} ({2})", book.Title, candidate.Cover.Url, unsafeReason);
                        continue;
                    }

                    try
                    {
                        if (EnsureMonitoredBookCover(book, candidate))
                        {
                            return;
                        }
                    }
                    catch (HttpException e) when (e.Response?.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _logger.Warn("Book cover download blocked (403 Forbidden) for {0} from URL: {1}", book, candidate.Cover.Url);
                    }
                    catch (HttpException e)
                    {
                        _logger.Warn("Couldn't download media cover for {0} from {1}. {2}", book, candidate.Cover.Url, e.Message);
                    }
                    catch (WebException e)
                    {
                        _logger.Warn("Couldn't download media cover for {0} from {1}. {2}", book, candidate.Cover.Url, e.Message);
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Couldn't download media cover for {0} from {1}", book, candidate.Cover.Url);
                    }
                }

                // A provider/CDN failure is not evidence that the selected edition
                // changed. Keep the last usable cover for the same edition; delete it
                // only when the sidecar proves it belongs to a different edition.
                RemoveBookCoverArtifactsForProvenEditionChange(book);
            }
            finally
            {
                _inFlightBookCovers.TryRemove(book.Id, out _);
            }
        }

        private bool EnsureMonitoredBookCover(Book book, BookCoverSelection selection)
        {
            var cover = selection.Cover;
            var fileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);
            var selectedUrlAlreadyStored = BookCoverMatchesRemoteUrl(book.Id, cover.Url);
            var hasOriginal = MediaCoverRendition.IsUsable(fileName, _diskProvider);

            if (selectedUrlAlreadyStored &&
                MediaCoverRendition.HasAllRenditions(
                    height => GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, height),
                    _diskProvider,
                    cover.CoverType))
            {
                _logger.Trace("Book {0} already has the monitored-edition cover and all renditions", book.Title);
                return true;
            }

            var downloadedOrReused = false;
            if (!selectedUrlAlreadyStored || !hasOriginal)
            {
                var urlHash = ComputeHash(cover.Url);
                if (!string.IsNullOrWhiteSpace(urlHash) &&
                    _bookCoverUrlToPath.TryGetValue(urlHash, out var cachedPath) &&
                    MediaCoverRendition.IsUsable(cachedPath, _diskProvider))
                {
                    ReplaceFileWithCopy(cachedPath, fileName);
                    downloadedOrReused = true;
                    _logger.Debug("Reused monitored-edition cover for '{0}' from cached file '{1}'", book.Title, cachedPath);
                }
                else
                {
                    DownloadBookCover(book, cover, DateTime.Now);
                    downloadedOrReused = true;
                }
            }

            if (!MediaCoverRendition.IsUsable(fileName, _diskProvider))
            {
                return false;
            }

            EnsureResizedBookCovers(book, cover, downloadedOrReused);
            StoreEditionCoverMetadata(book, selection);
            CleanupOtherBookCoverFiles(book.Id, cover);

            var storedHash = ComputeHash(cover.Url);
            if (!string.IsNullOrWhiteSpace(storedHash))
            {
                _bookCoverUrlToPath[storedHash] = fileName;
            }

            return true;
        }

	        private sealed class BookCoverMetadata
	        {
	            public BookCoverMetadataSelectedEdition SelectedEdition { get; set; }
	        }

	        private sealed class BookCoverMetadataSelectedEdition
	        {
	            public string EditionProviderId { get; set; }
	            public string CoverUrl { get; set; }
	            public DateTime? DownloadedAt { get; set; }
	        }

	        private BookCoverMetadata TryReadBookCoverMetadata(int bookId)
	        {
	            if (_bookCoverMetadataByBookId.TryGetValue(bookId, out var cachedMetadata))
	            {
	                return cachedMetadata.SelectedEdition == null ? null : cachedMetadata;
	            }

	            try
	            {
	                var metadataPath = Path.Combine(GetBookCoverPath(bookId), "cover-metadata.json");
	                if (!_diskProvider.FileExists(metadataPath))
	                {
	                    _bookCoverMetadataByBookId[bookId] = new BookCoverMetadata();
	                    return null;
	                }

	                var json = _diskProvider.ReadAllText(metadataPath);
	                var metadata = string.IsNullOrWhiteSpace(json)
	                    ? null
	                    : Json.Deserialize<BookCoverMetadata>(json);
	                _bookCoverMetadataByBookId[bookId] = metadata ?? new BookCoverMetadata();
	                return metadata;
	            }
	            catch
	            {
	                _bookCoverMetadataByBookId[bookId] = new BookCoverMetadata();
	                return null;
	            }
	        }

        private void RemoveBookCoverArtifactsForProvenEditionChange(Book book)
        {
            var storedProviderId = TryReadBookCoverMetadata(book.Id)?.SelectedEdition?.EditionProviderId;
            var monitoredEdition = BookEditionIdentity.GetMonitoredEdition(book);
            var monitoredProviderIds = BookEditionIdentity.GetEditionProviderIds(monitoredEdition);

            if (string.IsNullOrWhiteSpace(storedProviderId) ||
                monitoredProviderIds.Count == 0 ||
                monitoredProviderIds.Contains(storedProviderId, StringComparer.OrdinalIgnoreCase))
            {
                _logger.Debug("Retaining the last usable cover for book {0} ({1}); no different monitored-edition identity was proven", book.Title, book.Id);
                return;
            }

            _logger.Debug("Removing the stale cover for book {0} ({1}); sidecar edition {2} differs from the monitored edition", book.Title, book.Id, storedProviderId);
            RemoveBookCoverArtifacts(book.Id);
        }

        private bool BookCoverMatchesRemoteUrl(int bookId, string remoteUrl)
        {
            return BookCoverMatchesRemoteUrl(bookId, remoteUrl, out _);
        }

        private bool BookCoverMatchesRemoteUrl(int bookId, string remoteUrl, out string cacheToken)
        {
            var selectedEdition = TryReadBookCoverMetadata(bookId)?.SelectedEdition;
            var storedCoverUrl = selectedEdition?.CoverUrl;
            cacheToken = selectedEdition?.DownloadedAt?.Ticks.ToString() ?? ComputeHash(storedCoverUrl);

            return !string.IsNullOrWhiteSpace(storedCoverUrl) &&
                   !string.IsNullOrWhiteSpace(remoteUrl) &&
                   storedCoverUrl.Equals(remoteUrl, StringComparison.OrdinalIgnoreCase);
        }

        private void RemoveBookCoverArtifacts(int bookId)
        {
            _bookCoverMetadataByBookId.TryRemove(bookId, out _);

            var coverPath = GetBookCoverPath(bookId);
            if (!_diskProvider.FolderExists(coverPath))
            {
                return;
            }

            try
            {
                _diskProvider.DeleteFolder(coverPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to remove stale book-cover artifacts for BookId={0}", bookId);
            }
        }

        private void CleanupOtherBookCoverFiles(int bookId, MediaCover cover)
	        {
	            // Best-effort cleanup: keep the selected original, its generated renditions,
            // and metadata. Stale extensions from a previous monitored edition must go.
	            try
	            {
	                var coverFolder = GetBookCoverPath(bookId);
	                if (!_diskProvider.FolderExists(coverFolder))
	                {
	                    return;
	                }

	                var keepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFileName(GetCoverPath(bookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null)),
                    "cover-metadata.json"
                };

                foreach (var height in MediaCoverRendition.GetHeights(cover.CoverType))
                {
                    keepNames.Add(Path.GetFileName(GetCoverPath(bookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension, height)));
                }

	                foreach (var file in _diskProvider.GetFiles(coverFolder, recursive: false))
	                {
	                    var name = Path.GetFileName(file);
	                    if (string.IsNullOrWhiteSpace(name))
	                    {
	                        continue;
	                    }

	                    if (keepNames.Contains(name))
	                    {
	                        continue;
	                    }

	                    // Only delete cover image files (avoid touching unrelated artifacts).
	                    if (name.StartsWith("cover", StringComparison.OrdinalIgnoreCase) &&
	                        !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
	                    {
	                        try
	                        {
	                            _diskProvider.DeleteFile(file);
	                        }
	                        catch
	                        {
	                            // best-effort only
	                        }
	                    }
	                }
	            }
	            catch
	            {
	                // best-effort only
	            }
	        }

        private string DownloadCover(Author author, MediaCover cover, DateTime lastModified)
        {
            var fileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);

            // Ensure the directory exists before downloading
            var directory = Path.GetDirectoryName(fileName);
            _diskProvider.EnsureFolder(directory);

            _logger.Debug("Downloading {0} for {1} {2}", cover.CoverType, author, cover.Url);
            var response = GetImageResponse(cover.Url, rangeRequest: false);

            if (response?.ResponseData == null || response.ResponseData.Length == 0)
            {
                throw new InvalidDataException($"Author cover response from '{cover.Url}' was empty");
            }

            if (MediaCoverRendition.InspectDownloadedImage(cover.Url, response.ResponseData, out var contentHash))
            {
                _logger.Info("[AUTHOR-COVER-PLACEHOLDER] Rejected known provider placeholder bytes for {0} ({1}) from {2}", author.Name, author.Id, cover.Url);
                return null;
            }

            // Write the response data to file
            using (var stream = new MemoryStream(response.ResponseData))
            {
                _diskProvider.SaveStream(stream, fileName);
            }

            try
            {
                _diskProvider.FileSetLastWriteTime(fileName, lastModified);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to set modified date for {0} image for author {1}", cover.CoverType, author);
            }

            return contentHash;
        }

        private void DownloadBookCover(Book book, MediaCover cover, DateTime lastModified)
        {
            var fileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);

            // Ensure the directory exists before downloading
            var directory = Path.GetDirectoryName(fileName);
            _diskProvider.EnsureFolder(directory);

            _logger.Debug("Downloading {0} for {1} {2}", cover.CoverType, book, cover.Url);
            var response = GetImageResponse(cover.Url, rangeRequest: false);

            if (response?.ResponseData == null || response.ResponseData.Length == 0)
            {
                throw new InvalidDataException($"Cover response from '{cover.Url}' was empty");
            }

            if (response.Headers?.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
                response.Headers?.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidDataException($"Cover response from '{cover.Url}' had non-image content type '{response.Headers.ContentType}'");
            }

            var temporaryPath = fileName + ".download-" + Guid.NewGuid().ToString("N");
            try
            {
                // Write and then replace so an old hardlinked cover from an earlier build
                // cannot be modified in-place when the monitored edition changes.
                using (var stream = new MemoryStream(response.ResponseData))
                {
                    _diskProvider.SaveStream(stream, temporaryPath);
                }

                _diskProvider.MoveFile(temporaryPath, fileName, overwrite: true);
            }
            finally
            {
                if (_diskProvider.FileExists(temporaryPath))
                {
                    _diskProvider.DeleteFile(temporaryPath);
                }
            }

            // Cache by URL hash so other book instances can reuse without re-downloading.
            try
            {
                var urlHash = ComputeHash(cover.Url);
                if (!string.IsNullOrWhiteSpace(urlHash))
                {
                    _bookCoverUrlToPath[urlHash] = fileName;
                }
            }
            catch
            {
                // best-effort only
            }

            try
            {
                _diskProvider.FileSetLastWriteTime(fileName, lastModified);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to set modified date for {0} image for book {1}", cover.CoverType, book);
            }
        }

        private void ReplaceFileWithCopy(string source, string destination)
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                _diskProvider.EnsureFolder(directory);
            }

            var temporaryPath = destination + ".copy-" + Guid.NewGuid().ToString("N");
            try
            {
                _diskProvider.CopyFile(source, temporaryPath, overwrite: false);
                _diskProvider.MoveFile(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (_diskProvider.FileExists(temporaryPath))
                {
                    _diskProvider.DeleteFile(temporaryPath);
                }
            }
        }

        private void EnsureResizedCovers(Author author, MediaCover cover, bool forceResize)
        {
            var heights = MediaCoverRendition.GetHeights(cover.CoverType);
            var mainFileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);
            var allResizeSuccessful = true;

            foreach (var height in heights)
            {
                var resizeFileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height);

                if (forceResize || !_diskProvider.FileExists(resizeFileName) || _diskProvider.GetFileSize(resizeFileName) == 0)
                {
                    _logger.Debug("Resizing {0}-{1} for {2}", cover.CoverType, height, author);

                    try
                    {
                        _resizer.Resize(mainFileName, resizeFileName, height);
                    }
                    catch
                    {
                        _logger.Debug("Couldn't resize media cover {0}-{1} for author {2}, using full size image instead.", cover.CoverType, height, author);
                        allResizeSuccessful = false;
                    }
                }
            }

            // Delete original author image after successful resizing to save storage space
            // We can always re-download via author refresh if needed later
            if (allResizeSuccessful && _diskProvider.FileExists(mainFileName))
            {
                try
                {
                    _logger.Debug("Deleting original author image {0} after successful resize", mainFileName);
                    _diskProvider.DeleteFile(mainFileName);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to delete original author image {0}", mainFileName);
                }
            }
        }

        private void EnsureResizedBookCovers(Book book, MediaCover cover, bool forceResize)
        {
            var mainFileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension);

            try
            {
                _semaphore.Wait();

                foreach (var height in MediaCoverRendition.GetHeights(cover.CoverType))
                {
                    var resizeFileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, height);
                    if (!forceResize && MediaCoverRendition.IsUsable(resizeFileName, _diskProvider))
                    {
                        continue;
                    }

                    var temporaryPath = resizeFileName + ".resize-" + Guid.NewGuid().ToString("N") + Path.GetExtension(resizeFileName);
                    try
                    {
                        _logger.Debug("Resizing {0}-{1} for book {2}", cover.CoverType, height, book);
                        _resizer.Resize(mainFileName, temporaryPath, height);
                        _diskProvider.MoveFile(temporaryPath, resizeFileName, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Couldn't resize media cover {0}-{1} for book {2}; the original remains available as a fallback", cover.CoverType, height, book);
                    }
                    finally
                    {
                        if (_diskProvider.FileExists(temporaryPath))
                        {
                            _diskProvider.DeleteFile(temporaryPath);
                        }
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private string GetExtension(MediaCoverTypes coverType, string defaultExtension)
        {
            return coverType switch
            {
                MediaCoverTypes.Clearlogo => ".png",
                _ => defaultExtension
            };
        }

        private string ComputeHash(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(url));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private string GetVariantPath(string coverPath, string hash)
        {
            var directory = Path.GetDirectoryName(coverPath);
            var extension = Path.GetExtension(coverPath);
            var baseName = Path.GetFileNameWithoutExtension(coverPath);
            var shortHash = hash?.Length >= 16 ? hash.Substring(0, 16) : hash;
            var filename = $"{baseName}-{shortHash}{extension}";
            return Path.Combine(directory, filename);
        }

        private static string GetVariantVerificationPath(string variantPath)
        {
            return variantPath + ".sha256";
        }

        private HttpHeader GetServerHeaders(string url)
        {
            // Goodreads doesn't allow a HEAD, so request a zero byte range instead.
            return GetImageResponse(url, rangeRequest: true).Headers;
        }

        private long? GetContentLength(HttpHeader headers)
        {
            var range = headers.Get("content-range");

            if (range == null)
            {
                return null;
            }

            var split = range.Split('/');
            if (split.Length == 2 && long.TryParse(split[1], out var length))
            {
                return length;
            }

            return null;
        }

	        public void HandleAsync(AuthorRefreshCompleteEvent message)
	        {
	            EnsureAuthorCovers(message.Author);

                // Restore Readarr's eager child-cover lifecycle, while Chaptarr's
                // selector restricts persistence to each book's monitored edition.
                foreach (var book in _bookService.GetBooksByAuthor(message.Author.Id))
                {
                    EnsureBookCovers(book);
                }

	            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(message.Author));
	        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            var cachePrefix = message.Author.Id + ":";
            foreach (var cacheKey in _authorCoverUrlByAuthorAndType.Keys.Where(key => key.StartsWith(cachePrefix, StringComparison.Ordinal)))
            {
                _authorCoverUrlByAuthorAndType.TryRemove(cacheKey, out _);
            }

            var path = GetAuthorCoverPath(message.Author.Id);
            if (_diskProvider.FolderExists(path))
            {
                _diskProvider.DeleteFolder(path, true);
            }
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            _bookCoverMetadataByBookId.TryRemove(message.Book.Id, out _);
            var path = GetBookCoverPath(message.Book.Id);
            if (_diskProvider.FolderExists(path))
            {
                _diskProvider.DeleteFolder(path, true);
            }
        }

	        private static bool IsAudiobookEdition(Edition edition)
	        {
	            if (edition == null) return false;

	            if (edition.ReadingFormatId == 2) return true;

	            if (edition.DurationSeconds.HasValue && edition.DurationSeconds.Value > 0) return true;

	            var format = edition.EditionFormat ?? edition.Format;
	            if (string.IsNullOrWhiteSpace(format)) return false;

	            return format.Contains("audiobook", StringComparison.OrdinalIgnoreCase) ||
	                   format.Contains("audible", StringComparison.OrdinalIgnoreCase) ||
	                   format.Contains("audio", StringComparison.OrdinalIgnoreCase);
	        }

        private void StoreEditionCoverMetadata(Book book, BookCoverSelection coverChoice)
        {
            try
            {
                // Store the exact monitored-edition identity behind the one local cover.
                var bookCoverPath = GetBookCoverPath(book.Id);
                _diskProvider.EnsureFolder(bookCoverPath);
                var metadataPath = Path.Combine(bookCoverPath, "cover-metadata.json");
                var downloadedAt = DateTime.UtcNow;

	                var metadata = new
	                {
	                    BookId = book.Id,
	                    BookTitle = book.Title,
	                    SelectedEdition = new
	                    {
	                        Title = coverChoice.Edition.Title,
	                        LocalEditionId = coverChoice.Edition.Id,
	                        EditionProviderId = BookEditionIdentity.GetTrustedForeignEditionId(coverChoice.Edition),
	                        IsAudiobook = IsAudiobookEdition(coverChoice.Edition),
	                        CoverUrl = coverChoice.Cover.Url,
	                        DownloadedAt = downloadedAt
	                    }
	                };

                var metadataJson = Json.ToJson(metadata);
                _diskProvider.WriteAllText(metadataPath, metadataJson);
                _bookCoverMetadataByBookId[book.Id] = new BookCoverMetadata
                {
                    SelectedEdition = new BookCoverMetadataSelectedEdition
                    {
                        EditionProviderId = BookEditionIdentity.GetTrustedForeignEditionId(coverChoice.Edition),
                        CoverUrl = coverChoice.Cover.Url,
                        DownloadedAt = downloadedAt
                    }
                };

                _logger.Debug("Stored cover metadata for book {0} at {1}", book.Title, metadataPath);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to store cover metadata for book {0}", book.Title);
            }
        }
    }
}
