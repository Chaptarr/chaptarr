using System;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Notifications.Plex.Server;

namespace Chaptarr.Core.Test.Notifications.Plex
{
    [TestFixture]
    public class PlexServerProxyFixture
    {
        private class CapturingHttpClient : IHttpClient
        {
            public HttpRequest LastRequest { get; private set; }
            public string ThrowMessage { get; set; }

            public HttpResponse Execute(HttpRequest request)
            {
                LastRequest = request;

                if (ThrowMessage != null)
                {
                    throw new WebException(ThrowMessage.Replace("{url}", request.Url.ToString()));
                }

                var headers = new HttpHeader
                {
                    ContentType = "application/json"
                };

                // PlexServerProxy.Version parses PlexResponse<PlexIdentity> with MediaContainer.Version.
                const string identityJson = "{\"MediaContainer\":{\"version\":\"1.42.2.10156-f737b826c\"}}";

                return new HttpResponse(request, headers, identityJson);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private class TestConfigServiceProxy : DispatchProxy
        {
            public string PlexClientIdentifier { get; set; } = "test-client-id";

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_PlexClientIdentifier")
                {
                    return PlexClientIdentifier;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        [Test]
        public void should_use_plex_direct_hostname_when_ssl_and_ip_and_suffix_present()
        {
            var httpClient = new CapturingHttpClient();

            var configService = DispatchProxy.Create<IConfigService, TestConfigServiceProxy>();

            var proxy = new PlexServerProxy(httpClient, configService, LogManager.GetCurrentClassLogger());

            var settings = new PlexServerSettings
            {
                Host = "192.0.2.10",
                Port = 32400,
                UseSsl = true,
                PlexDirectSuffix = "00000000000000000000000000000000"
            };

            _ = proxy.Version(settings);

            Assert.That(httpClient.LastRequest, Is.Not.Null);
            Assert.That(httpClient.LastRequest.Url.ToString(), Does.Contain("https://192-0-2-10.00000000000000000000000000000000.plex.direct:32400"));
        }

        [Test]
        public void should_probe_identity_with_requested_timeout()
        {
            var httpClient = new CapturingHttpClient();
            var configService = DispatchProxy.Create<IConfigService, TestConfigServiceProxy>();
            var proxy = new PlexServerProxy(httpClient, configService, LogManager.GetCurrentClassLogger());

            var settings = new PlexServerSettings
            {
                Host = "plex.example.com",
                Port = 32400,
                UseSsl = true,
                AuthToken = "token"
            };

            var result = proxy.CanConnect(settings, TimeSpan.FromSeconds(3), out var message);

            Assert.That(result, Is.True);
            Assert.That(message, Is.EqualTo("Reachable"));
            Assert.That(httpClient.LastRequest, Is.Not.Null);
            Assert.That(httpClient.LastRequest.Url.ToString(), Does.Contain("https://plex.example.com:32400/identity"));
            Assert.That(httpClient.LastRequest.RequestTimeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
        }

        [Test]
        public void should_redact_token_from_probe_error_message()
        {
            var httpClient = new CapturingHttpClient
            {
                ThrowMessage = "Failed request {url}"
            };
            var configService = DispatchProxy.Create<IConfigService, TestConfigServiceProxy>();
            var proxy = new PlexServerProxy(httpClient, configService, LogManager.GetCurrentClassLogger());

            var settings = new PlexServerSettings
            {
                Host = "plex.example.com",
                Port = 32400,
                UseSsl = true,
                AuthToken = "secret-token"
            };

            var result = proxy.CanConnect(settings, TimeSpan.FromSeconds(3), out var message);

            Assert.That(result, Is.False);
            Assert.That(message, Does.Not.Contain("secret-token"));
            Assert.That(message, Does.Contain("X-Plex-Token=<redacted>"));
        }
    }
}
