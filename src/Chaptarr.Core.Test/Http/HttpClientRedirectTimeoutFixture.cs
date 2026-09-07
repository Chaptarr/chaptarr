using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Http;
using Chaptarr.Core.Test.Indexers;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpClientRedirectTimeoutFixture
    {
        [Test]
        public async Task should_follow_redirects_when_auto_redirect_is_enabled()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://source.example/start",
                request => Task.FromResult(BuildResponse(
                    request,
                    HttpStatusCode.Found,
                    string.Empty,
                    new Dictionary<string, string> { ["Location"] = "/final" })));
            transport.AddRoute(
                url => url == "https://source.example/final",
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK, "done")));

            var client = transport.CreateClient();
            var response = await client.ExecuteAsync(new HttpRequest("https://source.example/start")
            {
                AllowAutoRedirect = true
            });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content, Is.EqualTo("done"));
            Assert.That(transport.RequestedUrls, Is.EqualTo(new[]
            {
                "https://source.example/start",
                "https://source.example/final"
            }));
        }

        [Test]
        public void should_surface_timeout_failures_as_web_exceptions()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://source.example/slow",
                request => throw new WebException("Http request timed out", WebExceptionStatus.Timeout));

            var client = transport.CreateClient();

            var exception = Assert.ThrowsAsync<WebException>(async () => await client.ExecuteAsync(new HttpRequest("https://source.example/slow")
            {
                RequestTimeout = TimeSpan.FromMilliseconds(25)
            }));

            Assert.That(exception.Status, Is.EqualTo(WebExceptionStatus.Timeout));
        }

        private static HttpResponse BuildResponse(HttpRequest request, HttpStatusCode statusCode, string content, IDictionary<string, string> headers = null)
        {
            var httpHeaders = new HttpHeader();
            if (headers != null)
            {
                foreach (var pair in headers)
                {
                    httpHeaders[pair.Key] = pair.Value;
                }
            }

            httpHeaders.ContentType = "text/html; charset=utf-8";
            return new HttpResponse(request, httpHeaders, content, statusCode);
        }
    }
}
