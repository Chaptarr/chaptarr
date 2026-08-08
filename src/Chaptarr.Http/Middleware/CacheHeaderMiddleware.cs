using System.Threading.Tasks;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Http;

namespace Chaptarr.Http.Middleware
{
    public class CacheHeaderMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ICacheableSpecification _cacheableSpecification;

        public CacheHeaderMiddleware(RequestDelegate next, ICacheableSpecification cacheableSpecification)
        {
            _next = next;
            _cacheableSpecification = cacheableSpecification;
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Method != HttpMethods.Options)
            {
                context.Response.OnStarting(() =>
                {
                    ApplyCacheHeaders(context);
                    return Task.CompletedTask;
                });
            }

            return _next(context);
        }

        public void ApplyCacheHeaders(HttpContext context)
        {
            var headers = context.Response.Headers;

            if (context.Request.IsApiRequest())
            {
                headers.DisableCacheForApi();
                return;
            }

            if (context.Response.StatusCode != StatusCodes.Status200OK &&
                context.Response.StatusCode != StatusCodes.Status304NotModified)
            {
                headers.DisableCache();
                return;
            }

            var contentType = context.Response.ContentType;
            if (contentType != null && contentType.StartsWith("text/html", System.StringComparison.OrdinalIgnoreCase))
            {
                headers.DisableCache();
                return;
            }

            // Some non-API endpoints own a stricter cache policy than the generic
            // static-resource rules. In particular, MediaCoverProxy deliberately
            // uses a private one-day browser cache. Do not replace that explicit
            // policy with no-store and force virtualized cards to refetch on remount.
            if (headers.ContainsKey("Cache-Control"))
            {
                return;
            }

            if (_cacheableSpecification.IsCacheable(context.Request))
            {
                headers.EnableCache();
            }
            else
            {
                headers.DisableCache();
            }
        }
    }
}
