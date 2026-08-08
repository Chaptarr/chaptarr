using System.Text;
using System.Threading.Tasks;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Http;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;

namespace Chaptarr.Http.Middleware
{
    public class StartingUpMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IRuntimeInfo _runtimeInfo;
        private static readonly string MESSAGE = "Chaptarr is starting up, please try again later";
        private static readonly string RESET_MESSAGE = "Chaptarr is restarting after a factory reset, please try again shortly";

        public StartingUpMiddleware(RequestDelegate next, IRuntimeInfo runtimeInfo)
        {
            _next = next;
            _runtimeInfo = runtimeInfo;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // The factory reset drops the schema while this process is still alive; answering
            // anything but 503 here would execute queries against a wiped database.
            if (FactoryResetState.IsResetting)
            {
                await WriteUnavailable(context, RESET_MESSAGE);
                return;
            }

            if (_runtimeInfo.IsStarting)
            {
                await WriteUnavailable(context, MESSAGE);
                return;
            }

            await _next(context);
        }

        private static async Task WriteUnavailable(HttpContext context, string message)
        {
            var isJson = context.Request.IsApiRequest();
            var body = isJson ? STJson.ToJson(new { ErrorMessage = message }) : message;
            var bytes = Encoding.UTF8.GetBytes(body);

            context.Response.StatusCode = 503;
            context.Response.ContentType = isJson ? "application/json" : "text/plain";
            await context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}
