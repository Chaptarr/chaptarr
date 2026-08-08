using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyNoNegativeAuthorCacheFixture
    {
        private sealed class NotFoundHttpClient : IHttpClient
        {
            public int GetCount { get; private set; }

            public HttpResponse Get(HttpRequest request)
            {
                GetCount++;
                return new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.NotFound);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Execute(HttpRequest request) => throw new NotImplementedException();
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

        private class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_MetadataServerUrl" => "https://metadata.test",
                    _ => throw new NotImplementedException($"Config proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        [Test]
        public void v5_search_author_miss_should_be_requested_again()
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var httpClient = new NotFoundHttpClient();
            var logger = LogManager.GetCurrentClassLogger();
            var proxy = new BookInfoProxy(
                httpClient,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                configService,
                new MetadataRequestBuilder(configService),
                logger,
                new CacheManager(),
                new MetadataServerHealthGate(configService, new MetadataServerHealthService(logger), logger));
            var method = typeof(BookInfoProxy).GetMethod("TryGetAuthorInfoFromV5ForSearch", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(proxy, new object[] { "gr:123" }), Is.Null);
            Assert.That(method.Invoke(proxy, new object[] { "gr:123" }), Is.Null);
            Assert.That(httpClient.GetCount, Is.EqualTo(2));
        }
    }
}
