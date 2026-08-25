using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Chaptarr.Api.V1.Books;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Goodreads;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyCanonicalLookupFixture
    {
        private class RecordingHttpClient : IHttpClient
        {
            private readonly Func<HttpRequest, HttpResponse> _handler;

            public RecordingHttpClient(Func<HttpRequest, HttpResponse> handler)
            {
                _handler = handler;
            }

            public List<HttpRequest> Requests { get; } = new();

            public HttpResponse Get(HttpRequest request)
            {
                Requests.Add(request);
                return _handler(request);
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

        private static BookInfoProxy CreateProxy(IHttpClient httpClient)
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var requestBuilder = new MetadataRequestBuilder(configService);

            return new BookInfoProxy(httpClient,
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

        [Test]
        public void should_canonicalize_goodreads_text_search_edition_identity()
        {
            var proxy = CreateProxy(new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called")));
            var mapper = typeof(BookInfoProxy).GetMethod("CreateBookFromSearchResult", BindingFlags.Instance | BindingFlags.NonPublic);
            var book = (Book)mapper.Invoke(proxy, new object[]
            {
                new SearchJsonResource
                {
                    BookId = 22733729,
                    WorkId = 22733728,
                    Title = "The Long Way to a Small, Angry Planet",
                    Author = new AuthorJsonResource { Id = 1306980, Name = "Becky Chambers" }
                }
            });

            var resource = book.ToResource();
            resource.Editions = book.Editions.ToResource();
            var validationMethod = typeof(BookController).GetMethod("GetNativePrefixFailures", BindingFlags.Static | BindingFlags.NonPublic);
            var prefixFailures = (List<ValidationFailure>)validationMethod.Invoke(null, new object[] { resource, null });

            Assert.Multiple(() =>
            {
                Assert.That(book.Editions.Single().ForeignEditionId, Is.EqualTo("gr:22733729"));
                Assert.That(book.Editions.Single().GoodreadsEditionId, Is.EqualTo(22733729));
                Assert.That(book.GoodreadsWorkId, Is.EqualTo("gr:22733728"));
                Assert.That(book.Author.GoodreadsAuthorId, Is.EqualTo("gr:1306980"));
                Assert.That(resource.ForeignEditionId, Is.EqualTo("gr:22733729"));
                Assert.That(resource.Editions.Single().ForeignEditionId, Is.EqualTo("gr:22733729"));
                Assert.That(prefixFailures, Is.Empty);
            });
        }

        [Test]
        public void should_look_up_a_goodreads_author_term_under_the_goodreads_provider()
        {
            // The id in "author:2514" is a Goodreads author id. Sent bare it used to be resolved
            // as Hardcover — a different author, or none — and is now rejected outright. Assert
            // the outgoing request rather than a live author's metadata.
            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, "{}", HttpStatusCode.NotFound));

            var proxy = CreateProxy(httpClient);

            var results = proxy.SearchForNewBook("author:2514", author: null);

            var url = Uri.UnescapeDataString(httpClient.Requests.Single().Url.ToString());

            Assert.Multiple(() =>
            {
                Assert.That(url, Does.Contain("/api/v5/author"));
                Assert.That(url, Does.Contain("id=gr:2514"));
                Assert.That(url, Does.Not.Contain("hc:2514"));
                Assert.That(results, Is.Empty);
            });
        }

        [Test]
        public void should_reject_a_bare_numeric_author_id_without_calling_the_metadata_server()
        {
            var httpClient = new RecordingHttpClient(_ =>
                throw new AssertionException("HTTP should not be called for an author id with no provider"));

            var proxy = CreateProxy(httpClient);

            var exception = Assert.Throws<InvalidProviderIdException>(() => proxy.GetAuthorInfo("2514"));

            Assert.Multiple(() =>
            {
                Assert.That(exception.ProviderId, Is.EqualTo("2514"));
                Assert.That(exception.Message, Does.Contain("no provider prefix"));
                Assert.That(httpClient.Requests, Is.Empty);
            });
        }

        [Test]
        public void should_reject_a_bare_numeric_author_id_on_the_async_path_without_calling_the_metadata_server()
        {
            var httpClient = new RecordingHttpClient(_ =>
                throw new AssertionException("HTTP should not be called for an author id with no provider"));

            var proxy = CreateProxy(httpClient);

            var exception = Assert.ThrowsAsync<InvalidProviderIdException>(async () => await proxy.GetAuthorInfoAsync("2514"));

            Assert.Multiple(() =>
            {
                Assert.That(exception.ProviderId, Is.EqualTo("2514"));
                Assert.That(httpClient.Requests, Is.Empty);
            });
        }

        [Test]
        public void should_canonicalize_legacy_goodreads_edition_identity()
        {
            var mapper = typeof(BookInfoProxy).GetMethod("MapEdition", BindingFlags.Static | BindingFlags.NonPublic);
            var edition = (Edition)mapper.Invoke(null, new object[]
            {
                new NzbDrone.Core.MetadataSource.BookInfo.BookResource { ForeignId = 12345, Title = "Legacy Edition" }
            });

            Assert.Multiple(() =>
            {
                Assert.That(edition.ForeignEditionId, Is.EqualTo("gr:12345"));
                Assert.That(edition.GoodreadsEditionId, Is.EqualTo(12345));
            });
        }

        [Test]
        public void should_skip_direct_edition_lookup_when_metadata_server_circuit_is_open()
        {
            var httpClient = new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called while metadata circuit is open"));
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var requestBuilder = new MetadataRequestBuilder(configService);
            var logger = LogManager.GetCurrentClassLogger();
            var healthService = new MetadataServerHealthService(logger);
            var healthGate = new MetadataServerHealthGate(configService, healthService, logger);
            healthService.ReportFailure(healthGate.SourceName, new WebException("metadata server unavailable"));

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
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: healthGate);

            var results = proxy.SearchForNewBook("edition:123", author: null);

            Assert.That(results, Is.Empty);
            Assert.That(httpClient.Requests, Is.Empty);
        }

        [Test]
        public void should_skip_cached_author_lookup_when_metadata_server_circuit_is_open()
        {
            var httpClient = new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called while metadata circuit is open"));
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var requestBuilder = new MetadataRequestBuilder(configService);
            var logger = LogManager.GetCurrentClassLogger();
            var healthService = new MetadataServerHealthService(logger);
            var healthGate = new MetadataServerHealthGate(configService, healthService, logger);
            healthService.ReportFailure(healthGate.SourceName, new WebException("metadata server unavailable"));

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
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: healthGate);

            var exception = Assert.Throws<BookInfoException>(() => proxy.GetAuthorInfo("hc:123", useCache: false));

            Assert.That(exception.Message, Does.Contain("temporarily unavailable"));
            Assert.That(httpClient.Requests, Is.Empty);
        }

        private const string GoodreadsWorkPayload = @"{
  ""work"": { ""id"": ""gr:3046572"", ""title"": ""Harry Potter and the Goblet of Fire"", ""goodreadsWorkId"": ""gr:3046572"" },
  ""editions"": [ { ""id"": 10, ""title"": ""Harry Potter and the Goblet of Fire"", ""language"": ""en"", ""formatType"": ""audiobook"", ""providerIds"": { ""goodreads"": ""gr:58613424"" } } ],
  ""authors"": [ { ""id"": 20, ""name"": ""J.K. Rowling"", ""providerIds"": { ""gr"": ""gr:1077326"" } } ],
  ""series"": []
}";

        private const string HardcoverWorkPayload = @"{
  ""work"": { ""id"": ""hc:383236"", ""title"": ""Harry Potter and the Goblet of Fire"", ""hardcoverWorkId"": ""hc:383236"" },
  ""editions"": [ { ""id"": 10, ""title"": ""Harry Potter and the Goblet of Fire"", ""language"": ""en"", ""formatType"": ""audiobook"", ""providerIds"": { ""hardcover"": ""hc:11111"" } } ],
  ""authors"": [ { ""id"": 20, ""name"": ""J.K. Rowling"", ""providerIds"": { ""hc"": ""hc:80626"" } } ],
  ""series"": []
}";

        [Test]
        public void should_send_matching_goodreads_author_hint_on_v5_work_lookup()
        {
            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, GoodreadsWorkPayload, HttpStatusCode.OK));

            var proxy = CreateProxy(httpClient);

            proxy.GetWorkInfo("gr:3046572", BookMediaType.Audiobook, "gr:1077326");

            var url = Uri.UnescapeDataString(httpClient.Requests.Single().Url.ToString());
            Assert.That(url, Does.Contain("/api/v5/work/"));
            Assert.That(url, Does.Contain("author=gr:1077326"));
        }

        [Test]
        public void should_send_matching_hardcover_author_hint_on_v5_book_info_work_lookup()
        {
            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, HardcoverWorkPayload, HttpStatusCode.OK));

            var proxy = CreateProxy(httpClient);

            proxy.GetBookInfo("hc:383236", BookMediaType.Audiobook, "hc:80626");

            var url = Uri.UnescapeDataString(httpClient.Requests.Single().Url.ToString());
            Assert.That(url, Does.Contain("/api/v5/work/"));
            Assert.That(url, Does.Contain("author=hc:80626"));
        }

        [Test]
        public void should_omit_author_hint_when_provider_does_not_match_work_provider()
        {
            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, GoodreadsWorkPayload, HttpStatusCode.OK));

            var proxy = CreateProxy(httpClient);

            proxy.GetWorkInfo("gr:3046572", BookMediaType.Audiobook, "hc:80626");

            var url = Uri.UnescapeDataString(httpClient.Requests.Single().Url.ToString());
            Assert.That(url, Does.Not.Contain("author="));
        }

        [Test]
        public void should_route_hc_prefixed_term_to_v5_work_lookup()
        {
            const string payload = @"{
  ""work"": { ""id"": 1, ""title"": ""Test Work"", ""hardcoverWorkId"": ""hc:12345"" },
  ""editions"": [ { ""id"": 10, ""title"": ""Test Edition"", ""language"": ""en"", ""formatType"": ""ebook"", ""providerIds"": { ""hardcover"": ""11111"" } } ],
  ""authors"": [ { ""id"": 20, ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var results = proxy.SearchForNewBook("hc:12345", author: null);

            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.Select(r => r.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
            Assert.That(results.Single(r => r.MediaType == BookMediaType.Audiobook).Editions.Single().ReadingFormatId, Is.EqualTo(3));
            Assert.That(results.Single(r => r.MediaType == BookMediaType.Ebook).Editions.Single().ReadingFormatId, Is.EqualTo(3));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));

            var url = httpClient.Requests[0].Url.ToString();
            Assert.That(url, Does.Contain("/api/v5/work/"));
            Assert.That(url, Does.Contain("12345"));
        }

        [Test]
        public void should_return_both_media_types_when_work_has_audio_and_non_audio_editions()
        {
            const string payload = @"{
  ""work"": { ""id"": 1, ""title"": ""Test Work"", ""hardcoverWorkId"": ""hc:12345"" },
  ""editions"": [
    { ""id"": 10, ""title"": ""Test Edition Ebook"", ""language"": ""en"", ""formatType"": ""ebook"", ""providerIds"": { ""hardcover"": ""11111"" } },
    { ""id"": 11, ""title"": ""Test Edition Audio"", ""language"": ""en"", ""formatType"": ""audiobook"", ""providerIds"": { ""hardcover"": ""22222"" } }
  ],
  ""authors"": [ { ""id"": 20, ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var results = proxy.SearchForNewBook("hc:12345", author: null);

            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.Select(r => r.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));

            var audiobook = results.Single(r => r.MediaType == BookMediaType.Audiobook);
            var ebook = results.Single(r => r.MediaType == BookMediaType.Ebook);

            Assert.That(audiobook.Editions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "hc:edition:22222-audiobook", "hc:edition:11111-audiobook" }));
            Assert.That(ebook.Editions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "hc:edition:11111-ebook", "hc:edition:22222-ebook" }));
        }

        [Test]
        public void should_map_v5_work_lookup_editions_with_deterministic_identity_and_format_ids()
        {
            const string payload = @"{
  ""work"": { ""id"": 1, ""title"": ""Test Work"", ""hardcoverWorkId"": ""12345"", ""goodreadsWorkId"": 19079807 },
  ""editions"": [
    { ""id"": 10, ""title"": ""Test Ebook"", ""language"": ""eng"", ""formatType"": ""ebook"", ""readingFormatId"": 3, ""providerIds"": { ""hardcover"": ""11111"", ""goodreads"": 110933 }, ""rating"": 4.1, ""ratingsCount"": 50 },
    { ""id"": 11, ""title"": ""Test Audio"", ""language"": ""eng"", ""formatType"": ""audiobook"", ""readingFormatId"": 2, ""providerIds"": { ""hardcover"": ""22222"", ""goodreads"": 61304420, ""amazon"": ""B017WO34IQ"", ""audible"": ""B017WO34IQ"" }, ""rating"": 4.6, ""ratingsCount"": 200 }
  ],
  ""authors"": [ { ""id"": 20, ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var audioTuple = proxy.GetWorkInfo("gr:19079807", BookMediaType.Audiobook);
            var audioBook = audioTuple.Item2;

            Assert.That(audioBook.MediaType, Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(audioBook.Editions, Has.Count.EqualTo(2));

            var audio = audioBook.Editions.Single(e => e.Format == "audiobook");
            Assert.That(audio.ReadingFormatId, Is.EqualTo(2));
            Assert.That(audio.ForeignEditionId, Is.EqualTo("hc:edition:22222-audiobook"));
            Assert.That(audio.GoodreadsEditionId, Is.EqualTo(61304420));
            Assert.That(audio.HardcoverEditionId, Is.EqualTo("22222"));
            Assert.That(audio.AudibleASIN, Is.EqualTo("B017WO34IQ"));
            Assert.That(audio.Ratings.Votes, Is.EqualTo(200));
            Assert.That(audioBook.Editions.Single(e => e.Format == "ebook").ForeignEditionId, Is.EqualTo("hc:edition:11111-audiobook"));

            var ebookTuple = proxy.GetWorkInfo("gr:19079807", BookMediaType.Ebook);
            var ebookBook = ebookTuple.Item2;

            Assert.That(ebookBook.MediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(ebookBook.Editions, Has.Count.EqualTo(2));
            Assert.That(ebookBook.Editions.Single(e => e.Format == "ebook").ForeignEditionId, Is.EqualTo("hc:edition:11111-ebook"));
            Assert.That(ebookBook.Editions.Single(e => e.Format == "audiobook").ForeignEditionId, Is.EqualTo("hc:edition:22222-ebook"));
        }

        [Test]
        public void should_accept_raw_author_provider_ids_from_v5_work_lookup()
        {
            const string payload = @"{
  ""work"": { ""id"": 1, ""title"": ""Can't Hurt Me"", ""hardcoverWorkId"": ""431485"" },
  ""editions"": [
    { ""id"": 10, ""title"": ""Can't Hurt Me"", ""language"": ""en"", ""formatType"": ""audiobook"", ""readingFormatId"": 2, ""providerIds"": { ""hardcover"": ""2856"", ""amazon"": ""1544507852"" } }
  ],
  ""authors"": [ { ""id"": 20, ""name"": ""David Goggins"", ""providerIds"": { ""hardcover"": ""17977069"", ""goodreads"": 12345 } } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var result = proxy.GetWorkInfo("hc:431485", BookMediaType.Audiobook);

            Assert.That(result.Item2, Is.Not.Null);
            Assert.That(result.Item2.Author, Is.Not.Null);
            Assert.That(result.Item2.Author.HardcoverAuthorId, Is.EqualTo("hc:17977069"));
            Assert.That(result.Item2.Author.GoodreadsAuthorId, Is.EqualTo("gr:12345"));
        }

        [Test]
        public void should_preserve_asin_cluster_from_v5_work_lookup()
        {
            const string payload = @"{
  ""work"": { ""id"": ""gr:12345"", ""title"": ""Resolved Later"", ""goodreadsWorkId"": ""gr:12345"" },
  ""editions"": [
    {
      ""id"": ""az:B000BARE"",
      ""title"": ""Resolved Later"",
      ""language"": ""en"",
      ""formatType"": ""audiobook"",
      ""readingFormatId"": 2,
      ""asin"": ""b000fallback"",
      ""asins"": ["" b000bare "", ""B000OTHER"", ""b000bare""],
      ""providerIds"": { ""amazon"": ""az:B000BARE"" }
    }
  ],
  ""authors"": [ { ""id"": 20, ""name"": ""Test Author"", ""providerIds"": { ""gr"": ""gr:1"" } } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));

            var proxy = CreateProxy(httpClient);

            var result = proxy.GetWorkInfo("gr:12345", BookMediaType.Audiobook);
            var edition = result.Item2.Editions.Single();

            Assert.Multiple(() =>
            {
                Assert.That(edition.Asins, Is.EqualTo(new[] { "B000BARE", "B000OTHER" }));
                Assert.That(edition.Asin, Is.EqualTo("B000BARE"));
                Assert.That(edition.ForeignEditionId, Is.EqualTo("az:B000BARE-audiobook"));
            });
        }

        [Test]
        public void should_prefer_full_provider_id_arrays_from_v5_work_lookup_without_requiring_legacy_shape_change()
        {
            const string payload = @"{
  ""work"": {
    ""id"": ""hc:383236"",
    ""title"": ""Harry Potter and the Goblet of Fire"",
    ""goodreadsWorkId"": ""gr:231260754"",
    ""hardcoverWorkId"": ""hc:383236"",
    ""providerIds"": { ""gr"": ""gr:231260754"", ""hc"": ""hc:383236"" },
    ""providerIdsAll"": {
      ""gr"": [""gr:231260754"", ""gr:3046572""],
      ""hc"": [""hc:383236""],
      ""ol"": [],
      ""gb"": [],
      ""az"": []
    }
  },
  ""editions"": [
    {
      ""id"": ""hc:edition:22222"",
      ""title"": ""Harry Potter and the Goblet of Fire"",
      ""language"": ""en"",
      ""formatType"": ""audiobook"",
      ""readingFormatId"": 2,
      ""providerIds"": { ""gr"": ""gr:61304420"", ""hc"": ""hc:edition:22222"" },
      ""providerIdsAll"": {
        ""gr"": [""gr:61304420"", ""gr:110933""],
        ""hc"": [""hc:edition:22222""],
        ""ol"": [],
        ""gb"": [],
        ""az"": []
      }
    }
  ],
  ""authors"": [
    {
      ""id"": 20,
      ""name"": ""J.K. Rowling"",
      ""providerIds"": { ""gr"": ""gr:1077326"", ""hc"": ""hc:29004"" },
      ""providerIdsAll"": {
        ""gr"": [""gr:1077326"", ""gr:1244""],
        ""hc"": [""hc:29004""],
        ""ol"": [],
        ""gb"": [],
        ""az"": []
      }
    }
  ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var result = proxy.GetWorkInfo("gr:3046572", BookMediaType.Audiobook);

            Assert.That(result.Item2.GoodreadsWorkId, Is.EqualTo("gr:231260754"));
            Assert.That(result.Item2.RemoteProviderIds, Does.Contain("gr:231260754"));
            Assert.That(result.Item2.RemoteProviderIds, Does.Contain("gr:3046572"));
            Assert.That(result.Item2.Author.GoodreadsAuthorId, Is.EqualTo("gr:1077326"));
            Assert.That(result.Item2.Author.RemoteProviderIds, Does.Contain("gr:1077326"));
            Assert.That(result.Item2.Author.RemoteProviderIds, Does.Contain("gr:1244"));
        }

        [Test]
        public void should_accept_legacy_jarray_provider_ids_from_v5_work_lookup()
        {
            const string payload = @"{
  ""work"": {
    ""id"": ""hc:383236"",
    ""title"": ""Harry Potter and the Goblet of Fire"",
    ""providerIds"": {
      ""gr"": [""gr:231260754"", ""gr:3046572""],
      ""hc"": [""hc:383236""]
    }
  },
  ""editions"": [
    {
      ""id"": ""hc:edition:22222"",
      ""title"": ""Harry Potter and the Goblet of Fire"",
      ""language"": ""en"",
      ""formatType"": ""audiobook"",
      ""readingFormatId"": 2,
      ""providerIds"": {
        ""gr"": [""gr:61304420"", ""gr:110933""],
        ""hc"": [""hc:edition:22222""]
      }
    }
  ],
  ""authors"": [
    {
      ""id"": 20,
      ""name"": ""J.K. Rowling"",
      ""providerIds"": {
        ""gr"": [""gr:1077326"", ""gr:1244""],
        ""hc"": [""hc:29004""]
      }
    }
  ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var result = proxy.GetWorkInfo("gr:3046572", BookMediaType.Audiobook);

            Assert.That(result.Item2.RemoteProviderIds, Does.Contain("gr:231260754"));
            Assert.That(result.Item2.RemoteProviderIds, Does.Contain("gr:3046572"));
            Assert.That(result.Item2.Author.GoodreadsAuthorId, Is.EqualTo("gr:1077326"));
            Assert.That(result.Item2.Author.RemoteProviderIds, Does.Contain("gr:1077326"));
            Assert.That(result.Item2.Author.RemoteProviderIds, Does.Contain("gr:1244"));
        }

        [Test]
        public void should_return_empty_list_when_canonical_lookup_not_found()
        {
            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, "{}", HttpStatusCode.NotFound));

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

            var results = proxy.SearchForNewBook("hc:999999", author: null);

            Assert.That(results, Is.Empty);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_return_empty_list_when_canonical_lookup_not_ready()
        {
            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, "null", HttpStatusCode.Accepted));

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

            var results = proxy.SearchForNewBook("hc:12345", author: null);

            Assert.That(results, Is.Empty);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_preserve_declared_work_rescue_conflict_for_the_pending_request_handler()
        {
            const string reason = "Work rescue is blocked_safety_gate";
            var proxy = CreateProxy(new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader { ContentType = "text/plain" }, reason, HttpStatusCode.Conflict)));

            var exception = Assert.Throws<WorkRescueTerminalException>(() =>
                proxy.GetWorkInfo("gr:12345", BookMediaType.Ebook, "gr:author"));

            Assert.That(exception.ProviderId, Is.EqualTo("gr:12345"));
            Assert.That(exception.Message, Is.EqualTo(reason));
        }

        [Test]
        public void should_canonicalize_hardcover_work_id_when_v5_work_response_omits_prefix()
        {
            const string payload = @"{
  ""work"": { ""id"": 1, ""title"": ""Test Work"", ""hardcoverWorkId"": ""12345"", ""goodreadsWorkId"": null, ""openLibraryWorkId"": null },
  ""editions"": [ { ""id"": 10, ""title"": ""Test Edition"", ""languageCode"": ""eng"", ""formatType"": ""audiobook"", ""providerIds"": { ""hardcover"": ""11111"" } } ],
  ""authors"": [ { ""id"": 20, ""name"": ""Test Author"", ""providerIds"": { ""hc"": ""hc:999"" } } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
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

            var tuple = proxy.GetBookInfo("hc:12345");
            var authorKey = tuple.Item1;
            var book = tuple.Item2;

            Assert.That(authorKey, Is.EqualTo("hc:999"));
            Assert.That(book.HardcoverBookId, Is.EqualTo("hc:12345"));
            Assert.That(book.BaseBookId, Is.EqualTo("hc:12345"));
        }
    }
}
