using System;
using System.Linq;
using System.Net;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyEditionParityFixture
    {
        private class StubHttpClient : IHttpClient
        {
            private readonly Func<HttpRequest, HttpResponse> _handler;

            public StubHttpClient(Func<HttpRequest, HttpResponse> handler)
            {
                _handler = handler;
            }

            public HttpResponse Get(HttpRequest request) => _handler(request);
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
        public void author_blob_should_map_chapter_and_review_fields_on_editions()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
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
          ""readingFormatId"": 2,
          ""format"": ""audiobook"",
          ""title"": ""Test Audio"",
          ""languageCode"": ""en"",
          ""durationSeconds"": 3600,
          ""chapterCount"": 2,
          ""hasChapters"": true,
          ""chapters"": [
            { ""title"": ""Chapter 1"", ""startOffsetMs"": 0, ""startOffsetSec"": 0, ""lengthMs"": 1000 },
            { ""title"": ""Chapter 2"", ""startOffsetMs"": 1000, ""startOffsetSec"": 1, ""lengthMs"": 2000 }
          ],
          ""ratingAverage"": 4.6,
          ""ratingCount"": 321,
          ""reviewCount"": 42
        }
      ]
    }
  ],
  ""series"": []
}";

            var httpClient = new StubHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));
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
            var edition = author.Books
                .SelectMany(b => b.Editions)
                .First(e => e.Title == "Test Audio" && e.ChapterCount == 2);

            Assert.That(edition.ChapterCount, Is.EqualTo(2));
            Assert.That(edition.HasChapters, Is.True);
            Assert.That(edition.Chapters, Has.Count.EqualTo(2));
            Assert.That(edition.Chapters[1].Title, Is.EqualTo("Chapter 2"));
            Assert.That(edition.Chapters[1].StartOffsetMs, Is.EqualTo(1000));
            Assert.That(edition.ReviewCount, Is.EqualTo(42));
            Assert.That(edition.Ratings.Votes, Is.EqualTo(321));
        }

        [Test]
        public void work_lookup_should_map_same_chapter_and_review_fields_as_author_blob()
        {
            const string payload = @"{
  ""work"": {
    ""id"": ""hc:383404"",
    ""title"": ""Test Work"",
    ""goodreadsWorkId"": ""gr:19079807"",
    ""hardcoverBookId"": ""hc:383404"",
    ""editions"": [
      {
        ""id"": ""hc:edition:22222"",
        ""title"": ""Test Audio"",
        ""languageCode"": ""en"",
        ""format"": ""audiobook"",
        ""readingFormatId"": 2,
        ""providerIds"": { ""hardcover"": ""hc:edition:22222"", ""goodreads"": ""gr:61304420"", ""amazon"": ""az:B017WO34IQ"", ""audible"": ""az:B017WO34IQ"" },
        ""ratingAverage"": 4.6,
        ""ratingCount"": 321,
        ""reviewCount"": 42,
        ""durationSeconds"": 3600,
        ""chapterCount"": 2,
        ""hasChapters"": true,
        ""chapters"": [
          { ""title"": ""Chapter 1"", ""startOffsetMs"": 0, ""startOffsetSec"": 0, ""lengthMs"": 1000 },
          { ""title"": ""Chapter 2"", ""startOffsetMs"": 1000, ""startOffsetSec"": 1, ""lengthMs"": 2000 }
        ]
      }
    ]
  },
  ""editions"": [
    {
      ""id"": ""hc:edition:22222"",
      ""title"": ""Test Audio"",
      ""languageCode"": ""en"",
      ""format"": ""audiobook"",
      ""readingFormatId"": 2,
      ""providerIds"": { ""hardcover"": ""hc:edition:22222"", ""goodreads"": ""gr:61304420"", ""amazon"": ""az:B017WO34IQ"", ""audible"": ""az:B017WO34IQ"" },
      ""ratingAverage"": 4.6,
      ""ratingCount"": 321,
      ""reviewCount"": 42,
      ""durationSeconds"": 3600,
      ""chapterCount"": 2,
      ""hasChapters"": true,
      ""chapters"": [
        { ""title"": ""Chapter 1"", ""startOffsetMs"": 0, ""startOffsetSec"": 0, ""lengthMs"": 1000 },
        { ""title"": ""Chapter 2"", ""startOffsetMs"": 1000, ""startOffsetSec"": 1, ""lengthMs"": 2000 }
      ]
    }
  ],
  ""authors"": [ { ""id"": 20, ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new StubHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var requestBuilder = new MetadataRequestBuilder(configService);

            var proxy = new BookInfoProxy(httpClient,
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: null,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: configService,
                requestBuilder: requestBuilder,
                logger: LogManager.GetCurrentClassLogger(),
                cacheManager: new CacheManager(),
                metadataServerHealthGate: CreateHealthGate(configService));

            var tuple = proxy.GetWorkInfo("gr:19079807", BookMediaType.Audiobook);
            var edition = tuple.Item2.Editions.Single();

            Assert.That(edition.ChapterCount, Is.EqualTo(2));
            Assert.That(edition.HasChapters, Is.True);
            Assert.That(edition.Chapters, Has.Count.EqualTo(2));
            Assert.That(edition.Chapters[1].LengthMs, Is.EqualTo(2000));
            Assert.That(edition.ReviewCount, Is.EqualTo(42));
            Assert.That(edition.Ratings.Votes, Is.EqualTo(321));
        }

        [Test]
        public void work_lookup_should_keep_book_foreign_id_on_the_work_key()
        {
            const string payload = @"{
  ""work"": {
    ""id"": ""hc:383404"",
    ""title"": ""Test Work"",
    ""goodreadsWorkId"": ""gr:19079807"",
    ""hardcoverBookId"": ""hc:383404"",
    ""editions"": [
      {
        ""id"": ""hc:edition:22222"",
        ""title"": ""Target Audio"",
        ""languageCode"": ""en"",
        ""format"": ""audiobook"",
        ""readingFormatId"": 2,
        ""providerIds"": { ""hardcover"": ""hc:edition:22222"", ""goodreads"": ""gr:61304420"" }
      }
    ]
  },
  ""editions"": [
    {
      ""id"": ""hc:edition:22222"",
      ""title"": ""Target Audio"",
      ""languageCode"": ""en"",
      ""format"": ""audiobook"",
      ""readingFormatId"": 2,
      ""providerIds"": { ""hardcover"": ""hc:edition:22222"", ""goodreads"": ""gr:61304420"" }
    }
  ],
  ""authors"": [ { ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new StubHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var requestBuilder = new MetadataRequestBuilder(configService);

            var proxy = new BookInfoProxy(httpClient,
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: null,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: configService,
                requestBuilder: requestBuilder,
                logger: LogManager.GetCurrentClassLogger(),
                cacheManager: new CacheManager(),
                metadataServerHealthGate: CreateHealthGate(configService));

            var tuple = proxy.GetWorkInfo("hc:383404", BookMediaType.Audiobook);

            Assert.That(tuple.Item2.ForeignEditionId, Is.EqualTo("hc:383404"));
            Assert.That(tuple.Item2.HardcoverBookId, Is.EqualTo("hc:383404"));
            Assert.That(tuple.Item2.GoodreadsWorkId, Is.EqualTo("gr:19079807"));
            Assert.That(tuple.Item2.Editions.Single().ForeignEditionId, Is.EqualTo("hc:edition:22222-audiobook"));
        }

        [Test]
        public void legacy_work_envelope_should_keep_explicit_edition_id_over_aliases()
        {
            const string payload = @"{
  ""work"": {
    ""id"": ""hc:175280"",
    ""title"": ""Piranesi"",
    ""hardcoverBookId"": ""hc:175280""
  },
  ""editions"": [
    {
      ""id"": ""az:B0H75VCVGG"",
      ""title"": ""Piranesi"",
      ""languageCode"": ""en"",
      ""formatType"": ""audiobook"",
      ""readingFormatId"": 2,
      ""providerIds"": {
        ""hardcover"": ""hc:edition:999999"",
        ""amazon"": ""az:B0H75HGGRR""
      }
    }
  ],
  ""authors"": [
    {
      ""name"": ""Susanna Clarke"",
      ""role"": ""primary"",
      ""providerIds"": { ""hc"": ""hc:63836"" }
    }
  ],
  ""series"": []
}";

            var httpClient = new StubHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));
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
                requestBuilder: new MetadataRequestBuilder(configService),
                logger: LogManager.GetCurrentClassLogger(),
                cacheManager: new CacheManager(),
                metadataServerHealthGate: CreateHealthGate(configService));

            var edition = proxy.GetWorkInfo("hc:175280", BookMediaType.Audiobook).Item2.Editions.Single();

            Assert.That(edition.ForeignEditionId, Is.EqualTo("az:B0H75VCVGG-audiobook"));
        }

        [Test]
        public void work_lookup_should_reject_an_edition_without_server_identity()
        {
            const string payload = @"{
  ""work"": {
    ""id"": ""hc:383404"",
    ""title"": ""Test Work"",
    ""editions"": [
      {
        ""title"": ""Identity-less Audio"",
        ""languageCode"": ""en"",
        ""format"": ""audiobook"",
        ""readingFormatId"": 2
      }
    ]
  },
  ""authors"": [ { ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new StubHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));
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
                requestBuilder: new MetadataRequestBuilder(configService),
                logger: LogManager.GetCurrentClassLogger(),
                cacheManager: new CacheManager(),
                metadataServerHealthGate: CreateHealthGate(configService));

            Assert.Throws<BookNotFoundException>(() =>
                proxy.GetWorkInfo("hc:383404", BookMediaType.Audiobook));
        }

        [Test]
        public void author_import_then_work_refresh_should_keep_the_same_piranesi_blueprint()
        {
            const string bookPayload = @"{
  ""id"": ""hc:175280"",
  ""title"": ""Piranesi"",
  ""slug"": ""piranesi"",
  ""description"": ""The canonical description."",
  ""hardcoverBookId"": ""hc:175280"",
  ""providerIdsAll"": {
    ""hc"": [""hc:175280""],
    ""az"": [""az:B0H75VCVGG"", ""az:B0H75HGGRR""]
  },
  ""links"": { ""hardcover"": ""https://hardcover.app/books/piranesi"" },
  ""providerUrls"": { ""hardcover"": ""https://hardcover.app/books/piranesi"" },
  ""coverUrl"": ""https://images.example/piranesi.jpg"",
  ""ratingAverage"": 4.2,
  ""ratingCount"": 100,
  ""editions"": [
    {
      ""id"": ""az:B0H75VCVGG"",
      ""title"": ""Piranesi"",
      ""asin"": ""B0H75VCVGG"",
      ""asins"": [""B0H75HGGRR"", ""B0H75VCVGG""],
      ""languageCode"": ""en"",
      ""format"": ""audiobook"",
      ""readingFormatId"": 2,
      ""durationSeconds"": 36000,
      ""providerIdsAll"": {
        ""az"": [""az:B0H75VCVGG"", ""az:B0H75HGGRR""]
      }
    }
  ]
}";
            const string authorPayloadPrefix = @"{
  ""author"": {
    ""id"": ""hc:63836"",
    ""name"": ""Susanna Clarke"",
    ""sortName"": ""Clarke, Susanna"",
    ""slug"": ""susanna-clarke""
  },
  ""books"": [";
            const string authorPayloadSuffix = @"],
  ""series"": []
}";
            const string workPayloadPrefix = @"{
  ""work"": ";
            const string workPayloadSuffix = @",
  ""editions"": [
    {
      ""id"": ""az:B0H75VCVGG"",
      ""title"": ""Piranesi"",
      ""asin"": ""B0H75VCVGG"",
      ""asins"": [""B0H75HGGRR"", ""B0H75VCVGG""],
      ""languageCode"": ""en"",
      ""format"": ""audiobook"",
      ""readingFormatId"": 2,
      ""durationSeconds"": 36000
    }
  ],
  ""authors"": [
    {
      ""name"": ""Aaron Contributor"",
      ""role"": ""contributor"",
      ""providerIds"": { ""hc"": ""hc:999999"" }
    },
    {
      ""name"": ""Susanna Clarke"",
      ""role"": ""primary"",
      ""providerIds"": { ""hc"": ""hc:63836"" }
    }
  ],
  ""series"": []
}";

            var authorPayload = authorPayloadPrefix + bookPayload + authorPayloadSuffix;
            var workPayload = workPayloadPrefix + bookPayload + workPayloadSuffix;
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var requestBuilder = new MetadataRequestBuilder(configService);

            BookInfoProxy CreateProxy(string payload)
            {
                return new BookInfoProxy(
                    new StubHttpClient(req => new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK)),
                    cachedHttpClient: null,
                    goodreadsSearchProxy: null,
                    hardcoverSearchClient: null,
                    audibleCatalogProxy: null,
                    authorService: null,
                    bookService: null,
                    editionService: null,
                    configService: configService,
                    requestBuilder: requestBuilder,
                    logger: LogManager.GetCurrentClassLogger(),
                    cacheManager: new CacheManager(),
                    metadataServerHealthGate: CreateHealthGate(configService));
            }

            var authorBook = CreateProxy(authorPayload)
                .GetAuthorInfo("hc:63836", useCache: false)
                .Books
                .Single(book => book.MediaType == BookMediaType.Audiobook);
            var refreshedBook = CreateProxy(workPayload)
                .GetWorkInfo("hc:175280", BookMediaType.Audiobook)
                .Item2;

            Assert.Multiple(() =>
            {
                Assert.That(refreshedBook.Title, Is.EqualTo(authorBook.Title));
                Assert.That(refreshedBook.Overview, Is.EqualTo(authorBook.Overview));
                Assert.That(refreshedBook.HardcoverBookId, Is.EqualTo(authorBook.HardcoverBookId));
                Assert.That(refreshedBook.Links.Select(link => (link.Name, link.Url)),
                    Is.EquivalentTo(authorBook.Links.Select(link => (link.Name, link.Url))));
                Assert.That(refreshedBook.ProviderUrls, Is.EquivalentTo(authorBook.ProviderUrls));
                Assert.That(refreshedBook.Author.Name, Is.EqualTo(authorBook.Author.Name));
                Assert.That(refreshedBook.Editions.Select(edition => edition.ForeignEditionId),
                    Is.EqualTo(authorBook.Editions.Select(edition => edition.ForeignEditionId)));
                Assert.That(refreshedBook.Editions.Single().ForeignEditionId,
                    Is.EqualTo("az:B0H75VCVGG-audiobook"));
                Assert.That(refreshedBook.Editions.Single().ForeignEditionId,
                    Is.Not.EqualTo("az:B0H75HGGRR-audiobook"));
            });
        }

        [Test]
        public void import_time_selection_should_use_print_representative_when_native_ebook_is_missing()
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();

            var proxy = new BookInfoProxy(new StubHttpClient(_ => throw new NotImplementedException()),
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

            var book = new Book
            {
                Title = "Test Book",
                MediaType = BookMediaType.Ebook,
                Editions = new[]
                {
                    new Edition
                    {
                        Id = 10,
                        Title = "Print Edition",
                        ReadingFormatId = 1,
                        Ratings = new Ratings { Votes = 10, Value = 4.0m }
                    },
                    new Edition
                    {
                        Id = 20,
                        Title = "Audio Edition",
                        ReadingFormatId = 2,
                        Ratings = new Ratings { Votes = 100, Value = 4.5m }
                    }
                }.ToList()
            };

            var method = typeof(BookInfoProxy).GetMethod("SelectBestEditionAsMonitored", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            method.Invoke(proxy, new object[] { book });

            Assert.That(book.Editions.Single(e => e.Monitored).Id, Is.EqualTo(10));
        }
    }
}
