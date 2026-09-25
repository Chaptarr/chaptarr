using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpExceptionFixture
    {
        [Test]
        public void should_sanitize_request_url_in_default_message()
        {
            var request = new HttpRequest("https://example.com/api?apikey=real-secret&query=value");
            var response = new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.Unauthorized);

            var exception = new HttpException(request, response);

            Assert.That(exception.Message, Does.Not.Contain("real-secret"));
            Assert.That(exception.Message, Does.Contain("apikey=(removed)"));
        }

        [Test]
        public void should_keep_http_error_summary_without_appending_full_response_body()
        {
            const string responseBody = "synthetic provider error detail";
            var request = new HttpRequest("https://indexer.example/api?apikey=synthetic-key");
            var headers = new HttpHeader
            {
                ContentType = "application/json"
            };
            var response = new HttpResponse(request, headers, responseBody, HttpStatusCode.BadGateway);

            var responseSummary = response.ToString();
            var exceptionSummary = new HttpException(request, response).ToString();
            var traceBody = CleanseLogMessage.Cleanse($"Response content ({response.ResponseData.Length} bytes): {response.Content}");

            Assert.That(responseSummary, Does.Not.Contain(responseBody));
            Assert.That(responseSummary, Does.Contain("502.BadGateway"));
            Assert.That(responseSummary, Does.Contain($"({response.ResponseData.Length} bytes)"));
            Assert.That(exceptionSummary, Does.Not.Contain(responseBody));
            Assert.That(exceptionSummary, Does.Contain("502:BadGateway"));
            Assert.That(traceBody, Does.Contain(responseBody));
        }
    }
}
