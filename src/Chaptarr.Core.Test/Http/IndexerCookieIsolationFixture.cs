using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class IndexerCookieIsolationFixture
    {
        [Test]
        public async Task should_isolate_persisted_cookies_by_indexer_key()
        {
            var dispatcher = new RecordingDispatcher();
            var cacheManager = new CacheManager();
            var client = CreateClient(cacheManager, dispatcher);

            await client.ExecuteAsync(CreateRequest("101"));
            await client.ExecuteAsync(CreateRequest("202"));
            await client.ExecuteAsync(CreateRequest("101"));

            Assert.That(dispatcher.CookieHeaders, Is.EqualTo(new[] { string.Empty, string.Empty, "session=indexer-101" }));
        }

        [Test]
        public async Task should_isolate_persisted_cookies_between_http_client_types()
        {
            var cacheManager = new CacheManager();
            var standardDispatcher = new RecordingDispatcher();
            var scopedDispatcher = new RecordingDispatcher();
            var standardClient = CreateClient(cacheManager, standardDispatcher);
            var scopedClient = new AlternateHttpClient(cacheManager, scopedDispatcher);

            await standardClient.ExecuteAsync(CreateRequest("101"));
            await scopedClient.ExecuteAsync(CreateRequest("101"));

            Assert.That(scopedDispatcher.CookieHeaders, Is.EqualTo(new[] { string.Empty }));
        }

        private static NzbDrone.Common.Http.HttpClient CreateClient(CacheManager cacheManager, IHttpDispatcher dispatcher)
        {
            return new NzbDrone.Common.Http.HttpClient(
                Array.Empty<IHttpRequestInterceptor>(),
                cacheManager,
                new RateLimitService(cacheManager, LogManager.GetCurrentClassLogger()),
                dispatcher,
                LogManager.GetCurrentClassLogger());
        }

        private static HttpRequest CreateRequest(string indexerKey)
        {
            return new HttpRequest("https://indexer.example/api")
            {
                RateLimitKey = indexerKey,
                StoreResponseCookie = true
            };
        }

        private sealed class RecordingDispatcher : IHttpDispatcher
        {
            public List<string> CookieHeaders { get; } = new List<string>();

            public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies)
            {
                CookieHeaders.Add(cookies.GetCookieHeader((Uri)request.Url));

                var headers = new HttpHeader();
                if (CookieHeaders.Count == 1)
                {
                    headers.Add("Set-Cookie", "session=indexer-101; Path=/");
                }

                return Task.FromResult(new HttpResponse(request, headers, Array.Empty<byte>()));
            }
        }

        private sealed class AlternateHttpClient : NzbDrone.Common.Http.HttpClient
        {
            public AlternateHttpClient(CacheManager cacheManager, IHttpDispatcher dispatcher)
                : base(
                    Array.Empty<IHttpRequestInterceptor>(),
                    cacheManager,
                    new RateLimitService(cacheManager, LogManager.GetCurrentClassLogger()),
                    dispatcher,
                    LogManager.GetCurrentClassLogger())
            {
            }
        }
    }
}
