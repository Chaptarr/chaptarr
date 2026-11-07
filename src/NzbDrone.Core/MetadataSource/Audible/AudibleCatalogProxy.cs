using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource.Audible.Resources;

namespace NzbDrone.Core.MetadataSource.Audible
{
    public interface IAudibleCatalogProxy
    {
        AudibleCatalogBookResource GetBookInfo(string asin, bool useCache = true);
        List<AudibleCatalogBookResource> SearchBooks(string query, bool useCache = true);
    }

    public class AudibleCatalogProxy : IAudibleCatalogProxy
    {
        private const int CACHE_TTL_HOURS = 24;
        private const string SEARCH_RESPONSE_GROUPS = "product_desc,contributors,media,product_attrs";
        private static readonly string[] AudibleUserAgents =
        {
            "Mozilla/5.0 (Linux; Android 14; Pixel 8 Pro Build/UQ1A.240205.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/125.0.6422.165 Mobile Safari/537.36",
            "Mozilla/5.0 (Linux; Android 14; SM-S928U Build/UP1A.231005.007; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/125.0.6422.165 Mobile Safari/537.36",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.6422.165 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
            "Mozilla/5.0 (X11; Linux x86_64; rv:126.0) Gecko/20100101 Firefox/126.0"
        };

        private readonly ICachedHttpResponseService _cachedHttpClient;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public AudibleCatalogProxy(ICachedHttpResponseService cachedHttpClient,
                                   Logger logger,
                                   IConfigService configService)
        {
            _cachedHttpClient = cachedHttpClient;
            _logger = logger;
            _configService = configService;
        }

        public AudibleCatalogBookResource GetBookInfo(string asin, bool useCache = true)
        {
            try
            {
                _logger.DebugSafe("Getting book info from Audible catalog for ASIN: {0}", asin);

                var marketplace = AudibleMarketplaceOptions.Get(_configService?.AudibleMarketplace);
                var httpRequest = CreateRequestBuilder(marketplace).Create()
                    .SetSegment("route", $"1.0/catalog/products/{asin}")
                    .AddQueryParam("response_groups", SEARCH_RESPONSE_GROUPS)
                    .Build();

                ApplyAudibleHeaders(httpRequest, marketplace);
                httpRequest.AllowAutoRedirect = true;
                httpRequest.SuppressHttpError = true;

                var httpResponse = _cachedHttpClient.Get(
                    httpRequest,
                    useCache,
                    TimeSpan.FromHours(CACHE_TTL_HOURS),
                    candidate => Json.Deserialize<AudibleCatalogProductResponse>(candidate.Content)?.Product is { } product &&
                        !string.IsNullOrWhiteSpace(product.Asin) &&
                        !string.IsNullOrWhiteSpace(product.Title));

                if (httpResponse.HasHttpError)
                {
                    if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.DebugSafe("Book not found in Audible catalog: {0}", asin);
                        return null;
                    }

                    throw new AudibleCatalogException(httpResponse.StatusCode, $"Audible catalog HTTP {httpResponse.StatusCode}: {httpResponse.Content}");
                }

                var response = Json.Deserialize<AudibleCatalogProductResponse>(httpResponse.Content);
                var bookInfo = MapProduct(response?.Product, marketplace);

                _logger.DebugSafe("Retrieved book info from Audible catalog for: {0} - {1}", asin, bookInfo?.Title);
                return bookInfo;
            }
            catch (Exception ex)
            {
                _logger.ErrorSafe(ex, "Error getting book info from Audible catalog for ASIN: {0}", asin);
                throw new AudibleCatalogException(HttpStatusCode.InternalServerError, $"Failed to get book info: {ex.Message}", ex);
            }
        }

        public List<AudibleCatalogBookResource> SearchBooks(string query, bool useCache = true)
        {
            try
            {
                _logger.DebugSafe("Audiobooks search called with Audible catalog query: {0}, useCache: {1}", query, useCache);

                var sanitizedQuery = SanitizeSearchQuery(query);
                _logger.DebugSafe("Sanitized query: {0}", sanitizedQuery);

                var marketplace = AudibleMarketplaceOptions.Get(_configService?.AudibleMarketplace);
                var httpRequest = CreateRequestBuilder(marketplace).Create()
                    .SetSegment("route", "1.0/catalog/products")
                    .AddQueryParam("keywords", sanitizedQuery)
                    .AddQueryParam("num_results", "20")
                    .AddQueryParam("products_sort_by", "Relevance")
                    .AddQueryParam("response_groups", SEARCH_RESPONSE_GROUPS)
                    .Build();

                ApplyAudibleHeaders(httpRequest, marketplace);
                _logger.Debug("Audible catalog request URL: {0}", httpRequest.Url);

                httpRequest.AllowAutoRedirect = true;
                httpRequest.SuppressHttpError = true;

                _logger.Debug("Making HTTP request to Audible catalog...");
                var httpResponse = _cachedHttpClient.Get(
                    httpRequest,
                    useCache,
                    TimeSpan.FromHours(CACHE_TTL_HOURS),
                    candidate => Json.Deserialize<AudibleCatalogProductsResponse>(candidate.Content)?.Products?.Any(product =>
                        product != null &&
                        !string.IsNullOrWhiteSpace(product.Asin) &&
                        !string.IsNullOrWhiteSpace(product.Title)) == true);
                _logger.Debug("Audible catalog response status: {0}", httpResponse.StatusCode);

                if (httpResponse.HasHttpError)
                {
                    _logger.Warn("Audible catalog search failed with status {0}: {1}", httpResponse.StatusCode, httpResponse.Content);
                    return new List<AudibleCatalogBookResource>();
                }

                var content = httpResponse.Content?.Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.Warn("Audible catalog returned empty response");
                    return new List<AudibleCatalogBookResource>();
                }

                var response = Json.Deserialize<AudibleCatalogProductsResponse>(content);
                var searchResults = response?.Products?
                    .Select(product => MapProduct(product, marketplace))
                    .Where(product => product != null)
                    .ToList();

                _logger.DebugSafe("Found {0} results in Audible catalog search for: {1}", searchResults?.Count ?? 0, query);
                return searchResults ?? new List<AudibleCatalogBookResource>();
            }
            catch (Exception ex)
            {
                _logger.ErrorSafe(ex, "Error searching Audible catalog for: {0}", query);
                return new List<AudibleCatalogBookResource>();
            }
        }

        private IHttpRequestBuilderFactory CreateRequestBuilder(AudibleMarketplaceDescriptor marketplace)
        {
            return new HttpRequestBuilder(marketplace.ApiBaseUrl + "/{route}")
                .Accept(HttpAccept.Json)
                .KeepAlive()
                .CreateFactory();
        }

        private AudibleCatalogBookResource MapProduct(AudibleCatalogProductResource product, AudibleMarketplaceDescriptor marketplace)
        {
            if (product == null || string.IsNullOrWhiteSpace(product.Asin) || string.IsNullOrWhiteSpace(product.Title))
            {
                return null;
            }

            return new AudibleCatalogBookResource
            {
                Asin = product.Asin,
                Title = product.Title,
                Subtitle = product.Subtitle,
                Summary = StripHtml(product.MerchandisingSummary),
                Description = StripHtml(product.MerchandisingSummary),
                BookFormat = product.FormatType,
                ImageUrl = SelectBestImageUrl(product),
                LengthMinutes = product.RuntimeLengthMin,
                Publisher = product.PublisherName,
                Language = product.Language,
                ReleaseDate = product.ReleaseDate ?? product.IssueDate ?? product.PublicationDateTime,
                Link = $"{marketplace.StorefrontBaseUrl}/pd/{product.Asin}",
                Sku = product.Sku,
                SkuGroup = product.SkuLite,
                IsListenable = product.IsListenable,
                ContentType = product.ContentType,
                ContentDeliveryType = product.ContentDeliveryType,
                Authors = product.Authors?
                    .Where(author => !string.IsNullOrWhiteSpace(author?.Name))
                    .Select(author => new AudibleCatalogAuthorResource
                    {
                        Asin = author.Asin,
                        Name = author.Name
                    })
                    .ToList(),
                Narrators = product.Narrators?
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator?.Name))
                    .Select(narrator => new AudibleCatalogNarratorResource
                    {
                        Name = narrator.Name
                    })
                    .ToList()
            };
        }

        private static void ApplyAudibleHeaders(HttpRequest request, AudibleMarketplaceDescriptor marketplace)
        {
            request.Headers.Add("User-Agent", AudibleUserAgents[RandomNumberGenerator.GetInt32(AudibleUserAgents.Length)]);
            request.Headers.Add("Accept", "application/json,text/json,*/*;q=0.8");
            request.Headers.Add("Accept-Language", marketplace.AcceptLanguage);
            request.Headers.Add("Origin", marketplace.StorefrontBaseUrl);
            request.Headers.Add("Referer", marketplace.StorefrontBaseUrl + "/");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-site");
        }

        private string SelectBestImageUrl(AudibleCatalogProductResource product)
        {
            if (product?.ProductImages == null || product.ProductImages.Count == 0)
            {
                return null;
            }

            var numericImage = product.ProductImages
                .Select(pair => new { pair.Value, Parsed = int.TryParse(pair.Key, out var size) ? (int?)size : null })
                .Where(item => item.Parsed.HasValue && !string.IsNullOrWhiteSpace(item.Value))
                .OrderByDescending(item => item.Parsed.Value)
                .FirstOrDefault();

            return numericImage?.Value ??
                   product.ProductImages.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return html;
            }

            var text = Regex.Replace(html, "<.*?>", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private string SanitizeSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return query;
            }

            var sanitized = query
                .Replace("[", " ")
                .Replace("]", " ")
                .Replace("(", " ")
                .Replace(")", " ")
                .Replace("{", " ")
                .Replace("}", " ")
                .Replace("<", " ")
                .Replace(">", " ");

            sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

            if (sanitized.Length > 100)
            {
                var parts = sanitized.Split(' ');
                var result = new List<string>();
                var currentLength = 0;

                foreach (var part in parts)
                {
                    if (currentLength + part.Length + 1 > 100)
                    {
                        break;
                    }

                    result.Add(part);
                    currentLength += part.Length + 1;
                }

                sanitized = string.Join(" ", result);
            }

            return sanitized;
        }
    }
}
