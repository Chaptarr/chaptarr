using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Chaptarr.Http.Middleware
{
    public class ServarrMediaTypeScopeMiddleware
    {
        private static readonly PathString EbookPrefix = new PathString("/ebook");
        private static readonly PathString EbooksPrefix = new PathString("/ebooks");
        private static readonly PathString AudiobookPrefix = new PathString("/audiobook");
        private static readonly PathString AudiobooksPrefix = new PathString("/audiobooks");
        private static readonly PathString AudioboksPrefix = new PathString("/audioboks");

        private readonly RequestDelegate _next;

        public ServarrMediaTypeScopeMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (TryReadarrFacadeScope(context) ||
                TryScope(context, EbookPrefix, "ebook", dialect: "hc") ||
                TryScope(context, EbooksPrefix, "ebook", dialect: "hc") ||
                TryScope(context, AudiobookPrefix, "audiobook", dialect: "hc") ||
                TryScope(context, AudiobooksPrefix, "audiobook", dialect: "hc") ||
                TryScope(context, AudioboksPrefix, "audiobook", dialect: "hc"))
            {
                // Scoped successfully.
            }

            await _next(context);
        }

        private static bool TryReadarrFacadeScope(HttpContext context)
        {
            foreach (var dialect in new[] { "hc", "gr" })
            {
                if (TryScope(context, new PathString($"/readarr/{dialect}/ebook"), "ebook", dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/ebooks"), "ebook", dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/audiobook"), "audiobook", dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/audiobooks"), "audiobook", dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/audioboks"), "audiobook", dialect))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryScope(HttpContext context, PathString scopePrefix, string mediaType, string dialect = null)
        {
            if (!context.Request.Path.StartsWithSegments(scopePrefix, out var remaining))
            {
                return false;
            }

            // Only scope API paths (Seerr/Servarr will call e.g. /ebook/api/v1/...).
            if (!remaining.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            context.Request.Path = remaining;
            context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext(dialect ?? "hc", mediaType, scopePrefix.Value);

            // Inject mediaType query param unless explicitly provided.
            if (!context.Request.Query.ContainsKey("mediaType"))
            {
                var current = context.Request.QueryString.Value ?? string.Empty;
                context.Request.QueryString = string.IsNullOrEmpty(current)
                    ? new QueryString($"?mediaType={mediaType}")
                    : new QueryString($"{current}&mediaType={mediaType}");
            }

            return true;
        }
    }
}
