using System.Net;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Indexers;
using NUnit.Framework;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpClientCrossOriginRedirectSecurityFixture
    {
        private const string SourceUrl = "https://configured-service.example/api/resource";
        private const string RedirectUrl = "https://redirect-target.example/receive";

        [Test]
        public void should_not_forward_api_key_headers_to_another_origin()
        {
            var transport = CreateRedirectTransport();
            var request = CreateRequest();
            request.Headers.Add("X-Api-Key", "secret");

            var exception = Assert.ThrowsAsync<WebException>(async () => await transport.CreateClient().ExecuteAsync(request));

            Assert.That(exception.Status, Is.EqualTo(WebExceptionStatus.ProtocolError));
            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl }));
        }

        [Test]
        public void should_not_forward_credentials_to_another_origin()
        {
            var transport = CreateRedirectTransport();
            var request = CreateRequest();
            request.Credentials = new NetworkCredential("reader", "secret");

            Assert.ThrowsAsync<WebException>(async () => await transport.CreateClient().ExecuteAsync(request));

            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl }));
        }

        [Test]
        public void should_not_forward_cookies_to_another_origin()
        {
            var transport = CreateRedirectTransport();
            var request = CreateRequest();
            request.Cookies.Add("session", "secret");

            Assert.ThrowsAsync<WebException>(async () => await transport.CreateClient().ExecuteAsync(request));

            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl }));
        }

        [Test]
        public void should_not_forward_request_bodies_to_another_origin()
        {
            var transport = CreateRedirectTransport();
            var request = CreateRequest();
            request.SetContent("{\"password\":\"secret\"}");

            Assert.ThrowsAsync<WebException>(async () => await transport.CreateClient().ExecuteAsync(request));

            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl }));
        }

        [Test]
        public void should_not_send_redirect_destination_user_info_to_another_origin()
        {
            var transport = new DirectDownloadTestHttp();
            const string destinationUrl = "https://reader:secret@redirect-target.example/receive";
            transport.AddRoute(
                url => url == SourceUrl,
                request => Task.FromResult(BuildRedirectResponse(request, destinationUrl)));
            transport.AddRoute(
                url => url == destinationUrl,
                request => Task.FromResult(new HttpResponse(request, new HttpHeader(), new byte[0])));

            var exception = Assert.ThrowsAsync<WebException>(async () => await transport.CreateClient().ExecuteAsync(CreateRequest()));

            Assert.That(exception.Status, Is.EqualTo(WebExceptionStatus.ProtocolError));
            Assert.That(exception.Message, Does.Not.Contain("reader"));
            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl }));
        }

        [Test]
        public async Task should_follow_public_cross_origin_redirects_without_sensitive_request_data()
        {
            var transport = CreateRedirectTransport();

            var response = await transport.CreateClient().ExecuteAsync(CreateRequest());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl, RedirectUrl }));
        }

        [Test]
        public async Task should_follow_cross_origin_redirects_with_unrelated_custom_headers()
        {
            var transport = CreateRedirectTransport();
            var request = CreateRequest();
            request.Headers.Add("X-Request-Trace", "trace-id");

            var response = await transport.CreateClient().ExecuteAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(transport.RequestedUrls, Is.EqualTo(new[] { SourceUrl, RedirectUrl }));
        }

        [Test]
        public async Task should_follow_same_origin_redirects_with_sensitive_request_data()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == SourceUrl,
                request => Task.FromResult(BuildRedirectResponse(request, "https://configured-service.example/receive")));
            transport.AddRoute(
                url => url == "https://configured-service.example/receive",
                request => Task.FromResult(new HttpResponse(request, new HttpHeader(), new byte[0])));
            var request = CreateRequest();
            request.Headers.Add("X-Api-Key", "secret");

            var response = await transport.CreateClient().ExecuteAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(transport.RequestedUrls, Is.EqualTo(new[]
            {
                SourceUrl,
                "https://configured-service.example/receive"
            }));
        }

        private static DirectDownloadTestHttp CreateRedirectTransport()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == SourceUrl,
                request => Task.FromResult(BuildRedirectResponse(request, RedirectUrl)));
            transport.AddRoute(
                url => url == RedirectUrl,
                request => Task.FromResult(new HttpResponse(request, new HttpHeader(), new byte[0])));
            return transport;
        }

        private static HttpRequest CreateRequest()
        {
            return new HttpRequest(SourceUrl)
            {
                AllowAutoRedirect = true
            };
        }

        private static HttpResponse BuildRedirectResponse(HttpRequest request, string destinationUrl)
        {
            var headers = new HttpHeader
            {
                { "Location", destinationUrl }
            };
            return new HttpResponse(request, headers, new byte[0], HttpStatusCode.TemporaryRedirect);
        }
    }
}
