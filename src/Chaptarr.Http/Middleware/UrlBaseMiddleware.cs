using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NzbDrone.Common.Extensions;

namespace Chaptarr.Http.Middleware
{
    public class UrlBaseMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _urlBase;

        public UrlBaseMiddleware(RequestDelegate next, string urlBase)
        {
            _next = next;
            _urlBase = urlBase;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (_urlBase.IsNotNullOrWhiteSpace() && context.Request.PathBase.Value.IsNullOrWhiteSpace())
            {
                // Build a single-rooted local path. Request.Path is user-controlled and may
                // start with slashes that would otherwise turn the Location into a network path.
                var path = $"{_urlBase.TrimEnd('/')}/{context.Request.Path.Value?.TrimStart('/', '\\')}";
                path = "/" + path.TrimStart('/', '\\');
                // Let ASP.NET Core enforce local-only redirect semantics at the response boundary.
                await Results.LocalRedirect(
                    $"{path}{context.Request.QueryString}",
                    permanent: false,
                    preserveMethod: true).ExecuteAsync(context);

                return;
            }

            await _next(context);
        }
    }
}
