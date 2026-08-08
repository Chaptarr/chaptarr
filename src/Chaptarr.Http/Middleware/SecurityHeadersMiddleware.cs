using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Chaptarr.Http.Middleware
{
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        private static string BuildContentSecurityPolicy(HttpContext context)
        {
            // Allow SignalR/websocket connections to the current host without allowing arbitrary ws/wss destinations.
            var host = context?.Request?.Host.ToUriComponent();
            var websocketSources = string.IsNullOrWhiteSpace(host)
                ? string.Empty
                : $" ws://{host} wss://{host}";

            return
                "default-src 'self'; " +
                "base-uri 'self'; " +
                "object-src 'none'; " +
                "frame-ancestors 'self'; " +
                "form-action 'self'; " +
                "img-src 'self' data: blob: https:; " +
                "font-src 'self' data:; " +
                "style-src 'self' 'unsafe-inline'; " +
                "script-src 'self' 'unsafe-inline'; " +
                $"connect-src 'self'{websocketSources}; " +
                "manifest-src 'self';";
        }

        public Task InvokeAsync(HttpContext context)
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                if (!headers.ContainsKey("X-Content-Type-Options"))
                {
                    headers["X-Content-Type-Options"] = "nosniff";
                }

                if (!headers.ContainsKey("X-Frame-Options"))
                {
                    headers["X-Frame-Options"] = "SAMEORIGIN";
                }

                if (!headers.ContainsKey("Referrer-Policy"))
                {
                    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                }

                if (context.Request.IsHttps &&
                    !headers.ContainsKey("Strict-Transport-Security"))
                {
                    headers["Strict-Transport-Security"] = "max-age=15552000";
                }

                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers["Content-Security-Policy"] = BuildContentSecurityPolicy(context);
                }

                return Task.CompletedTask;
            });

            return _next(context);
        }
    }
}
