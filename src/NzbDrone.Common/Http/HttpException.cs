using System;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Common.Http
{
    public class HttpException : Exception
    {
        public HttpRequest Request { get; private set; }
        public HttpResponse Response { get; private set; }

        public HttpException(HttpRequest request, HttpResponse response, string message)
            : base(message)
        {
            Request = request;
            Response = response;
        }

        public HttpException(HttpRequest request, HttpResponse response)
            : this(request, response, string.Format("HTTP request failed: [{0}:{1}] [{2}] at [{3}]", (int)response.StatusCode, response.StatusCode, request.Method, CleanseLogMessage.Cleanse(request.GetLogUrl())))
        {
        }

        public HttpException(HttpResponse response)
            : this(response.Request, response)
        {
        }
    }
}
