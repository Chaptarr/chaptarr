using System;
using System.Net;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Http
{
    public interface ICachedHttpResponseService
    {
        HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl);
        HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive);
        HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl)
            where T : new();
        HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive)
            where T : new();
    }

    public class CachedHttpResponseService : ICachedHttpResponseService
    {
        private readonly ICachedHttpResponseRepository _repo;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public CachedHttpResponseService(ICachedHttpResponseRepository httpResponseRepository,
                                         IHttpClient httpClient,
                                         Logger logger)
        {
            _repo = httpResponseRepository;
            _httpClient = httpClient;
            _logger = logger;
        }

        public HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl)
        {
            return Get(request, useCache, ttl, null);
        }

        public HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive)
        {
            var cached = _repo.FindByUrl(request.Url.ToString());

            // Historical versions could persist transitional 2xx responses such as
            // 202/null. They are negative discovery state, so remove them instead of
            // allowing them to mask a later positive response until their TTL expires.
            if (cached != null && cached.StatusCode != (int)HttpStatusCode.OK)
            {
                _repo.Delete(cached);
                cached = null;
            }

            if (cached != null && !IsPositiveResponse(
                    new HttpResponse(request, new HttpHeader(), cached.Value, (HttpStatusCode)cached.StatusCode),
                    isPositive))
            {
                _repo.Delete(cached);
                cached = null;
            }

            if (useCache && cached != null && cached.Expiry > DateTime.UtcNow)
            {
                _logger.Trace($"Returning cached response for [GET] {request.Url}");
                _logger.Trace($"[CACHE-HIT] Using cached response for {request.Url} (expires: {cached.Expiry:yyyy-MM-dd HH:mm:ss})");
                var headers = new HttpHeader();
                headers["X-Cache-Status"] = "HIT";
                return new HttpResponse(request, headers, cached.Value, (HttpStatusCode)cached.StatusCode);
            }

            _logger.Trace($"[CACHE-MISS] Fetching fresh data from {request.Url} (useCache: {useCache})");
            var result = _httpClient.Get(request);

            var positiveResponse = result.StatusCode == HttpStatusCode.OK && IsPositiveResponse(result, isPositive);
            var storageDisabled = ResponseDisablesStorage(result);
            var shouldStore = useCache && positiveResponse && !storageDisabled;

            // Preserve an origin marker. Otherwise report whether this response
            // was eligible for local storage, rather than labelling negatives MISS.
            if (result.Headers != null && string.IsNullOrWhiteSpace(result.Headers.GetSingleValue("X-Cache-Status")))
            {
                result.Headers["X-Cache-Status"] = shouldStore ? "MISS" : "BYPASS";
            }

            if (shouldStore)
            {
                if (cached == null)
                {
                    cached = new CachedHttpResponse
                    {
                        Url = request.Url.ToString(),
                    };
                }

                var now = DateTime.UtcNow;

                cached.LastRefresh = now;
                cached.Expiry = now.Add(ttl);
                cached.Value = result.Content;
                cached.StatusCode = (int)result.StatusCode;

                _repo.Upsert(cached);
            }

            return result;
        }

        private bool IsPositiveResponse(HttpResponse response, Func<HttpResponse, bool> isPositive)
        {
            if (isPositive == null)
            {
                return true;
            }

            try
            {
                return isPositive(response);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Cached response positive-result predicate rejected malformed payload");
                return false;
            }
        }

        private static bool ResponseDisablesStorage(HttpResponse response)
        {
            if (response?.Headers == null)
            {
                return false;
            }

            return string.Equals(response.Headers.GetSingleValue("X-Cache-Status"), "BYPASS", StringComparison.OrdinalIgnoreCase) ||
                   ContainsNoStore(response.Headers.GetSingleValue("Cache-Control")) ||
                   ContainsNoStore(response.Headers.GetSingleValue("CDN-Cache-Control")) ||
                   ContainsNoStore(response.Headers.GetSingleValue("Cloudflare-CDN-Cache-Control"));
        }

        private static bool ContainsNoStore(string value)
        {
            return value?.IndexOf("no-store", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl)
            where T : new()
        {
            return Get<T>(request, useCache, ttl, null);
        }

        public HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive)
            where T : new()
        {
            var response = Get(request, useCache, ttl, isPositive);
            return new HttpResponse<T>(response);
        }
    }
}
