using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
// using System.Text.Json; // Removed - using Newtonsoft.Json instead
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LazyCache;
using LazyCache.Providers;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.MetadataSource.Audible;
using NzbDrone.Core.MetadataSource.Audible.Resources;
using NzbDrone.Core.MetadataSource.Hardcover;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Validation;
// using JsonSerializer = System.Text.Json.JsonSerializer; // Removed - using Newtonsoft.Json instead

namespace NzbDrone.Core.MetadataSource.BookInfo
{
    public class BookInfoProxy : IProvideAuthorInfoAsync, IProvideBookInfo, ISearchForNewBook, ISearchForNewAuthor, ISearchForNewEntity
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.None
        };

        private const int V5AuthorMaxPages = 200;
        private const int V5AuthorMaxBooks = 20000;
        private static readonly TimeSpan V5AuthorRequestTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HardcoverSearchCacheDuration = TimeSpan.FromMinutes(5);

        private readonly IHttpClient _httpClient;
        private readonly ICachedHttpResponseService _cachedHttpClient;
        private readonly IGoodreadsSearchProxy _goodreadsSearchProxy;
        private readonly IHardcoverSearchClient _hardcoverSearchClient;
        private readonly IAudibleCatalogProxy _audibleCatalogProxy;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly IMetadataRequestBuilder _requestBuilder;
        private readonly IMetadataServerHealthGate _metadataServerHealthGate;
        private readonly ICached<HashSet<string>> _cache;
        private readonly ICached<List<object>> _hardcoverSearchCache;
        private readonly CachingService _authorCache;
        // private readonly IGoodreadsAutocompleteFallback _autocompleteFallback; // Removed - not used

        public BookInfoProxy(IHttpClient httpClient,
                             ICachedHttpResponseService cachedHttpClient,
                             IGoodreadsSearchProxy goodreadsSearchProxy,
                             IHardcoverSearchClient hardcoverSearchClient,
                             IAudibleCatalogProxy audibleCatalogProxy,
                             IAuthorService authorService,
                             IBookService bookService,
                             IEditionService editionService,
                             IConfigService configService,
                             IMetadataRequestBuilder requestBuilder,
                             Logger logger,
                             ICacheManager cacheManager,
                             IMetadataServerHealthGate metadataServerHealthGate)
        {
            _httpClient = httpClient;
            _cachedHttpClient = cachedHttpClient;
            _goodreadsSearchProxy = goodreadsSearchProxy;
            _hardcoverSearchClient = hardcoverSearchClient;
            _audibleCatalogProxy = audibleCatalogProxy;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _configService = configService;
            _requestBuilder = requestBuilder;
            _cache = cacheManager.GetCache<HashSet<string>>(GetType());
            _hardcoverSearchCache = cacheManager.GetCache<List<object>>(GetType(), "hardcoverSearch");
            _logger = logger;
            _metadataServerHealthGate = metadataServerHealthGate ?? throw new ArgumentNullException(nameof(metadataServerHealthGate));

            _authorCache = new CachingService(new MemoryCacheProvider(new MemoryCache(new MemoryCacheOptions { SizeLimit = 150 })));
            _authorCache.DefaultCachePolicy = new CacheDefaults
            {
                DefaultCacheDurationSeconds = 60
            };
        }

        public V5.V5AuthorChangesResponse GetBulkAuthorChanges(List<V5.V5AuthorETag> authorsWithETags)
        {
            var metadataServerUrl = _configService.MetadataServerUrl;
            var url = $"{metadataServerUrl}/api/v5/authors/diff";

            _logger.Debug("Checking bulk author changes for {0} authors", authorsWithETags.Count);

            var request = new V5.V5AuthorChangesRequest
            {
                Items = authorsWithETags
            };

            var httpRequest = new HttpRequestBuilder(url)
                .SetHeader("User-Agent", "Chaptarr/2.0")
                .Accept(HttpAccept.Json)
                .Build();
            
            httpRequest.Method = HttpMethod.Post;

            httpRequest.SetContent(JsonConvert.SerializeObject(request, SerializerSettings));

            try
            {
                var httpResponse = ExecuteV5Request(httpRequest, _httpClient.Execute, "bulk author diff");

                if (httpResponse.HasHttpError)
                {
                    _logger.Error("Bulk changes API error: Status {0}", httpResponse.StatusCode);
                    return null;
                }

                var response = JsonConvert.DeserializeObject<V5.V5AuthorChangesResponse>(httpResponse.Content, SerializerSettings);

                if (response?.Rejected?.Count > 0)
                {
                    _logger.Warn("Bulk author diff rejected {0} items: {1}",
                        response.Rejected.Count,
                        string.Join(", ", response.Rejected.Select(r => $"{r.RequestedId} ({r.Reason})")));
                }
                
                _logger.Info("Bulk changes check complete: {0} changed, {1} deleted, {2} merged",
                    response.Changed?.Count ?? 0,
                    response.Deleted?.Count ?? 0,
                    response.Merged?.Count ?? 0);
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to check bulk author changes");
                return null;
            }
        }

        public Author GetAuthorInfo(string foreignAuthorId, bool useCache = true)
        {
            _logger.Debug("Getting Author details for ID: {0}", foreignAuthorId);

            if (LooksLikeV5WorkKey(foreignAuthorId))
            {
                throw new InvalidProviderIdException(foreignAuthorId, $"Invalid author id '{foreignAuthorId}' (looks like a legacy V5 work key).");
            }

            // Check if this is a V5 ID with provider prefix (hc:123, gr:456, etc.)
            if (foreignAuthorId.Contains(":"))
            {
                _logger.Debug("Detected provider-prefixed V5 ID: {0}", foreignAuthorId);
                return GetAuthorInfoFromV5(foreignAuthorId, useCache, importAllWorks: false);
            }

            // A bare number carries no provider. Every provider numbers its authors from the same
            // small integers, so guessing one (this used to assume Hardcover) resolves to an
            // unrelated author or to nothing at all. Callers know which catalog they searched;
            // they supply the prefix.
            if (foreignAuthorId.All(char.IsDigit))
            {
                throw new InvalidProviderIdException(foreignAuthorId,
                    $"Author id '{foreignAuthorId}' has no provider prefix. Expected one of {ProviderIdValidator.ValidPrefixesDisplay} (for example gr:{foreignAuthorId}).");
            }

            // Legacy Goodreads ID - still use old method for backward compatibility
            _logger.Debug("Using legacy Goodreads fetch for ID: {0}", foreignAuthorId);
            try
            {
                if (useCache)
                {
                    return PollAuthor(foreignAuthorId);
                }

                return PollAuthorUncached(foreignAuthorId);
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Unexpected error getting author info");
                throw new AuthorNotFoundException(foreignAuthorId);
            }
        }

        public async Task<Author> GetAuthorInfoAsync(string foreignAuthorId, bool useCache = true)
        {
            _logger.Debug("Getting Author details (async) for ID: {0}", foreignAuthorId);

            if (foreignAuthorId.Contains(":"))
            {
                _logger.Debug("Detected provider-prefixed V5 ID: {0}", foreignAuthorId);
                return await GetAuthorInfoFromV5Async(foreignAuthorId, useCache, importAllWorks: false);
            }

            // Same contract as the sync path: a bare number has no provider meaning, so it is
            // rejected rather than guessed at.
            if (foreignAuthorId.All(char.IsDigit))
            {
                throw new InvalidProviderIdException(foreignAuthorId,
                    $"Author id '{foreignAuthorId}' has no provider prefix. Expected one of {ProviderIdValidator.ValidPrefixesDisplay} (for example gr:{foreignAuthorId}).");
            }

            // Legacy Goodreads path - keep sync semantics (rare in Chaptarr/V5 flows)
            return GetAuthorInfo(foreignAuthorId, useCache);
        }

        private Author GetAuthorInfoFromV5(string authorId, bool useCache = true, bool importAllWorks = true)
        {
            try
            {
                _logger.Debug("GetAuthorInfoFromV5 called with ID: {0}, importAllWorks: {1}", authorId, importAllWorks);

                var v5Response = FetchAuthorInfoFromV5Paged(authorId);
                return ConvertV5AuthorToDomain(v5Response, authorId);
            }
            catch (AuthorTerminalException)
            {
                throw;
            }

            catch (AuthorNotFoundException)
            {
                throw;
            }
            catch (BookInfoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting author info from V5 API");
                throw new BookInfoException("Failed to get author info from V5 API", ex);
            }
        }

        private async Task<Author> GetAuthorInfoFromV5Async(string authorId, bool useCache = true, bool importAllWorks = true)
        {
            try
            {
                _logger.Debug("GetAuthorInfoFromV5Async called with ID: {0}, importAllWorks: {1}", authorId, importAllWorks);

                var v5Response = await FetchAuthorInfoFromV5PagedAsync(authorId);
                return ConvertV5AuthorToDomain(v5Response, authorId);
            }
            catch (AuthorTerminalException)
            {
                throw;
            }

            catch (AuthorNotFoundException)
            {
                throw;
            }
            catch (BookInfoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting author info from V5 API (async)");
                throw new BookInfoException("Failed to get author info from V5 API", ex);
            }
        }

        private V5.V5AuthorResponse FetchAuthorInfoFromV5Paged(string authorId)
        {
            var metadataServerUrl = _configService.MetadataServerUrl;
            var encodedAuthorId = Uri.EscapeDataString(authorId);

            var allBooks = new List<V5.V5Book>();
            var allSeries = new List<V5.V5Series>();
            var seenBooksById = new Dictionary<string, V5.V5Book>(StringComparer.OrdinalIgnoreCase);
            var seenSeriesById = new Dictionary<string, V5.V5Series>(StringComparer.OrdinalIgnoreCase);

            V5.V5AuthorData author = null;
            V5.V5Summary summary = null;
            V5.V5Pagination pagination = null;
            string etag = null;

            for (var page = 1; page <= V5AuthorMaxPages; page++)
            {
                var url = $"{metadataServerUrl}/api/v5/author?id={encodedAuthorId}";
                if (page > 1)
                {
                    url += $"&page={page}";
                }

                var httpRequest = new HttpRequestBuilder(url)
                    .SetHeader("User-Agent", "Chaptarr/2.0")
                    .Accept(HttpAccept.Json)
                    .Build();

                httpRequest.RequestTimeout = V5AuthorRequestTimeout;

                var httpResponse = ExecuteV5Request(httpRequest, _httpClient.Get, $"author {authorId} page {page}");
                _logger.Debug("V5 author API response status (page {0}): {1}", page, httpResponse.StatusCode);
                etag ??= httpResponse.Headers?.GetSingleValue("ETag");

                if (httpResponse.HasHttpError)
                {
                    _logger.Error("V5 author API error for author {0} (page {1}): Status {2}, Content: {3}",
                        authorId,
                        page,
                        httpResponse.StatusCode,
                        httpResponse.Content?.Substring(0, Math.Min(500, httpResponse.Content?.Length ?? 0)));
                    var typedTerminal = ParseTypedAuthorTerminal(httpResponse, authorId);
                    if (typedTerminal != null)
                    {
                        throw typedTerminal;
                    }

                    if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new AuthorNotFoundException(authorId);
                    }

                    throw new BookInfoException($"V5 API error: {httpResponse.StatusCode}");
                }

                var pageResponse = JsonConvert.DeserializeObject<V5.V5AuthorResponse>(httpResponse.Content, SerializerSettings);
                if (pageResponse?.Author == null)
                {
                    throw new AuthorNotFoundException(authorId);
                }

                author ??= pageResponse.Author;
                summary ??= pageResponse.Summary;
                pagination = pageResponse.Pagination ?? pagination;

                foreach (var book in pageResponse.Books ?? new List<V5.V5Book>())
                {
                    if (book?.Id.IsNullOrWhiteSpace() != false)
                    {
                        continue;
                    }

                    var bookId = book.Id.Trim();
                    if (seenBooksById.TryAdd(bookId, book))
                    {
                        allBooks.Add(book);
                        continue;
                    }

                    LogDuplicateV5PagedBook(authorId, page, seenBooksById[bookId], book);
                }

                foreach (var series in pageResponse.Series ?? new List<V5.V5Series>())
                {
                    if (series?.Id.IsNullOrWhiteSpace() != false)
                    {
                        continue;
                    }

                    var seriesId = series.Id.Trim();
                    if (seenSeriesById.TryAdd(seriesId, series))
                    {
                        allSeries.Add(series);
                        continue;
                    }

                    LogDuplicateV5PagedSeries(authorId, page, seenSeriesById[seriesId], series);
                }

                if (allBooks.Count > V5AuthorMaxBooks)
                {
                    _logger.Warn("V5 author payload exceeded max book threshold ({0}) for author {1}. Returning partial data.", V5AuthorMaxBooks, authorId);
                    break;
                }

                if (pageResponse.Pagination == null || !pageResponse.Pagination.HasNext)
                {
                    break;
                }
            }

            _logger.Debug("V5 author payload received: {0} books, {1} series (authorId={2})", allBooks.Count, allSeries.Count, authorId);

            return new V5.V5AuthorResponse
            {
                ETag = etag,
                Author = author,
                Summary = summary,
                Pagination = pagination,
                Books = allBooks,
                Series = allSeries
            };
        }

        private async Task<V5.V5AuthorResponse> FetchAuthorInfoFromV5PagedAsync(string authorId)
        {
            var metadataServerUrl = _configService.MetadataServerUrl;
            var encodedAuthorId = Uri.EscapeDataString(authorId);

            var allBooks = new List<V5.V5Book>();
            var allSeries = new List<V5.V5Series>();
            var seenBooksById = new Dictionary<string, V5.V5Book>(StringComparer.OrdinalIgnoreCase);
            var seenSeriesById = new Dictionary<string, V5.V5Series>(StringComparer.OrdinalIgnoreCase);

            V5.V5AuthorData author = null;
            V5.V5Summary summary = null;
            V5.V5Pagination pagination = null;
            string etag = null;

            for (var page = 1; page <= V5AuthorMaxPages; page++)
            {
                var url = $"{metadataServerUrl}/api/v5/author?id={encodedAuthorId}";
                if (page > 1)
                {
                    url += $"&page={page}";
                }

                var httpRequest = new HttpRequestBuilder(url)
                    .SetHeader("User-Agent", "Chaptarr/2.0")
                    .Accept(HttpAccept.Json)
                    .Build();

                httpRequest.RequestTimeout = V5AuthorRequestTimeout;

                var httpResponse = await ExecuteV5RequestAsync(httpRequest, _httpClient.GetAsync, $"author {authorId} page {page}");
                _logger.Debug("V5 author API response status (page {0}): {1}", page, httpResponse.StatusCode);
                etag ??= httpResponse.Headers?.GetSingleValue("ETag");

                if (httpResponse.HasHttpError)
                {
                    _logger.Error("V5 author API error for author {0} (page {1}): Status {2}, Content: {3}",
                        authorId,
                        page,
                        httpResponse.StatusCode,
                        httpResponse.Content?.Substring(0, Math.Min(500, httpResponse.Content?.Length ?? 0)));
                    var typedTerminal = ParseTypedAuthorTerminal(httpResponse, authorId);
                    if (typedTerminal != null)
                    {
                        throw typedTerminal;
                    }

                    if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new AuthorNotFoundException(authorId);
                    }

                    throw new BookInfoException($"V5 API error: {httpResponse.StatusCode}");
                }

                var pageResponse = JsonConvert.DeserializeObject<V5.V5AuthorResponse>(httpResponse.Content, SerializerSettings);
                if (pageResponse?.Author == null)
                {
                    throw new AuthorNotFoundException(authorId);
                }

                author ??= pageResponse.Author;
                summary ??= pageResponse.Summary;
                pagination = pageResponse.Pagination ?? pagination;

                foreach (var book in pageResponse.Books ?? new List<V5.V5Book>())
                {
                    if (book?.Id.IsNullOrWhiteSpace() != false)
                    {
                        continue;
                    }

                    var bookId = book.Id.Trim();
                    if (seenBooksById.TryAdd(bookId, book))
                    {
                        allBooks.Add(book);
                        continue;
                    }

                    LogDuplicateV5PagedBook(authorId, page, seenBooksById[bookId], book);
                }

                foreach (var series in pageResponse.Series ?? new List<V5.V5Series>())
                {
                    if (series?.Id.IsNullOrWhiteSpace() != false)
                    {
                        continue;
                    }

                    var seriesId = series.Id.Trim();
                    if (seenSeriesById.TryAdd(seriesId, series))
                    {
                        allSeries.Add(series);
                        continue;
                    }

                    LogDuplicateV5PagedSeries(authorId, page, seenSeriesById[seriesId], series);
                }

                if (allBooks.Count > V5AuthorMaxBooks)
                {
                    _logger.Warn("V5 author payload exceeded max book threshold ({0}) for author {1}. Returning partial data.", V5AuthorMaxBooks, authorId);
                    break;
                }

                if (pageResponse.Pagination == null || !pageResponse.Pagination.HasNext)
                {
                    break;
                }
            }

            _logger.Debug("V5 author payload received: {0} books, {1} series (authorId={2})", allBooks.Count, allSeries.Count, authorId);

            return new V5.V5AuthorResponse
            {
                ETag = etag,
                Author = author,
                Summary = summary,
                Pagination = pagination,
                Books = allBooks,
                Series = allSeries
            };
        }

        public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false)
        {
            _logger.Debug("RefreshAuthorInfo called for author {0}, ETag: {1}, ForceRefresh: {2}, ExpectedPublishedETag: {3}, BypassETag: {4}",
                authorId, etag ?? "none", forceRefresh, expectedPublishedETag ?? "none", bypassEtag);

            try
            {
                var metadataServerUrl = _configService.MetadataServerUrl;
                // Chaptarr should only ask for the latest published snapshot and let the
                // metadata service decide when to rebuild it. Manual/force refresh still
                // matters locally, but it should not trigger server-side force refreshes.
                var url = $"{metadataServerUrl}/api/v5/author?id={Uri.EscapeDataString(authorId)}";

                // When the scoped diff already told us a newer payload ETag exists, request
                // a versioned snapshot URL so we don't get stuck on an older CDN cache key.
                // Manual refreshes without a diff hint also bypass the edge cache key so the
                // user sees the latest published blob without triggering server-side work.
                var snapshotHint = BuildAuthorSnapshotHint(expectedPublishedETag, forceRefresh);
                if (!string.IsNullOrWhiteSpace(snapshotHint))
                {
                    url += $"&snapshot={Uri.EscapeDataString(snapshotHint)}";
                    _logger.Debug("Using versioned author snapshot URL for {0}: snapshot={1}", authorId, snapshotHint);
                }

                var httpRequestBuilder = new HttpRequestBuilder(url)
                    .SetHeader("User-Agent", "Chaptarr/2.0")
                    .Accept(HttpAccept.Json);

                // Local hydration refreshes need the already-published payload even when
                // the author's ETag is unchanged, but they still must not trigger a
                // metadata-server rebuild.
                if (!bypassEtag && !string.IsNullOrWhiteSpace(etag))
                {
                    httpRequestBuilder.SetHeader("If-None-Match", etag);
                    _logger.Debug("Adding If-None-Match header with ETag: {0}", etag);
                }

                var httpRequest = httpRequestBuilder.Build();
                var httpResponse = ExecuteV5Request(httpRequest, _httpClient.Get, $"author refresh {authorId}");

                _logger.Debug("Refresh API response status: {0}", httpResponse.StatusCode);

                // Handle 304 Not Modified - no changes since last ETag
                if (httpResponse.StatusCode == HttpStatusCode.NotModified)
                {
                    _logger.Debug("Author {0} not modified since ETag {1}", authorId, etag);
                    var notModifiedETag = httpResponse.Headers?.GetSingleValue("ETag");
                    return RefreshResult.NoChanges(notModifiedETag ?? etag);
                }
                // A typed terminal on refresh must never erase a previously good local author.
                // Preserve the local snapshot and stop this refresh cycle; pending imports handle
                // the same response as an immediate declared failure.
                if (httpResponse.StatusCode == HttpStatusCode.NotFound ||
                    httpResponse.StatusCode == HttpStatusCode.Conflict)
                {
                    var typedTerminal = ParseTypedAuthorTerminal(httpResponse, authorId);
                    if (typedTerminal != null)
                    {
                        _logger.Warn("Author {0} refresh declared {1}; preserving the existing local snapshot. {2}",
                            authorId, typedTerminal.Code, typedTerminal.Message);
                        return RefreshResult.NoChanges(etag);
                    }

                    if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.Warn("Author {0} returned legacy untyped 404 on refresh", authorId);
                        return RefreshResult.NotFound();
                    }

                    return RefreshResult.Error($"Untyped author conflict for {authorId}", httpResponse.StatusCode);
                }

                // Handle 202 Accepted — the metadata server has no ready published payload yet.
                // Treat this as "no changes this cycle" so the next refresh can pick up data
                // once the server's own rebuild/import pipeline has finished.
                if (httpResponse.StatusCode == HttpStatusCode.Accepted)
                {
                    _logger.Info("Author {0} refresh queued by server (HTTP 202); deferring until next refresh.", authorId);
                    return RefreshResult.NoChanges(etag);
                }

                // Handle other HTTP errors
                if (httpResponse.HasHttpError)
                {
                    var errorMsg = $"Refresh API error for author {authorId}: {httpResponse.StatusCode}";
                    _logger.Error(errorMsg + ", Content: {0}", 
                        httpResponse.Content?.Substring(0, Math.Min(500, httpResponse.Content?.Length ?? 0)));
                    return RefreshResult.Error(errorMsg, httpResponse.StatusCode);
                }

                // Parse successful response
                var v5Response = JsonConvert.DeserializeObject<V5.V5AuthorResponse>(httpResponse.Content, SerializerSettings);
                
                if (v5Response?.Author == null)
                {
                    _logger.Error("Refresh API returned empty author data for {0}", authorId);
                    return RefreshResult.Error("Empty author data received");
                }

                // Extract new ETag from response headers
                var newETag = httpResponse.Headers?.GetSingleValue("ETag");
                _logger.Debug("Received new ETag for author {0}: {1}", authorId, newETag ?? "none");

                // Convert V5 response to domain model
                var updatedAuthor = ConvertV5AuthorToDomain(v5Response, authorId);
                
                _logger.Info("Successfully refreshed author {0}: {1}", authorId, updatedAuthor.Name);
                return RefreshResult.Updated(updatedAuthor, newETag);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error refreshing author info for {0}", authorId);
                return RefreshResult.Error($"Refresh failed: {ex.Message}");
            }
        }

        private static AuthorTerminalException ParseTypedAuthorTerminal(HttpResponse response, string requestedProviderId)
        {
            if (response == null ||
                (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.Conflict) ||
                string.IsNullOrWhiteSpace(response.Content))
            {
                return null;
            }

            V5.V5AuthorTerminalResponse terminal;
            try
            {
                terminal = JsonConvert.DeserializeObject<V5.V5AuthorTerminalResponse>(response.Content, SerializerSettings);
            }
            catch (JsonException)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    throw new BookInfoException("Metadata server returned an untyped author conflict for {0}", requestedProviderId);
                }

                return null;
            }

            if (terminal?.Code.IsNullOrWhiteSpace() != false)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    throw new BookInfoException("Metadata server returned an untyped author conflict for {0}", requestedProviderId);
                }

                return null;
            }

            if (!AuthorTerminalException.IsKnownCode(terminal.Code))
            {
                throw new BookInfoException("Metadata server returned unknown author terminal code '{0}' for {1}", terminal.Code, requestedProviderId);
            }

            var identityAmbiguous = terminal.Code == "author_identity_ambiguous";
            if ((identityAmbiguous && response.StatusCode != HttpStatusCode.Conflict) ||
                (!identityAmbiguous && response.StatusCode != HttpStatusCode.NotFound))
            {
                throw new BookInfoException("Metadata server returned author terminal code '{0}' with invalid HTTP status {1}", terminal.Code, response.StatusCode);
            }

            if (terminal.Retryable)
            {
                throw new BookInfoException("Metadata server violated the author terminal contract: code '{0}' was marked retryable", terminal.Code);
            }

            if (identityAmbiguous && !terminal.Reopenable)
            {
                throw new BookInfoException("Metadata server violated the author terminal contract: identity ambiguity was not reopenable");
            }

            return new AuthorTerminalException(
                terminal.Code,
                terminal.ProviderId ?? requestedProviderId,
                terminal.ResolvedProviderId,
                terminal.Message ?? terminal.Code,
                terminal.Reopenable);
        }

        private static string BuildAuthorSnapshotHint(string expectedPublishedETag, bool forceRefresh)
        {
            if (!string.IsNullOrWhiteSpace(expectedPublishedETag))
            {
                var normalized = Regex.Replace(expectedPublishedETag, @"[^A-Za-z0-9._-]+", string.Empty);
                return string.IsNullOrWhiteSpace(normalized) ? "etag" : normalized;
            }

            if (forceRefresh)
            {
                return $"manual-{DateTime.UtcNow.Ticks}";
            }

            return null;
        }

        private HttpResponse ExecuteV5Request(HttpRequest request, Func<HttpRequest, HttpResponse> execute, string operation)
        {
            if (!_metadataServerHealthGate.TryBeginRequest(out var retryAfter))
            {
                throw new BookInfoException("Metadata server is temporarily unavailable for {0}; retrying in {1}",
                    operation,
                    MetadataServerHealthGate.FormatRetryAfter(retryAfter));
            }

            try
            {
                var response = execute(request);
                _metadataServerHealthGate.ReportResponse(response);
                return response;
            }
            catch (Exception ex)
            {
                _metadataServerHealthGate.ReportException(ex);
                throw;
            }
        }

        private HttpResponse<T> ExecuteV5Request<T>(HttpRequest request, Func<HttpRequest, HttpResponse<T>> execute, string operation)
            where T : new()
        {
            if (!_metadataServerHealthGate.TryBeginRequest(out var retryAfter))
            {
                throw new BookInfoException("Metadata server is temporarily unavailable for {0}; retrying in {1}",
                    operation,
                    MetadataServerHealthGate.FormatRetryAfter(retryAfter));
            }

            try
            {
                var response = execute(request);
                _metadataServerHealthGate.ReportResponse(response);
                return response;
            }
            catch (Exception ex)
            {
                _metadataServerHealthGate.ReportException(ex);
                throw;
            }
        }

        private async Task<HttpResponse> ExecuteV5RequestAsync(HttpRequest request, Func<HttpRequest, Task<HttpResponse>> execute, string operation)
        {
            if (!_metadataServerHealthGate.TryBeginRequest(out var retryAfter))
            {
                throw new BookInfoException("Metadata server is temporarily unavailable for {0}; retrying in {1}",
                    operation,
                    MetadataServerHealthGate.FormatRetryAfter(retryAfter));
            }

            try
            {
                var response = await execute(request);
                _metadataServerHealthGate.ReportResponse(response);
                return response;
            }
            catch (Exception ex)
            {
                _metadataServerHealthGate.ReportException(ex);
                throw;
            }
        }

        private Author ConvertV5AuthorToDomain(V5.V5AuthorResponse v5Response, string originalAuthorId = null)
        {
            var authorData = v5Response.Author;
            var born = authorData.BirthDate.ToValidAuthorDate();
            var died = authorData.DeathDate.ToValidAuthorDate();

            var author = new Author
            {
                TitleSlug = authorData.Slug,
                Name = authorData.Name,
                SortName = authorData.SortName,
                Overview = authorData.Bio,
                RemoteMetadataETag = v5Response.ETag,
                Ratings = new Ratings
                {
                    Votes = authorData.RatingCount,
                    Value = authorData.RatingAverage
                },
                Born = born,
                Died = died,
                Status = AuthorExtensions.GetLifeStatus(died)
            };

                // Parse and set provider ID from V5 API ID
                var v5Id = originalAuthorId ?? authorData.Id.ToString();
                var (provider, parsedId) = ParseProviderId(v5Id);
                SetAuthorProviderId(author, provider, parsedId);

            // Set name variations
            author.NameLastFirst = author.Name.ToLastFirst();
            author.SortNameLastFirst = author.NameLastFirst.ToLower();

            // Provider IDs (store with a single, normalized prefix; tolerate legacy/raw values and accidental double-prefixing)
            var normalizedGoodreadsAuthorId = ProviderIdHelper.Normalize(authorData.GoodreadsAuthorId, "gr");
            if (!string.IsNullOrWhiteSpace(normalizedGoodreadsAuthorId))
            {
                author.GoodreadsAuthorId = normalizedGoodreadsAuthorId;
            }

            var normalizedHardcoverAuthorId = ProviderIdHelper.Normalize(authorData.HardcoverAuthorId, "hc");
            if (!string.IsNullOrWhiteSpace(normalizedHardcoverAuthorId))
            {
                author.HardcoverAuthorId = normalizedHardcoverAuthorId;
            }

            var normalizedOpenLibraryAuthorId = ProviderIdHelper.Normalize(authorData.OpenLibraryAuthorId, "ol");
            if (!string.IsNullOrWhiteSpace(normalizedOpenLibraryAuthorId))
            {
                author.OpenLibraryAuthorId = normalizedOpenLibraryAuthorId;
            }

            var normalizedGoogleBooksAuthorId = ProviderIdHelper.Normalize(authorData.GoogleBooksAuthorId, "gb");
            if (!string.IsNullOrWhiteSpace(normalizedGoogleBooksAuthorId))
            {
                author.GoogleBooksAuthorId = normalizedGoogleBooksAuthorId;
            }

            // Amazon/Audible author IDs use Amazon's author identifier; store them as az:{id}.
            if (!string.IsNullOrWhiteSpace(authorData.AudnexusAuthorId))
            {
                author.AudnexusAuthorId = ProviderIdHelper.Normalize(authorData.AudnexusAuthorId, "az");
            }

                // Capture upstream provider ID sets (if present) for robust identity matching and parent lookups.
                // This is not persisted; it only exists for the lifetime of the mapping/refresh operation.
                HashSet<string> remoteProviderIds = null;
                void AddRemoteProviderId(string id)
                {
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return;
                    }

                    remoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    remoteProviderIds.Add(id.Trim());
                }

                // Always include the canonical single IDs we already parsed.
                AddRemoteProviderId(author.HardcoverAuthorId);
                AddRemoteProviderId(author.GoodreadsAuthorId);
                AddRemoteProviderId(author.OpenLibraryAuthorId);
                AddRemoteProviderId(author.GoogleBooksAuthorId);
                AddRemoteProviderId(author.AudnexusAuthorId);

                foreach (var id in EnumerateProviderAliases(authorData.ProviderIds))
                {
                    AddRemoteProviderId(id);
                }

                if (authorData.ProviderIdsAll != null)
                {
                    foreach (var kvp in authorData.ProviderIdsAll)
                    {
                        if (kvp.Value == null)
                        {
                            continue;
                        }

                        foreach (var id in kvp.Value)
                        {
                            AddRemoteProviderId(id);
                        }
                    }
                }

                if (remoteProviderIds?.Count > 0)
                {
                    author.RemoteProviderIds = remoteProviderIds;
                }
                author.ISNI = authorData.Isni;
                author.VIAF = authorData.Viaf;

                // Add pseudonyms with validation
                author.Pseudonyms = authorData.Pseudonyms?.ValidatePseudonyms(author.Name) ?? new List<string>();

            // Add provider URLs with validation
            author.ProviderUrls = authorData.ProviderUrls?.ValidateProviderUrls() ?? new ProviderUrlMap();

            // Set last updated - parse from string
            author.LastUpdated = authorData.LastUpdated.ToUtcDateTime();

            // Add images from Photos collection
            if (authorData.Photos != null && authorData.Photos.Any())
            {
                foreach (var photo in authorData.Photos)
                {
                    if (photo.Url.IsValidHttpUrl() &&
                        !MediaCoverRendition.IsKnownPlaceholderImageUrl(photo.Url))
                    {
                        author.Images.Add(new MediaCover.MediaCover
                        {
                            Url = photo.Url,
                            CoverType = MediaCoverTypes.Poster
                        });
                    }
                }
            }

            // Add links from provider URLs (excluding _metadata)
            author.Links = author.ProviderUrls?.Where(kvp => kvp.Key != "_metadata").Select(kvp => new Links
            {
                Url = kvp.Value ?? "",
                Name = kvp.Key
            }).ToList() ?? new List<Links>();

            // Store all provider IDs in Aliases for backward compatibility
            if (!string.IsNullOrWhiteSpace(author.GoodreadsAuthorId))
            {
                author.Aliases.Add(author.GoodreadsAuthorId);
            }
            if (!string.IsNullOrWhiteSpace(author.HardcoverAuthorId))
            {
                author.Aliases.Add(author.HardcoverAuthorId);
            }

            // Set additional Author properties. Monitoring is configured by the
            // add/import boundary; metadata conversion must not select book rows.
            author.CleanName = author.Name.CleanAuthorName();
            author.Monitored = false;
            author.AudiobookMonitored = null;
            author.EbookMonitored = null;
            author.AudiobookMonitorNewItems = null;
            author.EbookMonitorNewItems = null;
            author.Books = new List<Book>();
            author.Series = new List<Series>();

            // Add books - each V5 book should already represent one SMS work/pocket.
            if (v5Response.Books != null && v5Response.Books.Any())
            {
                var allBookInstances = new List<Book>();
                var bookGroups = GroupV5BooksBySharedProviderIds(v5Response.Books);

                foreach (var booksInGroup in bookGroups)
                {
                    if (booksInGroup.Count > 1)
                    {
                        LogDuplicateV5BookPocket(author, booksInGroup);
                    }
                }

                foreach (var v5Book in v5Response.Books.Where(book => book != null))
                {
                    // Create audiobook instance
                    var audiobookInstance = ConvertV5BookToDomain(v5Book, author, BookMediaType.Audiobook);
                    audiobookInstance.BaseBookId ??= v5Book.BaseBookId ?? v5Book.Id;
                    if (audiobookInstance.Editions.Any())
                    {
                        SelectBestEditionAsMonitored(audiobookInstance);
                        allBookInstances.Add(audiobookInstance);
                    }

                    // Create ebook instance
                    var ebookInstance = ConvertV5BookToDomain(v5Book, author, BookMediaType.Ebook);
                    ebookInstance.BaseBookId ??= v5Book.BaseBookId ?? v5Book.Id;
                    if (ebookInstance.Editions.Any())
                    {
                        SelectBestEditionAsMonitored(ebookInstance);
                        allBookInstances.Add(ebookInstance);
                    }

                    _logger.Trace("[DUAL-INSTANCE] Created audiobook and ebook instances for book: {0}", v5Book.Title);
                }

                author.Books = allBookInstances;
                _logger.Trace("[DUAL-INSTANCE] Created {0} book instances ({1} audiobook, {2} ebook) for author {3}",
                    allBookInstances.Count,
                    allBookInstances.Count(b => b.MediaType == BookMediaType.Audiobook),
                    allBookInstances.Count(b => b.MediaType == BookMediaType.Ebook),
                    author.Name);
            }

            // Process series from V5 Series array only (provider-ID-backed).
            // We do NOT synthesize series from Books.SeriesName; series without provider IDs are not persisted.
            var allSeries = new List<Series>();

            if (v5Response.Series != null && v5Response.Series.Any())
            {
                _logger.Trace("[SERIES-V5] Processing {0} series from V5 API Series array for author {1}",
                    v5Response.Series.Count, author.Name);

                foreach (var v5Series in v5Response.Series)
                {
                    if (string.IsNullOrWhiteSpace(v5Series.Name))
                    {
                        continue;
                    }

                    // Use ConvertV5SeriesToDomain to properly process series including SeriesBooks membership.
                    var baseSeries = ConvertV5SeriesToDomain(v5Series);

                    // Enforce Goodreads-backed series only. Amazon-only series are considered invalid and are not persisted.
                    if (string.IsNullOrWhiteSpace(baseSeries.GoodreadsSeriesId))
                    {
                        _logger.Debug("[SERIES-V5] Skipping series '{0}' for author {1} because it has no Goodreads series ID",
                            baseSeries.Title, author.Name);
                        continue;
                    }

                    // Create audiobook series
                    var audiobookSeries = new Series
                    {
                        Title = baseSeries.Title,
                        Description = baseSeries.Description,
                        Numbered = baseSeries.Numbered,
                        WorkCount = baseSeries.WorkCount,
                        PrimaryWorkCount = baseSeries.PrimaryWorkCount,
                        MediaType = BookMediaType.Audiobook,
                        SeriesType = baseSeries.SeriesType,
                        GoodreadsSeriesId = baseSeries.GoodreadsSeriesId,
                        HardcoverSeriesId = baseSeries.HardcoverSeriesId,
                        OpenLibrarySeriesId = baseSeries.OpenLibrarySeriesId,
                        AmazonSeriesAsin = baseSeries.AmazonSeriesAsin,
                        Links = baseSeries.Links,
                        ProviderUrls = baseSeries.ProviderUrls,
                        LinkItems = baseSeries.LinkItems,
                        SeriesBooks = baseSeries.SeriesBooks
                    };
                    allSeries.Add(audiobookSeries);

                    // Create ebook series
                    var ebookSeries = new Series
                    {
                        Title = baseSeries.Title,
                        Description = baseSeries.Description,
                        Numbered = baseSeries.Numbered,
                        WorkCount = baseSeries.WorkCount,
                        PrimaryWorkCount = baseSeries.PrimaryWorkCount,
                        MediaType = BookMediaType.Ebook,
                        SeriesType = baseSeries.SeriesType,
                        GoodreadsSeriesId = baseSeries.GoodreadsSeriesId,
                        HardcoverSeriesId = baseSeries.HardcoverSeriesId,
                        OpenLibrarySeriesId = baseSeries.OpenLibrarySeriesId,
                        AmazonSeriesAsin = baseSeries.AmazonSeriesAsin,
                        Links = baseSeries.Links,
                        ProviderUrls = baseSeries.ProviderUrls,
                        LinkItems = baseSeries.LinkItems?.ToList() ?? new List<SeriesBookLink>(),
                        SeriesBooks = baseSeries.SeriesBooks?.ToList() ?? new List<SeriesBook>()
                    };
                    allSeries.Add(ebookSeries);

                    _logger.Trace("[SERIES-V5] Created dual instances for series '{0}' (ID: {1}, Type: {2}, SeriesBooks: {3})",
                        v5Series.Name, v5Series.Id, v5Series.SeriesType ?? "main", baseSeries.SeriesBooks?.Count ?? 0);
                }
            }

            // Build in-memory Book.SeriesLinks from provider-ID-based series membership (SeriesBooks),
            // not from Books.SeriesName. This supports multiple series per book without overwriting.
            if (author.Books?.Any() == true && allSeries.Any())
            {
                string NormalizePrefixedId(string providerId)
                {
                    if (providerId.IsNullOrWhiteSpace())
                    {
                        return null;
                    }

                    providerId = providerId.Trim();
                    var idx = providerId.IndexOf(':');
                    if (idx <= 0 || idx >= providerId.Length - 1)
                    {
                        return null;
                    }

                    var prefix = providerId.Substring(0, idx).Trim().ToLowerInvariant();
                    return ProviderIdHelper.Normalize(providerId, prefix);
                }

                var bookByMediaAndProviderId = new Dictionary<BookMediaType, Dictionary<string, Book>>();
                foreach (var book in author.Books)
                {
                    if (book == null)
                    {
                        continue;
                    }

                    if (!bookByMediaAndProviderId.TryGetValue(book.MediaType, out var map))
                    {
                        map = new Dictionary<string, Book>(StringComparer.OrdinalIgnoreCase);
                        bookByMediaAndProviderId[book.MediaType] = map;
                    }

                    void AddKey(string id)
                    {
                        var normalized = NormalizePrefixedId(id);
                        if (normalized.IsNullOrWhiteSpace())
                        {
                            return;
                        }

                        if (!map.ContainsKey(normalized))
                        {
                            map[normalized] = book;
                        }
                    }

                    AddKey(book.HardcoverBookId);
                    AddKey(book.GoodreadsWorkId);
                    AddKey(book.OpenLibraryWorkId);
                }

                foreach (var series in allSeries.Where(s => s != null))
                {
                    if (series.SeriesBooks?.Any() != true)
                    {
                        continue;
                    }

                    if (!bookByMediaAndProviderId.TryGetValue(series.MediaType, out var map))
                    {
                        continue;
                    }

                    foreach (var seriesBook in series.SeriesBooks)
                    {
                        var normalizedBookId = NormalizePrefixedId(seriesBook?.BookId);
                        if (normalizedBookId.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        if (!map.TryGetValue(normalizedBookId, out var matchingBook))
                        {
                            _logger.Trace("[SERIES-LINKS] No local {0} book match for series '{1}' member '{2}' ({3})",
                                series.MediaType, series.Title, seriesBook?.Title, normalizedBookId);
                            continue;
                        }

                        matchingBook.SeriesLinks ??= new List<SeriesBookLink>();

                        if (matchingBook.SeriesLinks.Any(l => l?.Series?.IsLoaded == true && ReferenceEquals(l.Series.Value, series)))
                        {
                            continue;
                        }

                        matchingBook.SeriesLinks.Add(new SeriesBookLink
                        {
                            Series = new LazyLoaded<Series>(series),
                            Position = seriesBook.Position,
                            SeriesPosition = int.TryParse(seriesBook.Position, out var pos) ? pos : 0,
                            IsPrimary = seriesBook.IsPrimary ?? true
                        });
                    }
                }
            }

            author.Series = allSeries;
            _logger.Trace("[SERIES-TOTAL] Created {0} total series instances ({1} audiobook, {2} ebook) from V5 API data",
                allSeries.Count,
                allSeries.Count(s => s.MediaType == BookMediaType.Audiobook),
                allSeries.Count(s => s.MediaType == BookMediaType.Ebook));

            _logger.Trace("Final series count for author {0}: {1}",
                author.Name, author.Series?.Count ?? 0);

            return author;
        }

        private static List<List<V5.V5Book>> GroupV5BooksBySharedProviderIds(IEnumerable<V5.V5Book> books)
        {
            var entries = books
                .Where(book => book != null)
                .Select(book => new
                {
                    Book = book,
                    ProviderIds = GetV5BookProviderIdSet(book)
                })
                .ToList();

            var providerOwners = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < entries.Count; i++)
            {
                foreach (var providerId in entries[i].ProviderIds)
                {
                    if (!providerOwners.TryGetValue(providerId, out var owners))
                    {
                        owners = new List<int>();
                        providerOwners[providerId] = owners;
                    }

                    owners.Add(i);
                }
            }

            var groups = new List<List<V5.V5Book>>();
            var visited = new bool[entries.Count];

            for (var i = 0; i < entries.Count; i++)
            {
                if (visited[i])
                {
                    continue;
                }

                var group = new List<V5.V5Book>();
                var pending = new Queue<int>();
                pending.Enqueue(i);
                visited[i] = true;

                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    group.Add(entries[current].Book);

                    foreach (var providerId in entries[current].ProviderIds)
                    {
                        foreach (var owner in providerOwners[providerId])
                        {
                            if (visited[owner])
                            {
                                continue;
                            }

                            visited[owner] = true;
                            pending.Enqueue(owner);
                        }
                    }
                }

                groups.Add(group);
            }

            return groups;
        }

        private void LogDuplicateV5BookPocket(Author author, List<V5.V5Book> books)
        {
            var duplicatedProviderIds = FindDuplicatedV5BookProviderIds(books);
            var bookDetails = string.Join(" | ", books.Select(book =>
                $"'{book?.Title ?? "Unknown"}' ({FormatV5BookProviderIds(book)}, editions={book?.Editions?.Count ?? 0})"));

            _logger.Warn("[SERVER-BUG-CANDIDATE] V5 returned {0} books with provider id(s) appearing in multiple pockets: [{1}] for author '{2}' ({3}). Preserving all returned pockets locally; duplicates are not merged locally. Books: {4}",
                books.Count,
                string.Join(", ", duplicatedProviderIds),
                author?.Name ?? "Unknown",
                FormatAuthorProviderIds(author),
                bookDetails);
        }

        private void LogDuplicateV5PagedBook(string requestedAuthorId, int page, V5.V5Book firstBook, V5.V5Book duplicateBook)
        {
            var duplicatedProviderIds = FindDuplicatedV5BookProviderIds(new[] { firstBook, duplicateBook });

            _logger.Warn("[SERVER-BUG-CANDIDATE] V5 paged author response returned provider id(s) appearing in multiple book pockets: [{0}] for author '{1}' on page {2}. Keeping first occurrence and dropping duplicate. First: '{3}' ({4}); Duplicate: '{5}' ({6})",
                string.Join(", ", duplicatedProviderIds),
                requestedAuthorId,
                page,
                firstBook?.Title ?? "Unknown",
                FormatV5BookProviderIds(firstBook),
                duplicateBook?.Title ?? "Unknown",
                FormatV5BookProviderIds(duplicateBook));
        }

        private void LogDuplicateV5PagedSeries(string requestedAuthorId, int page, V5.V5Series firstSeries, V5.V5Series duplicateSeries)
        {
            var duplicatedProviderIds = FindDuplicatedV5SeriesProviderIds(new[] { firstSeries, duplicateSeries });

            _logger.Warn("[SERVER-BUG-CANDIDATE] V5 paged author response returned series provider id(s) appearing in multiple series pockets: [{0}] for author '{1}' on page {2}. Keeping first occurrence and dropping duplicate. First: '{3}' ({4}); Duplicate: '{5}' ({6})",
                string.Join(", ", duplicatedProviderIds),
                requestedAuthorId,
                page,
                firstSeries?.Name ?? "Unknown",
                FormatV5SeriesProviderIds(firstSeries),
                duplicateSeries?.Name ?? "Unknown",
                FormatV5SeriesProviderIds(duplicateSeries));
        }

        private static List<string> FindDuplicatedV5SeriesProviderIds(IEnumerable<V5.V5Series> series)
        {
            return series
                .Where(s => s != null)
                .SelectMany(GetV5SeriesProviderIdSet)
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<string> GetV5SeriesProviderIdSet(V5.V5Series series)
        {
            // BINDING: Only stable provider IDs. SMS row identifiers (series.Id)
            // MUST NOT appear in this set — series.Id is SMS-internal.
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddProviderId(ids, series?.HardcoverSeriesId, "hc");
            AddProviderId(ids, series?.GoodreadsSeriesId, "gr");
            AddProviderId(ids, series?.OpenLibrarySeriesId, "ol");
            AddProviderId(ids, series?.AmazonSeriesAsin, "az");
            return ids;
        }

        private static string FormatAuthorProviderIds(Author author)
        {
            if (author == null)
            {
                return "authorIds=unknown";
            }

            var ids = new List<string>();
            AddProviderField(ids, "hcAuthor", author.HardcoverAuthorId);
            AddProviderField(ids, "grAuthor", author.GoodreadsAuthorId);
            AddProviderField(ids, "olAuthor", author.OpenLibraryAuthorId);
            AddProviderField(ids, "gbAuthor", author.GoogleBooksAuthorId);
            AddProviderField(ids, "audnexusAuthor", author.AudnexusAuthorId);

            if (author.RemoteProviderIds?.Any() == true)
            {
                ids.Add("remoteProviderIds=[" + string.Join("|", author.RemoteProviderIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)) + "]");
            }

            return ids.Any() ? string.Join(", ", ids) : "authorIds=none";
        }

        private static string FormatV5BookProviderIds(V5.V5Book book)
        {
            if (book == null)
            {
                return "bookIds=unknown";
            }

            var ids = GetV5BookProviderIdSet(book)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return ids.Any() ? "providerIds=[" + string.Join(", ", ids) + "]" : "providerIds=[]";
        }

        private static string FormatV5SeriesProviderIds(V5.V5Series series)
        {
            if (series == null)
            {
                return "seriesIds=unknown";
            }

            var ids = new List<string>();
            AddProviderField(ids, "hardcoverSeriesProviderId", series.HardcoverSeriesId);
            AddProviderField(ids, "goodreadsSeriesProviderId", series.GoodreadsSeriesId);
            AddProviderField(ids, "openLibrarySeriesProviderId", series.OpenLibrarySeriesId);
            AddProviderField(ids, "amazonSeriesProviderId", series.AmazonSeriesAsin);

            return ids.Any() ? string.Join(", ", ids) : "seriesIds=none";
        }

        private static void AddProviderField(List<string> ids, string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ids.Add($"{label}={value.Trim()}");
            }
        }

        private static List<string> FindDuplicatedV5BookProviderIds(IEnumerable<V5.V5Book> books)
        {
            return books
                .Where(book => book != null)
                .SelectMany(book => GetV5BookProviderIdSet(book))
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<string> GetV5BookProviderIdSet(V5.V5Book book)
        {
            // BINDING: Only stable provider IDs (HC, GR, OL, GB, AZ, Audible).
            // SMS row identifiers (book.Id) and pocket keys (BaseBookId) MUST NOT
            // appear in this set — they are SMS-internal and not stable identifiers.
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddProviderId(ids, book?.HardcoverBookId, "hc");
            AddProviderId(ids, book?.GoodreadsBookId, "gr");
            AddProviderId(ids, book?.GoodreadsWorkId, "gr");
            AddProviderId(ids, book?.OpenLibraryWorkId, "ol");
            AddProviderId(ids, book?.GoogleBooksId, "gb");
            AddProviderId(ids, book?.Asin, "az");
            AddProviderId(ids, book?.AudibleAsin, "az");

            if (book?.LegacyProviderIds != null)
            {
                foreach (var providerId in book.LegacyProviderIds.Values)
                {
                    AddProviderId(ids, providerId, null);
                }
            }

            if (book?.ProviderIds != null)
            {
                foreach (var providerId in book.ProviderIds.SelectMany(kvp => kvp.Value ?? new List<string>()))
                {
                    AddProviderId(ids, providerId, null);
                }
            }

            return ids;
        }

        private static void AddProviderId(HashSet<string> ids, string providerId, string defaultPrefix)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return;
            }

            var trimmed = providerId.Trim();

            try
            {
                var normalized = ProviderIdHelper.Normalize(trimmed, defaultPrefix);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    ids.Add(normalized);
                }
            }
            catch
            {
                ids.Add(trimmed);
            }
        }

        private static void AddProviderMap(List<string> ids, Dictionary<string, List<string>> providerIds)
        {
            if (providerIds?.Any() != true)
            {
                return;
            }

            var rendered = providerIds
                .Where(kvp => kvp.Value?.Any(v => !string.IsNullOrWhiteSpace(v)) == true)
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key}=[{string.Join("|", kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()))}]")
                .ToList();

            if (rendered.Any())
            {
                ids.Add("allProviderIds={" + string.Join(", ", rendered) + "}");
            }
        }

        private Book ConvertV5BookToDomain(V5.V5Book v5Book, Author author, BookMediaType? mediaType = null)
        {
            // BaseBookId must be provider-based. Never fall back to local row IDs (workId).
            // If base_book_id is missing, fall back to the provider-prefixed book/work id.
            var stableBaseBookId = !string.IsNullOrWhiteSpace(v5Book.BaseBookId)
                ? v5Book.BaseBookId
                : v5Book.Id;

            // Capture upstream work-level provider ID sets (if present) for robust identity matching.
            // Do not copy SMS row/pocket ids or edition aliases into book-level identity.
            HashSet<string> remoteProviderIds = null;
            void AddRemoteProviderId(string id)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return;
                }

                remoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                remoteProviderIds.Add(id.Trim());
            }

            void AddRemoteWorkProviderId(string id, string providerKey = null)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return;
                }

                string normalized = null;
                try
                {
                    var trimmed = id.Trim();
                    if (trimmed.Contains(":"))
                    {
                        normalized = ProviderIdHelper.Normalize(trimmed, defaultPrefix: null);
                    }
                    else if (TryGetStableWorkPrefix(providerKey, out var expectedPrefix))
                    {
                        normalized = ProviderIdHelper.Canonicalize(trimmed, expectedPrefix);
                    }
                }
                catch
                {
                    normalized = null;
                }

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                var colon = normalized.IndexOf(':');
                if (colon <= 0 || colon >= normalized.Length - 1)
                {
                    return;
                }

                var prefix = normalized.Substring(0, colon);
                var value = normalized.Substring(colon + 1);
                if (value.Length == 0 || value.Contains(":") ||
                    (!prefix.Equals("hc", StringComparison.OrdinalIgnoreCase) &&
                     !prefix.Equals("gr", StringComparison.OrdinalIgnoreCase) &&
                     !prefix.Equals("ol", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                AddRemoteProviderId(normalized);
            }

            if (v5Book.LegacyProviderIds != null)
            {
                foreach (var kvp in v5Book.LegacyProviderIds)
                {
                    AddRemoteWorkProviderId(kvp.Value, kvp.Key);
                }
            }

            if (v5Book.ProviderIds != null)
            {
                foreach (var kvp in v5Book.ProviderIds)
                {
                    foreach (var id in kvp.Value ?? new List<string>())
                    {
                        AddRemoteWorkProviderId(id, kvp.Key);
                    }
                }
            }

            var inferredAsinFromId = v5Book.Id?.StartsWith("az:", StringComparison.OrdinalIgnoreCase) == true
                ? ProviderIdHelper.StripPrefix(v5Book.Id)
                : null;

            var inferredAsinFromProviderIds = TryGetProviderAlias(v5Book.ProviderIds, out var providerAzId, "az", "amazon", "audible") ||
                                              TryGetLegacyProviderId(v5Book.LegacyProviderIds, out providerAzId, "az", "amazon", "audible")
                ? ProviderIdHelper.StripPrefix(providerAzId)
                : null;

            string providerGrId = null;
            string providerHcId = null;
            string providerOlId = null;
            string providerGbId = null;
            TryGetProviderAlias(v5Book.ProviderIds, out providerGrId, "gr", "goodreads");
            TryGetProviderAlias(v5Book.ProviderIds, out providerHcId, "hc", "hardcover");
            TryGetProviderAlias(v5Book.ProviderIds, out providerOlId, "ol", "openlibrary", "openLibrary");
            TryGetProviderAlias(v5Book.ProviderIds, out providerGbId, "gb", "googlebooks", "googleBooks");
            if (string.IsNullOrWhiteSpace(providerGrId))
            {
                TryGetLegacyProviderId(v5Book.LegacyProviderIds, out providerGrId, "gr", "goodreads");
            }
            if (string.IsNullOrWhiteSpace(providerHcId))
            {
                TryGetLegacyProviderId(v5Book.LegacyProviderIds, out providerHcId, "hc", "hardcover");
            }
            if (string.IsNullOrWhiteSpace(providerOlId))
            {
                TryGetLegacyProviderId(v5Book.LegacyProviderIds, out providerOlId, "ol", "openlibrary", "openLibrary");
            }
            if (string.IsNullOrWhiteSpace(providerGbId))
            {
                TryGetLegacyProviderId(v5Book.LegacyProviderIds, out providerGbId, "gb", "googlebooks", "googleBooks");
            }

            var inferredHardcoverBookIdFromId = v5Book.Id?.StartsWith("hc:", StringComparison.OrdinalIgnoreCase) == true
                ? v5Book.Id
                : null;

            var inferredOpenLibraryWorkIdFromId = v5Book.Id?.StartsWith("ol:", StringComparison.OrdinalIgnoreCase) == true
                ? v5Book.Id
                : null;

            var book = new Book
            {
                // Core identity
                Title = v5Book.Title,
                Subtitle = v5Book.Subtitle,
                // CleanTitle is used for matching and must use the same normalization as Parser/BookRepository lookups.
                CleanTitle = (v5Book.Title ?? string.Empty).CleanBookTitle().CleanAuthorName(),
                TitleSlug = v5Book.Slug, // CRITICAL: Fixes undefined URLs
                Overview = v5Book.Description, // CRITICAL: Adds book descriptions
                ForeignEditionId = v5Book.Id, // Use V5 book ID as foreign edition ID

                // Provider IDs (store with a single, normalized prefix; tolerate legacy/raw values and accidental double-prefixing)
                GoodreadsBookId = null,
                GoodreadsWorkId = ProviderIdHelper.Normalize(v5Book.GoodreadsWorkId ?? providerGrId, "gr"),
                HardcoverBookId = ProviderIdHelper.Normalize(v5Book.HardcoverBookId ?? providerHcId ?? inferredHardcoverBookIdFromId, "hc"),
                ISBN10 = null,
                ISBN13 = null,
                // Amazon-only book identifiers come through as az:* IDs; prefer that source of truth.
                ASIN = null,
                AudibleASIN = null,
                OpenLibraryEditionId = null,
                OpenLibraryWorkId = ProviderIdHelper.Normalize(v5Book.OpenLibraryWorkId ?? providerOlId ?? inferredOpenLibraryWorkIdFromId, "ol"),
                GoogleBooksId = null,
                BaseBookId = stableBaseBookId,
                RemoteProviderIds = remoteProviderIds,

                // Metadata
                OriginalTitle = v5Book.OriginalTitle,
                Publisher = v5Book.Publisher,
                PublicationYear = v5Book.PublicationYear,
                LanguageCode = v5Book.LanguageCode,
                LanguageName = v5Book.LanguageName,
                ReleaseDate = v5Book.PublicationDate.ToUtcDateTime(),
                PageCount = v5Book.PageCount ?? 0,
                DurationMinutes = v5Book.DurationSeconds.HasValue ? v5Book.DurationSeconds.Value / 60 : null,

                // Ratings
                Ratings = new Ratings
                {
                    Votes = v5Book.RatingCount,
                    Value = v5Book.RatingAverage
                },

                // Monitoring is seeded by AuthorLibraryService from the explicit
                // add/import action. Keep provider conversion neutral.
                Monitored = false, // Legacy field for database compatibility
                AudiobookMonitored = false, // Will be set based on media type
                EbookMonitored = false, // Will be set based on media type
                AnyEditionOk = true,
                MediaType = mediaType ?? BookMediaType.Audiobook,

                // Relationships
                Author = author,
                AuthorId = author?.Id ?? 0,

                // New metadata fields
                ProviderUrls = v5Book.ProviderUrls?.ValidateProviderUrls() ?? new ProviderUrlMap(),
                LastUpdated = DateTime.UtcNow,  // Books don't have LastUpdated from server

                // Omnibus/Collection flag
                IsOmnibus = v5Book.IsOmnibus
            };

            // Add genres with validation
            book.Genres = v5Book.Genres?.ValidateGenres(book.Title) ?? new List<string>();

            // Add external links
            if (v5Book.Links != null && v5Book.Links.Any())
            {
                book.Links = v5Book.Links.Select(kvp => new Links
                {
                    Url = kvp.Value,
                    Name = kvp.Key
                }).ToList();
            }

            // Add cover image at book level (ignore invalid/relative URLs)
            if (v5Book.CoverUrl.IsValidHttpUrl())
            {
                book.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover
                    {
                        Url = v5Book.CoverUrl,
                        CoverType = MediaCoverTypes.Cover
                    }
                };
            }

            // SeriesLinks are properly handled by the dual-instance creation logic above
            // Don't override them here as they're already set with correct LazyLoaded series references
            if (book.SeriesLinks == null)
            {
                book.SeriesLinks = new List<SeriesBookLink>();
            }

            // Store series info at book level for denormalization
            if (v5Book.SeriesId.HasValue || !string.IsNullOrWhiteSpace(v5Book.SeriesName))
            {
                book.SeriesName = v5Book.SeriesName;
                book.SeriesPosition = v5Book.SeriesPosition;
                book.SeriesId = v5Book.SeriesId.HasValue ? (int?)v5Book.SeriesId.Value : null;
            }

            // Create editions. Keep the full remote candidate set here; metadata-profile
            // filtering and media-type retention are applied by the shared add/refresh
            // pipeline after the author/book context and profile are known.
            if (v5Book.Editions != null && v5Book.Editions.Any(e => e != null))
            {
                var editionsToConvert = v5Book.Editions.Where(e => e != null).ToList();
                book.Editions = editionsToConvert.Select(e => ConvertV5EditionToDomain(e, mediaType)).ToList();
                _logger.Trace("[EDITION-CONVERT] Converted {0} editions for book '{1}' (MediaType: {2})",
                    book.Editions.Count, book.Title, mediaType);

                // NOTE: Do not populate Book.Narrator from edition metadata.
                // Book-level narrator is reserved for user-pinned or otherwise confirmed narrator selection.
            }
            else
            {
                book.Editions = new List<Edition>();
            }

            // If the V5 book doesn't have a book-level publication date, infer it from editions.
            // Prefer matching the instance media type (audiobook/ebook) to avoid mixing physical dates into audiobook instances.
            if (!book.ReleaseDate.HasValue)
            {
                var targetReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 :
                                            mediaType == BookMediaType.Ebook ? 3 :
                                            (int?)null;

                var editionCandidates = v5Book.Editions ?? new List<V5.V5Edition>();
                if (targetReadingFormatId.HasValue)
                {
                    var matchingFormat = editionCandidates.Where(e => e != null && e.ReadingFormatId == targetReadingFormatId.Value).ToList();
                    if (matchingFormat.Any())
                    {
                        editionCandidates = matchingFormat;
                    }
                }

                var inferredDates = editionCandidates
                    .Select(e => e?.PublicationDate.ToUtcDateTime())
                    .Where(d => d.HasValue)
                    .Select(d => d.Value)
                    .ToList();

                if (inferredDates.Any())
                {
                    book.ReleaseDate = inferredDates.Min();
                    _logger.Trace("[BOOK-DATE] Inferred ReleaseDate for '{0}' as {1:yyyy-MM-dd} from editions (mediaType={2})",
                        book.Title, book.ReleaseDate.Value, mediaType?.ToString() ?? "null");
                }
                else if (v5Book.PublicationYear.HasValue)
                {
                    book.ReleaseDate = new DateTime(v5Book.PublicationYear.Value, 1, 1);
                }
            }

            return book;
            }

        private void SelectBestEditionAsMonitored(Book book)
        {
            if (book.Editions == null || !book.Editions.Any())
            {
                return;
            }

            foreach (var edition in book.Editions)
            {
                edition.Monitored = false;
            }

            // Align import-time monitoring with EditionSelector:
            // native format first, then most-rated. Language and excluded-term
            // filtering happen later once the metadata profile is available.
            var bestEdition = EditionSelector.SelectByNativeFormatThenRatings(book.Editions, book.MediaType);

            if (bestEdition != null)
            {
                bestEdition.Monitored = true;
                _logger.Trace("[EDITION-SELECT] Selected edition '{0}' as monitored for book '{1}' (votes: {2}, rating: {3}, format: {4})",
                    bestEdition.Title ?? "Unknown",
                    book.Title,
                    bestEdition.Ratings?.Votes ?? 0,
                    bestEdition.Ratings?.Value ?? 0m,
                    bestEdition.ReadingFormatId);
            }
        }

        private Edition ConvertV5EditionToDomain(V5.V5Edition v5Edition, BookMediaType? mediaType = null)
        {
            if (string.IsNullOrWhiteSpace(v5Edition?.Id))
            {
                throw new BookInfoException("V5 edition is missing its provider-owned id");
            }

            // The server's edition.id is the identity. Provider aliases such as
            // asins[] are matching evidence, never a replacement identity.
            var baseEditionId = v5Edition.Id.Trim();
            var foreignEditionId = mediaType switch
            {
                BookMediaType.Audiobook => $"{baseEditionId}-audiobook",
                BookMediaType.Ebook => $"{baseEditionId}-ebook",
                _ => $"{baseEditionId}-default"
            };

            // Generate TitleSlug with media type suffix for uniqueness
            var baseSlug = (v5Edition.Title ?? "edition").ToLower()
                .Replace(" ", "-")
                .Replace("'", "")
                .Replace("\"", "")
                .Replace(":", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("[", "")
                .Replace("]", "")
                .Replace(",", "")
                .Replace(".", "")
                .Replace("!", "")
                .Replace("?", "");

            var titleSlug = mediaType switch
            {
                BookMediaType.Audiobook => $"{baseSlug}-audiobook",
                BookMediaType.Ebook => $"{baseSlug}-ebook",
                _ => $"{baseSlug}-edition"
            };

            _logger.Trace("[EDITION-FIX] Generated ForeignEditionId: {0}, TitleSlug: {1} for media type: {2}",
                foreignEditionId, titleSlug, mediaType?.ToString() ?? "null");

            var narratorCredits = BuildNarratorCredits(v5Edition);
            var narratorNames = narratorCredits.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var chapters = NormalizeV5AuthorEditionChapters(v5Edition.Chapters);
            var chapterCount = v5Edition.ChapterCount ?? (chapters.Count > 0 ? chapters.Count : (int?)null);
            var hasChapters = v5Edition.HasChapters || chapterCount.GetValueOrDefault() > 0 || chapters.Count > 0;

            var edition = new Edition
            {
                // Core identity - FIXED: Now unique per media type
                ForeignEditionId = foreignEditionId,
                TitleSlug = titleSlug,
                Title = !string.IsNullOrWhiteSpace(v5Edition.Title) ? v5Edition.Title : "Unknown Edition",
                Subtitle = v5Edition.Subtitle,
                Language = (string.IsNullOrWhiteSpace(v5Edition.Language) ? v5Edition.LanguageCode : v5Edition.Language)?.CanonicalizeLanguage(),
                Overview = v5Edition.Description ?? string.Empty,

                // ISBNs and identifiers
                Isbn13 = v5Edition.Isbn13,
                Isbn10 = v5Edition.Isbn10,

                // Build normalized Asins list, then set Asin = first element (invariant: Asin ⊆ Asins)
                Asins = BuildNormalizedAsins(v5Edition.Asins, v5Edition.Asin),
                Asin = BuildNormalizedAsins(v5Edition.Asins, v5Edition.Asin).FirstOrDefault(),

                // New provider IDs
                AudibleASIN = v5Edition.Asin, // Same as ASIN for audiobooks
                GoogleBooksEditionId = v5Edition.GoogleBooksEditionId,
                GoodreadsEditionId = long.TryParse(v5Edition.GoodreadsEditionId, out var grId) ? grId : (long?)null,
                HardcoverEditionId = ResolveHardcoverEditionId(v5Edition),
                OpenLibraryEditionId = v5Edition.OpenLibraryEditionId,

                // Format and type
                ReadingFormatId = v5Edition.ReadingFormatId,
                Format = v5Edition.Format,
                EditionFormat = v5Edition.EditionFormat, // Store detailed format info
                EditionInfo = v5Edition.EditionInfo,
                IsEbook = v5Edition.IsEbook,
                Disambiguation = v5Edition.EditionInfo,

                // Publishing info
                Publisher = v5Edition.Publisher,
                PageCount = v5Edition.PageCount ?? 0,
                ReleaseDate = v5Edition.PublicationDate.ToUtcDateTime(),

                // Audiobook specific
                DurationSeconds = v5Edition.DurationSeconds,
                ChapterCount = chapterCount,
                HasChapters = hasChapters,
                Chapters = chapters,
                AudioProductionType = v5Edition.AudioProductionType,

                // Narrator info
                Narrator = narratorNames.FirstOrDefault() ?? string.Empty,
                NarratorNames = narratorNames,
                NarratorCredits = narratorCredits,

                // Monitoring - default to false during import; SetMonitored() is called after insertion
                // to select the appropriate edition to monitor (see BookService.cs:138)
                Monitored = false,

                // Ratings
                Ratings = new Ratings
                {
                    Votes = v5Edition.RatingCount,
                    Value = v5Edition.RatingAverage
                },
                ReviewCount = v5Edition.ReviewCount,

                // New metadata fields
                ProviderUrls = v5Edition.ProviderUrls?.ValidateProviderUrls() ?? new ProviderUrlMap(),
                LastUpdated = DateTime.UtcNow  // Editions don't have LastUpdated from server
            };

            // Keep backward-compatible Links in sync with ProviderUrls for UI display.
            edition.Links = edition.ProviderUrls?
                .Where(kvp => kvp.Key != "_metadata")
                .Select(kvp => new Links { Name = kvp.Key, Url = kvp.Value ?? string.Empty })
                .ToList() ?? new List<Links>();

            // Add cover image (ignore invalid/relative URLs)
            if (v5Edition.CoverUrl.IsValidHttpUrl())
            {
                edition.Images.Add(new MediaCover.MediaCover
                {
                    Url = v5Edition.CoverUrl,
                    CoverType = MediaCoverTypes.Cover
                });
            }

            // EDITION-FIX: Move per-edition success log to Trace to reduce overhead during large imports
            _logger.Trace("[EDITION-FIX] Successfully created edition: ForeignEditionId='{0}', TitleSlug='{1}', Title='{2}', MediaType={3}, ReadingFormatId={4}",
                edition.ForeignEditionId, edition.TitleSlug, edition.Title, mediaType?.ToString() ?? "null", edition.ReadingFormatId);

            return edition;
        }

        private static List<NarratorCredit> BuildNarratorCredits(V5.V5Edition v5Edition)
        {
            var credits = new List<NarratorCredit>();

            if (v5Edition?.Narrators != null && v5Edition.Narrators.Any(c => !string.IsNullOrWhiteSpace(c?.Name)))
            {
                for (var i = 0; i < v5Edition.Narrators.Count; i++)
                {
                    var credit = v5Edition.Narrators[i];
                    if (credit?.Name == null || string.IsNullOrWhiteSpace(credit.Name))
                    {
                        continue;
                    }

                    var order = credit.Order ?? i;
                    credits.Add(new NarratorCredit
                    {
                        Name = credit.Name.Trim(),
                        GoodreadsNarratorId = ProviderIdHelper.Normalize(credit.GoodreadsNarratorId, "gr"),
                        HardcoverNarratorId = ProviderIdHelper.Normalize(credit.HardcoverNarratorId, "hc"),
                        Order = order,
                        IsPrimary = credit.IsPrimary ?? order == 0,
                        Role = string.IsNullOrWhiteSpace(credit.Role) ? "Narrator" : credit.Role
                    });
                }

                credits = credits
                    .OrderBy(c => c.Order)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (credits.Any() && !credits.Any(c => c.IsPrimary))
                {
                    credits[0].IsPrimary = true;
                }

                return credits;
            }

            if (v5Edition?.NarratorNames != null && v5Edition.NarratorNames.Any(n => !string.IsNullOrWhiteSpace(n)))
            {
                var order = 0;
                foreach (var name in v5Edition.NarratorNames)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    credits.Add(new NarratorCredit
                    {
                        Name = name.Trim(),
                        Order = order,
                        IsPrimary = order == 0,
                        Role = "Narrator"
                    });
                    order++;
                }
            }

            return credits;
        }

        private static string ResolveHardcoverEditionId(V5.V5Edition v5Edition)
        {
            if (v5Edition == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(v5Edition.HardcoverEditionId))
            {
                return v5Edition.HardcoverEditionId.Trim();
            }

            var id = v5Edition.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            id = id.Trim();

            var firstColon = id.IndexOf(':');
            if (firstColon <= 0 || firstColon >= id.Length - 1)
            {
                return null;
            }

            var prefix = id.Substring(0, firstColon).ToLowerInvariant();
            if (prefix != "hc" && prefix != "work" && prefix != "hc-ed")
            {
                return null;
            }

            var lastColon = id.LastIndexOf(':');
            if (lastColon < 0 || lastColon >= id.Length - 1)
            {
                return null;
            }

            var raw = id.Substring(lastColon + 1);
            if (raw.Length == 0)
            {
                return null;
            }

            foreach (var ch in raw)
            {
                if (!char.IsDigit(ch))
                {
                    return null;
                }
            }

            return raw;
        }

        /// <summary>
        /// Builds a normalized, deduplicated list of ASINs from API data.
        /// All ASINs are uppercase and trimmed for consistent matching.
        /// </summary>
        private static List<string> BuildNormalizedAsins(List<string> asins, string fallbackAsin)
        {
            var result = new List<string>();

            // First try the Asins array from API
            if (asins != null && asins.Count > 0)
            {
                result = asins
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim().ToUpperInvariant())
                    .Distinct()
                    .ToList();
            }

            // Fallback: if Asins empty but single Asin exists, use it
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(fallbackAsin))
            {
                result.Add(fallbackAsin.Trim().ToUpperInvariant());
            }

            return result;
        }

        private Series ConvertV5SeriesToDomain(V5.V5Series v5Series)
        {
            _logger.Trace("[SERIES-DEBUG] Converting V5 series to domain: ID={0}, Name={1}",
                v5Series.Id, v5Series.Name);

            var series = new Series
            {
                Title = v5Series.Name,
                Description = v5Series.Description ?? string.Empty,
                Numbered = v5Series.Numbered,
                SeriesType = v5Series.SeriesType ?? "main",
                WorkCount = v5Series.TotalBooks,
                PrimaryWorkCount = v5Series.PrimaryBooks,
                TotalBooks = v5Series.TotalBooks,
                PrimaryBooks = v5Series.PrimaryBooks,

                // Provider IDs
                GoodreadsSeriesId = ProviderIdHelper.Normalize(v5Series.GoodreadsSeriesId, "gr"),
                HardcoverSeriesId = ProviderIdHelper.Normalize(v5Series.HardcoverSeriesId, "hc"),
                OpenLibrarySeriesId = ProviderIdHelper.Normalize(v5Series.OpenLibrarySeriesId, "ol"),
                AmazonSeriesAsin = v5Series.AmazonSeriesAsin.IsNullOrWhiteSpace() ? null : ProviderIdHelper.Normalize(v5Series.AmazonSeriesAsin, "az"),

                ProviderUrls = v5Series.ProviderUrls?.ValidateProviderUrls() ?? new ProviderUrlMap(),
                Links = v5Series.Links ?? new Dictionary<string, string>()
            };

            // Parse and set provider ID from V5 API ID
            var (provider, id) = ParseProviderId(v5Series.Id);
            SetSeriesProviderId(series, provider, id);

            // Process books in series if provided - create SeriesBookLink objects for database persistence
            if (v5Series.Books != null && v5Series.Books.Any())
            {
                _logger.Trace("[SERIES-DEBUG] Series '{0}' has {1} books from API, but LinkItems will be created after database save",
                    series.Title, v5Series.Books.Count);

                // Don't populate LinkItems here - they are created after all entities
                // are saved, using proper database IDs
                series.LinkItems = new List<SeriesBookLink>();

                // Store series book metadata with provider IDs for handshaking ONLY
                // BookId contains provider IDs (e.g., "hc:123456") - used STRICTLY for matching during refresh
                // NEVER use these IDs for local database operations - all relationships use database IDs
                series.SeriesBooks = v5Series.Books.Select(b => new SeriesBook
                {
                    BookId = b.BookId, // Provider ID for API handshaking ONLY (e.g., "hc:123456", "gr:789012")
                    Title = b.Title,
                    Position = b.Position,
                    CoverUrl = b.CoverUrl,
                    IsPrimary = b.IsPrimary
                }).ToList();
            }

            return series;
        }

        public HashSet<string> GetChangedBooks(DateTime startTime)
        {
            return _cache.Get("ChangedBooks", () => GetChangedBooksUncached(startTime), TimeSpan.FromMinutes(30));
        }

        private HashSet<string> GetChangedBooksUncached(DateTime startTime)
        {
            return null;
        }

        public Tuple<string, Book, List<Author>> GetBookInfo(string foreignBookId, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null)
        {
            var normalizedForeignBookId = NormalizeGoodreadsProviderId(foreignBookId);

            try
            {
                _logger.Debug("Getting book info for foreignBookId: {0}", normalizedForeignBookId);

                return PollBook(normalizedForeignBookId, mediaType, authorHintProviderId);
            }
            catch (WebException e)
            {
                _logger.Warn(e, "Request failure getting book info for {0}", normalizedForeignBookId);
                throw new BookNotFoundException(normalizedForeignBookId);
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "Request failure getting book info for {0}", normalizedForeignBookId);
                throw new BookNotFoundException(normalizedForeignBookId);
            }
            catch (BadRequestException e)
            {
                _logger.Debug(e, "Bad request getting book info for {0}", normalizedForeignBookId);
                throw new BookNotFoundException(normalizedForeignBookId);
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Unexpected error getting book info for {0}", normalizedForeignBookId);
                throw new BookNotFoundException(normalizedForeignBookId);
            }
        }

        public Tuple<string, Book, List<Author>> GetWorkInfo(string foreignWorkId, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null)
        {
            var normalizedForeignWorkId = NormalizeGoodreadsProviderId(foreignWorkId);

            try
            {
                _logger.Debug("Getting work info for foreignWorkId: {0}", normalizedForeignWorkId);
                return PollBook(normalizedForeignWorkId, mediaType, authorHintProviderId);
            }
            catch (WebException e)
            {
                _logger.Warn(e, "Request failure getting work info for {0}", normalizedForeignWorkId);
                throw new BookNotFoundException(normalizedForeignWorkId);
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "Request failure getting work info for {0}", normalizedForeignWorkId);
                throw new BookNotFoundException(normalizedForeignWorkId);
            }
            catch (BadRequestException e)
            {
                _logger.Debug(e, "Bad request getting work info for {0}", normalizedForeignWorkId);
                throw new BookNotFoundException(normalizedForeignWorkId);
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Unexpected error getting work info for {0}", normalizedForeignWorkId);
                throw new BookNotFoundException(normalizedForeignWorkId);
            }
        }

        public Tuple<string, Book, List<Author>> GetEditionInfo(string foreignEditionId, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            var normalizedForeignEditionId = NormalizeGoodreadsProviderId(foreignEditionId);

            try
            {
                _logger.Debug("Getting edition info for foreignEditionId: {0}", normalizedForeignEditionId);
                return PollEditionBook(normalizedForeignEditionId, mediaType);
            }
            catch (WebException e)
            {
                _logger.Warn(e, "Request failure getting edition info for {0}", normalizedForeignEditionId);
                throw new BookNotFoundException(normalizedForeignEditionId);
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "Request failure getting edition info for {0}", normalizedForeignEditionId);
                throw new BookNotFoundException(normalizedForeignEditionId);
            }
            catch (BadRequestException e)
            {
                _logger.Debug(e, "Bad request getting edition info for {0}", normalizedForeignEditionId);
                throw new BookNotFoundException(normalizedForeignEditionId);
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Unexpected error getting edition info for {0}", normalizedForeignEditionId);
                throw new BookNotFoundException(normalizedForeignEditionId);
            }
        }

        private static string NormalizeGoodreadsProviderId(string foreignBookId)
        {
            if (foreignBookId.IsNullOrWhiteSpace())
            {
                return foreignBookId;
            }

            return ProviderIdHelper.Normalize(foreignBookId.Trim(), defaultPrefix: null);
        }

        public List<object> SearchForNewEntity(string title)
        {
            return SearchForNewEntity(title, null);
        }

        public List<object> SearchForNewEntity(string title, string provider)
        {
            _logger.Debug($"[BookInfoProxy] SearchForNewEntity called with title: '{title}', provider: '{provider ?? "null"}'");

            // Route the explicit Audible search provider to the Audible catalog
            if (provider?.Equals("audible", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    _logger.Debug($"[BookInfoProxy] Routing to Audible catalog for: '{title}'");

                    // Auto-detect ASIN format: starts with B followed by 9 alphanumeric characters (B + 9 = 10 total)
                    // Examples: B00JCDK5ME, B0B1X2Y3Z4
                    var asinPattern = new Regex(@"^B[A-Z0-9]{9}$", RegexOptions.IgnoreCase);

                    if (asinPattern.IsMatch(title.Trim()))
                    {
                        var asin = title.Trim().ToUpperInvariant();
                        _logger.Debug($"[BookInfoProxy] Detected ASIN format: {asin}, performing direct lookup");

                        var bookInfo = _audibleCatalogProxy.GetBookInfo(asin, useCache: true);

                        if (bookInfo != null)
                        {
                            var book = MapAudibleCatalogBookToDomain(bookInfo);
                            if (book != null)
                            {
                                return BuildAudibleCatalogSearchResults(new List<Book> { book });
                            }
                        }

                        _logger.Warn($"[BookInfoProxy] ASIN {asin} not found in Audible catalog");
                        return new List<object>();
                    }

                    // Regular text search
                    _logger.Debug($"[BookInfoProxy] Performing text search in Audible catalog");
                    var audibleResults = _audibleCatalogProxy.SearchBooks(title, useCache: true);

                    if (audibleResults != null && audibleResults.Any())
                    {
                        var audibleBooks = audibleResults
                            .Select(MapAudibleCatalogBookToDomain)
                            .Where(b => b != null)
                            .ToList();

                        return BuildAudibleCatalogSearchResults(audibleBooks);
                    }

                    return new List<object>();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"[BookInfoProxy] Audible catalog search failed");
                    return new List<object>();
                }
            }

            // Search providers should hit their own upstream APIs only.
            // No fallback to the metadata server search endpoint (/api/v5/search).
            if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "hardcover", StringComparison.OrdinalIgnoreCase))
            {
                if (_hardcoverSearchClient == null)
                {
                    throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable, "Hardcover search is unavailable");
                }

                if (!_configService.HardcoverEnabled || string.IsNullOrWhiteSpace(_configService.HardcoverApiToken))
                {
                    throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable, "Hardcover search failed");
                }

                try
                {
                    _logger.Debug($"[BookInfoProxy] Searching Hardcover for: '{title}'");

                    var cacheKey = BuildHardcoverSearchCacheKey(title, _configService.HardcoverApiToken);
                    var rawHc = _hardcoverSearchCache.Find(cacheKey);

                    if (rawHc?.Any() != true)
                    {
                        if (rawHc != null)
                        {
                            _hardcoverSearchCache.Remove(cacheKey);
                        }

                        rawHc = _hardcoverSearchClient.Search(title);

                        if (rawHc?.Any() == true)
                        {
                            _hardcoverSearchCache.Set(cacheKey, rawHc, HardcoverSearchCacheDuration);
                        }
                    }
                    else
                    {
                        _logger.Debug($"[BookInfoProxy] Reusing cached Hardcover search for: '{title}'");
                    }

                    if (rawHc == null)
                    {
                        _logger.Warn("[BookInfoProxy] Hardcover search failed (null response)");
                        throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable, "Hardcover search failed");
                    }

                    // Map Hardcover DTOs to domain objects and filter
                    var mapped = MapHardcoverResultsToDomain(rawHc);
                    _logger.Debug($"[BookInfoProxy] Hardcover mapped results: {mapped.Count}");

                    var filtered = FilterHardcoverSearchResultsPreservingOrder(mapped, title);
                    _logger.Debug($"[BookInfoProxy] Hardcover filtered results: {filtered.Count}");
                    return filtered;
                }
                catch (NzbDroneClientException ex)
                {
                    // Preserve the provider's own error verbatim for the UI.
                    _logger.Warn("[BookInfoProxy] Hardcover search failed: {0}", ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[BookInfoProxy] Hardcover search threw exception");
                    throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable, "Hardcover search failed", ex);
                }
            }

            if (!string.Equals(provider, "goodreads", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"[BookInfoProxy] Unknown search provider '{provider ?? "null"}', returning empty results");
                return new List<object>();
            }

            _logger.Debug($"[BookInfoProxy] Using Goodreads search (provider was: '{provider ?? "null"}')");

            // Default to Goodreads behavior
            _logger.Debug($"[BookInfoProxy] Executing Goodreads search for: {title}");
            var books = SearchForNewBook(title, null, false);
            _logger.Debug($"[BookInfoProxy] Goodreads search returned {books?.Count ?? 0} books");

            var result = new List<object>();
            if (books?.Any() == true)
            {
                var authors = books
                    .Select(b => b?.Author)
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(GetAuthorMetadataKey(a)))
                    .DistinctBy(a => GetAuthorMetadataKey(a))
                    .ToList();

                foreach (var author in authors)
                {
                    EnsureGoodreadsAuthorLink(author);

                    var authorId = GetAuthorMetadataKey(author);
                    var enriched = TryGetAuthorInfoFromV5ForSearch(authorId);
                    if (enriched != null)
                    {
                        author.UseMetadataFrom(enriched);
                    }

                    EnsureGoodreadsAuthorLink(author);
                    result.Add(author);
                }

                result.AddRange(books.Cast<object>());
            }

            _logger.Debug($"[BookInfoProxy] Final result count: {result.Count} (unique authors + books)");
            return result;
        }

        private static string BuildHardcoverSearchCacheKey(string title, string apiToken)
        {
            var normalizedTerm = (title ?? string.Empty).Trim().ToLowerInvariant();
            var tokenBytes = Encoding.UTF8.GetBytes(apiToken ?? string.Empty);
            var tokenFingerprint = Convert.ToHexString(SHA256.HashData(tokenBytes));

            return $"hardcover:{tokenFingerprint}:{normalizedTerm}";
        }

        public List<Author> SearchForNewAuthor(string title)
        {
            var books = SearchForNewBook(title, null);

            return books
                .Select(x => x.Author)
                .DistinctBy(x => GetAuthorMetadataKey(x))
                .ToList();
        }

            public List<Book> SearchForNewBook(string title, string author, bool getAllEditions = true)
            {
                var q = title.ToLower().Trim();
                if (author != null)
                {
                    q += " " + author;
                }

                try
                {
                    // Canonical provider-prefixed lookup (hc:/gr:/ol:/gb:/az:) should route to the V5 work endpoint,
                    // not to Goodreads text search.
                    var trimmed = title?.Trim();
                    if (!trimmed.IsNullOrWhiteSpace())
                    {
                        var firstColon = trimmed.IndexOf(':');
                        if (firstColon > 0 &&
                            firstColon < trimmed.Length - 1 &&
                            trimmed.IndexOf(':', firstColon + 1) == -1)
                        {
                            var canonicalPrefix = trimmed.Substring(0, firstColon).Trim().ToLowerInvariant();
                            if (ProviderIdHelper.IsCanonicalPrefix(canonicalPrefix))
                            {
                                try
                                {
                                    var normalized = ProviderIdHelper.Normalize(trimmed, defaultPrefix: null);
                                    return SearchByV5WorkId(normalized);
                                }
                                catch (BookNotFoundException)
                                {
                                    return new List<Book>();
                                }
                            }
                        }
                    }

                    var lowerTitle = title.ToLowerInvariant();

                    var split = lowerTitle.Split(':');
                    var prefix = split[0];

                if (split.Length == 2 && new[] { "author", "work", "edition", "isbn", "asin" }.Contains(prefix))
                {
                    var slug = split[1].Trim();

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace))
                    {
                        return new List<Book>();
                    }

                    if (prefix == "author" || prefix == "work" || prefix == "edition")
                    {
                        var isValid = int.TryParse(slug, out var searchId);
                        if (!isValid)
                        {
                            return new List<Book>();
                        }

                        if (prefix == "author")
                        {
                            return SearchByGoodreadsAuthorId(searchId);
                        }

                        if (prefix == "work")
                        {
                            return SearchByGoodreadsWorkId(searchId);
                        }

                        if (prefix == "edition")
                        {
                            return SearchByGoodreadsBookId(searchId, getAllEditions);
                        }
                    }

                    // to handle isbn / asin
                    q = slug;
                }

                return Search(q, getAllEditions);
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with Goodreads.", ex, title);
            }
            catch (Exception ex) when (ex is not BookInfoException)
            {
                _logger.Warn(ex, ex.Message);
                throw new GoodreadsException("Search for '{0}' failed. Invalid response received from Goodreads.", ex, title);
            }
        }

        public List<Book> SearchByIsbn(string isbn)
        {
            return Search(isbn, true);
        }

        private List<Book> SearchByV5WorkId(string foreignWorkId)
        {
            var lookup = PollV5WorkLookup(foreignWorkId);

            if (lookup.RedirectAuthor != null)
            {
                var author = lookup.RedirectAuthor;
                return author?.Books?.Where(b => GetBookKey(b) == foreignWorkId).ToList() ?? new List<Book>();
            }

            return MapV5WorkResponseToBookInstances(lookup.WorkResponse, foreignWorkId);
        }

        public List<Book> SearchByAsin(string asin)
        {
            return Search(asin, true);
        }

        private List<Book> Search(string query, bool getAllEditions)
        {
            List<SearchJsonResource> result;
            try
            {
                result = _goodreadsSearchProxy.Search(query);
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Error searching for {0}", query);
                return new List<Book>();
            }

            var books = new List<Book>();

            // IMPORTANT: According to the comprehensive flow MD file, we should NOT fetch full book data during search
            // The search should return basic Book objects created from autocomplete data
            // Full author data (with ALL books) is only fetched in Step 3 when discovering a new author
            _logger.Debug("Creating Book objects from {0} search results without fetching additional data", result.Count);

            foreach (var searchResult in result)
            {
                var book = CreateBookFromSearchResult(searchResult);
                if (book != null)
                {
                    books.Add(book);
                }
            }

            return books;
        }

        private List<Book> SearchByGoodreadsAuthorId(int id)
        {
            try
            {
                // The term that routed here is a Goodreads author id, so carry the provider with it.
                // GetAuthorInfo used to assume Hardcover for a bare number, resolving an unrelated
                // author under the same digits; it now rejects one outright.
                var authorId = EnsureCanonicalProviderId(id.ToString(), "gr");
                var result = GetAuthorInfo(authorId);
                var books = result.Books;
                var authors = new Dictionary<string, Author> { { authorId, result } };

                foreach (var book in books)
                {
                    AddDbIds(authorId, book, authors);
                }

                return books;
            }
            catch (AuthorTerminalException)
            {
                throw;
            }

            catch (AuthorNotFoundException)
            {
                return new List<Book>();
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Error searching by author id");
                return new List<Book>();
            }
        }

            public List<Book> SearchByGoodreadsWorkId(int id)
            {
                try
                {
                    var tuple = GetBookInfo(ProviderIdHelper.WithPrefix("gr", id.ToString()));
                    // Create dictionary with V5 ID as key for backward compatibility
                    var authorDict = new Dictionary<string, Author>();
                    foreach (var author in tuple.Item3)
                    {
                        // Add entries for all possible ID formats
                    if (!string.IsNullOrEmpty(author.HardcoverAuthorId))
                    {
                        authorDict[ProviderIdHelper.Normalize(author.HardcoverAuthorId, "hc")] = author;
                    }
                    if (!string.IsNullOrEmpty(author.GoodreadsAuthorId))
                    {
                        authorDict[ProviderIdHelper.Normalize(author.GoodreadsAuthorId, "gr")] = author;
                    }
                    // Also add the raw V5 ID if available (for backward compatibility)
                    var v5Id = tuple.Item1; // The author ID from the tuple
                    if (!authorDict.ContainsKey(v5Id))
                    {
                        authorDict[v5Id] = author;
                    }
                }
                AddDbIds(tuple.Item1, tuple.Item2, authorDict);
                return new List<Book> { tuple.Item2 };
            }
            catch (BookNotFoundException)
            {
                return new List<Book>();
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Error searching by work id");
                return new List<Book>();
            }
        }

        public List<Book> SearchByGoodreadsBookId(int id, bool getAllEditions)
        {
            try
            {
                var book = GetEditionInfo(id, getAllEditions);

                return new List<Book> { book };
            }
            catch (AuthorTerminalException)
            {
                throw;
            }

            catch (AuthorNotFoundException)
            {
                return new List<Book>();
            }
            catch (BookNotFoundException)
            {
                return new List<Book>();
            }
            catch (EditionNotFoundException)
            {
                return new List<Book>();
            }
            catch (BookInfoException e)
            {
                _logger.Warn(e, "Error searching by book id");
                return new List<Book>();
            }
        }

        private Book GetEditionInfo(int id, bool getAllEditions)
        {
            HttpRequest httpRequest;
            HttpResponse httpResponse;

            while (true)
            {
                httpRequest = _requestBuilder.GetRequestBuilder().Create()
                    .SetSegment("route", $"book/{id}")
                    .Build();

                httpRequest.SuppressHttpError = true;

                // we expect a redirect
                httpResponse = ExecuteV5Request(httpRequest, _httpClient.Get, $"book redirect {id}");

                if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    WaitUntilRetry(httpResponse);
                }
                else
                {
                    break;
                }
            }

            if (httpResponse.StatusCode == HttpStatusCode.NotFound)
            {
                throw new EditionNotFoundException(id.ToString());
            }

            if (!httpResponse.HasHttpRedirect)
            {
                throw new BookInfoException($"Unexpected response from {httpRequest.Url}");
            }

            var location = httpResponse.Headers.GetSingleValue("Location");
            var split = location.Split('/').Reverse().ToList();
            var newId = split[0];
            var type = split[1];

            var requestedEditionId = EnsureCanonicalProviderId(id.ToString(), "gr");
            Book book;
            List<Author> authors;

            if (type == "author")
            {
                var author = PollAuthor(newId);

                book = author.Books.Where(b => b.Editions.Any(e => BookEditionIdentity.EditionMatchesProviderId(e, requestedEditionId))).FirstOrDefault();
                authors = new List<Author> { author };
            }
            else if (type == "work")
            {
                var tuple = PollBook(newId, BookMediaType.Audiobook);

                book = tuple.Item2;
                authors = tuple.Item3;
            }
            else
            {
                throw new NotImplementedException($"Unexpected response from {httpResponse.Request.Url}");
            }

            if (book == null || book.Editions.All(e => !BookEditionIdentity.EditionMatchesProviderId(e, requestedEditionId)))
            {
                throw new EditionNotFoundException(id.ToString());
            }

            if (!getAllEditions)
            {
                var trimmed = new Book();
                trimmed.UseMetadataFrom(book);
                trimmed.SeriesLinks = book.SeriesLinks;
                var edition = book.Editions.SingleOrDefault(e => BookEditionIdentity.EditionMatchesProviderId(e, requestedEditionId));
                if (edition != null)
                {
                    edition.Monitored = true;
                }

                trimmed.Editions = new List<Edition> { edition };
                book = trimmed;
            }

            var authorDict = authors.ToDictionary(x => GetAuthorMetadataKey(x));
            var authorKey = GetAuthorMetadataKey(book.Author);
            AddDbIds(authorKey, book, authorDict);

            return book;
        }

        private List<Book> MapSearchResult(List<int> ids)
        {
            HttpResponse<BulkBookResource> httpResponse;

            while (true)
            {
                var httpRequest = _requestBuilder.GetRequestBuilder().Create()
                    .SetSegment("route", "book/bulk")
                    .SetHeader("Content-Type", "application/json")
                    .Build();

                httpRequest.SetContent(ids.ToJson());
                httpRequest.ContentSummary = ids.ToJson(Formatting.None);

                httpRequest.AllowAutoRedirect = true;
                httpRequest.SuppressHttpErrorStatusCodes = new[] { HttpStatusCode.TooManyRequests };

                httpResponse = ExecuteV5Request(httpRequest, _httpClient.Post<BulkBookResource>, "bulk book lookup");

                if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    WaitUntilRetry(httpResponse);
                }
                else
                {
                    break;
                }
            }

            var mapped = MapBulkBook(httpResponse.Resource);

            var idStr = ids.Select(x => x.ToString()).ToList();

            return mapped.OrderBy(b => idStr.IndexOf(b.Editions.First().ForeignEditionId)).ToList();
        }

        private List<Book> MapBulkBook(BulkBookResource resource)
        {
            var books = new List<Book>();

            if (resource == null)
            {
                return books;
            }

            var authors = resource.Authors.Select(MapAuthor).ToDictionary(x => GetAuthorMetadataKey(x), x => x);
            var series = resource.Series.Select(MapSeries).ToList();

            foreach (var work in resource.Works)
            {
                var book = MapBook(work, _logger);
                var authorId = work.Books.OrderByDescending(b => b.AverageRating * b.RatingCount).First().Contributors.First().ForeignId.ToString();

                AddDbIds(authorId, book, authors);

                books.Add(book);
            }

            MapSeriesLinks(series, books, resource.Series);

            return books;
        }

        private void AddDbIds(string authorId, Book book, Dictionary<string, Author> authors)
        {
            // Find book by provider ID
            Book dbBook = null;

            // Try to find by the provider IDs we have
            if (!string.IsNullOrEmpty(book.HardcoverBookId))
            {
                dbBook = _bookService.FindByProviderId("hc", book.HardcoverBookId);
            }

            var goodreadsEditionId = BookEditionIdentity.GetGoodreadsEditionProviderId(book);
            if (dbBook == null && !string.IsNullOrEmpty(goodreadsEditionId))
            {
                dbBook = _bookService.FindByProviderId("gr", goodreadsEditionId);
            }

            if (dbBook == null && !string.IsNullOrEmpty(book.OpenLibraryWorkId))
            {
                dbBook = _bookService.FindByProviderId("ol", book.OpenLibraryWorkId);
            }

            var googleBooksEditionId = BookEditionIdentity.GetGoogleBooksEditionId(book);
            if (dbBook == null && !string.IsNullOrEmpty(googleBooksEditionId))
            {
                dbBook = _bookService.FindByProviderId("gb", googleBooksEditionId);
            }

            if (dbBook != null)
            {
                book.UseDbFieldsFrom(dbBook);

                var editions = _editionService.GetEditionsByBook(dbBook.Id).ToDictionary(x => x.ForeignEditionId);

                // If we have any database editions, exactly one will be monitored.
                // So unmonitor all the found editions and let the UseDbFieldsFrom set
                // the monitored status
                foreach (var edition in book.Editions)
                {
                    edition.Monitored = false;
                    if (editions.TryGetValue(edition.ForeignEditionId, out var dbEdition))
                    {
                        edition.UseDbFieldsFrom(dbEdition);
                    }
                }

                // Don't force a monitored edition here - let existing DB state persist
                // or wait for metadata profile filtering if this is a new book
            }

            // Parse author ID to find by provider
            var (authorProvider, authorProviderIdStr) = ParseProviderId(authorId);
            Author author = null;

            if (authorProvider == "hardcover")
            {
                author = _authorService.FindByProviderId("hc", authorProviderIdStr);
            }
            else if (authorProvider == "goodreads" && long.TryParse(authorProviderIdStr, out var grAuthorId))
            {
                author = _authorService.FindByProviderId("gr", authorProviderIdStr);
            }
            else if (authorProvider == "openlibrary")
            {
                author = _authorService.FindByProviderId("ol", authorProviderIdStr);
            }

            if (author == null)
            {
                if (!authors.TryGetValue(authorId, out var metadata))
                {
                    throw new BookInfoException(string.Format("Expected author metadata for id [{0}] in book data {1}", authorId, book));
                }

                // Copy all properties from the metadata author
                author = new Author
                {
                    CleanName = Parser.Parser.CleanAuthorName(metadata.Name),
                    Name = metadata.Name,
                    TitleSlug = metadata.TitleSlug,
                    Status = metadata.Status,
                    Born = metadata.Born,
                    Died = metadata.Died,
                    Overview = metadata.Overview,
                    Images = metadata.Images,
                    Links = metadata.Links,
                    Genres = metadata.Genres,
                    Ratings = metadata.Ratings,
                    HardcoverAuthorId = metadata.HardcoverAuthorId,
                    GoodreadsAuthorId = metadata.GoodreadsAuthorId,
                    OpenLibraryAuthorId = metadata.OpenLibraryAuthorId,
                    GoogleBooksAuthorId = metadata.GoogleBooksAuthorId,
                    AudnexusAuthorId = metadata.AudnexusAuthorId
                };
            }

            book.Author = author;
            book.AuthorId = author.Id;
        }

        private Author PollAuthor(string foreignAuthorId)
        {
            // Check if already in cache
            var cachedAuthor = _authorCache.Get<Author>(foreignAuthorId);
            if (cachedAuthor != null)
            {
                _logger.Trace("Using cached author data for {0}", foreignAuthorId);
                return cachedAuthor;
            }

            return _authorCache.GetOrAdd(foreignAuthorId,
                () =>
                {
                    _logger.Trace("Fetching fresh author data for {0}", foreignAuthorId);
                    return PollAuthorUncached(foreignAuthorId);
                },
                new LazyCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                    ImmediateAbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                    Size = 1,
                    SlidingExpiration = TimeSpan.FromMinutes(1),

                    // ExpirationMode = ExpirationMode.ImmediateEviction // REMOVED: This was defeating the purpose of caching!
                }.RegisterPostEvictionCallback((key, value, reason, state) => _logger.Debug($"Clearing cache for {key} due to {reason}")));
        }

        private Author PollAuthorUncached(string foreignAuthorId)
        {
            var overallStopwatch = Stopwatch.StartNew();
            AuthorResource resource = null;

            var useCache = true;

            for (var i = 0; i < 3; i++)
            {
                var httpRequest = _requestBuilder.GetRequestBuilder().Create()
                    .SetSegment("route", $"author/{foreignAuthorId}")
                    .Build();

                httpRequest.AllowAutoRedirect = true;
                httpRequest.SuppressHttpError = true;
                httpRequest.RequestTimeout = TimeSpan.FromSeconds(30);

                _logger.Trace("[PERF] Starting author HTTP request for foreignAuthorId: {0}", foreignAuthorId);
                var httpStopwatch = Stopwatch.StartNew();

                HttpResponse httpResponse = null;
                var responseSize = 0;
                try
                {
                    httpResponse = ExecuteV5Request(
                        httpRequest,
                        request => _cachedHttpClient.Get(
                            request,
                            useCache,
                            TimeSpan.FromMinutes(30),
                            candidate =>
                            {
                                var author = JsonConvert.DeserializeObject<AuthorResource>(candidate.Content, SerializerSettings);
                                return author != null && author.ForeignId > 0;
                            }),
                        $"author lookup {foreignAuthorId}");

                    httpStopwatch.Stop();
                    responseSize = httpResponse.Content?.Length ?? 0;
                    var cacheStatus = httpResponse.Headers.GetSingleValue("X-Cache-Status") ?? "unknown";
                    _logger.Trace("[PERF] HTTP request completed in {0}ms, response size: {1} bytes, cache: {2}, useCache: {3}", httpStopwatch.ElapsedMilliseconds, responseSize, cacheStatus, useCache);
                }
                catch (Exception ex) when (ex is WebException || ex is HttpException)
                {
                    httpStopwatch.Stop();
                    _logger.Warn("HTTP request failed after {0}ms: {1}", httpStopwatch.ElapsedMilliseconds, ex.Message);

                    throw;
                }

                if (httpResponse.HasHttpError)
                {
                    if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        WaitUntilRetry(httpResponse);
                        continue;
                    }
                    else if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new AuthorNotFoundException(foreignAuthorId);
                    }
                    else if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
                    {
                        throw new BadRequestException(foreignAuthorId);
                    }
                    else
                    {
                        throw new BookInfoException("Unexpected error fetching author data");
                    }
                }

                _logger.Trace("[PERF] Starting JSON deserialization of {0:F1}MB response", responseSize / 1024.0 / 1024.0);
                var deserializeStopwatch = Stopwatch.StartNew();

                resource = JsonConvert.DeserializeObject<AuthorResource>(httpResponse.Content, SerializerSettings);

                deserializeStopwatch.Stop();
                var worksCount = resource?.Works?.Count ?? 0;
                var seriesCount = resource?.Series?.Count ?? 0;
                var totalBooks = resource?.Works?.Sum(w => w.Books?.Count ?? 0) ?? 0;
                _logger.Trace("[PERF] JSON deserialization completed in {0}ms - Works: {1}, Series: {2}, Total Books: {3}", deserializeStopwatch.ElapsedMilliseconds, worksCount, seriesCount, totalBooks);

                if (resource.Works != null)
                {
                    resource.Works ??= new List<WorkResource>();
                    resource.Series ??= new List<SeriesResource>();
                    break;
                }

                useCache = false;
                Thread.Sleep(2000);
            }

            if (resource?.Works == null)
            {
                throw new BookInfoException($"Failed to get works for {foreignAuthorId}");
            }

            _logger.Trace("[PERF] Starting MapAuthor for {0} with {1} works", resource.Name, resource.Works.Count);
            var mapStopwatch = Stopwatch.StartNew();

            var result = MapAuthor(resource);

            mapStopwatch.Stop();
            overallStopwatch.Stop();
            _logger.Trace("[PERF] MapAuthor completed in {0}ms", mapStopwatch.ElapsedMilliseconds);
            _logger.Trace("[PERF] Total PollAuthorUncached time: {0}ms for author {1}", overallStopwatch.ElapsedMilliseconds, foreignAuthorId);

            return result;
        }

        private sealed class V5WorkLookupResult
        {
            public V5Resource.V5WorkResponse WorkResponse { get; init; }
            public Author RedirectAuthor { get; init; }
        }

        private V5WorkLookupResult PollV5Lookup(string foreignBookId, string route, string authorHintProviderId = null)
        {
            var normalizedAuthorHint = string.Equals(route, "work", StringComparison.OrdinalIgnoreCase)
                ? AuthorIdentity.NormalizeWorkLookupAuthorHint(foreignBookId, authorHintProviderId)
                : null;

            for (var i = 0; i < 3; i++)
            {
                var builder = _requestBuilder.GetRequestBuilder().Create()
                    .SetSegment("route", $"api/v5/{route}/{foreignBookId}");

                if (!string.IsNullOrWhiteSpace(normalizedAuthorHint))
                {
                    builder.AddQueryParam("author", normalizedAuthorHint);
                }

                var httpRequest = builder.Build();

                httpRequest.SuppressHttpError = true;
                httpRequest.RequestTimeout = TimeSpan.FromSeconds(30);

                HttpResponse httpResponse = null;
                try
                {
                    // this may redirect to an author
                    httpResponse = ExecuteV5Request(httpRequest, _httpClient.Get, $"{route} lookup {foreignBookId}");
                }
                catch (Exception ex) when (ex is WebException || ex is HttpException)
                {
                    throw;
                }

                if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    WaitUntilRetry(httpResponse);
                    continue;
                }

                if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new BookNotFoundException(foreignBookId);
                }

                if (httpResponse.StatusCode == HttpStatusCode.Accepted)
                {
                    // Work not ready (typically because the upstream metadata server queued the author/work for import).
                    // Treat as "not found" so callers can retry later without surfacing an exception.
                    throw new BookNotFoundException(foreignBookId);
                }

                if (httpResponse.StatusCode == HttpStatusCode.Conflict &&
                    string.Equals(route, "work", StringComparison.OrdinalIgnoreCase))
                {
                    var declaredReason = httpResponse.Content?.Trim();
                    if (string.IsNullOrWhiteSpace(declaredReason))
                    {
                        declaredReason = $"Work rescue for {foreignBookId} reached a terminal state.";
                    }

                    throw new WorkRescueTerminalException(foreignBookId, declaredReason);
                }

                if (httpResponse.HasHttpRedirect)
                {
                    var location = httpResponse.Headers.GetSingleValue("Location");
                    var split = location.Split('/').Reverse().ToList();
                    var newId = split[0];
                    var type = split[1];

                    if (type == "author")
                    {
                        var author = PollAuthor(newId);
                        return new V5WorkLookupResult { RedirectAuthor = author };
                    }
                    else
                    {
                        throw new NotImplementedException($"Unexpected response from {httpResponse.Request.Url}");
                    }
                }

                    if (httpResponse.HasHttpError)
                    {
                        if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
                        {
                            throw new BadRequestException(foreignBookId);
                        }
                        else
                        {
                            throw new BookInfoException("Unexpected response fetching book data");
                        }
                    }

                // Parse V5 response format only
                V5Resource.V5WorkResponse v5Response;
                try
                {
                    v5Response = JsonConvert.DeserializeObject<V5Resource.V5WorkResponse>(httpResponse.Content, SerializerSettings);
                }
                catch (JsonException ex)
                {
                    throw new BookInfoException(
                        "Invalid JSON returned from metadata server for {0} (status={1}, contentType='{2}', url='{3}')",
                        ex,
                        foreignBookId,
                        (int)httpResponse.StatusCode,
                        httpResponse.Headers?.ContentType ?? string.Empty,
                        httpResponse.Request?.Url?.ToString() ?? string.Empty);
                }

                var hasWork = v5Response?.Work != null;
                var editionsCount = v5Response?.Editions?.Count ?? 0;
                var authorsIsNull = v5Response?.Authors == null;
                var authorsCount = v5Response?.Authors?.Count ?? 0;

                if (hasWork && editionsCount > 0 && authorsCount > 0)
                {
                    _logger.Debug("Successfully parsed V5 work response for {0}", foreignBookId);
                    return new V5WorkLookupResult { WorkResponse = v5Response };
                }

                throw new BookInfoException(
                    "Invalid V5 work response for {0} (status={1}, contentType='{2}', url='{3}', hasWork={4}, editions={5}, authorsIsNull={6}, authors={7})",
                    foreignBookId,
                    (int)httpResponse.StatusCode,
                    httpResponse.Headers?.ContentType ?? string.Empty,
                    httpResponse.Request?.Url?.ToString() ?? string.Empty,
                    hasWork,
                    editionsCount,
                    authorsIsNull,
                    authorsCount);
            }

            // If all retries failed, throw an exception
            throw new BookInfoException($"Failed to get book data for {foreignBookId} after 3 attempts");
        }

        private V5WorkLookupResult PollV5WorkLookup(string foreignBookId, string authorHintProviderId = null)
        {
            return PollV5Lookup(foreignBookId, "work", authorHintProviderId);
        }

        private V5WorkLookupResult PollV5BookLookup(string foreignBookId)
        {
            return PollV5Lookup(foreignBookId, "book");
        }

        private Tuple<string, Book, List<Author>> PollBook(string foreignBookId, BookMediaType mediaType, string authorHintProviderId = null)
        {
            var lookup = PollV5WorkLookup(foreignBookId, authorHintProviderId);
            return MapLookupToBookTuple(lookup, foreignBookId, mediaType);
        }

        private Tuple<string, Book, List<Author>> PollEditionBook(string foreignBookId, BookMediaType mediaType)
        {
            var lookup = PollV5BookLookup(foreignBookId);
            return MapLookupToBookTuple(lookup, foreignBookId, mediaType);
        }

        private Tuple<string, Book, List<Author>> MapLookupToBookTuple(V5WorkLookupResult lookup, string foreignBookId, BookMediaType mediaType)
        {
            if (lookup.RedirectAuthor != null)
            {
                var author = lookup.RedirectAuthor;

                // Find the book by matching provider ID
                var (searchProvider, searchId) = ParseProviderId(foreignBookId);
                var matches = new List<Book>();

                foreach (var searchBook in author.Books)
                {
                    var bookKey = GetBookKey(searchBook);
                    if (bookKey == foreignBookId ||
                        (searchProvider != null && bookKey == $"{searchProvider}:{searchId}"))
                    {
                        matches.Add(searchBook);
                    }
                }

                var authorBook = matches.FirstOrDefault(b => b.MediaType == mediaType) ?? matches.FirstOrDefault();
                if (authorBook == null)
                {
                    throw new BookNotFoundException(foreignBookId);
                }

                var authorMetadata = new List<Author> { author };
                var authorKey = GetAuthorMetadataKey(author);
                return Tuple.Create(authorKey, authorBook, authorMetadata);
            }

            return MapV5WorkResponse(lookup.WorkResponse, foreignBookId, mediaType);
        }

        private void WaitUntilRetry(HttpResponse response)
        {
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(response) ??
                             MetadataServerHealthService.DefaultRateLimitRetryAfter;

            _logger.Info("BookInfo returned 429, backing off for {0}", MetadataServerHealthGate.FormatRetryAfter(retryAfter));

            Thread.Sleep(retryAfter);
        }

        private static string EnhanceGoodreadsImageUrl(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.Contains("gr-assets.com"))
            {
                return imageUrl;
            }

            // Replace small size parameters with larger ones
            // Common patterns: _SX50_, _SY75_, _SX98_, _SY98_, etc.
            var enhancedUrl = imageUrl;

            // Replace width-based sizing
            enhancedUrl = Regex.Replace(enhancedUrl, @"_SX\d{1,3}_", "_SX500_", RegexOptions.IgnoreCase);

            // Replace height-based sizing
            enhancedUrl = Regex.Replace(enhancedUrl, @"_SY\d{1,3}_", "_SY750_", RegexOptions.IgnoreCase);

            // Also handle the pattern without underscore prefix (like .SX50.)
            enhancedUrl = Regex.Replace(enhancedUrl, @"\.SX\d{1,3}\.", ".SX500.", RegexOptions.IgnoreCase);
            enhancedUrl = Regex.Replace(enhancedUrl, @"\.SY\d{1,3}\.", ".SY750.", RegexOptions.IgnoreCase);

            return enhancedUrl;
        }


        private Author MapAuthor(AuthorResource resource)
        {
            var mapStopwatch = Stopwatch.StartNew();
            var logger = _logger;

            logger.Info("[PERF] MapAuthor started for {0} - Works: {1}, Series: {2}", resource.Name, resource.Works?.Count ?? 0, resource.Series?.Count ?? 0);

            // Create Author with metadata fields directly
            var author = new Author
            {
                TitleSlug = resource.ForeignId.ToString(),
                Name = resource.Name.CleanSpaces(),
                Overview = resource.Description,
                Ratings = new Ratings { Votes = resource.RatingCount, Value = (decimal)resource.AverageRating },
                Status = AuthorStatusType.Continuing
            };

                // Goodreads API resources use numeric IDs; canonicalize to gr:{id}.
                SetAuthorProviderId(author, "goodreads", resource.ForeignId.ToString());

            author.SortName = author.Name.ToLower();
            author.NameLastFirst = author.Name.ToLastFirst();
            author.SortNameLastFirst = author.NameLastFirst.ToLower();

            if (resource.ImageUrl.IsNotNullOrWhiteSpace())
            {
                var enhancedImageUrl = EnhanceGoodreadsImageUrl(resource.ImageUrl);
                if (enhancedImageUrl.IsValidHttpUrl() &&
                    !MediaCoverRendition.IsKnownPlaceholderImageUrl(enhancedImageUrl))
                {
                    author.Images.Add(new MediaCover.MediaCover
                    {
                        Url = enhancedImageUrl,
                        CoverType = MediaCoverTypes.Poster
                    });
                }
            }

            if (resource.Url.IsNotNullOrWhiteSpace())
            {
                author.Links.Add(new Links { Url = resource.Url, Name = "Goodreads" });
            }

            // DEBUG: Log full Goodreads response for this author
            logger.Debug("=== GOODREADS API RESPONSE FOR AUTHOR: {0} (ID: {1}) ===", resource.Name, resource.ForeignId);
            logger.Debug("Total works returned: {0}", resource.Works?.Count ?? 0);

            if (resource.Works != null)
            {
                logger.Debug("All books from Goodreads for {0}:", resource.Name);
                foreach (var work in resource.Works.OrderBy(w => w.Title))
                {
                    logger.Debug("  - Work ID: {0}, Title: '{1}', ReleaseDate: {2}",
                        work.ForeignId,
                        work.Title ?? "[NO TITLE]",
                        work.ReleaseDate?.ToString("yyyy-MM-dd") ?? "[NO DATE]");

                    if (work.Books != null && work.Books.Any())
                    {
                        foreach (var book in work.Books)
                        {
                            logger.Debug("    - Edition: '{0}' (ID: {1}, ISBN: {2})",
                                book.Title ?? "[NO TITLE]",
                                book.ForeignId,
                                book.Isbn13 ?? "[NO ISBN]");
                        }
                    }
                }
            }

            // Check specifically for "Zealot's Eleventh Crusade" or similar
            var zealotVariations = new[] { "zealot", "eleventh", "crusade", "11th" };
            var potentialMatches = resource.Works?.Where(w =>
                w.Title != null &&
                zealotVariations.Any(v => w.Title.ToLowerInvariant().Contains(v)))
                .ToList();

            if (potentialMatches?.Any() == true)
            {
                logger.Debug("Found potential matches for 'Zealot's Eleventh Crusade':");
                foreach (var match in potentialMatches)
                {
                    logger.Debug("  - Work ID: {0}, Title: '{1}'", match.ForeignId, match.Title);
                }
            }
            else
            {
                logger.Debug("NO matches found for 'Zealot's Eleventh Crusade' variations in Goodreads response");
            }

            logger.Debug("=== END GOODREADS RESPONSE ===");

            var worksStopwatch = Stopwatch.StartNew();

            // DEBUG: Log author filtering
            logger.Debug("=== AUTHOR BOOK FILTERING DEBUG ===");
            logger.Debug("Processing works for author: {0} (ForeignId: {1})", resource.Name, resource.ForeignId);
            logger.Debug("Total works from API: {0}", resource.Works?.Count ?? 0);

            var filteredWorks = new List<WorkResource>();
            var excludedWorks = new List<WorkResource>();

            foreach (var work in resource.Works.Where(x => x.ForeignId > 0))
            {
                var primaryAuthorId = GetAuthorId(work);
                var isContributor = IsAuthorContributor(work, resource.ForeignId);

                if (primaryAuthorId == resource.ForeignId || isContributor)
                {
                    filteredWorks.Add(work);
                    logger.Debug("  INCLUDING work '{0}' - Primary author: {1}, Is contributor: {2}", work.Title ?? "[NO TITLE]", primaryAuthorId, isContributor);
                }
                else
                {
                    excludedWorks.Add(work);
                    logger.Debug("  EXCLUDING work '{0}' - Primary author: {1}, Is contributor: {2}", work.Title ?? "[NO TITLE]", primaryAuthorId, isContributor);
                }
            }

            logger.Debug("Filtering complete: {0} included, {1} excluded", filteredWorks.Count, excludedWorks.Count);
            logger.Debug("=== END AUTHOR BOOK FILTERING DEBUG ===");

            // Apply quality filtering for authors with suspiciously large catalogs
            if (filteredWorks.Count > 300)
            {
                logger.Info("Author {0} has {1} works - applying automatic quality filtering", resource.Name, filteredWorks.Count);

                var beforeCount = filteredWorks.Count;

                filteredWorks = filteredWorks.Where(work =>
                {
                    // Must have a release date
                    if (!work.ReleaseDate.HasValue)
                    {
                        logger.Debug("  AUTO-EXCLUDING work '{0}' - No release date", work.Title ?? "[NO TITLE]");
                        return false;
                    }

                    // Must have at least one edition with an ISBN
                    var hasIsbn = work.Books?.Any(b => !string.IsNullOrWhiteSpace(b.Isbn13)) ?? false;
                    if (!hasIsbn)
                    {
                        logger.Debug("  AUTO-EXCLUDING work '{0}' - No ISBN in any edition", work.Title ?? "[NO TITLE]");
                        return false;
                    }

                    return true;
                }).ToList();

                logger.Info("Automatic quality filtering removed {0} works, {1} remain", beforeCount - filteredWorks.Count, filteredWorks.Count);
            }

            var books = filteredWorks
                .Select(work => MapBook(work, _logger))
                .ToList();


            // DEBUG: Log links for all books
            logger.Debug("=== BOOK LINKS DEBUG ===");
            foreach (var book in books)
            {
                logger.Debug("Book '{0}' has {1} links:", book.Title, book.Links?.Count ?? 0);
                if (book.Links != null)
                {
                    foreach (var link in book.Links)
                    {
                        logger.Debug("  - {0}: {1}", link.Name, link.Url);
                    }
                }
            }

            logger.Debug("=== END BOOK LINKS DEBUG ===");

            worksStopwatch.Stop();
            logger.Info("[PERF] Processed {0} works into {1} books in {2}ms", resource.Works?.Count ?? 0, books.Count, worksStopwatch.ElapsedMilliseconds);

            var seriesStopwatch = Stopwatch.StartNew();
            var series = resource.Series.Select(MapSeries).ToList();
            seriesStopwatch.Stop();
            logger.Info("[PERF] Processed {0} series in {1}ms", series.Count, seriesStopwatch.ElapsedMilliseconds);

            var seriesLinksStopwatch = Stopwatch.StartNew();
            MapSeriesLinks(series, books, resource.Series);
            seriesLinksStopwatch.Stop();
            logger.Info("[PERF] Mapped series relationships in {0}ms", seriesLinksStopwatch.ElapsedMilliseconds);

            // Add books and series to the author we created earlier
            author.CleanName = Parser.Parser.CleanAuthorName(author.Name);
            author.Books = books;
            author.Series = series;

            mapStopwatch.Stop();
            logger.Info("[PERF] Total MapAuthor time: {0}ms for {1}", mapStopwatch.ElapsedMilliseconds, resource.Name);

            return author;
        }

        private void MapSeriesLinks(List<Series> series, List<Book> books, List<SeriesResource> resource)
        {
            var bookDict = books.ToDictionary(x => GetBookKey(x));
            var seriesDict = series.ToDictionary(x => GetSeriesKey(x));

            foreach (var book in books)
            {
                book.SeriesLinks = new List<SeriesBookLink>();
            }

            // only take series where there are some works
                foreach (var s in resource.Where(x => x.LinkItems.Any()))
                {
                    // Goodreads API resources use numeric IDs; canonicalize to gr:{id}.
                    var seriesKey = ProviderIdHelper.WithPrefix("gr", s.ForeignId.ToString());

                if (seriesDict.TryGetValue(seriesKey, out var curr))
                {
                        curr.LinkItems = s.LinkItems.Where(x => x.ForeignWorkId != 0).Select(l =>
                        {
                            // Goodreads API resources use numeric IDs; canonicalize to gr:{id}.
                            var bookKey = ProviderIdHelper.WithPrefix("gr", l.ForeignWorkId.ToString());

                        if (bookDict.TryGetValue(bookKey, out var book))
                        {
                            return new SeriesBookLink
                            {
                                Book = book,
                                Series = curr,
                                IsPrimary = l.Primary,
                                Position = l.PositionInSeries,
                                SeriesPosition = l.SeriesPosition
                            };
                        }
                        return null;
                    }).Where(x => x != null).ToList();

                    foreach (var l in curr.LinkItems)
                    {
                        l.Book.Value.SeriesLinks.Add(l);
                    }
                }
            }
        }

        private Series MapSeries(SeriesResource resource)
        {
            var series = new Series
            {
                Title = resource.Title,
                TitleSlug = resource.ForeignId.ToString(),
                Description = resource.Description
            };

                // Goodreads API resources use numeric IDs; canonicalize to gr:{id}.
                SetSeriesProviderId(series, "goodreads", resource.ForeignId.ToString());

            return series;
        }

        private Book MapBook(WorkResource resource, Logger logger)
        {
            var title = resource.Title;

            // Remove series information from title if present
            // Pattern: "Book Title (Series Name, #N)" or "Book Title (Series Name #N)"
            var cleanTitle = string.IsNullOrWhiteSpace(title)
                ? string.Empty
                : Regex.Replace(title, @"\s*\([^)]+[,\s]+#\d+\)$", "").Trim();

            var book = new Book
            {
                Title = cleanTitle,  // Use cleaned title
                TitleSlug = resource.ForeignId.ToString(),
                CleanTitle = Parser.Parser.CleanAuthorName(cleanTitle),
                ReleaseDate = resource.ReleaseDate,
                Genres = resource.Genres,
                RelatedBooks = resource.RelatedWorks
            };

            book.Links.Add(new Links { Url = resource.Url, Name = "Goodreads Editions" });

                // Goodreads API resources use numeric IDs; canonicalize to gr:{id}.
                SetBookProviderId(book, "goodreads", resource.ForeignId.ToString());

            if (resource.Books != null)
            {
                book.Editions = resource.Books.Select(x => MapEdition(x)).ToList();

                // Don't select monitored edition here - wait until after metadata profile is applied
                // This ensures we only monitor editions that pass language/content filters

                // fix work title if missing by using most popular edition
                if (book.Title.IsNullOrWhiteSpace() && book.Editions.Any())
                {
                    var mostPopular = book.Editions.MaxBy(x => x.Ratings.Popularity);
                    if (mostPopular != null)
                    {
                        // Clean series information from edition title too
                        var editionTitle = Regex.Replace(mostPopular.Title, @"\s*\([^)]+[,\s]+#\d+\)$", "").Trim();
                        book.Title = editionTitle;
                        book.CleanTitle = Parser.Parser.CleanAuthorName(editionTitle);
                    }
                }
            }
            else
            {
                book.Editions = new List<Edition>();
            }

            // If we are missing the book release date, set as the earliest edition release date
            if (!book.ReleaseDate.HasValue)
            {
                var editionReleases = book.Editions
                    .Where(x => x.ReleaseDate.HasValue && x.ReleaseDate.Value.Month != 1 && x.ReleaseDate.Value.Day != 1)
                    .ToList();

                if (editionReleases.Any())
                {
                    book.ReleaseDate = editionReleases.Min(x => x.ReleaseDate.Value);
                }
                else
                {
                    editionReleases = book.Editions.Where(x => x.ReleaseDate.HasValue).ToList();
                    if (editionReleases.Any())
                    {
                        book.ReleaseDate = editionReleases.Min(x => x.ReleaseDate.Value);
                    }
                }
            }

            // Monitored edition will be selected after metadata profile is applied
            book.AnyEditionOk = true;

            var ratingCount = book.Editions.Sum(x => x.Ratings.Votes);

            if (ratingCount > 0)
            {
                book.Ratings = new Ratings
                {
                    Votes = ratingCount,
                    Value = book.Editions.Sum(x => x.Ratings.Votes * x.Ratings.Value) / ratingCount
                };
            }
            else
            {
                book.Ratings = new Ratings { Votes = 0, Value = 0 };
            }

            return book;
        }

        // Both live callers consume Goodreads-shaped author/work payloads, so this numeric edition ID is Goodreads identity.
        private static Edition MapEdition(BookResource resource)
        {
            var edition = new Edition
            {
                ForeignEditionId = EnsureCanonicalProviderId(resource.ForeignId.ToString(), "gr"),
                GoodreadsEditionId = resource.ForeignId,
                TitleSlug = resource.ForeignId.ToString(),
                Isbn13 = resource.Isbn13,
                Asin = resource.Asin,
                Title = resource.Title.CleanSpaces(),
                Language = resource.Language,
                Overview = resource.Description,
                Format = resource.Format,
                IsEbook = resource.IsEbook,
                Disambiguation = resource.EditionInformation,
                Publisher = resource.Publisher,
                PageCount = resource.NumPages ?? 0,
                ReleaseDate = resource.ReleaseDate,
                Ratings = new Ratings { Votes = resource.RatingCount, Value = (decimal)resource.AverageRating }
            };

            if (resource.ImageUrl.IsNotNullOrWhiteSpace())
            {
                var enhancedImageUrl = EnhanceGoodreadsImageUrl(resource.ImageUrl);
                if (enhancedImageUrl.IsValidHttpUrl())
                {
                    edition.Images.Add(new MediaCover.MediaCover
                    {
                        Url = enhancedImageUrl,
                        CoverType = MediaCoverTypes.Cover
                    });
                }
            }

            edition.Links.Add(new Links { Url = resource.Url, Name = "Goodreads Book" });

            return edition;
        }

        // Language comes directly from the metadata server — no client-side detection/correction.
        // If language data is wrong, fix it in the golden pipeline on the server.

        private static int GetAuthorId(WorkResource b)
        {
            return b.Books.OrderByDescending(x => x.RatingCount * x.AverageRating).FirstOrDefault(x => x.Contributors.Any())?.Contributors.First().ForeignId ?? 0;
        }

        private static bool IsAuthorContributor(WorkResource work, int authorForeignId)
        {
            if (work.Books == null || !work.Books.Any())
            {
                return false;
            }

            // Check if the author appears as a contributor in ANY edition of this work
            foreach (var book in work.Books)
            {
                if (book.Contributors != null && book.Contributors.Any(c => c.ForeignId == authorForeignId))
                {
                    return true;
                }
            }

            return false;
        }

        private Book CreateBookFromSearchResult(SearchJsonResource searchResult)
        {
            if (searchResult == null || searchResult.Author == null)
            {
                return null;
            }

            _logger.Debug("Creating Book from search result: {0} by {1}", searchResult.Title, searchResult.Author.Name);

            var author = new Author
            {
                GoodreadsAuthorId = $"gr:{searchResult.Author.Id}",
                Name = searchResult.Author.Name,
                Status = AuthorStatusType.Continuing,
                CleanName = Parser.Parser.CleanAuthorName(searchResult.Author.Name),
                Monitored = false
            };
            EnsureGoodreadsAuthorLink(author);

            var book = new Book
            {
                GoodreadsWorkId = EnsureCanonicalProviderId(searchResult.WorkId.ToString(), "gr"),
                Title = searchResult.Title,
                TitleSlug = searchResult.WorkId.ToString(),
                CleanTitle = Parser.Parser.CleanAuthorName(searchResult.Title),
                ReleaseDate = null, // Will be populated when actually adding to DB
                AudiobookMonitored = false,
                EbookMonitored = false,
                AnyEditionOk = true,
                Author = author,
                Ratings = new Ratings
                {
                    Votes = searchResult.RatingsCount,
                    Value = (decimal)searchResult.AverageRating
                }
            };

            var edition = new Edition
            {
                ForeignEditionId = EnsureCanonicalProviderId(searchResult.BookId.ToString(), "gr"),
                GoodreadsEditionId = long.TryParse(searchResult.BookId.ToString(), out var goodreadsEditionId) ? goodreadsEditionId : (long?)null,
                Title = searchResult.Title,
                TitleSlug = searchResult.WorkId.ToString(),
                Overview = searchResult.Description?.Html ?? string.Empty,
                Monitored = true,
                ManualAdd = false, // Should be false for automatic imports
                PageCount = searchResult.PageCount,
                Ratings = new Ratings
                {
                    Votes = searchResult.RatingsCount,
                    Value = (decimal)searchResult.AverageRating
                },
                Book = book
            };

            var enhancedSearchImageUrl = EnhanceGoodreadsImageUrl(searchResult.ImageUrl);
            if (enhancedSearchImageUrl.IsValidHttpUrl())
            {
                edition.Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new NzbDrone.Core.MediaCover.MediaCover
                    {
                        Url = enhancedSearchImageUrl,
                        CoverType = MediaCoverTypes.Cover
                    }
                };
            }

            book.Editions = new List<Edition> { edition };

            return book;
        }

            private static void EnsureGoodreadsAuthorLink(Author author)
            {
            if (author == null)
            {
                return;
            }

            var providerId = author.GoodreadsAuthorId;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return;
            }

            var trimmed = providerId.Trim().Trim('{', '}');
            var idx = trimmed.IndexOf(':');
            if (idx >= 0)
            {
                trimmed = trimmed.Substring(idx + 1);
            }

            if (!int.TryParse(trimmed, out var authorId) || authorId <= 0)
            {
                return;
            }

            var url = BuildGoodreadsAuthorUrl(authorId, author.Name);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            author.Links ??= new List<Links>();

            var existingIndex = author.Links.FindIndex(l =>
                l != null &&
                !string.IsNullOrWhiteSpace(l.Url) &&
                l.Url.Contains("goodreads.com/author/show", StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                var existing = author.Links[existingIndex];
                existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? "goodreads" : existing.Name;
                existing.Url = url;

                // Ensure Goodreads is the primary external link for Goodreads authors.
                if (existingIndex != 0)
                {
                    author.Links.RemoveAt(existingIndex);
                    author.Links.Insert(0, existing);
                }

                return;
            }

            author.Links.Insert(0, new Links { Name = "goodreads", Url = url });
            }

        private static string BuildGoodreadsAuthorUrl(int authorId, string authorName)
        {
            if (authorId <= 0)
            {
                return null;
            }

            var slug = SlugifyGoodreadsAuthorName(authorName);
            return string.IsNullOrWhiteSpace(slug)
                ? $"https://www.goodreads.com/author/show/{authorId}"
                : $"https://www.goodreads.com/author/show/{authorId}.{slug}";
        }

        private static string SlugifyGoodreadsAuthorName(string authorName)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return string.Empty;
            }

            // Goodreads uses underscores and preserves initials reasonably well.
            var slug = Regex.Replace(authorName.Trim(), @"[^\p{L}\p{Nd}]+", "_");
            slug = Regex.Replace(slug, @"_+", "_").Trim('_');
            return slug;
        }

        // V5 API Response Classes
        private class V5SearchResult
        {
            [JsonProperty("foreignId")]
            public string ForeignId { get; set; }

            [JsonProperty("author")]
            public V5Author Author { get; set; }

            [JsonProperty("book")]
            public V5Book Book { get; set; }

            [JsonProperty("series")]
            public V5Series Series { get; set; }

            [JsonProperty("id")]
            public int Id { get; set; }
        }

        private class V5Author
        {
            [JsonProperty("authorName")]
            public string AuthorName { get; set; }

            [JsonProperty("foreignAuthorId")]
            public string ForeignAuthorId { get; set; }

            [JsonProperty("titleSlug")]
            public string TitleSlug { get; set; }

            [JsonProperty("remotePoster")]
            public string RemotePoster { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("birthDate")]
            public string BirthDate { get; set; }

            [JsonProperty("deathDate")]
            public string DeathDate { get; set; }

            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("images")]
            public List<V5Image> Images { get; set; }

            [JsonProperty("ratings")]
            public V5Ratings Ratings { get; set; }

            [JsonProperty("links")]
            public List<V5Link> Links { get; set; }

            [JsonProperty("books")]
            public List<string> Books { get; set; }
        }

        private class V5Book
        {
            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("authorTitle")]
            public string AuthorTitle { get; set; }

            [JsonProperty("seriesTitle")]
            public string SeriesTitle { get; set; }

            [JsonProperty("overview")]
            public string Overview { get; set; }

            [JsonProperty("foreignBookId")]
            public string ForeignBookId { get; set; }

            [JsonProperty("titleSlug")]
            public string TitleSlug { get; set; }

            [JsonProperty("position")]
            public string Position { get; set; }

            [JsonProperty("authorName")]
            public string AuthorName { get; set; }

            [JsonProperty("isbn13")]
            public string Isbn13 { get; set; }

            [JsonProperty("asin")]
            public string Asin { get; set; }

            [JsonProperty("images")]
            public List<V5Image> Images { get; set; }

            [JsonProperty("ratings")]
            public decimal Ratings { get; set; }

            [JsonProperty("releaseDate")]
            public string ReleaseDate { get; set; }

            [JsonProperty("remoteCover")]
            public string RemoteCover { get; set; }

            [JsonProperty("author")]
            public V5Author Author { get; set; }
        }

        private class V5Series
        {
            [JsonProperty("foreignSeriesId")]
            public string ForeignSeriesId { get; set; }

            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("titleSlug")]
            public string TitleSlug { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("numbered")]
            public bool Numbered { get; set; }

            [JsonProperty("workCount")]
            public int WorkCount { get; set; }

            [JsonProperty("primaryWorkCount")]
            public int PrimaryWorkCount { get; set; }

            [JsonProperty("goodreadsSeriesId")]
            public string GoodreadsSeriesId { get; set; }

            [JsonProperty("hardcoverSeriesId")]
            public string HardcoverSeriesId { get; set; }

            [JsonProperty("openLibrarySeriesId")]
            public string OpenLibrarySeriesId { get; set; }

            [JsonProperty("amazonSeriesAsin")]
            public string AmazonSeriesAsin { get; set; }

            [JsonProperty("seriesType")]
            public string SeriesType { get; set; }

            [JsonProperty("totalBooks")]
            public int TotalBooks { get; set; }

            [JsonProperty("primaryBooks")]
            public int PrimaryBooks { get; set; }

                [JsonProperty("providerUrls")]
                public ProviderUrlMap ProviderUrls { get; set; }

            [JsonProperty("books")]
            public List<V5SeriesBook> Books { get; set; }

            [JsonProperty("images")]
            public List<V5Image> Images { get; set; }
        }

        private class V5SeriesBook
        {
            [JsonProperty("foreignBookId")]
            public string ForeignBookId { get; set; }

            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("authorName")]
            public string AuthorName { get; set; }

            [JsonProperty("releaseDate")]
            public string ReleaseDate { get; set; }

            [JsonProperty("position")]
            public string Position { get; set; }

            [JsonProperty("ratings")]
            public V5Ratings Ratings { get; set; }

            [JsonProperty("images")]
            public List<V5Image> Images { get; set; }

            [JsonProperty("editions")]
            public List<V5Edition> Editions { get; set; }
        }

        private class V5Image
        {
            [JsonProperty("url")]
            public string Url { get; set; }

            [JsonProperty("coverType")]
            public string CoverType { get; set; }

            [JsonProperty("extension")]
            public string Extension { get; set; }
        }

        private class V5Link
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("url")]
            public string Url { get; set; }
        }

        [JsonConverter(typeof(V5RatingsConverter))]
        private class V5Ratings
        {
            [JsonProperty("value")]
            public decimal Value { get; set; }

            [JsonProperty("votes")]
            public int Votes { get; set; }
        }

        private class V5RatingsConverter : JsonConverter<V5Ratings>
        {
            public override void WriteJson(JsonWriter writer, V5Ratings value, JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }

            public override V5Ratings ReadJson(JsonReader reader, Type objectType, V5Ratings existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Integer || reader.TokenType == JsonToken.Float)
                {
                    // Handle numeric rating (0, 3.5, etc.)
                    var numericValue = Convert.ToDecimal(reader.Value);
                    return new V5Ratings { Value = numericValue, Votes = 0 };
                }
                else if (reader.TokenType == JsonToken.StartObject)
                {
                    // Handle object rating with value and votes
                    var obj = JObject.Load(reader);
                    return new V5Ratings
                    {
                        Value = obj["value"]?.ToObject<decimal>() ?? 0m,
                        Votes = obj["votes"]?.ToObject<int>() ?? 0
                    };
                }
                else
                {
                    // Handle null or unexpected types
                    return new V5Ratings { Value = 0m, Votes = 0 };
                }
            }
        }

        private class V5Edition
        {
            [JsonProperty("language")]
            public V5Language Language { get; set; }
        }

        private class V5Language
        {
            [JsonProperty("language")]
            public string Language { get; set; }

            [JsonProperty("code2")]
            public string Code2 { get; set; }
        }

        private List<object> FilterHardcoverSearchResultsPreservingOrder(List<object> results, string searchQuery)
        {
            _logger.Debug($"[BookInfoProxy] Starting filtering of {results.Count} results for query '{searchQuery}'");

            var filteredResults = new List<object>();
            var booksFiltered = 0;

            foreach (var result in results)
            {
                switch (result)
                {
                    case Author author:
                        // Trust the provider - don't filter authors
                        filteredResults.Add(author);
                        break;

                    case Book book:
                        // Filter books
                        var lowerTitle = book.Title?.ToLower() ?? "";
                        var skipPatterns = new[] { "collection", "box set", "boxed set",
                                                  "complete series", "bundle", "omnibus",
                                                  "boxset", "box-set" };

                        if (skipPatterns.Any(pattern => lowerTitle.Contains(pattern)))
                        {
                            _logger.Debug($"[BookInfoProxy] Filtering out collection/box set: {book.Title}");
                            booksFiltered++;
                            continue;
                        }

                        // Removed problematic 2-word filter that was incorrectly filtering out books
                        // like "Harry Potter" when searching for "harry potter"

                        filteredResults.Add(book);
                        break;

                    case Series seriesResult:
                        // Trust the provider - series might not have books populated during search
                        // but could still be valid results
                        filteredResults.Add(seriesResult);
                        break;

                    default:
                        // Keep unknown types
                        filteredResults.Add(result);
                        break;
                }
            }

            _logger.Debug($"[BookInfoProxy] Hardcover filtering removed {booksFiltered} books and preserved provider ordering");
            return filteredResults;
        }

        // V5 API Conversion Methods
        private Author ConvertV5Author(V5Author v5Author)
        {
            var name = v5Author.Name ?? v5Author.AuthorName; // V5 API uses 'name' field
            var cleanName = Parser.Parser.CleanAuthorName(name);
            var sortName = name?.ToLower() ?? string.Empty;
            var born = v5Author.BirthDate.ToValidAuthorDate();
            var died = v5Author.DeathDate.ToValidAuthorDate();

            var author = new Author
            {
                Name = name,
                TitleSlug = v5Author.TitleSlug,
                SortName = sortName,
                Born = born,
                Died = died,
                Status = AuthorExtensions.GetLifeStatus(died),
                Overview = v5Author.Description,
                Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links>(),
                Ratings = new Ratings { Value = 0, Votes = 0 },
                CleanName = cleanName,
                Path = cleanName,
                Monitored = false,
                // NULL means the media side is unconfigured until the add/import
                // boundary applies its explicit monitoring settings.
                AudiobookMonitored = null,
                AudiobookMonitorNewItems = null,
                EbookMonitored = null,
                EbookMonitorNewItems = null
            };

            // Parse and set provider ID from V5 API ID
            var (provider, id) = ParseProviderId(v5Author.ForeignAuthorId);
            SetAuthorProviderId(author, provider, id);

            // Add images
            if (v5Author.Images != null)
            {
                author.Images = v5Author.Images
                    .Where(img => img?.Url.IsValidHttpUrl() == true &&
                                  !MediaCoverRendition.IsKnownPlaceholderImageUrl(img.Url))
                    .Select(img => new MediaCover.MediaCover
                    {
                        Url = img.Url,
                        CoverType = ParseCoverType(img.CoverType)
                        // Extension is automatically set from URL
                    }).ToList();
            }
            if (author.Images.Count == 0 &&
                v5Author.RemotePoster.IsValidHttpUrl() &&
                !MediaCoverRendition.IsKnownPlaceholderImageUrl(v5Author.RemotePoster))
            {
                author.Images.Add(new MediaCover.MediaCover
                {
                    Url = v5Author.RemotePoster,
                    CoverType = MediaCoverTypes.Poster
                });
            }

            // Add links
            if (v5Author.Links != null)
            {
                author.Links = v5Author.Links.Select(link => new Links
                {
                    Name = link.Name,
                    Url = link.Url
                }).ToList();
            }

            // Add ratings
            if (v5Author.Ratings != null)
            {
                author.Ratings = new Ratings
                {
                    Value = v5Author.Ratings.Value,
                    Votes = v5Author.Ratings.Votes
                };
            }

            // Add books list for sorting purposes
            if (v5Author.Books != null && v5Author.Books.Count > 0)
            {
                author.Books = new List<Book>();
                foreach (var bookTitle in v5Author.Books)
                {
                    // Create minimal book objects for sorting
                    // These are just for display/sorting, not full book entities
                    author.Books.Add(new Book
                    {
                        Title = bookTitle,
                        // Set a dummy ID to avoid null issues
                    });
                }
            }

            return author;
        }

        private Book ConvertV5Book(V5Book v5Book)
        {
            var book = new Book
            {
                Title = v5Book.Title,
                TitleSlug = v5Book.TitleSlug,
                AudiobookMonitored = false,
                EbookMonitored = false,
                AnyEditionOk = true
            };

            // Parse and set provider ID from V5 API ID
            var (provider, id) = ParseProviderId(v5Book.ForeignBookId);
            SetBookProviderId(book, provider, id);

            // Convert author if present
            if (v5Book.Author != null)
            {
                book.Author = ConvertV5Author(v5Book.Author);
            }

            // Parse release date
            if (DateTime.TryParse(v5Book.ReleaseDate, out var releaseDate))
            {
                book.ReleaseDate = releaseDate;
            }

            // Add ratings
            book.Ratings = new Ratings
            {
                Value = v5Book.Ratings,
                Votes = 0  // V5 API doesn't provide vote counts, only the rating value
            };

            // Add cover image
            var images = new List<MediaCover.MediaCover>();
            if (v5Book.Images != null && v5Book.Images.Any())
            {
                images = v5Book.Images.Select(img => new MediaCover.MediaCover
                {
                    Url = img.Url,
                    CoverType = ParseCoverType(img.CoverType)
                    // Extension is automatically set from URL
                }).ToList();
            }
            else if (!string.IsNullOrEmpty(v5Book.RemoteCover))
            {
                images.Add(new MediaCover.MediaCover
                {
                    Url = v5Book.RemoteCover,
                    CoverType = MediaCoverTypes.Cover
                });
            }

            // Create edition
            var edition = new Edition
            {
                Title = v5Book.Title,
                TitleSlug = v5Book.TitleSlug,
                Overview = v5Book.Overview ?? string.Empty,
                Monitored = true,
                ManualAdd = false, // Should be false for automatic imports
                Book = book,
                Images = images
            };

            book.Editions = new List<Edition> { edition };

            return book;
        }

        private Series ConvertV5Series(V5Series v5Series)
        {
            var series = new Series
            {
                Title = v5Series.Title,
                TitleSlug = v5Series.TitleSlug,
                WorkCount = v5Series.WorkCount,
                PrimaryWorkCount = v5Series.PrimaryWorkCount,
                Description = v5Series.Description,
                Numbered = v5Series.Numbered,

                // Provider IDs
                GoodreadsSeriesId = v5Series.GoodreadsSeriesId,
                HardcoverSeriesId = v5Series.HardcoverSeriesId,
                OpenLibrarySeriesId = v5Series.OpenLibrarySeriesId,

                // Series metadata
                SeriesType = v5Series.SeriesType,
                TotalBooks = v5Series.TotalBooks,
                PrimaryBooks = v5Series.PrimaryBooks,

                // New metadata fields
                ProviderUrls = v5Series.ProviderUrls?.ValidateProviderUrls() ?? new ProviderUrlMap(),
                LastUpdated = DateTime.UtcNow  // Series don't have LastUpdated from server
            };

            // Parse and set provider ID from V5 API ID if it doesn't match the specific provider IDs
            if (!string.IsNullOrEmpty(v5Series.ForeignSeriesId))
            {
                var (provider, id) = ParseProviderId(v5Series.ForeignSeriesId);
                SetSeriesProviderId(series, provider, id);
            }

            // Preserve lightweight series-book metadata for search result covers and previews.
            // Do NOT populate Series.Books here (heavy domain objects + serialization pitfalls).
            var seriesBooks = new List<SeriesBook>();

            if (v5Series.Books?.Any() == true)
            {
                seriesBooks = v5Series.Books.Select(b =>
                {
                    var coverUrl =
                        b.Images?.FirstOrDefault(i => i?.CoverType?.Equals("cover", StringComparison.OrdinalIgnoreCase) == true)?.Url
                        ?? b.Images?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i?.Url))?.Url;

                    return new SeriesBook
                    {
                        BookId = b.ForeignBookId,
                        Title = b.Title,
                        Position = b.Position,
                        CoverUrl = coverUrl
                    };
                }).ToList();
            }

            // Some V5 series results include an `images` array but omit images on the per-book items.
            // Use the series-level images as a fallback so series tiles don't get stuck on "Loading...".
            if (v5Series.Images?.Any() == true)
            {
                var imageUrls = v5Series.Images
                    .Where(i => !string.IsNullOrWhiteSpace(i?.Url))
                    .Select(i => i.Url)
                    .ToList();

                if (imageUrls.Count > 0)
                {
                    // Fill missing per-book covers (best-effort).
                    for (var i = 0; i < seriesBooks.Count && i < imageUrls.Count; i++)
                    {
                        if (string.IsNullOrWhiteSpace(seriesBooks[i].CoverUrl))
                        {
                            seriesBooks[i].CoverUrl = imageUrls[i];
                        }
                    }

                    // If we still don't have any covers, fall back to image-only seriesBooks.
                    if (!seriesBooks.Any(b => !string.IsNullOrWhiteSpace(b.CoverUrl)))
                    {
                        seriesBooks = imageUrls.Select(u => new SeriesBook { CoverUrl = u }).ToList();
                    }
                }
            }

            if (seriesBooks.Any())
            {
                series.SeriesBooks = seriesBooks;
            }

            // Note: Skip populating Books property to avoid serialization issues
            // The Books property should be populated through proper entity relationships
            // when needed by the API layer, not stored as cached JSON

            // Note: Series domain model doesn't have Images property
            // Images are handled at the API resource level during conversion

            return series;
        }

        // Hardcover result mapping helpers
        private List<object> MapHardcoverResultsToDomain(List<object> raw)
        {
            var results = new List<object>();
            foreach (var item in raw)
            {
                switch (item)
                {
                    case Hardcover.HardcoverAuthorResult a:
                        results.Add(ConvertHardcoverAuthor(a));
                        break;
                    case Hardcover.HardcoverBookResult b:
                        results.Add(ConvertHardcoverBook(b));
                        break;
                    case Hardcover.HardcoverSeriesResult s:
                        results.Add(ConvertHardcoverSeries(s));
                        break;
                    default:
                        _logger.Debug($"[BookInfoProxy] Unknown Hardcover result type: {item?.GetType().Name}");
                        break;
                }
            }
            return results;
        }

        private Author ConvertHardcoverAuthor(Hardcover.HardcoverAuthorResult a)
        {
            var name = a?.Name ?? string.Empty;
            var born = a?.BornDate.ToValidAuthorDate();
            var died = a?.DeathDate.ToValidAuthorDate();
            var author = new Author
            {
                Name = name,
                TitleSlug = !string.IsNullOrWhiteSpace(a?.Slug) ? a.Slug : $"hc:{a?.Id}",
                Overview = a?.Bio ?? string.Empty,
                Born = born,
                Died = died,
                Status = AuthorExtensions.GetLifeStatus(died),
                CleanName = Parser.Parser.CleanAuthorName(name),
                SortName = name.ToLowerInvariant(),
                NameLastFirst = name.ToLastFirst(),
                SortNameLastFirst = name.ToLastFirst().ToLowerInvariant(),
                Ratings = new Ratings { Value = 0, Votes = 0 },
                Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links>(),
                Monitored = false,
                AudiobookMonitored = null,
                AudiobookMonitorNewItems = null,
                EbookMonitored = null,
                EbookMonitorNewItems = null
            };

            // Provider ID
            SetAuthorProviderId(author, "hardcover", a?.Id);

            // Image
            if (a?.ImageUrl.IsValidHttpUrl() == true &&
                !MediaCoverRendition.IsKnownPlaceholderImageUrl(a.ImageUrl))
            {
                author.Images.Add(new MediaCover.MediaCover
                {
                    Url = a.ImageUrl,
                    CoverType = MediaCoverTypes.Poster
                });
            }

            // Provider URL
            if (!string.IsNullOrWhiteSpace(a?.Slug))
            {
                author.ProviderUrls = new ProviderUrlMap();
                author.ProviderUrls.SetNormalized("hardcover", $"https://hardcover.app/authors/{a.Slug}");
            }

            return author;
        }

        private Book ConvertHardcoverBook(Hardcover.HardcoverBookResult b)
        {
            var book = new Book
            {
                Title = b?.Title ?? string.Empty,
                Subtitle = b?.Subtitle,
                Overview = b?.Description ?? string.Empty,
                TitleSlug = $"hc:{b?.Id}", // stable unique slug
                CleanTitle = Parser.Parser.CleanAuthorName(b?.Title ?? string.Empty),
                AudiobookMonitored = false,
                EbookMonitored = false,
                AnyEditionOk = true,
                Ratings = new Ratings { Value = (decimal)(b?.Rating ?? 0f), Votes = 0 },
                Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links>(),
                Editions = new List<Edition>()
            };

            // Provider ID
            SetBookProviderId(book, "hardcover", b?.Id);

            // Parse release date if present
            if (!string.IsNullOrWhiteSpace(b?.ReleaseDate) && DateTime.TryParse(b.ReleaseDate, out var rd))
            {
                book.ReleaseDate = rd;
            }

            // Primary cover (ignore invalid/relative URLs)
            if (b?.ImageUrl.IsValidHttpUrl() == true)
            {
                book.Images.Add(new MediaCover.MediaCover
                {
                    Url = b.ImageUrl,
                    CoverType = MediaCoverTypes.Cover
                });
            }

            // Minimal author shell (Hardcover search returns names and IDs)
            var authorName = b?.AuthorNames?.FirstOrDefault();
            var authorId = b?.AuthorIds?.FirstOrDefault();
            
            _logger.Debug($"ConvertHardcoverBook: Book '{b?.Title}' has author name: '{authorName}', author ID: '{authorId}'");
            
            if (!string.IsNullOrWhiteSpace(authorName))
            {
                book.Author = new Author
                {
                    Name = authorName,
                    CleanName = Parser.Parser.CleanAuthorName(authorName),
                    Status = AuthorStatusType.Continuing,
                    Monitored = false
                };
                
                // Set the Hardcover author ID if available
                if (!string.IsNullOrWhiteSpace(authorId))
                {
                    SetAuthorProviderId(book.Author, "hardcover", authorId);
                    _logger.Debug($"Set HardcoverAuthorId to: {book.Author.HardcoverAuthorId}");
                }
                else
                {
                    _logger.Debug($"No author ID to set for book '{b?.Title}'");
                }
            }

            // Create a single lightweight edition for display parity with V5 search
            var edition = new Edition
            {
                Title = b?.Title ?? string.Empty,
                TitleSlug = $"hc:{b?.Id}",
                Overview = b?.Description ?? string.Empty,
                Monitored = true,
                ManualAdd = false,
                PageCount = b?.Pages ?? 0,
                Ratings = new Ratings { Value = (decimal)(b?.Rating ?? 0f), Votes = 0 }
            };

            if (b?.ImageUrl.IsValidHttpUrl() == true)
            {
                edition.Images.Add(new MediaCover.MediaCover
                {
                    Url = b.ImageUrl,
                    CoverType = MediaCoverTypes.Cover
                });
            }

            // Capture first ISBN if any
            if (b?.Isbns != null && b.Isbns.Length > 0)
            {
                var firstIsbn = b.Isbns.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                if (!string.IsNullOrWhiteSpace(firstIsbn))
                {
                    // naive assignment; downstream services normalize/validate further if needed
                    if (firstIsbn.Length == 13) edition.Isbn13 = firstIsbn;
                    else edition.Isbn10 = firstIsbn;
                }
            }

            book.Editions.Add(edition);
            return book;
        }

            private Series ConvertHardcoverSeries(Hardcover.HardcoverSeriesResult s)
            {
            var totalBooks = s?.BooksCount ?? 0;
            var primaryBooks = s?.PrimaryBooksCount ?? 0;
            var displayBooks = primaryBooks > 0 ? primaryBooks : totalBooks;

            var series = new Series
            {
                Title = s?.Name ?? string.Empty,
                TitleSlug = !string.IsNullOrWhiteSpace(s?.Slug) ? s.Slug : $"hc:{s?.Id}",
                Description = s?.Description ?? string.Empty,
                Numbered = true,
                // Hardcover returns both total and primary counts; we only surface primary (featured) works in search/preview.
                WorkCount = displayBooks,
                PrimaryWorkCount = displayBooks,
                SeriesType = "main",
                TotalBooks = totalBooks,
                PrimaryBooks = primaryBooks,
                ProviderUrls = new ProviderUrlMap(),
                SeriesBooks = new List<SeriesBook>()
            };

            // Provider ID
            SetSeriesProviderId(series, "hardcover", s?.Id);

            if (!string.IsNullOrWhiteSpace(s?.Slug))
            {
                series.ProviderUrls.SetNormalized("hardcover", $"https://hardcover.app/series/{s.Slug}");
            }

            if (s?.CoverUrls?.Any() == true)
            {
                series.SeriesBooks = s.CoverUrls
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Take(3)
                    .Select((url, index) => new SeriesBook
                    {
                        // Keep provider ID for handshaking; book IDs aren't included in this lightweight search path
                        BookId = null,
                        Title = null,
                        Position = (index + 1).ToString(),
                        CoverUrl = url
                    })
                    .ToList();
            }

            return series;
        }

        private MediaCoverTypes ParseCoverType(string coverType)
        {
            if (string.IsNullOrEmpty(coverType))
            {
                return MediaCoverTypes.Cover;
            }

            switch (coverType.ToLower())
            {
                case "poster":
                    return MediaCoverTypes.Poster;
                case "banner":
                    return MediaCoverTypes.Banner;
                case "fanart":
                    return MediaCoverTypes.Fanart;
                case "logo":
                    return MediaCoverTypes.Logo;
                case "clearlogo":
                    return MediaCoverTypes.Clearlogo;
                case "disc":
                    return MediaCoverTypes.Disc;
                default:
                    return MediaCoverTypes.Cover;
            }
        }

        // Helper method to parse V5 API IDs with provider prefixes (e.g., "hc:123" -> provider="hardcover", id="123")
        private (string provider, string id) ParseProviderId(string v5Id)
        {
            if (string.IsNullOrEmpty(v5Id))
            {
                return (null, null);
            }

            v5Id = v5Id.Trim();

            var colonIndex = v5Id.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= v5Id.Length - 1)
            {
                throw new InvalidOperationException($"V5 provider ID '{v5Id}' is not in canonical '<prefix>:<id>' form.");
            }

            if (v5Id.IndexOf(':', colonIndex + 1) != -1)
            {
                throw new InvalidOperationException($"V5 provider ID '{v5Id}' contains multiple ':' separators; expected a single canonical '<prefix>:<id>'.");
            }

            var prefix = v5Id.Substring(0, colonIndex).Trim().ToLowerInvariant();
            var id = v5Id.Substring(colonIndex + 1).Trim();

            if (id.Length == 0)
            {
                return (null, null);
            }

            if (!ProviderIdHelper.IsCanonicalPrefix(prefix))
            {
                throw new InvalidOperationException($"V5 provider ID '{v5Id}' uses unsupported prefix '{prefix}'. Expected one of az/gr/hc/ol/gb.");
            }

            return prefix switch
            {
                "hc" => ("hardcover", id),
                "gr" => ("goodreads", id),
                "ol" => ("openlibrary", id),
                "gb" => ("googlebooks", id),
                "az" => ("az", id),
                _ => throw new InvalidOperationException($"V5 provider ID '{v5Id}' uses unsupported prefix '{prefix}'. Expected one of az/gr/hc/ol/gb.")
            };
        }

            private Book MapAudibleCatalogBookToDomain(AudibleCatalogBookResource resource)
            {
            if (resource == null)
            {
                _logger.Warn("[MapAudibleCatalog] Received null AudibleCatalogBookResource");
                return null;
            }

            _logger.Debug($"[MapAudibleCatalog] Mapping book: {resource.Title} (ASIN: {resource.Asin})");

            // Create book with az: prefixed ASIN
            var book = new Book
            {
                Title = resource.FullTitle,
                CleanTitle = Parser.Parser.CleanAuthorName(resource.Title ?? string.Empty),
                ReleaseDate = resource.ReleaseDate,
                Overview = resource.Summary ?? resource.Description,
                MediaType = BookMediaType.Audiobook, // CRITICAL: Audible catalog only returns audiobooks
                AudiobookMonitored = false, // Set during add flow
                EbookMonitored = false,
                TitleSlug = $"az:{resource.Asin}",
                AnyEditionOk = true,

                // Rating conversion
                Ratings = resource.Rating.HasValue
                    ? new Ratings
                    {
                        Value = resource.Rating.Value,
                        Votes = 0 // Audible catalog doesn't provide vote count
                    }
                    : new Ratings { Value = 0, Votes = 0 },

                // Duration
                DurationMinutes = resource.LengthMinutes,

                // Series info
                SeriesName = resource.Series?.FirstOrDefault()?.Name,
                SeriesPosition = resource.Series?.FirstOrDefault()?.Position,

                // Publisher
                Publisher = resource.Publisher,

                // Images
                Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links>(),
                Editions = new List<Edition>()
            };

            // Add cover image (ignore invalid/relative URLs)
            if (resource.ImageUrl.IsValidHttpUrl())
            {
                book.Images.Add(new MediaCover.MediaCover
                {
                    CoverType = MediaCoverTypes.Cover,
                    Url = resource.ImageUrl
                });
            }

            // Prefer an Audible deep-link; some upstream sources return Amazon links in `resource.Link`.
            var audibleUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(resource.Link))
            {
                var candidate = resource.Link.Trim();
                if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = "https://" + candidate.Substring("http://".Length);
                }
                if (candidate.StartsWith("https://audible.com", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = "https://www.audible.com" + candidate.Substring("https://audible.com".Length);
                }
                if (candidate.StartsWith("https://www.audible.com", StringComparison.OrdinalIgnoreCase))
                {
                    audibleUrl = candidate;
                }
            }

            if (!string.IsNullOrWhiteSpace(audibleUrl))
            {
                book.Links.Add(new Links { Name = "audible", Url = audibleUrl });
            }

            // Map author (required)
            if (resource.Authors?.Any() == true)
            {
                var primaryAuthor = resource.Authors.First();
                var normalizedAuthorAsin = string.IsNullOrWhiteSpace(primaryAuthor.Asin)
                    ? null
                    : primaryAuthor.Asin.Trim().ToUpperInvariant();
                book.Author = new Author
                {
                    Name = primaryAuthor.Name,
                    CleanName = Parser.Parser.CleanAuthorName(primaryAuthor.Name ?? string.Empty),
                    TitleSlug = !string.IsNullOrWhiteSpace(normalizedAuthorAsin) ? $"az:{normalizedAuthorAsin}" : null,
                    Status = AuthorStatusType.Continuing,
                    SortName = primaryAuthor.Name?.ToLowerInvariant(),
                    NameLastFirst = primaryAuthor.Name?.ToLastFirst(),
                    SortNameLastFirst = primaryAuthor.Name?.ToLastFirst()?.ToLowerInvariant(),
                    // Only emit Amazon/Audible author IDs when the upstream payload actually provides one.
                    AudnexusAuthorId = !string.IsNullOrWhiteSpace(normalizedAuthorAsin) ? $"az:{normalizedAuthorAsin}" : null,
                    Monitored = false,
                    AudiobookMonitored = null,
                    AudiobookMonitorNewItems = null,
                    EbookMonitored = null,
                    EbookMonitorNewItems = null,
                    Ratings = new Ratings { Value = 0, Votes = 0 },
                    Images = new List<MediaCover.MediaCover>(),
                    Links = new List<Links>()
                };

                // Add author image if available
                if (primaryAuthor.Image.IsValidHttpUrl() &&
                    !MediaCoverRendition.IsKnownPlaceholderImageUrl(primaryAuthor.Image))
                {
                    book.Author.Images.Add(new MediaCover.MediaCover
                    {
                        CoverType = MediaCoverTypes.Poster,
                        Url = primaryAuthor.Image
                    });
                }
            }
            else
            {
                _logger.Warn($"[MapAudibleCatalog] Book '{resource.Title}' has no authors");
                return null; // Cannot create book without author
            }

            // Create Edition for audiobook
            var edition = new Edition
            {
                Title = book.Title,
                ForeignEditionId = $"az:{resource.Asin}", // Use ASIN as edition ID
                Asin = resource.Asin,
                Publisher = resource.Publisher,
                ReleaseDate = resource.ReleaseDate,
                Format = "Audiobook",
                Monitored = false, // Set during add flow
                Overview = book.Overview,

                // Narrator info - extract names from resource
                NarratorNames = new List<string>(),

                // Images
                Images = book.Images,

                // Rating
                Ratings = book.Ratings
            };

            // Extract narrator names
            if (resource.Narrators?.Any() == true)
            {
                edition.NarratorNames = resource.Narrators
                    .Select(n => n.Name?.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
            }

            // Apply GraphicAudio normalization
            if (resource.IsGraphicAudio)
            {
                edition.NarratorNames = new List<string> { "GraphicAudio" };
            }

            book.Editions.Add(edition);
            EditionPinPolicy.MarkSelectionAsAutomatic(book, book.Editions);

            _logger.Debug($"[MapAudibleCatalog] Successfully mapped book: {book.Title} with {edition.NarratorNames.Count} narrators");

            return book;
            }

            private List<object> BuildAudibleCatalogSearchResults(List<Book> books)
            {
            if (books == null || books.Count == 0)
            {
                return new List<object>();
            }

            var results = new List<object>();

            // Emit unique authors first (like the Goodreads path), enriching from V5 if available.
            var authors = books
                .Select(b => b?.Author)
                .Where(a => a != null && !string.IsNullOrWhiteSpace(GetAuthorMetadataKey(a)))
                .DistinctBy(a => GetAuthorMetadataKey(a))
                .ToList();

            foreach (var author in authors)
            {
                var authorId = GetAuthorMetadataKey(author);
                var enriched = TryGetAuthorInfoFromV5ForSearch(authorId);
                if (enriched != null)
                {
                    author.UseMetadataFrom(enriched);
                }

                results.Add(author);
            }

            // Then the books
            results.AddRange(books.Cast<object>());
            return results;
            }

            private Author TryGetAuthorInfoFromV5ForSearch(string prefixedAuthorId)
            {
            if (string.IsNullOrWhiteSpace(prefixedAuthorId) || !prefixedAuthorId.Contains(":"))
            {
                return null;
            }

            var cacheKey = $"v5-search-author:{prefixedAuthorId}";

            try
            {
                return _authorCache.GetOrAdd(cacheKey,
                    () =>
                    {
                        // V5 payload is authoritative for author bio/images/provider URLs.
                        // Let failures escape the factory so LazyCache never stores a null/error.
                        return GetAuthorInfoFromV5(prefixedAuthorId, useCache: false, importAllWorks: false)
                            ?? throw new AuthorNotFoundException(prefixedAuthorId);
                    },
                    new LazyCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                        ImmediateAbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                        Size = 1,
                        SlidingExpiration = TimeSpan.FromMinutes(2)
                    });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "V5 author cache lookup failed for {0}", prefixedAuthorId);
                return null;
            }
            }

        // Helper method to set provider-specific IDs on Author
            private void SetAuthorProviderId(Author author, string provider, string id)
            {
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id))
            {
                return;
            }

            provider = provider.Trim().ToLowerInvariant();
            id = id.Trim();

            if (id.Contains(":"))
            {
                throw new InvalidOperationException($"Unexpected prefixed provider ID '{id}' when setting Author provider ID for provider '{provider}'. Expected raw id portion only.");
            }

            switch (provider)
            {
                case "az":
                    author.AudnexusAuthorId = ProviderIdHelper.WithPrefix("az", id);
                    break;
                case "hardcover":
                    author.HardcoverAuthorId = ProviderIdHelper.WithPrefix("hc", id);
                    break;
                case "goodreads":
                    author.GoodreadsAuthorId = ProviderIdHelper.WithPrefix("gr", id);
                    break;
                case "openlibrary":
                    author.OpenLibraryAuthorId = ProviderIdHelper.WithPrefix("ol", id);
                    break;
                case "googlebooks":
                    author.GoogleBooksAuthorId = ProviderIdHelper.WithPrefix("gb", id);
                    break;
            }
            }

        // Helper method to set provider-specific IDs on Book
        private void SetBookProviderId(Book book, string provider, string id)
        {
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id))
            {
                return;
            }

            switch (provider.ToLower())
            {
                case "hardcover":
                    book.HardcoverBookId = $"hc:{id}";
                    break;
                case "goodreads":
                    if (long.TryParse(id, out _))
                    {
                        book.GoodreadsWorkId = $"gr:{id}";
                    }
                    break;
                case "openlibrary":
                    book.OpenLibraryWorkId = $"ol:{id}";
                    break;
            }
        }

        // Helper method to set provider-specific IDs on Series
        private void SetSeriesProviderId(Series series, string provider, string id)
        {
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id))
            {
                return;
            }

            switch (provider.ToLower())
            {
                case "hardcover":
                    series.HardcoverSeriesId = $"hc:{id}";
                    break;
                case "goodreads":
                    if (long.TryParse(id, out var grId))
                    {
                        series.GoodreadsSeriesId = $"gr:{id}";
                    }
                    break;
                case "openlibrary":
                    series.OpenLibrarySeriesId = $"ol:{id}";
                    break;
                case "az":
                    series.AmazonSeriesAsin = ProviderIdHelper.WithPrefix("az", id);
                    break;
            }
        }

        // Helper method to get a unique key for Author
        private string GetAuthorMetadataKey(Author author)
        {
            // IDs are already stored with prefixes, just return them
            if (!string.IsNullOrEmpty(author.HardcoverAuthorId))
            {
                return author.HardcoverAuthorId;
            }
            if (!string.IsNullOrEmpty(author.GoodreadsAuthorId))
            {
                return author.GoodreadsAuthorId;
            }
            if (!string.IsNullOrEmpty(author.OpenLibraryAuthorId))
            {
                return author.OpenLibraryAuthorId;
            }
            if (!string.IsNullOrEmpty(author.GoogleBooksAuthorId))
            {
                return author.GoogleBooksAuthorId;
            }
            if (!string.IsNullOrEmpty(author.AudnexusAuthorId))
            {
                return author.AudnexusAuthorId;
            }

            // Fallback to name-based key
            return author.Name ?? author.TitleSlug ?? "unknown";
        }

        // Helper method to get a unique key for Book
        private string GetBookKey(Book book)
        {
            var providerId = BookIdentity.GetStableWorkProviderIdentityTokens(book).FirstOrDefault()
                             ?? book.RemoteProviderIds?.FirstOrDefault(id => id.IsNotNullOrWhiteSpace())
                             ?? BookIdentity.GetEditionProviderIdentityTokens(book).FirstOrDefault();
            if (!string.IsNullOrEmpty(providerId))
            {
                return providerId;
            }
            // Fallback to title-based key
            return book.Title ?? book.TitleSlug ?? "unknown";
        }

        private static bool TryGetStableWorkPrefix(string providerKey, out string prefix)
        {
            prefix = null;

            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            switch (providerKey.Trim().ToLowerInvariant())
            {
                case "hc":
                case "hardcover":
                    prefix = "hc";
                    return true;
                case "gr":
                case "goodreads":
                    prefix = "gr";
                    return true;
                case "ol":
                case "openlibrary":
                    prefix = "ol";
                    return true;
                default:
                    return false;
            }
        }

        // Helper method to get a unique key for Series
        private string GetSeriesKey(Series series)
        {
            // IDs are already stored with prefixes, just return them
            if (!string.IsNullOrEmpty(series.HardcoverSeriesId))
            {
                return series.HardcoverSeriesId;
            }
            if (!string.IsNullOrEmpty(series.GoodreadsSeriesId))
            {
                return series.GoodreadsSeriesId;
            }
            if (!string.IsNullOrEmpty(series.AmazonSeriesAsin))
            {
                return series.AmazonSeriesAsin;
            }
            if (!string.IsNullOrEmpty(series.OpenLibrarySeriesId))
            {
                return series.OpenLibrarySeriesId;
            }

            // Fallback to title-based key
            return series.Title ?? series.TitleSlug ?? "unknown";
        }

        private Tuple<string, Book, List<Author>> MapV5WorkResponse(V5Resource.V5WorkResponse v5Response, string foreignBookId, BookMediaType mediaType)
        {
            _logger.Debug("Mapping canonical V5 work response for {0}", foreignBookId);
            var work = PrepareCanonicalV5Work(v5Response);
            var mappedAuthors = MapV5WorkAuthors(v5Response.Authors);
            var primaryAuthor = mappedAuthors.FirstOrDefault();
            if (primaryAuthor == null)
            {
                throw new BookInfoException($"V5 work response contained no authors for '{foreignBookId}' (workId={work.Id ?? "unknown"})");
            }

            var book = ConvertV5BookToDomain(work, primaryAuthor, mediaType);
            if (book.Editions?.Any() != true)
            {
                throw new BookInfoException($"No editions found for work {foreignBookId}");
            }

            book.AnyEditionOk = true;
            var authorKey = GetAuthorMetadataKey(primaryAuthor);
            if (authorKey.IsNullOrWhiteSpace() || (!authorKey.Contains(":") && !authorKey.All(char.IsDigit)))
            {
                throw new BookInfoException($"Invalid author key '{authorKey}' for V5 work '{foreignBookId}' (workId={work.Id ?? "unknown"})");
            }

            return Tuple.Create(authorKey, book, mappedAuthors);
        }

        private List<Book> MapV5WorkResponseToBookInstances(V5Resource.V5WorkResponse v5Response, string foreignWorkId)
        {
            if (v5Response?.Work == null)
            {
                return new List<Book>();
            }

            var work = PrepareCanonicalV5Work(v5Response);
            if (work.Editions?.Any(e => e != null) != true)
            {
                return new List<Book>();
            }

            var mappedAuthors = MapV5WorkAuthors(v5Response.Authors);
            var primaryAuthor = mappedAuthors.FirstOrDefault();
            if (primaryAuthor == null)
            {
                throw new BookInfoException($"V5 work response contained no authors for '{foreignWorkId}' (workId={work.Id ?? "unknown"})");
            }

            return new[]
                {
                    BookMediaType.Audiobook,
                    BookMediaType.Ebook
                }
                .Select(mediaType =>
                {
                    var book = ConvertV5BookToDomain(work, primaryAuthor, mediaType);
                    book.AnyEditionOk = true;
                    return book;
                })
                .Where(book => book.Editions?.Any() == true)
                .ToList();
        }

        private static V5.V5Book PrepareCanonicalV5Work(V5Resource.V5WorkResponse v5Response)
        {
            var work = v5Response?.Work;
            if (work == null)
            {
                throw new BookInfoException("V5 work response contained no canonical work payload");
            }

            // Transitional compatibility for metadata servers predating the
            // persisted canonical fragment. Remove when those versions are no
            // longer supported. Current servers emit the same editions both
            // nested in work and in the compatibility envelope.
            if (work.Editions?.Any(e => e != null) != true &&
                v5Response.Editions?.Any(e => e != null) == true)
            {
                work.Editions = v5Response.Editions.Where(e => e != null).ToList();
                PrepareLegacyV5Work(work);
            }

            return work;
        }

        private static void PrepareLegacyV5Work(V5.V5Book work)
        {
            string hcWorkId = work.HardcoverBookId ?? work.LegacyHardcoverWorkId;
            string grWorkId = work.GoodreadsWorkId;
            string olWorkId = work.OpenLibraryWorkId;

            if (string.IsNullOrWhiteSpace(hcWorkId))
            {
                TryGetProviderAlias(work.ProviderIds, out hcWorkId, "hc", "hardcover");
            }
            if (string.IsNullOrWhiteSpace(grWorkId))
            {
                TryGetProviderAlias(work.ProviderIds, out grWorkId, "gr", "goodreads");
            }
            if (string.IsNullOrWhiteSpace(olWorkId))
            {
                TryGetProviderAlias(work.ProviderIds, out olWorkId, "ol", "openlibrary", "openLibrary");
            }

            work.HardcoverBookId = ProviderIdHelper.Canonicalize(hcWorkId, "hc");
            work.GoodreadsWorkId = ProviderIdHelper.Canonicalize(grWorkId, "gr");
            work.OpenLibraryWorkId = ProviderIdHelper.Canonicalize(olWorkId, "ol");
            work.Id = work.HardcoverBookId ?? work.GoodreadsWorkId ?? work.OpenLibraryWorkId ??
                      (work.Id?.Contains(":") == true ? ProviderIdHelper.Normalize(work.Id, defaultPrefix: null) : null);

            if (string.IsNullOrWhiteSpace(work.Id))
            {
                throw new BookInfoException("Legacy V5 work response contained no provider-owned work id");
            }

            work.BaseBookId ??= work.Id;
            foreach (var edition in work.Editions)
            {
                PrepareLegacyV5Edition(edition);
            }
        }

        private static void PrepareLegacyV5Edition(V5.V5Edition edition)
        {
            if (edition == null)
            {
                return;
            }

            edition.Format ??= edition.LegacyFormatType;
            edition.PageCount ??= edition.LegacyPages;
            edition.DurationSeconds ??= edition.LegacyDurationMinutes.HasValue
                ? edition.LegacyDurationMinutes.Value * 60
                : null;
            if (edition.RatingAverage == 0m && edition.LegacyRating.HasValue)
            {
                edition.RatingAverage = edition.LegacyRating.Value;
            }
            if (edition.RatingCount == 0 && edition.LegacyRatingsCount.HasValue)
            {
                edition.RatingCount = edition.LegacyRatingsCount.Value;
            }
            if (edition.ReadingFormatId == 0)
            {
                edition.ReadingFormatId = edition.Format?.Trim().ToLowerInvariant() switch
                {
                    "audiobook" => 2,
                    "ebook" => 3,
                    "physical" => 1,
                    _ => 0
                };
            }

            TryGetProviderAlias(edition.ProviderIds, out var hcEditionId, "hc", "hardcover");
            TryGetProviderAlias(edition.ProviderIds, out var grEditionId, "gr", "goodreads");
            TryGetProviderAlias(edition.ProviderIds, out var azEditionId, "az", "amazon", "audible");
            TryGetProviderAlias(edition.ProviderIds, out var olEditionId, "ol", "openlibrary", "openLibrary");
            TryGetProviderAlias(edition.ProviderIds, out var gbEditionId, "gb", "googlebooks", "googleBooks");

            string rawHcEditionId = null;
            if (!string.IsNullOrWhiteSpace(hcEditionId))
            {
                rawHcEditionId = hcEditionId.Trim();
                if (rawHcEditionId.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase))
                {
                    rawHcEditionId = rawHcEditionId.Substring("hc:edition:".Length);
                }
                else
                {
                    rawHcEditionId = ProviderIdHelper.StripPrefix(rawHcEditionId);
                }
            }

            edition.HardcoverEditionId ??= rawHcEditionId;
            edition.GoodreadsEditionId ??= ProviderIdHelper.StripPrefix(grEditionId);
            edition.Asin ??= ProviderIdHelper.StripPrefix(azEditionId);
            edition.OpenLibraryEditionId ??= ProviderIdHelper.StripPrefix(olEditionId);
            edition.GoogleBooksEditionId ??= ProviderIdHelper.StripPrefix(gbEditionId);

            var explicitEditionId = edition.Id?.Trim();
            if (explicitEditionId?.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase) == true)
            {
                edition.Id = $"hc:edition:{explicitEditionId.Substring("hc:edition:".Length)}";
                edition.HardcoverEditionId ??= explicitEditionId.Substring("hc:edition:".Length);
            }
            else if (explicitEditionId?.Contains(":") == true)
            {
                edition.Id = ProviderIdHelper.Normalize(explicitEditionId, defaultPrefix: null);
            }
            else if (!string.IsNullOrWhiteSpace(rawHcEditionId))
            {
                edition.Id = $"hc:edition:{rawHcEditionId}";
            }
            else if (!string.IsNullOrWhiteSpace(grEditionId))
            {
                edition.Id = ProviderIdHelper.Canonicalize(grEditionId, "gr");
            }
            else if (!string.IsNullOrWhiteSpace(azEditionId))
            {
                edition.Id = ProviderIdHelper.Canonicalize(azEditionId, "az");
            }
            else if (!string.IsNullOrWhiteSpace(olEditionId))
            {
                edition.Id = ProviderIdHelper.Canonicalize(olEditionId, "ol");
            }
            else
            {
                throw new BookInfoException("Legacy V5 edition contained no provider-owned id");
            }
        }

        private List<Author> MapV5WorkAuthors(IEnumerable<V5Resource.V5Author> authors)
        {
            return (authors ?? Enumerable.Empty<V5Resource.V5Author>())
                .Where(author => author != null)
                .OrderBy(author => string.Equals(author.Role, "primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(author => author.Name, StringComparer.OrdinalIgnoreCase)
                .Select(author => MapV5Author(author, _logger))
                .ToList();
        }

        private static IEnumerable<string> EnumerateProviderAliases(Dictionary<string, List<string>> providerIds)
        {
            if (providerIds == null)
            {
                yield break;
            }

            foreach (var values in providerIds.Values)
            {
                if (values == null)
                {
                    continue;
                }

                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        yield return value.Trim();
                    }
                }
            }
        }

        private static bool TryGetProviderAlias(Dictionary<string, List<string>> providerIds, out string value, params string[] keys)
        {
            value = null;

            if (providerIds == null || keys == null || keys.Length == 0)
            {
                return false;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!providerIds.TryGetValue(key, out var values) || values == null)
                {
                    continue;
                }

                value = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetLegacyProviderId(Dictionary<string, string> providerIds, out string value, params string[] keys)
        {
            value = null;

            if (providerIds == null || keys == null || keys.Length == 0)
            {
                return false;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (providerIds.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                {
                    value = value.Trim();
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static IEnumerable<string> EnumerateV5ProviderIds(Dictionary<string, object> providerIds)
        {
            if (providerIds == null)
            {
                yield break;
            }

            foreach (var raw in providerIds.Values)
            {
                foreach (var value in EnumerateProviderValues(raw))
                {
                    yield return value;
                }
            }
        }

        private static IEnumerable<string> EnumerateProviderValues(object raw)
        {
            switch (raw)
            {
                case null:
                    yield break;
                case string s when !string.IsNullOrWhiteSpace(s):
                    yield return s.Trim();
                    yield break;
                case int i:
                    yield return i.ToString();
                    yield break;
                case long l:
                    yield return l.ToString();
                    yield break;
                case JValue jv when !string.IsNullOrWhiteSpace(jv.ToString()):
                    yield return jv.ToString().Trim();
                    yield break;
                case JArray ja:
                    foreach (var token in ja)
                    {
                        var value = token?.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            yield return value.Trim();
                        }
                    }
                    yield break;
                case IEnumerable<object> values:
                    foreach (var value in values)
                    {
                        var coerced = value?.ToString();
                        if (!string.IsNullOrWhiteSpace(coerced))
                        {
                            yield return coerced.Trim();
                        }
                    }
                    yield break;
                default:
                    yield break;
            }
        }

        private static void AddRemoteProviderId(ISet<string> providerIds, string providerId)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                providerIds?.Add(providerId.Trim());
            }
        }

        private static string EnsureCanonicalProviderId(string rawOrPrefixed, string expectedPrefix)
        {
            return ProviderIdHelper.Canonicalize(rawOrPrefixed, expectedPrefix);
        }

        private static List<EditionChapter> NormalizeV5AuthorEditionChapters(IEnumerable<V5.V5EditionChapter> chapters)
        {
            return chapters?
                .Where(c => c != null)
                .Select(c => new EditionChapter
                {
                    Title = c.Title,
                    StartOffsetMs = c.StartOffsetMs,
                    StartOffsetSec = c.StartOffsetSec,
                    LengthMs = c.LengthMs
                })
                .ToList() ?? new List<EditionChapter>();
        }

        private Author MapV5Author(V5Resource.V5Author v5Author, Logger logger)
        {
            var born = v5Author.BirthDate.ToValidAuthorDate();
            var died = v5Author.DeathDate.ToValidAuthorDate();
            var author = new Author
            {
                Name = v5Author.Name?.CleanSpaces() ?? "",
                Overview = v5Author.Biography,
                TitleSlug = null,
                Born = born,
                Died = died,
                Status = AuthorExtensions.GetLifeStatus(died)
            };

            // `providerIds` is the legacy primary/scalar map. `providerIdsAll`
            // is for alias matching only and must not overwrite primary fields.
            var primaryProviderIds = v5Author.LegacyProviderIds ?? v5Author.ProviderIds;
            if (primaryProviderIds != null)
            {
                foreach (var providerId in primaryProviderIds)
                {
                    var value = EnumerateProviderValues(providerId.Value).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        SetAuthorProviderIdFromKey(author, providerId.Key, value);
                    }
                }
            }

            var remoteProviderIds = EnumerateV5ProviderIds(v5Author.ProviderIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            AddRemoteProviderId(remoteProviderIds, author.GoodreadsAuthorId);
            AddRemoteProviderId(remoteProviderIds, author.HardcoverAuthorId);
            AddRemoteProviderId(remoteProviderIds, author.OpenLibraryAuthorId);
            AddRemoteProviderId(remoteProviderIds, author.GoogleBooksAuthorId);
            AddRemoteProviderId(remoteProviderIds, author.AudnexusAuthorId);
            author.RemoteProviderIds = remoteProviderIds.Count > 0 ? remoteProviderIds : null;

            author.TitleSlug = GetAuthorMetadataKey(author);

            // Set name variants
            author.SortName = author.Name.ToLower();
            author.NameLastFirst = author.Name.ToLastFirst();
            author.SortNameLastFirst = author.NameLastFirst.ToLower();

            // Add author image
            var enhancedV5AuthorImageUrl = EnhanceGoodreadsImageUrl(v5Author.ImageUrl);
            if (enhancedV5AuthorImageUrl.IsValidHttpUrl() &&
                !MediaCoverRendition.IsKnownPlaceholderImageUrl(enhancedV5AuthorImageUrl))
            {
                author.Images.Add(new MediaCover.MediaCover
                {
                    Url = enhancedV5AuthorImageUrl,
                    CoverType = MediaCoverTypes.Poster
                });
            }

            return author;
        }

        private static bool LooksLikeV5WorkKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            key = key.Trim();
            if (key.Length < 2)
            {
                return false;
            }

            if (key[0] != 'W' && key[0] != 'w')
            {
                return false;
            }

            for (var i = 1; i < key.Length; i++)
            {
                if (!char.IsDigit(key[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void SetAuthorProviderIdFromKey(Author author, string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            switch (key.ToLowerInvariant())
            {
                case "goodreads":
                case "gr":
                    author.GoodreadsAuthorId = EnsureCanonicalProviderId(value, "gr");
                    break;
                case "hardcover":
                case "hc":
                    author.HardcoverAuthorId = EnsureCanonicalProviderId(value, "hc");
                    break;
                case "openlibrary":
                case "ol":
                    author.OpenLibraryAuthorId = EnsureCanonicalProviderId(value, "ol");
                    break;
                case "googlebooks":
                case "gb":
                    author.GoogleBooksAuthorId = EnsureCanonicalProviderId(value, "gb");
                    break;
                case "amazon":
                case "audible":
                case "audnexus":
                case "az":
                    author.AudnexusAuthorId = EnsureCanonicalProviderId(value, "az");
                    break;
            }
        }
    }

}
