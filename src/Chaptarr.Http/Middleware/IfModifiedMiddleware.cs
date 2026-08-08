using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Chaptarr.Http.Middleware
{
    public class IfModifiedMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ICacheableSpecification _cacheableSpecification;
        private readonly IContentTypeProvider _mimeTypeProvider;

        public IfModifiedMiddleware(RequestDelegate next, ICacheableSpecification cacheableSpecification)
        {
            _next = next;
            _cacheableSpecification = cacheableSpecification;

            _mimeTypeProvider = new FileExtensionContentTypeProvider();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if ((context.Request.Method == HttpMethods.Get || context.Request.Method == HttpMethods.Head) &&
                _cacheableSpecification.IsCacheable(context.Request) &&
                context.Request.Headers["If-Modified-Since"].Any())
            {
                context.Response.StatusCode = 304;

                if (!_mimeTypeProvider.TryGetContentType(context.Request.Path.ToString(), out var mimeType))
                {
                    mimeType = "application/octet-stream";
                }

                context.Response.ContentType = mimeType;

                return;
            }

            await _next(context);
        }
    }
}
