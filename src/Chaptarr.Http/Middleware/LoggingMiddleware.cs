using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Http;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace Chaptarr.Http.Middleware
{
    public class LoggingMiddleware
    {
        private static readonly Logger _loggerHttp = LogManager.GetLogger("Http");
        private static readonly Logger _loggerApi = LogManager.GetLogger("Api");
        private static int _requestSequenceID;

        private readonly RequestDelegate _next;

        public LoggingMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var isHttpTraceEnabled = _loggerHttp.IsTraceEnabled;
            var isApiDebugEnabled = _loggerApi.IsDebugEnabled && context.Request.IsApiRequest();

            if (!isHttpTraceEnabled && !isApiDebugEnabled)
            {
                await _next(context);
                return;
            }

            var id = Interlocked.Increment(ref _requestSequenceID);
            var startTime = DateTime.UtcNow;

            context.Items["ApiRequestSequenceID"] = id;
            context.Items["ApiRequestStartTime"] = startTime;

            string reqPath;
            try
            {
                reqPath = GetRequestPathAndQuery(context.Request);
            }
            catch
            {
                reqPath = context.Request?.Path.Value ?? "/";
            }

            if (isHttpTraceEnabled)
            {
                _loggerHttp.Trace("Req: {0} [{1}] {2} (from {3})", id, context.Request.Method, reqPath, GetOrigin(context));
            }

            try
            {
                await _next(context);
            }
            finally
            {
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                if (isHttpTraceEnabled)
                {
                    _loggerHttp.Trace("Res: {0} [{1}] {2}: {3}.{4} ({5} ms)", id, context.Request.Method, reqPath, context.Response.StatusCode, (HttpStatusCode)context.Response.StatusCode, (int)duration.TotalMilliseconds);
                }

                if (isApiDebugEnabled)
                {
                    _loggerApi.Debug("[{0}] {1}: {2}.{3} ({4} ms)", context.Request.Method, reqPath, context.Response.StatusCode, (HttpStatusCode)context.Response.StatusCode, (int)duration.TotalMilliseconds);
                }
            }
        }

        private static string SanitizeUserAgent(string userAgent)
        {
            if (userAgent.IsNullOrWhiteSpace())
            {
                return null;
            }

            const int maxLength = 256;
            var builder = new StringBuilder(Math.Min(userAgent.Length, maxLength));

            foreach (var ch in userAgent)
            {
                if (builder.Length >= maxLength)
                {
                    break;
                }

                if (ch == '\r' || ch == '\n' || ch == '\t')
                {
                    builder.Append(' ');
                    continue;
                }

                if (char.IsControl(ch))
                {
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString().Trim();
        }

        private static string GetRequestPathAndQuery(HttpRequest request)
        {
            string pathAndQuery;

            if (request.QueryString.Value.IsNotNullOrWhiteSpace() && request.QueryString.Value != "?")
            {
                pathAndQuery = string.Concat(request.Path, request.QueryString);
            }
            else
            {
                pathAndQuery = request.Path;
            }

            return SensitiveDataSanitizer.SanitizeUrl(pathAndQuery);
        }

        private static string GetOrigin(HttpContext context)
        {
            var userAgent = SanitizeUserAgent(context.Request.Headers["User-Agent"].ToString());

            if (userAgent.IsNullOrWhiteSpace())
            {
                return context.GetRemoteIP();
            }
            else
            {
                return $"{context.GetRemoteIP()} {userAgent}";
            }
        }
    }
}
