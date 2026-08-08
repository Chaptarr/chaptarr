using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class ManagedHttpDispatcherCredentialCacheFixture
    {
        [Test]
        public async Task should_not_throw_when_parallel_network_credential_requests_use_same_url()
        {
            var dispatcher = new TestDispatcher(new CacheManager());
            var start = new ManualResetEventSlim(false);
            var credentials = new NetworkCredential("seedbox-user", "seedbox-pass");

            var tasks = Enumerable.Range(0, 256)
                .Select(_ => Task.Run(async () =>
                {
                    start.Wait();

                    var request = new HttpRequest("https://seedbox.example.com/RPC2")
                    {
                        Credentials = credentials
                    };

                    await dispatcher.GetResponseAsync(request, new CookieContainer());
                }))
                .ToArray();

            start.Set();

            await Task.WhenAll(tasks);
        }

        [Test]
        public async Task should_replace_cached_network_credential_when_password_changes()
        {
            var url = new Uri("https://seedbox.example.com/RPC2");
            var cacheManager = new CacheManager();
            var dispatcher = new TestDispatcher(cacheManager);

            await dispatcher.GetResponseAsync(new HttpRequest(url.ToString())
            {
                Credentials = new NetworkCredential("seedbox-user", "old-pass")
            }, new CookieContainer());

            await dispatcher.GetResponseAsync(new HttpRequest(url.ToString())
            {
                Credentials = new NetworkCredential("seedbox-user", "new-pass")
            }, new CookieContainer());

            var credentialCache = cacheManager
                .GetCache<CredentialCache>(typeof(ManagedHttpDispatcher), "credentialcache")
                .Get("credentialCache", () => new CredentialCache());

            Assert.That(credentialCache.GetCredential(url, "Basic")?.Password, Is.EqualTo("new-pass"));
            Assert.That(credentialCache.GetCredential(url, "Digest")?.Password, Is.EqualTo("new-pass"));
        }

        private sealed class TestDispatcher : ManagedHttpDispatcher
        {
            private readonly System.Net.Http.HttpClient _client = new System.Net.Http.HttpClient(new OkHandler());

            public TestDispatcher(CacheManager cacheManager)
                : base(new NoProxySettingsProvider(),
                    new NoopSocketsHttpHandlerFactory(),
                    new TestUserAgentBuilder(),
                    cacheManager,
                    LogManager.GetCurrentClassLogger())
            {
            }

            protected override System.Net.Http.HttpClient GetClient(HttpUri uri)
            {
                return _client;
            }
        }

        private sealed class OkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };

                return Task.FromResult(response);
            }
        }

        private sealed class NoProxySettingsProvider : IHttpProxySettingsProvider
        {
            public HttpProxySettings GetProxySettings(HttpUri uri)
            {
                return null;
            }

            public HttpProxySettings GetProxySettings()
            {
                return null;
            }
        }

        private sealed class NoopSocketsHttpHandlerFactory : ICreateManagedSocketsHttpHandler
        {
            public SocketsHttpHandler CreateHandler(HttpProxySettings proxySettings,
                bool allowAutoRedirect,
                bool useCookies,
                ICredentials credentials,
                bool preAuthenticate,
                int maxConnectionsPerServer)
            {
                return new SocketsHttpHandler();
            }
        }

        private sealed class TestUserAgentBuilder : IUserAgentBuilder
        {
            public string GetUserAgent(bool simplified = false)
            {
                return "ChaptarrTest/1.0";
            }
        }
    }
}
