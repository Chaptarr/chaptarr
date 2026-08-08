using System;
using Microsoft.AspNetCore.Http;
using NzbDrone.Common.EnvironmentInfo;

namespace Chaptarr.Http.Middleware
{
    public interface ICacheableSpecification
    {
        bool IsCacheable(HttpRequest request);
    }

    public class CacheableSpecification : ICacheableSpecification
    {
        public bool IsCacheable(HttpRequest request)
        {
            if (!RuntimeInfo.IsProduction)
            {
                return false;
            }

            if (request.Path.StartsWithSegments("/api", StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }

            if (request.Path.StartsWithSegments("/signalr", StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }

            var path = request.Path.Value ?? "";

            if (path.EndsWith("/index.js"))
            {
                return false;
            }

            if (path.EndsWith("/initialize.json"))
            {
                return false;
            }

            if (path.StartsWith("/feed", StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }

            if ((path.StartsWith("/logfile", StringComparison.CurrentCultureIgnoreCase) ||
                path.StartsWith("/updatelogfile", StringComparison.CurrentCultureIgnoreCase)) &&
                path.EndsWith(".txt", StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }

            // Cache only when the request includes an explicit version signal (cache breaker).
            // - `h`: our production cache-breaker hash
            // - `cb`: webpack cache-buster (from index.html)
            // - `lastWrite`: MediaCover URL versioning
            return request.Query.ContainsKey("h") ||
                   request.Query.ContainsKey("cb") ||
                   request.Query.ContainsKey("lastWrite");
        }
    }
}
