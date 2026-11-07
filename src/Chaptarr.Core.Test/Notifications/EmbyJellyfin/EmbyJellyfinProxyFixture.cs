using System;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.EmbyJellyfin;

namespace Chaptarr.Core.Test.Notifications.EmbyJellyfin
{
    [TestFixture]
    public class EmbyJellyfinProxyFixture
    {
        private class CapturingHttpClient : IHttpClient
        {
            public HttpRequest LastRequest { get; private set; }

            public HttpResponse Execute(HttpRequest request)
            {
                LastRequest = request;
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "{}");
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        [TestCase((int)EmbyJellyfinServerType.Emby, "X-Emby-Token")]
        [TestCase((int)EmbyJellyfinServerType.Jellyfin, "Authorization")]
        public void should_refresh_library_using_the_server_auth_header(int serverType, string expectedAuthHeader)
        {
            var httpClient = new CapturingHttpClient();
            var proxy = new EmbyJellyfinProxy(httpClient, LogManager.GetCurrentClassLogger());

            proxy.RefreshLibrary(new EmbyJellyfinSettings
            {
                ServerType = serverType,
                Host = "media.example.com",
                Port = 8096,
                ApiKey = "test-api-key"
            });

            Assert.That(httpClient.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(httpClient.LastRequest.Url.ToString(), Does.EndWith("/Library/Refresh"));
            Assert.That(httpClient.LastRequest.Url.ToString(), Does.Not.Contain("test-api-key"));
            Assert.That(httpClient.LastRequest.Headers[expectedAuthHeader], Is.Not.Empty);

            if (serverType == (int)EmbyJellyfinServerType.Jellyfin)
            {
                Assert.That(httpClient.LastRequest.Headers["Authorization"], Does.StartWith("MediaBrowser "));
                Assert.That(httpClient.LastRequest.Headers["Authorization"], Does.Contain("Token=\"test-api-key\""));
            }
            else
            {
                Assert.That(httpClient.LastRequest.Headers["X-Emby-Token"], Is.EqualTo("test-api-key"));
            }
        }

        [Test]
        public void should_include_url_base_and_use_ssl_for_connection_test()
        {
            var httpClient = new CapturingHttpClient();
            var proxy = new EmbyJellyfinProxy(httpClient, LogManager.GetCurrentClassLogger());

            var failure = proxy.Test(new EmbyJellyfinSettings
            {
                ServerType = (int)EmbyJellyfinServerType.Jellyfin,
                Host = "media.example.com",
                Port = 8920,
                UseSsl = true,
                UrlBase = "/jellyfin",
                ApiKey = "test-api-key"
            });

            Assert.That(failure, Is.Null);
            Assert.That(httpClient.LastRequest.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(httpClient.LastRequest.Url.ToString(), Does.StartWith("https://media.example.com:8920/jellyfin/System/Info"));
        }
    }
}
