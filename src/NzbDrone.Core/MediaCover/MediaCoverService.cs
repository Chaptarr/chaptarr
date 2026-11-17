using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
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
        void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<MediaCover> covers, string selectedAuthorImageHash = null);
        string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null);
        bool EnsureAuthorCovers(Author author, bool forceDownload = false);
        void EnsureBookCovers(Book book, bool forceDownload = false);
        void EnsureBookCoversForBooks(List<Book> books, bool forceDownload = false);
        void EnsureBookCoversForImport(Book book);
        void RepairBookCovers() => throw new NotSupportedException();
        bool AuthorCoverMatchesRemoteUrl(int authorId, MediaCoverTypes coverType, string remoteUrl, out bool unreadable) => throw new NotSupportedException();
        void ConvertBookCoversToLocalUrls(IEnumerable<KeyValuePair<int, List<MediaCover>>> covers, IReadOnlyDictionary<int, string> editionIds = null);
        void DeleteBookCovers(int bookId);
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
        private readonly IConfigService _configService;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeferredCoverService _deferredCoverService;
        private readonly Logger _logger;

        private readonly string _coverRootFolder;

        private const string CachedRenditionVerification = "cached-rendition";

        // ImageSharp is slow on ARM (no hardware acceleration on mono yet)
        // So limit the number of concurrent resizing tasks
        private static SemaphoreSlim _semaphore = new SemaphoreSlim((int)Math.Ceiling(Environment.ProcessorCount / 2.0));
        private readonly ConcurrentDictionary<string, Task<EnsureImageResult>> _inFlightDownloads = new();
        private readonly ConcurrentDictionary<int, byte> _inFlightBookCovers = new();
        private readonly ConcurrentDictionary<int, byte> _inFlightAuthorCovers = new();
        private readonly ConcurrentDictionary<string, (int BookId, string Path)> _bookCoverUrlToPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, BookCoverMetadata> _bookCoverMetadataByBookId = new();
        private readonly ConcurrentDictionary<string, string> _authorCoverUrlByAuthorAndType = new(StringComparer.OrdinalIgnoreCase);

        public MediaCoverService(IMediaCoverProxy mediaCoverProxy,
                                 IImageResizer resizer,
                                 IBookService bookService,
                                 IHttpClient httpClient,
                                 IDiskProvider diskProvider,
                                 IAppFolderInfo appFolderInfo,
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
            _configService = configService;
            _configFileProvider = configFileProvider;
            _eventAggregator = eventAggregator;
            _deferredCoverService = deferredCoverService;
            _logger = logger;

            _coverRootFolder = appFolderInfo.GetMediaCoverPath();
        }

        private bool TryGetSafeImageUrl(string url, out string safeUrl, out string reason)
        {
            return MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(url, _configService, out safeUrl, out reason);
        }

        private HttpResponse GetImageResponse(string url)
        {
            return MediaCoverHttpRequest.Get(_httpClient, _configService, url);
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

        public void ConvertBookCoversToLocalUrls(IEnumerable<KeyValuePair<int, List<MediaCover>>> covers, IReadOnlyDictionary<int, string> editionIds = null)
        {
            var books = covers.ToList();
            var eligible = LibraryCoversOnly
                ? _bookService.GetLibraryBookIds(books.Select(book => book.Key)).ToHashSet()
                : null;

            foreach (var book in books)
            {
                ConvertToLocalUrls(book.Key, MediaCoverEntity.Book, book.Value, null,
                    eligible == null || eligible.Contains(book.Key), editionIds?.GetValueOrDefault(book.Key));
            }
        }

        private bool LibraryCoversOnly => _configService?.CoverDownloadScope == CoverDownloadScope.LibraryBooks;

        public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<MediaCover> covers, string selectedAuthorImageHash = null)
        {
            var eligible = coverEntity != MediaCoverEntity.Book || entityId <= 0 || !LibraryCoversOnly ||
                _bookService.GetLibraryBookIds(new[] { entityId }).Contains(entityId);
            ConvertToLocalUrls(entityId, coverEntity, covers, selectedAuthorImageHash, eligible);
        }

        private void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<MediaCover> covers, string selectedAuthorImageHash, bool cacheBookCover, string editionId = null)
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
                mediaCover.TemporaryUrl = null;
                if (coverEntity == MediaCoverEntity.Book && !cacheBookCover)
                {
                    mediaCover.TemporaryUrl = GetTemporaryBookCoverUrl(entityId, remoteUrl) ?? string.Empty;
                    mediaCover.Url = null;
                    continue;
                }

                string filePath = null;
                string cacheToken = null;

                if (ReferenceEquals(mediaCover, selectedAuthorCover))
                {
                    var coverPath = GetCoverPath(entityId, coverEntity, mediaCover.CoverType, mediaCover.Extension);
                    filePath = GetVariantPath(coverPath, ComputeHash(remoteUrl));
                    // The selection hash identifies a URL, not its bytes. A fallback
                    // rendition can be replaced by the original at this same path.
                }
                else if (coverEntity == MediaCoverEntity.Author)
                {
                    // A selected poster is the only local poster exposed. Other carousel
                    // candidates stay remote even if an old on-demand variant still exists.
                    var selectedCoverOwnsType = selectedAuthorCover != null &&
                                                selectedAuthorCover.CoverType == mediaCover.CoverType;

                    if (!selectedCoverOwnsType &&
                        TryReadAuthorCoverIdentity(entityId, mediaCover.CoverType, out var storedUrl, out _, out var publishedVersion) &&
                        !string.IsNullOrWhiteSpace(remoteUrl) &&
                        MediaCoverRendition.RemoteUrlsMatch(storedUrl, remoteUrl))
                    {
                        filePath = GetDeterministicLocalCoverPath(entityId, coverEntity, mediaCover);
                        cacheToken = publishedVersion;
                    }
                }
                else if (coverEntity == MediaCoverEntity.Book)
                {
                    var stored = TryReadBookCoverMetadata(entityId)?.SelectedEdition;
                    var sameEdition = !string.IsNullOrWhiteSpace(editionId) &&
                        string.Equals(stored?.EditionProviderId, editionId, StringComparison.OrdinalIgnoreCase);
                    var sameUrlWithoutIdentity = string.IsNullOrWhiteSpace(editionId) && BookCoverMatchesRemoteUrl(entityId, remoteUrl);
                    if (stored != null && (sameEdition || sameUrlWithoutIdentity) &&
                        !MediaCoverRendition.IsKnownPlaceholderImageHash(stored.ContentHash))
                    {
                        var storedCover = new MediaCover(mediaCover.CoverType, stored.CoverUrl);
                        filePath = GetDeterministicLocalCoverPath(entityId, coverEntity, storedCover);
                        cacheToken = stored.DownloadedAt?.Ticks.ToString() ?? ComputeHash(stored.CoverUrl);
                    }
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
                    if (coverEntity == MediaCoverEntity.Book)
                    {
                        mediaCover.Url = GetTemporaryBookCoverUrl(entityId, remoteUrl);
                    }
                    else
                    {
                        mediaCover.Url = remoteUrl;
                        _mediaCoverProxy.ProxyRemoteUrls(new[] { mediaCover });
                    }
                }
            }

            // Selecting a local rendition must not reorder the provider list: carousel
            // positions and navigation must survive the resource update after each save.
        }

        private string GetTemporaryBookCoverUrl(int bookId, string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl) || MediaCoverRendition.IsKnownPlaceholderImageUrl(remoteUrl))
            {
                return null;
            }

            return $"{_configFileProvider.UrlBase}/MediaCoverProxy/book/{bookId}/{MediaCoverProxy.GetProxyFileName(remoteUrl)}?v={remoteUrl.SHA256Hash()}";
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

        private bool TryReadAuthorCoverIdentity(int authorId, MediaCoverTypes coverType, out string remoteUrl, out string contentHash) =>
            TryReadAuthorCoverIdentity(authorId, coverType, out remoteUrl, out contentHash, out _);

        private bool TryReadAuthorCoverIdentity(int authorId, MediaCoverTypes coverType, out string remoteUrl, out string contentHash, out string publishedVersion) =>
            TryReadAuthorCoverIdentity(authorId, coverType, out remoteUrl, out contentHash, out publishedVersion, out _);

        private bool TryReadAuthorCoverIdentity(int authorId, MediaCoverTypes coverType, out string remoteUrl, out string contentHash, out string publishedVersion, out bool unreadable)
        {
            var receipt = ReadCoverReceipt(_authorCoverUrlByAuthorAndType,
                GetAuthorCoverIdentityCacheKey(authorId, coverType), () => GetAuthorCoverIdentityPath(authorId, coverType),
                text => MediaCoverRendition.TryParseAuthorCoverIdentity(text, out _, out _, out _)
                    ? text : throw new FormatException(text != null && text.Split('\n').Any(MediaCoverRendition.IsKnownPlaceholderImageHash)
                        ? "Known placeholder receipt" : "Invalid author cover identity"), () => string.Empty);
            unreadable = !receipt.Readable;
            return MediaCoverRendition.TryParseAuthorCoverIdentity(receipt.Value, out remoteUrl, out contentHash, out publishedVersion);
        }

        private bool AuthorCoverMatchesRemoteUrl(int authorId, MediaCoverTypes coverType, string remoteUrl)
            => AuthorCoverMatchesRemoteUrl(authorId, coverType, remoteUrl, out _);

        public bool AuthorCoverMatchesRemoteUrl(int authorId, MediaCoverTypes coverType, string remoteUrl, out bool unreadable)
        {
            return TryReadAuthorCoverIdentity(authorId, coverType, out var storedUrl, out _, out _, out unreadable) &&
                   MediaCoverRendition.RemoteUrlsMatch(storedUrl, remoteUrl);
        }

        private bool StoreAuthorCoverIdentity(int authorId, MediaCover cover, string contentHash, out string failure)
        {
            failure = "missing cover URL";
            if (cover == null || string.IsNullOrWhiteSpace(cover.Url))
            {
                return false;
            }

            try
            {
                var identityPath = GetAuthorCoverIdentityPath(authorId, cover.CoverType);
                var identity = MediaCoverRendition.BuildAuthorCoverIdentity(cover.Url, contentHash, Guid.NewGuid().ToString("N"));
                _diskProvider.EnsureFolder(Path.GetDirectoryName(identityPath));
                _diskProvider.WriteAllText(identityPath, identity);
                _authorCoverUrlByAuthorAndType[GetAuthorCoverIdentityCacheKey(authorId, cover.CoverType)] = identity;
                return true;
            }
            catch (Exception ex)
            {
                // The old sidecar may still exist. Do not derive sizes from an unrecorded
                // replacement; a later refresh verifies the original before reusing it.
                _authorCoverUrlByAuthorAndType[GetAuthorCoverIdentityCacheKey(authorId, cover.CoverType)] = string.Empty;
                failure = "saving author-cover receipt: " + ex.Message;
                _logger.Debug(ex, "Failed to store author-cover identity for AuthorId={0}, CoverType={1}", authorId, cover.CoverType);
                return false;
            }
        }

        private bool OriginalMatchesIdentity(string path, string contentHash)
        {
            if (string.IsNullOrWhiteSpace(contentHash) || !MediaCoverRendition.IsUsable(path, _diskProvider))
            {
                return false;
            }

            using var stream = _diskProvider.OpenReadStream(path);
            return MediaCoverRendition.ComputeContentSha256(stream).Equals(contentHash, StringComparison.OrdinalIgnoreCase);
        }

        public bool EnsureAuthorCovers(Author author, bool forceDownload = false)
        {
            // Refresh events and missing-photo repair can overlap for the same author.
            // Keep its original, receipt and resize lifecycle together, as for book covers.
            if (author == null) return false;
            if (!_inFlightAuthorCovers.TryAdd(author.Id, 0))
            {
                _logger.Debug("Author cover update already in flight for {0}; skipping duplicate request", author.Id);
                return false;
            }
            try
            {
                return EnsureAuthorCoversCore(author, forceDownload);
            }
            finally
            {
                _inFlightAuthorCovers.TryRemove(author.Id, out _);
            }
        }

        private bool EnsureAuthorCoversCore(Author author, bool forceDownload)
        {
            _logger.Debug("EnsureAuthorCovers called for author: {0} (ID: {1})", author.Name, author.Id);
            _logger.Debug("Author has {0} images in metadata", author.Images?.Count ?? 0);

            // Null guard to prevent NullReferenceException when Images is null
            if (author.Images == null || author.Images.Count == 0)
            {
                _logger.Debug("No images for author {0} (ID: {1}), skipping cover download", author.Name, author.Id);
                return false;
            }

            var changed = false;
            var toResize = new List<(MediaCover Cover, bool AlreadyExists)>();
            var rejectedCoverTypes = new HashSet<MediaCoverTypes>();
            var retainedCoverTypes = new HashSet<MediaCoverTypes>();
            var warnedUnreadable = false;

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
                    var hasReceipt = TryReadAuthorCoverIdentity(author.Id, cover.CoverType, out var storedUrl, out var contentHash, out _, out var unreadable);
                    if (unreadable)
                    {
                        if (!warnedUnreadable) _logger.Warn("Cannot read the cover receipt for author {0} ({1}); keeping its photos", author.Name, author.Id);
                        warnedUnreadable = true;
                        retainedCoverTypes.Add(cover.CoverType);
                        break;
                    }
                    var selectedUrlAlreadyStored = hasReceipt && MediaCoverRendition.RemoteUrlsMatch(storedUrl, cover.Url);

                    _logger.Debug("Processing cover type {0} for author {1}, URL: {2}", cover.CoverType, author.Name, cover.Url);

                    try
                    {
                        if (!forceDownload && selectedUrlAlreadyStored &&
                            MediaCoverRendition.HasAllGeneratedRenditions(
                                height => GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height),
                                _diskProvider,
                                cover.CoverType))
                        {
                            retainedCoverTypes.Add(cover.CoverType);
                            break;
                        }

                        alreadyExists = !forceDownload && selectedUrlAlreadyStored && OriginalMatchesIdentity(fileName, contentHash);
                        if (!alreadyExists)
                        {
                            contentHash = null;
                            var downloaded = DownloadCover(author, cover);
                            contentHash = downloaded.Hash;

                            if (downloaded.Placeholder)
                            {
                                if (forceDownload && MediaCoverRendition.FindExistingPath(
                                    height => GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height),
                                    _diskProvider, cover.CoverType) != null)
                                {
                                    _logger.Info("[AUTHOR-COVER-PLACEHOLDER] Provider returned a placeholder for {0}; forced download left the stored photo unchanged", author.Name);
                                    retainedCoverTypes.Add(cover.CoverType);
                                    break;
                                }
                                _logger.Info("[AUTHOR-COVER-PLACEHOLDER] Rejected known provider placeholder bytes for {0} ({1}) from {2}", author.Name, author.Id, cover.Url);
                                MediaCoverRendition.RegisterKnownPlaceholderImage(cover.Url, contentHash);
                                rejectedCoverTypes.Add(cover.CoverType);
                                changed |= RejectPlaceholderAuthorImage(author, cover.Url);
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
                    catch (UnsafeImageUrlException e)
                    {
                        _logger.Debug("Skipping unsafe author cover for {0} (ID: {1}): {2}", author.Name, author.Id, e.Message);
                    }
                    catch (WebException e)
                    {
                        _logger.Warn("Network error downloading author cover for {0}. Error: {1}, URL: {2}", author.Name, e.Message, cover.Url);
                    }
                    catch (InvalidDataException e)
                    {
                        _logger.Warn("Invalid author cover for {0} from {1}: {2}", author.Name, cover.Url, e.Message);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        _logger.Warn(e, "Unable to publish author cover for {0}; keeping the previous sizes", author.Name);
                        retainedCoverTypes.Add(cover.CoverType);
                        break;
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Unexpected error downloading author cover for {0} from URL: {1}", author.Name, cover.Url);
                    }

                    if (string.IsNullOrWhiteSpace(contentHash) ||
                        !MediaCoverRendition.IsUsable(fileName, _diskProvider))
                    {
                        if (selectedUrlAlreadyStored && MediaCoverRendition.FindExistingPath(
                            height => GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height),
                            _diskProvider, cover.CoverType) != null)
                        {
                            // Keep the verified selection; a later refresh can retry its missing sizes.
                            retainedCoverTypes.Add(cover.CoverType);
                            break;
                        }

                        continue;
                    }

                    retainedCoverTypes.Add(cover.CoverType);
                    if (!alreadyExists && (!CleanupCoverFiles(fileName, out var failure, cover.CoverType, replacing: true) ||
                        !StoreAuthorCoverIdentity(author.Id, cover, contentHash, out failure)))
                    {
                        RollBackCoverFile(fileName, failure);
                        break;
                    }

                    changed |= !alreadyExists;
                    toResize.Add((cover, alreadyExists));
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
                }
            }
            finally
            {
                _semaphore.Release();
            }
            return changed;
        }

        private bool RejectPlaceholderAuthorImage(Author author, string rejectedUrl)
        {
            if (author?.Images == null || string.IsNullOrWhiteSpace(rejectedUrl))
            {
                return false;
            }

            try
            {
                var changed = (_authorService?.RemoveKnownPlaceholderImages(author) ?? false);
                if (changed) _logger.Info("[AUTHOR-COVER-PLACEHOLDER] Removed provider placeholder image for {0} ({1}) after content verification", author.Name, author.Id);
                return changed;
            }
            catch (Exception ex)
            {
                // Display remains safe because the process-wide content verdict is already
                // active. Startup/daily repair will retry the durable row scrub.
                _logger.Warn(ex, "[AUTHOR-COVER-PLACEHOLDER] Failed to persist placeholder removal for author {0}", author.Id);
                return false;
            }
        }

        private void RemoveCanonicalAuthorCoverArtifacts(int authorId, MediaCoverTypes coverType)
        {
            var folder = GetAuthorCoverPath(authorId);
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
                    var isIdentity = fileName.Equals(MediaCoverRendition.GetAuthorCoverIdentityFileName(coverType), StringComparison.OrdinalIgnoreCase);
                    if (isIdentity || MediaCoverRendition.IsCanonicalCoverFile(file, coverType))
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
                        try
                        {
                            using var stream = _diskProvider.OpenReadStream(variantPath);
                            var header = new byte[64];
                            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                            MediaCoverRendition.ValidateImageResponse(header.AsSpan(0, read).ToArray(), null);
                            return new EnsureImageResult { State = "downloaded", Path = variantPath };
                        }
                        catch (InvalidDataException ex)
                        {
                            _logger.Debug(ex, "Stored author variant is not an image; downloading it again for author {0}", author.Id);
                        }
                    }

                    if (ExistingAuthorVariantIsPlaceholder(variantPath, verificationPath, cover.Url))
                    {
                        RejectPlaceholderAuthorImage(author, cover.Url);
                        return new EnsureImageResult { State = "error", ErrorCode = "placeholder_image" };
                    }
                }

                byte[] imageData;
                string contentType = null;
                string cachedCoverPath = null;

                try
                {
                    var response = GetImageResponse(cover.Url);
                    imageData = response?.ResponseData;
                    contentType = response?.Headers?.ContentType;
                }
                catch (Exception ex) when (ex is HttpException || ex is WebException)
                {
                    var unsafeTarget = ex is UnsafeImageUrlException;
                    if (unsafeTarget)
                    {
                        _logger.Debug(ex.Message);
                    }
                    else
                    {
                        _logger.Warn(ex, "On-demand author image download failed for {0} from URL: {1}", author.Name, cover.Url);
                    }

                    // Keep a URL-keyed fallback even if the canonical photo has since changed.
                    cachedCoverPath = MediaCoverRendition.IsUsable(variantPath, _diskProvider) ? variantPath : null;
                    if (cachedCoverPath == null && AuthorCoverMatchesRemoteUrl(author.Id, cover.CoverType, cover.Url))
                    {
                        cachedCoverPath = MediaCoverRendition.FindExistingPath(
                            height => GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height),
                            _diskProvider, cover.CoverType) ?? MediaCoverRendition.FindExistingPath(
                            height => GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, ".jpg", height),
                            _diskProvider, cover.CoverType);
                    }

                    if (cachedCoverPath == null)
                    {
                        return new EnsureImageResult { State = "error", ErrorCode = unsafeTarget ? "unsafe_url" : "download_failed" };
                    }

                    using var cachedStream = _diskProvider.OpenReadStream(cachedCoverPath);
                    using var buffer = new MemoryStream();
                    cachedStream.CopyTo(buffer);
                    imageData = buffer.ToArray();
                }

                if (imageData == null || imageData.Length == 0)
                {
                    return new EnsureImageResult { State = "error", ErrorCode = "empty_response" };
                }

                MediaCoverRendition.ValidateImageResponse(imageData, contentType);
                if (MediaCoverRendition.InspectDownloadedImage(cover.Url, imageData, out var contentHash))
                {
                    DeleteAuthorVariant(variantPath, verificationPath);
                    RejectPlaceholderAuthorImage(author, cover.Url);
                    return new EnsureImageResult { State = "error", ErrorCode = "placeholder_image" };
                }

                if (cachedCoverPath == variantPath)
                {
                    return new EnsureImageResult { State = "downloaded", Path = variantPath };
                }

                _diskProvider.EnsureFolder(Path.GetDirectoryName(variantPath));

                SaveCoverFile(imageData, variantPath);

                // Reuse the existing sidecar, but never certify a thumbnail as the original.
                _diskProvider.WriteAllText(verificationPath, cachedCoverPath != null ? CachedRenditionVerification : MediaCoverRendition.OriginalImageVerificationPrefix + contentHash);

                return new EnsureImageResult { State = "downloaded", Path = variantPath };
            }
            catch (InvalidDataException ex)
            {
                _logger.Warn("Invalid author image for {0} from {1}: {2}", author.Name, cover.Url, ex.Message);
                return new EnsureImageResult { State = "error", ErrorCode = "invalid_image" };
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

        private bool ExistingAuthorVariantIsPlaceholder(string variantPath, string verificationPath, string remoteUrl)
        {

            try
            {
                if (_diskProvider.FileExists(verificationPath) &&
                    _diskProvider.ReadAllText(verificationPath) == CachedRenditionVerification)
                {
                    return false;
                }

                using var stream = _diskProvider.OpenReadStream(variantPath);
                var contentHash = MediaCoverRendition.ComputeContentSha256(stream);
                if (MediaCoverRendition.RegisterKnownPlaceholderImage(remoteUrl, contentHash))
                {
                    DeleteAuthorVariant(variantPath, verificationPath);
                    return true;
                }

                // Legacy hashes certify bytes, not whether those bytes were a resized copy.
                return false;
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

        public void EnsureBookCovers(Book book, bool forceDownload = false)
        {
            if (book == null || book.Id <= 0)
            {
                return;
            }

            var eligible = !LibraryCoversOnly || _bookService.GetLibraryBookIds(new[] { book.Id }).Contains(book.Id);
            EnsureBookCovers(book, eligible, forImport: false, forceDownload);
        }

        public void EnsureBookCoversForImport(Book book)
        {
            // An import needs its cover before the first file record can be committed.
            EnsureBookCovers(book, eligible: true, forImport: true);
        }

        public void EnsureBookCoversForBooks(List<Book> books, bool forceDownload = false)
        {
            var candidates = books?.Where(book => book?.Id > 0).DistinctBy(book => book.Id).ToList() ?? new List<Book>();
            var timer = Stopwatch.StartNew();
            HashSet<int> eligible = null;
            var skipped = 0;
            foreach (var book in candidates)
            {
                var libraryOnly = LibraryCoversOnly;
                if (libraryOnly && eligible == null)
                {
                    eligible = _bookService.GetLibraryBookIds(candidates.Select(candidate => candidate.Id)).ToHashSet();
                }

                var keep = !libraryOnly || eligible.Contains(book.Id);
                if (!keep)
                {
                    skipped++;
                    continue;
                }

                EnsureBookCovers(book, eligible: true, forImport: false, forceDownload);
            }

            _logger.Debug("Book-cover batch: {0} considered, {1} eligible, {2} skipped in {3} ms",
                candidates.Count, candidates.Count - skipped, skipped, timer.ElapsedMilliseconds);
        }

        public void RepairBookCovers()
        {
            var timer = Stopwatch.StartNew();
            if (RepairDeferredByImport()) return;
            List<int> ids;
            try
            {
                ids = LibraryCoversOnly ? _bookService.GetLibraryBookIds() : _bookService.GetBookIds(includeUnmonitored: true);
                _logger.Debug("Book cover repair selected {0} IDs in {1} ms", ids.Count, timer.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn(ex, "Book cover repair could not select IDs after {0} ms; the next run retries", timer.ElapsedMilliseconds);
                return;
            }

            var candidates = 0;
            var repaired = 0;
            var incomplete = 0;
            var unreadableCount = 0;
            foreach (var page in ids.Chunk(250))
            {
                if (RepairDeferredByImport()) return;
                try
                {
                    var read = Stopwatch.StartNew();
                    var books = _bookService.GetExistingBooks(page);
                    _logger.Debug("Book cover repair loaded {0} of {1} books in {2} ms", books.Count, page.Length, read.ElapsedMilliseconds);
                    var missing = new List<Book>();
                    foreach (var book in books)
                    {
                        try
                        {
                            var needsRepair = NeedsBookCoverRepair(book, out var unreadable);
                            if (unreadable) unreadableCount++;
                            if (needsRepair == true) missing.Add(book);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            incomplete++;
                            _logger.Warn(ex, "Book cover repair could not inspect book {0}; continuing with the remaining books", book.Id);
                        }
                    }
                    if (RepairDeferredByImport()) return;
                    candidates += missing.Count;
                    EnsureBookCoversForBooks(missing);
                    foreach (var book in missing)
                    {
                        if (NeedsBookCoverRepair(book, out _, logUnreadable: false) == false) repaired++;
                        else incomplete++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    incomplete += page.Length;
                    _logger.Warn(ex, "Book cover repair failed for a page of {0} books; continuing with the next page", page.Length);
                }
            }
            _logger.Info("[BOOK-COVER-REPAIR] {0} checked, {1} candidates, {2} repaired, {3} incomplete, {4} unreadable in {5} ms",
                ids.Count, candidates, repaired, incomplete, unreadableCount, timer.ElapsedMilliseconds);
        }

        private bool RepairDeferredByImport()
        {
            if (!_deferredCoverService.IsCoverDownloadDeferred) return false;
            _logger.Info("Book cover repair paused: import running; the next run retries");
            return true;
        }

        private bool? NeedsBookCoverRepair(Book book, out bool unreadable, bool logUnreadable = true)
        {
            unreadable = false;
            var candidates = MediaCoverRendition.SelectMonitoredBookCovers(book);
            if (candidates.Count == 0) return null;
            TryReadBookCoverMetadata(book.Id, out unreadable);
            if (unreadable)
            {
                if (logUnreadable) _logger.Warn("Cannot read the cover receipt for book {0} ({1}); keeping its cover", book.Title, book.Id);
                return null;
            }
            var contentHash = StoredBookContentHash(book.Id);
            return !candidates.Any(candidate => HasCompleteBookCover(book.Id, candidate.Cover, contentHash));
        }

        private void EnsureBookCovers(Book book, bool eligible, bool forImport, bool forceDownload = false)
        {
            if (book == null || book.Id <= 0 || (!forImport && LibraryCoversOnly && !eligible))
            {
                return;
            }

            // Queue non-forced requests before testing the gate: an active older download
            // does not fulfill a monitored-edition change made during an import.
            if (!forceDownload && !forImport && _deferredCoverService.MarkBookForCoverDownload(book.Id))
            {
                _logger.Debug("Cover download deferred for book {0}: {1}", book.Id, book.Title);
                return;
            }

            // Avoid duplicate in-flight downloads for the same book (can be triggered by multiple events/UI requests).
            if (!_inFlightBookCovers.TryAdd(book.Id, 0))
            {
                _logger.Debug("Book cover download already in-flight for book {0}: {1}", book.Id, book.Title);
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

                TryReadBookCoverMetadata(book.Id, out var unreadable);
                if (unreadable)
                {
                    _logger.Warn("Cannot read the cover receipt for book {0} ({1}); keeping its cover", book.Title, book.Id);
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
                    try
                    {
                        if (EnsureMonitoredBookCover(book, candidate, forceDownload))
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
                    catch (UnsafeImageUrlException e)
                    {
                        _logger.Debug("Skipping unsafe monitored-edition cover for book {0}: {1}", book.Title, e.Message);
                    }
                    catch (WebException e)
                    {
                        _logger.Warn("Couldn't download media cover for {0} from {1}. {2}", book, candidate.Cover.Url, e.Message);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        _logger.Warn(e, "Unable to publish book cover for {0}; keeping the previous sizes", book);
                        return;
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Couldn't download media cover for {0} from {1}", book, candidate.Cover.Url);
                    }

                    if (forceDownload && CanKeepBookCover(book.Id, candidate.Cover)) return;
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

        private bool HasCompleteBookCover(int bookId, MediaCover cover, string contentHash) =>
            BookCoverMatchesRemoteUrl(bookId, cover.Url) && !MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash) &&
            MediaCoverRendition.HasAllRenditions(height => GetCoverPath(bookId, MediaCoverEntity.Book,
                cover.CoverType, cover.Extension, height), _diskProvider, cover.CoverType);

        private string StoredBookContentHash(int bookId)
        {
            var stored = TryReadBookCoverMetadata(bookId)?.SelectedEdition;
            var originalPath = GetCoverPath(bookId, MediaCoverEntity.Book, MediaCoverTypes.Cover,
                new MediaCover(MediaCoverTypes.Cover, stored?.CoverUrl).Extension);
            if (stored != null && string.IsNullOrWhiteSpace(stored.ContentHash) && MediaCoverRendition.IsUsable(originalPath, _diskProvider))
            {
                // Cache the legacy original's verdict for repair and write decisions,
                // never hash an image while serving it or rewrite its receipt for this.
                using var stream = _diskProvider.OpenReadStream(originalPath);
                stored.ContentHash = MediaCoverRendition.ComputeContentSha256(stream);
            }
            return stored?.ContentHash;
        }

        private bool CanKeepBookCover(int bookId, MediaCover cover)
        {
            if (!BookCoverMatchesRemoteUrl(bookId, cover.Url)) return false;
            var original = GetCoverPath(bookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension);
            if (MediaCoverRendition.IsKnownPlaceholderImageHash(StoredBookContentHash(bookId)) ||
                MediaCoverRendition.FindExistingPath(height => GetCoverPath(bookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension, height),
                    _diskProvider, cover.CoverType) == null) return false;
            _logger.Info("Kept the stored cover for book {0} after its provider failed", bookId);
            return true;
        }

        private bool EnsureMonitoredBookCover(Book book, BookCoverSelection selection, bool forceDownload)
        {
            var cover = selection.Cover;
            var fileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);
            var selectedUrlAlreadyStored = BookCoverMatchesRemoteUrl(book.Id, cover.Url);
            var contentHash = StoredBookContentHash(book.Id);

            if (!forceDownload && HasCompleteBookCover(book.Id, cover, contentHash))
            {
                _logger.Trace("Book {0} already has the monitored-edition cover and all renditions", book.Title);
                return true;
            }

            var downloadedOrReused = false;
            if (forceDownload || !selectedUrlAlreadyStored || MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash) || !OriginalMatchesIdentity(fileName, contentHash))
            {
                if (!forceDownload && TryReuseBookCover(book.Id, cover, fileName, out contentHash))
                {
                    downloadedOrReused = true;
                    _logger.Debug("Reused monitored-edition cover for '{0}'", book.Title);
                }
                else
                {
                    var downloaded = DownloadBookCover(book, cover);
                    contentHash = downloaded.Hash;
                    if (downloaded.Placeholder)
                    {
                        if (CanKeepBookCover(book.Id, cover)) return true;
                        _logger.Info("Rejected known provider placeholder bytes for book {0} from {1}", book, cover.Url);
                        MediaCoverRendition.RegisterKnownPlaceholderImage(cover.Url, contentHash);
                        return false;
                    }
                    downloadedOrReused = true;
                }
            }

            if (!MediaCoverRendition.IsUsable(fileName, _diskProvider))
            {
                return false;
            }

            if (downloadedOrReused && (!CleanupCoverFiles(fileName, out var failure, cover.CoverType, replacing: true) ||
                !StoreEditionCoverMetadata(book, selection, contentHash, out failure)))
            {
                RollBackCoverFile(fileName, failure);
                return true; // Publication failed locally; another provider cannot fix it.
            }

            EnsureResizedBookCovers(book, cover, downloadedOrReused);

            var storedHash = ComputeHash(cover.Url);
            if (!string.IsNullOrWhiteSpace(storedHash))
            {
                _bookCoverUrlToPath[storedHash] = (book.Id, fileName);
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
                public string ContentHash { get; set; }
	        }

        private (T Value, bool Readable) ReadCoverReceipt<TKey, T>(ConcurrentDictionary<TKey, T> cache, TKey key,
            Func<string> pathFactory, Func<string, T> parse, Func<T> absent) where T : class
        {
            if (cache.TryGetValue(key, out var cached)) return (cached, true);
            var path = pathFactory();
            try
            {
                return (cache.GetOrAdd(key, _ =>
                {
                    // A missing parent otherwise triggers a case-insensitive scan of the
                    // whole cover directory for every uncached entity.
                    if (!_diskProvider.FolderExists(Path.GetDirectoryName(path)) || !_diskProvider.FileExists(path)) return absent();
                    var text = _diskProvider.ReadAllText(path);
                    try { return parse(text); }
                    catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or FormatException)
                    {
                        _logger.Warn("Ignoring cover receipt {0}: {1}", path, ex.Message);
                        return absent();
                    }
                }), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable receipt is not evidence of absence. Do not cache that
                // conclusion or replace/delete the image until its ownership can be read.
                _logger.Debug(ex, "Unable to read cover receipt {0}; a later caller can retry", path);
                return (null, false);
            }
        }

        private BookCoverMetadata TryReadBookCoverMetadata(int bookId) => TryReadBookCoverMetadata(bookId, out _);

        private BookCoverMetadata TryReadBookCoverMetadata(int bookId, out bool unreadable)
        {
            var receipt = ReadCoverReceipt(_bookCoverMetadataByBookId, bookId,
                () => Path.Combine(GetBookCoverPath(bookId), "cover-metadata.json"),
                text => Json.Deserialize<BookCoverMetadata>(text) ?? throw new Newtonsoft.Json.JsonException("Empty cover receipt"),
                () => new BookCoverMetadata());
            unreadable = !receipt.Readable;
            return receipt.Value?.SelectedEdition == null ? null : receipt.Value;
        }

        private void RemoveBookCoverArtifactsForProvenEditionChange(Book book)
        {
            var stored = TryReadBookCoverMetadata(book.Id)?.SelectedEdition;
            if (MediaCoverRendition.IsKnownPlaceholderImageHash(StoredBookContentHash(book.Id)))
            {
                RemoveBookCoverArtifacts(book.Id);
                return;
            }
            var storedProviderId = stored?.EditionProviderId;
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

        public void DeleteBookCovers(int bookId)
        {
            var coverPath = GetBookCoverPath(bookId);
            try
            {
                if (_diskProvider.FolderExists(coverPath)) _diskProvider.DeleteFolder(coverPath, recursive: true);
            }
            finally { _bookCoverMetadataByBookId.TryRemove(bookId, out _); }
        }

        private void RemoveBookCoverArtifacts(int bookId)
        {
            try
            {
                DeleteBookCovers(bookId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to remove stale book-cover artifacts for BookId={0}", bookId);
            }
        }

        private (string Hash, bool Placeholder) DownloadCover(Author author, MediaCover cover)
        {
            var fileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);

            // Ensure the directory exists before downloading
            var directory = Path.GetDirectoryName(fileName);
            _diskProvider.EnsureFolder(directory);

            _logger.Debug("Downloading {0} for {1} {2}", cover.CoverType, author, cover.Url);
            var response = GetImageResponse(cover.Url);

            MediaCoverRendition.ValidateImageResponse(response?.ResponseData, response?.Headers?.ContentType);

            var contentHash = MediaCoverRendition.ComputeContentSha256(response.ResponseData);
            if (MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash))
            {
                return (contentHash, true);
            }

            TryReadAuthorCoverIdentity(author.Id, cover.CoverType, out _, out var previousHash);
            PublishCoverFile(fileName, temporary => SaveImageBytes(response.ResponseData, temporary),
                GetAuthorCoverIdentityPath(author.Id, cover.CoverType), previousHash,
                () => _authorCoverUrlByAuthorAndType.TryRemove(GetAuthorCoverIdentityCacheKey(author.Id, cover.CoverType), out _));

            return (contentHash, false);
        }

        private (string Hash, bool Placeholder) DownloadBookCover(Book book, MediaCover cover)
        {
            var fileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);

            // Ensure the directory exists before downloading
            var directory = Path.GetDirectoryName(fileName);
            _diskProvider.EnsureFolder(directory);

            _logger.Debug("Downloading {0} for {1} {2}", cover.CoverType, book, cover.Url);
            var response = GetImageResponse(cover.Url);

            MediaCoverRendition.ValidateImageResponse(response?.ResponseData, response?.Headers?.ContentType);

            var contentHash = MediaCoverRendition.ComputeContentSha256(response.ResponseData);
            if (MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash))
            {
                return (contentHash, true);
            }

            PublishCoverFile(fileName, temporary => SaveImageBytes(response.ResponseData, temporary),
                Path.Combine(GetBookCoverPath(book.Id), "cover-metadata.json"),
                TryReadBookCoverMetadata(book.Id)?.SelectedEdition?.ContentHash,
                () => _bookCoverMetadataByBookId.TryRemove(book.Id, out _));
            return (contentHash, false);
        }

        private bool TryReuseBookCover(int bookId, MediaCover cover, string destination, out string contentHash)
        {
            contentHash = null;
            var urlHash = ComputeHash(cover.Url);
            if (string.IsNullOrWhiteSpace(urlHash) ||
                !_bookCoverUrlToPath.TryGetValue(urlHash, out var cached) ||
                !_inFlightBookCovers.TryAdd(cached.BookId, 0))
            {
                return false;
            }

            try
            {
                // The cached path is mutable. Verify its current owner, and keep the
                // existing per-book gate until copying finishes so a refresh cannot replace it.
                var legacy = string.IsNullOrWhiteSpace(TryReadBookCoverMetadata(cached.BookId)?.SelectedEdition?.ContentHash);
                contentHash = StoredBookContentHash(cached.BookId);
                if (string.IsNullOrWhiteSpace(contentHash) || MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash) ||
                    !BookCoverMatchesRemoteUrl(cached.BookId, cover.Url) || (!legacy && !OriginalMatchesIdentity(cached.Path, contentHash)))
                {
                    return false;
                }

                _diskProvider.EnsureFolder(Path.GetDirectoryName(destination));
                PublishCoverFile(destination, temporary => _diskProvider.CopyFile(cached.Path, temporary, overwrite: false),
                    Path.Combine(GetBookCoverPath(bookId), "cover-metadata.json"),
                    TryReadBookCoverMetadata(bookId)?.SelectedEdition?.ContentHash,
                    () => _bookCoverMetadataByBookId.TryRemove(bookId, out _));
                return true;
            }
            finally
            {
                _inFlightBookCovers.TryRemove(cached.BookId, out _);
            }
        }

        private void SaveImageBytes(byte[] bytes, string path)
        {
            using var stream = new MemoryStream(bytes);
            _diskProvider.SaveStream(stream, path);
        }

        private void SaveCoverFile(byte[] bytes, string path)
        {
            _diskProvider.WriteFileAtomically(path, temporary => SaveImageBytes(bytes, temporary));
            CleanupCoverFiles(path, out _);
        }

        private void PublishCoverFile(string path, Action<string> stage, string receiptPath, string previousHash, Action invalidateCache)
        {
            string previousReceipt = null;
            var invalidated = false;
            var hadOriginal = _diskProvider.FileExists(path);
            try
            {
                _diskProvider.WriteFileAtomically(path, temporary =>
                {
                    stage(temporary);
                    if (_diskProvider.FileExists(receiptPath))
                    {
                        previousReceipt = _diskProvider.ReadAllText(receiptPath);
                        // Legacy book receipts lack hashes. Capture their original only on replacement.
                        if (string.IsNullOrWhiteSpace(previousHash) && MediaCoverRendition.IsUsable(path, _diskProvider))
                        {
                            using var stream = _diskProvider.OpenReadStream(path);
                            previousHash = MediaCoverRendition.ComputeContentSha256(stream);
                        }
                        _diskProvider.DeleteFile(receiptPath);
                    }
                    // A refused delete leaves both the on-disk and cached receipt intact.
                    invalidateCache();
                    invalidated = true;
                });
            }
            catch
            {
                try
                {
                    // A throw can follow publication (e.g. network filesystem failure).
                    // Restore only if the old original is demonstrably still there.
                    if (invalidated && previousReceipt != null &&
                        ((!hadOriginal && !_diskProvider.FileExists(path)) || OriginalMatchesIdentity(path, previousHash)))
                    {
                        _diskProvider.WriteAllText(receiptPath, previousReceipt);
                        invalidateCache();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to restore the previous cover receipt for {0}", path);
                }
                throw;
            }
        }

        private void RollBackCoverFile(string path, string failure)
        {
            _logger.Warn("Unable to finish cover replacement for {0}: {1}. Keeping the provider URL until a refresh downloads covers", path, failure);
            try
            {
                _diskProvider.DeleteFile(path);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to remove unrecorded cover {0}", path);
            }
        }

        private bool CleanupCoverFiles(string originalPath, out string failure, MediaCoverTypes? coverType = null, bool replacing = false)
        {
            failure = null;
            var currentSizesCleared = true;
            try
            {
                foreach (var file in _diskProvider.GetFiles(Path.GetDirectoryName(originalPath), recursive: false))
                {
                    var name = Path.GetFileName(file);
                    var previousWrite = name.StartsWith(DiskProviderBase.TemporaryWritePrefix, StringComparison.Ordinal) &&
                        !name.StartsWith(DiskProviderBase.CurrentTemporaryWritePrefix, StringComparison.Ordinal);
                    var obsoleteImage = coverType.HasValue && MediaCoverRendition.IsCanonicalCoverFile(file, coverType.Value) &&
                        !file.PathEquals(originalPath) &&
                        (replacing || !Path.GetExtension(file).Equals(Path.GetExtension(originalPath), StringComparison.OrdinalIgnoreCase));
                    if (!previousWrite && !obsoleteImage) continue;
                    try
                    {
                        _diskProvider.DeleteFile(file);
                    }
                    catch (Exception ex)
                    {
                        if (obsoleteImage && Path.GetExtension(file).Equals(Path.GetExtension(originalPath), StringComparison.OrdinalIgnoreCase))
                        {
                            currentSizesCleared = false;
                            failure ??= "deleting old size " + file + ": " + ex.Message;
                        }
                        _logger.Debug(ex, "Unable to remove obsolete cover file {0}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = "listing old sizes: " + ex.Message;
                _logger.Debug(ex, "Unable to enumerate obsolete cover files in {0}", Path.GetDirectoryName(originalPath));
                currentSizesCleared = false;
            }
            return currentSizesCleared;
        }

        private void EnsureResizedCovers(Author author, MediaCover cover, bool forceResize)
        {
            var heights = MediaCoverRendition.GetHeights(cover.CoverType);
            var mainFileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);
            var allResizeSuccessful = true;
            if (!forceResize) CleanupCoverFiles(mainFileName, out _);

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
            if (!forceResize) CleanupCoverFiles(mainFileName, out _, cover.CoverType);
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

                    try
                    {
                        _logger.Debug("Resizing {0}-{1} for book {2}", cover.CoverType, height, book);
                        _resizer.Resize(mainFileName, resizeFileName, height);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Couldn't resize media cover {0}-{1} for book {2}; the original remains available as a fallback", cover.CoverType, height, book);
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


	        public void HandleAsync(AuthorRefreshCompleteEvent message)
	        {
	            EnsureAuthorCovers(message.Author);

                // Restore Readarr's eager child-cover lifecycle, while Chaptarr's
                // selector restricts persistence to each book's monitored edition.
                EnsureBookCoversForBooks(_bookService.GetBooksByAuthor(message.Author.Id));

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
            DeleteBookCovers(message.Book.Id);
        }

        private bool StoreEditionCoverMetadata(Book book, BookCoverSelection coverChoice, string contentHash, out string failure)
        {
            failure = "missing edition cover";
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
	                        CoverUrl = coverChoice.Cover.Url,
	                        DownloadedAt = downloadedAt,
                        ContentHash = contentHash
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
                        DownloadedAt = downloadedAt,
                        ContentHash = contentHash
                    }
                };

                _logger.Debug("Stored cover metadata for book {0} at {1}", book.Title, metadataPath);
                return true;
            }
            catch (Exception ex)
            {
                _bookCoverMetadataByBookId[book.Id] = new BookCoverMetadata();
                failure = "saving book-cover receipt: " + ex.Message;
                _logger.Debug(ex, "Failed to store cover metadata for book {0}", book.Title);
                return false;
            }
        }
    }
}
