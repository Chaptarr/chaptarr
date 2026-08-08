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
    public class BookInfoProxyEditionPruningFixture
    {
        private class StubHttpClient : IHttpClient
        {
            private readonly string _payload;

            public StubHttpClient(string payload)
            {
                _payload = payload;
            }

            public HttpResponse Get(HttpRequest request)
            {
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
        public void should_create_media_instances_with_full_candidate_editions_before_profile_filtering()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [
    {
      ""id"": ""hc:book1"",
      ""title"": ""Test Book"",
      ""slug"": ""test-book"",
      ""base_book_id"": ""base1"",
      ""editions"": [
        { ""id"": ""eng-kindle"", ""readingFormatId"": 3, ""format"": ""Kindle Edition"", ""title"": ""Test Book"", ""languageCode"": ""en"", ""isbn13"": ""1111111111111"" },
        { ""id"": ""eng-epub"",   ""readingFormatId"": 3, ""format"": ""EPUB"",          ""title"": ""Test Book"", ""languageCode"": ""en"", ""isbn13"": ""2222222222222"" },
        { ""id"": ""spa-epub"",   ""readingFormatId"": 3, ""format"": ""EPUB"",          ""title"": ""Test Book"", ""languageCode"": ""es"" },
        { ""id"": ""fra-print"",  ""readingFormatId"": 1, ""format"": ""Paperback"",     ""title"": ""Test Book"", ""languageCode"": ""fr"" }
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

            var audiobookBook = author.Books.Single(b => b.MediaType == BookMediaType.Audiobook);
            var ebookBook = author.Books.Single(b => b.MediaType == BookMediaType.Ebook);

            Assert.That(audiobookBook.Editions, Has.Count.EqualTo(4));
            Assert.That(audiobookBook.Editions.Count(e => e.ReadingFormatId == 3), Is.EqualTo(3));
            Assert.That(audiobookBook.Editions.Count(e => e.ReadingFormatId == 1), Is.EqualTo(1));
            Assert.That(audiobookBook.Editions.Select(e => e.Language), Is.EquivalentTo(new[] { "eng", "eng", "spa", "fra" }));
            Assert.That(ebookBook.Editions, Has.Count.EqualTo(4));
            Assert.That(ebookBook.Editions.Count(e => e.ReadingFormatId == 3), Is.EqualTo(3));
            Assert.That(ebookBook.Editions.Count(e => e.ReadingFormatId == 1), Is.EqualTo(1));

            Assert.That(audiobookBook.Editions.Any(e => e.Isbn13 == "1111111111111"), Is.True);
            Assert.That(ebookBook.Editions.Any(e => e.ForeignEditionId == "fra-print-ebook"), Is.True);
        }

        [Test]
        public void should_not_prune_native_or_representative_candidates_at_source_mapping()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [
    {
      ""id"": ""hc:book1"",
      ""title"": ""Test Book"",
      ""slug"": ""test-book"",
      ""base_book_id"": ""base1"",
      ""editions"": [
        { ""id"": ""eng-audio1"", ""readingFormatId"": 2, ""format"": ""Audiobook"",      ""title"": ""Test Book"", ""languageCode"": ""en"" },
        { ""id"": ""eng-audio2"", ""readingFormatId"": 2, ""format"": ""Audiobook"",      ""title"": ""Test Book"", ""languageCode"": ""en"" },
        { ""id"": ""eng-kindle"", ""readingFormatId"": 3, ""format"": ""Kindle Edition"", ""title"": ""Test Book"", ""languageCode"": ""en"" },
        { ""id"": ""spa-epub"",   ""readingFormatId"": 3, ""format"": ""EPUB"",           ""title"": ""Test Book"", ""languageCode"": ""es"" }
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

            var audiobookBook = author.Books.Single(b => b.MediaType == BookMediaType.Audiobook);
            var ebookBook = author.Books.Single(b => b.MediaType == BookMediaType.Ebook);

            // Source mapping keeps the full candidate set. The shared add/refresh
            // normalizer applies metadata-profile filtering and final retention.
            Assert.That(ebookBook.Editions, Has.Count.EqualTo(4));
            Assert.That(audiobookBook.Editions, Has.Count.EqualTo(4));

            Assert.That(audiobookBook.Editions.Any(e => e.ForeignEditionId == "eng-audio1-audiobook"), Is.True);
            Assert.That(audiobookBook.Editions.Any(e => e.ForeignEditionId == "eng-audio2-audiobook"), Is.True);

            Assert.That(audiobookBook.Editions.Any(e => e.ForeignEditionId == "eng-kindle-audiobook"), Is.True);
            Assert.That(audiobookBook.Editions.Any(e => e.ForeignEditionId == "spa-epub-audiobook"), Is.True);
            Assert.That(ebookBook.Editions.Any(e => e.ForeignEditionId == "eng-audio1-ebook"), Is.True);
            Assert.That(ebookBook.Editions.Any(e => e.ForeignEditionId == "eng-audio2-ebook"), Is.True);
        }

        [Test]
        public void should_defer_representative_selection_until_shared_normalization()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [
    {
      ""id"": ""hc:book1"",
      ""title"": ""Test Book"",
      ""slug"": ""test-book"",
      ""base_book_id"": ""base1"",
      ""editions"": [
        { ""id"": ""eng-rich"",    ""readingFormatId"": 3, ""format"": ""EPUB"", ""title"": ""Test Book"", ""languageCode"": ""en"", ""isbn13"": ""1111111111111"", ""publisher"": ""Rich Publisher"", ""coverUrl"": ""https://example.com/rich.jpg"", ""ratingCount"": 1,   ""ratingAverage"": 1.0 },
        { ""id"": ""eng-popular"", ""readingFormatId"": 3, ""format"": ""EPUB"", ""title"": ""Test Book"", ""languageCode"": ""en"", ""ratingCount"": 500, ""ratingAverage"": 4.5 }
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

            var audiobookBook = author.Books.Single(b => b.MediaType == BookMediaType.Audiobook);

            Assert.That(audiobookBook.Editions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-rich-audiobook", "eng-popular-audiobook" }));
        }
    }
}
