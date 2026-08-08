using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Chaptarr.Http.Middleware
{
    public class BufferingMiddleware
    {
        private const int BufferThresholdBytes = 64 * 1024; // 64 KiB in-memory, then spool to disk
        private const long BufferLimitBytes = 1024 * 1024; // 1 MiB max (prevents disk/memory DoS)

        private readonly RequestDelegate _next;
        private readonly IReadOnlyList<PathString> _pathPrefixes;

        public BufferingMiddleware(RequestDelegate next, IReadOnlyList<PathString> pathPrefixes)
        {
            _next = next;
            _pathPrefixes = pathPrefixes;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (_pathPrefixes != null &&
                (HttpMethods.IsPost(context.Request.Method) ||
                 HttpMethods.IsPut(context.Request.Method) ||
                 HttpMethods.IsPatch(context.Request.Method)))
            {
                foreach (var prefix in _pathPrefixes)
                {
                    if (context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Request.EnableBuffering(BufferThresholdBytes, BufferLimitBytes);
                        break;
                    }
                }
            }

            await _next(context);
        }
    }
}
