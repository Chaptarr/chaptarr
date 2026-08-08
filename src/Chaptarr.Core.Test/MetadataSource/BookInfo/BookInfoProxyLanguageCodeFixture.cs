using System;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyLanguageCodeFixture
    {
        private class StubHttpClient : IHttpClient
        {
            private readonly string _payload;

            public StubHttpClient(string payload)
            {
                _payload = payload;
            }

            public HttpRequest LastRequest { get; private set; }

            public HttpResponse Get(HttpRequest request)
            {
                LastRequest = request;

                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, _payload);
            }

            public HttpResponse Execute(HttpRequest request) => throw new NotImplementedException();
            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
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

        private class ConfigServiceProxy : DispatchProxy
        {
            public string MetadataServerUrl { get; set; } = "http://metadata";

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_MetadataServerUrl")
                {
                    return MetadataServerUrl;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        private static IMetadataServerHealthGate CreateHealthGate(IConfigService configService)
        {
            var logger = LogManager.GetCurrentClassLogger();
            return new MetadataServerHealthGate(configService, new MetadataServerHealthService(logger), logger);
        }

        [Test]
        public void should_map_v5_languageCode_to_canonical_language()
        {
            const string payload = @"{
  ""author"": {
    ""id"": ""hc:123"",
    ""name"": ""Test Author"",
    ""sortName"": ""Author, Test"",
    ""slug"": ""test-author"",
    ""ratingAverage"": 4.2,
    ""ratingCount"": 10
  },
  ""books"": [
    {
      ""id"": ""hc:book1"",
      ""title"": ""Test Book"",
      ""slug"": ""test-book"",
      ""base_book_id"": ""base1"",
      ""ratingAverage"": 4.0,
      ""ratingCount"": 5,
      ""editions"": [
        {
          ""id"": ""ed1"",
          ""readingFormatId"": 3,
          ""format"": ""Kindle Edition"",
          ""title"": ""Test Book"",
          ""languageCode"": ""es""
        }
      ]
    }
  ],
  ""series"": []
}";

            var httpClient = new StubHttpClient(payload);
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();

            var proxy = new BookInfoProxy(httpClient,
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: null,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: configService,
                requestBuilder: null,
                logger: LogManager.GetCurrentClassLogger(),
                cacheManager: new CacheManager(),
                metadataServerHealthGate: CreateHealthGate(configService));

            var author = proxy.GetAuthorInfo("hc:123", useCache: false);

            var languages = author.Books
                .SelectMany(b => b.Editions)
                .Select(e => e.Language)
                .Distinct()
                .ToList();

            Assert.That(languages, Is.EquivalentTo(new[] { "spa" }));
        }
    }
}
