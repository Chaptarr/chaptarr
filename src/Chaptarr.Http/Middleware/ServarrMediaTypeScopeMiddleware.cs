using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NzbDrone.Core.Configuration;

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

        public async Task InvokeAsync(HttpContext context, IConfigService configService)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (TryReadarrFacadeScope(context, configService) ||
                TryScope(context, EbookPrefix, "ebook", configService, dialect: "hc") ||
                TryScope(context, EbooksPrefix, "ebook", configService, dialect: "hc") ||
                TryScope(context, AudiobookPrefix, "audiobook", configService, dialect: "hc") ||
                TryScope(context, AudiobooksPrefix, "audiobook", configService, dialect: "hc") ||
                TryScope(context, AudioboksPrefix, "audiobook", configService, dialect: "hc"))
            {
                // Scoped successfully.
            }
            else if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                     !IsNativeChaptarrRequest(context) &&
                     HasBookshelfCompatibilityProfile(configService))
            {
                // A migrated BookshelfNG installation keeps its old API address. On that
                // address bare numeric IDs retain the provider meaning they had before the
                // conversion; Chaptarr's own prefixed IDs remain valid as well.
                var dialect = configService.BookshelfNGMigrationProfile.ProviderDialect;
                context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext(
                    dialect,
                    mediaType: null,
                    prefix: string.Empty,
                    isBookshelfCompatibility: true);
            }

            await _next(context);
        }

        private static bool IsNativeChaptarrRequest(HttpContext context)
        {
            return string.Equals(
                       context.Request.Headers["X-ChaptarrNG-Client"].ToString(),
                       "native",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       context.Request.Query["chaptarrngClient"].ToString(),
                       "native",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadarrFacadeScope(HttpContext context, IConfigService configService)
        {
            foreach (var dialect in new[] { "hc", "gr" })
            {
                if (TryScope(context, new PathString($"/readarr/{dialect}/ebook"), "ebook", configService, dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/ebooks"), "ebook", configService, dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/audiobook"), "audiobook", configService, dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/audiobooks"), "audiobook", configService, dialect) ||
                    TryScope(context, new PathString($"/readarr/{dialect}/audioboks"), "audiobook", configService, dialect))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryScope(
            HttpContext context,
            PathString scopePrefix,
            string mediaType,
            IConfigService configService,
            string dialect = null)
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
            var acceptsBookshelfLegacyBookIds = configService?.BookshelfNGDatabaseMigrated == true &&
                                                configService.BookshelfNGMigrationProfile?.IsCompatible == true &&
                                                !IsNativeChaptarrRequest(context);
            context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext(
                dialect ?? "hc",
                mediaType,
                scopePrefix.Value,
                acceptsBookshelfLegacyBookIds: acceptsBookshelfLegacyBookIds);

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

        private static bool HasBookshelfCompatibilityProfile(IConfigService configService)
        {
            return configService?.BookshelfNGDatabaseMigrated == true &&
                   configService.BookshelfNGMigrationProfile?.IsCompatible == true;
        }
    }
}
