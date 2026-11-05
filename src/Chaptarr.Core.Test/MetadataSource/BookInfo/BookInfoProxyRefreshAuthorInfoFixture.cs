using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using NLog;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Hardcover;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyRefreshAuthorInfoFixture
    {
        private class RecordingHttpClient : IHttpClient
        {
            private readonly string _payload;
            private readonly string _executePayload;
            private readonly HttpStatusCode _executeStatusCode;
            private readonly HttpStatusCode _getStatusCode;

            public RecordingHttpClient(string payload, string executePayload = @"{""changed"":[],""deleted"":[],""merged"":[]}", HttpStatusCode executeStatusCode = HttpStatusCode.OK, HttpStatusCode getStatusCode = HttpStatusCode.OK)
            {
                _payload = payload;
                _executePayload = executePayload;
                _executeStatusCode = executeStatusCode;
                _getStatusCode = getStatusCode;
            }

            public List<HttpRequest> Requests { get; } = new();

            public HttpResponse Get(HttpRequest request)
            {
                Requests.Add(request);

                var headers = new HttpHeader { ContentType = "application/json" };
                headers["ETag"] = "W/\"v1\"";

                return new HttpResponse(request, headers, _payload, _getStatusCode);
            }

            public HttpResponse Execute(HttpRequest request)
            {
                Requests.Add(request);

                var headers = new HttpHeader { ContentType = "application/json" };
                return new HttpResponse(request, headers, _executePayload, _executeStatusCode);
            }
            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> GetAsync(HttpRequest request) => System.Threading.Tasks.Task.FromResult(Get(request));
            public System.Threading.Tasks.Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public string MetadataServerUrl { get; set; } = "http://metadata";
            public bool BookshelfMatchCompatibility { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_MetadataServerUrl")
                {
                    return MetadataServerUrl;
                }

                if (targetMethod?.Name == "get_BookshelfMatchCompatibility")
                {
                    return BookshelfMatchCompatibility;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        private static IMetadataServerHealthGate CreateHealthGate(IConfigService configService)
        {
            var logger = LogManager.GetCurrentClassLogger();
            return new MetadataServerHealthGate(configService, new MetadataServerHealthService(logger), logger);
        }

        [TestCase("hc:123")]
        [TestCase("hardcover-author:123")]
        [TestCase("123")]
        public void should_refresh_native_hardcover_without_contacting_v5_or_reusing_its_etag(string authorId)
        {
            var native = new Mock<IHardcoverMetadataProxy>(MockBehavior.Strict);
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            native.Setup(x => x.GetAuthor("123", false)).Returns(new AuthorResource
            {
                ForeignId = 123,
                Name = "Test Author",
                Works = new List<WorkResource>(),
                Series = new List<SeriesResource>()
            });
            var proxy = CreateNativeRefreshProxy(native.Object, out var httpClient);

            var result = proxy.RefreshAuthorInfo(authorId, etag: "old-v5-etag", forceRefresh: true);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.Name, Is.EqualTo("Test Author"));
            Assert.That(result.Author.HardcoverAuthorId, Is.EqualTo("hc:123"));
            Assert.That(result.Author.Books, Is.Empty);
            Assert.That(result.ETag, Is.Null);
            Assert.That(httpClient.Requests, Is.Empty);
            native.Verify(x => x.GetAuthor("123", false), Times.Once);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_preserve_native_refresh_failure_without_falling_back_to_v5(bool notFound)
        {
            var native = new Mock<IHardcoverMetadataProxy>(MockBehavior.Strict);
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            if (notFound)
            {
                native.Setup(x => x.GetAuthor("123", false)).Throws(new AuthorNotFoundException("123"));
            }
            else
            {
                native.Setup(x => x.GetAuthor("123", false)).Throws(new InvalidOperationException("Provider unavailable"));
            }

            var proxy = CreateNativeRefreshProxy(native.Object, out var httpClient);
            var result = proxy.RefreshAuthorInfo("hc:123");

            Assert.That(result.Reason, Is.EqualTo(notFound ? RefreshReason.NotFound : RefreshReason.Error));
            Assert.That(result.Author, Is.Null);
            Assert.That(httpClient.Requests, Is.Empty);
        }

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public void should_return_independent_native_book_instances_for_both_format_profiles(bool compatibility, int readingFormatId)
        {
            var native = new Mock<IHardcoverMetadataProxy>();
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            native.Setup(x => x.GetAuthor("123", false)).Returns(new AuthorResource
            {
                ForeignId = 123,
                Name = "Test Author",
                Works = new List<WorkResource>
                {
                    new WorkResource
                    {
                        ForeignId = 456,
                        Title = "Test Work",
                        Genres = new List<string> { "Fiction" },
                        Books = new List<BookResource>
                        {
                            new BookResource { ForeignId = 789, Title = "Test Work", ReadingFormatId = readingFormatId }
                        }
                    }
                }
            });
            var proxy = CreateNativeRefreshProxy(native.Object, out _, compatibility);

            var result = proxy.RefreshAuthorInfo("hc:123");

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            var books = result.Author.Books;
            Assert.That(books.Select(book => book.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
            Assert.That(books.All(book => book.HardcoverBookId == "hc:456"), Is.True);
            Assert.That(books.All(book => book.Links.Single().Name == (compatibility ? "Book Editions" : "Hardcover")), Is.True);
            Assert.That(books[0].Editions.Single().ReadingFormatId, Is.EqualTo(compatibility && readingFormatId == 0 ? 2 : readingFormatId));
            Assert.That(books[1].Editions.Single().ReadingFormatId, Is.EqualTo(compatibility && readingFormatId == 0 ? 3 : readingFormatId));
            Assert.That(books[0].Editions.Single(), Is.Not.SameAs(books[1].Editions.Single()));
            books[0].Editions.Clear();
            Assert.That(books[1].Editions, Has.Count.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_schedule_native_authors_without_sending_them_to_v5_diff(bool mixedProviders)
        {
            var native = new Mock<IHardcoverMetadataProxy>(MockBehavior.Strict);
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            var proxy = CreateNativeRefreshProxy(native.Object, out var httpClient);
            var items = new List<NzbDrone.Core.MetadataSource.BookInfo.V5.V5AuthorETag>
            {
                new() { RequestedId = "hc:123", ETag = "previous-v5-etag" }
            };
            if (mixedProviders)
            {
                items.Add(new() { RequestedId = "gr:456", ETag = "goodreads-etag" });
            }

            var result = proxy.GetBulkAuthorChanges(items);

            Assert.That(result.Changed, Has.Count.EqualTo(1));
            Assert.That(result.Changed[0].RequestedId, Is.EqualTo("hc:123"));
            Assert.That(result.Changed[0].CanonicalId, Is.EqualTo("hc:123"));
            Assert.That(result.Changed[0].ETag, Is.Null);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(mixedProviders ? 1 : 0));
            if (mixedProviders)
            {
                var body = Encoding.UTF8.GetString(httpClient.Requests.Single().ContentData);
                Assert.That(body, Does.Contain("gr:456"));
                Assert.That(body, Does.Not.Contain("hc:123"));
            }
        }

        [TestCase(false, "hc:123", "Hardcover")]
        [TestCase(true, "123", "Goodreads")]
        public void should_preserve_source_author_metadata_only_for_migrated_libraries(bool compatibility, string slug, string linkName)
        {
            var native = new Mock<IHardcoverMetadataProxy>();
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            native.Setup(x => x.GetAuthor("123", false)).Returns(new AuthorResource
            {
                ForeignId = 123,
                Name = "Test Author",
                Url = "https://hardcover.app/authors/test-author"
            });
            var proxy = CreateNativeRefreshProxy(native.Object, out _, compatibility);

            var result = proxy.RefreshAuthorInfo("hc:123");

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.TitleSlug, Is.EqualTo(slug));
            Assert.That(result.Author.Links.Single().Name, Is.EqualTo(linkName));
            Assert.That(result.Author.HardcoverAuthorId, Is.EqualTo("hc:123"));
        }

        [TestCase(true, 602)]
        [TestCase(false, 0)]
        public void should_keep_source_catalogs_over_300_works_without_native_date_or_isbn_requirements(bool compatibility, int expectedBooks)
        {
            var native = new Mock<IHardcoverMetadataProxy>();
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            native.Setup(x => x.GetAuthor("123", false)).Returns(new AuthorResource
            {
                ForeignId = 123,
                Name = "Test Author",
                Works = Enumerable.Range(1, 301).Select(id => new WorkResource
                {
                    ForeignId = id,
                    Title = "Work " + id,
                    Books = new List<BookResource> { new() { ForeignId = 1000 + id, Title = "Work " + id } }
                }).ToList()
            });
            var proxy = CreateNativeRefreshProxy(native.Object, out _, compatibility);

            var result = proxy.RefreshAuthorInfo("hc:123");

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.Books, Has.Count.EqualTo(expectedBooks));
        }

        [TestCase("A Work (A Series, #1)", "A Work (A Series, #1)")]
        [TestCase(null, "Fallback Edition (A Series, #1)")]
        public void should_keep_source_work_titles_and_ignore_unusable_editions(string title, string expected)
        {
            var native = new Mock<IHardcoverMetadataProxy>();
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            native.Setup(x => x.GetAuthor("123", false)).Returns(new AuthorResource
            {
                ForeignId = 123,
                Name = "Test Author",
                Works = new List<WorkResource>
                {
                    new()
                    {
                        ForeignId = 456, Title = title,
                        Books = new List<BookResource>
                        {
                            null,
                            new() { ForeignId = 0, Title = "Invalid identity" },
                            new() { ForeignId = 789, Title = "Fallback Edition (A Series, #1)", RatingCount = 10 },
                            new() { ForeignId = 790, Title = null },
                            new() { ForeignId = 791, Title = "More popular edition", RatingCount = 20, AverageRating = 4 }
                        }
                    }
                }
            });
            var proxy = CreateNativeRefreshProxy(native.Object, out _, compatibility: true);

            var result = proxy.RefreshAuthorInfo("hc:123");

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.Books.Select(book => book.Title), Is.All.EqualTo(expected));
            Assert.That(result.Author.Books, Has.Count.EqualTo(2));
            foreach (var book in result.Author.Books)
            {
                Assert.That(book.Editions.Select(edition => edition.HardcoverEditionId), Is.EquivalentTo(new[] { "789", "791" }));
                Assert.That(book.Editions.Single(edition => edition.Monitored).HardcoverEditionId, Is.EqualTo("791"));
            }
        }

        [TestCase(true, "123", "456", "789", "8", "Goodreads", "Book Editions", "Goodreads Book", "_SX50_")]
        [TestCase(false, "hc:123", "hc:456", "hc:edition:789", "hc:8", "Hardcover", "Hardcover", "Hardcover", "_SX500_")]
        public void should_preserve_source_metadata_presentation_without_rewriting_native_defaults(bool compatibility,
            string authorSlug, string bookSlug, string editionSlug, string seriesSlug,
            string authorLink, string bookLink, string editionLink, string imageSize)
        {
            const string image = "https://images.gr-assets.com/books/123._SX50_.jpg";
            var native = new Mock<IHardcoverMetadataProxy>();
            native.SetupGet(x => x.IsNativeEnabled).Returns(true);
            native.Setup(x => x.GetAuthor("123", false)).Returns(new AuthorResource
            {
                ForeignId = 123, Name = "Test Author", ImageUrl = image, Url = "https://hardcover.app/authors/test-author",
                Series = new List<SeriesResource> { new() { ForeignId = 8, Title = "Test Series" } },
                Works = new List<WorkResource>
                {
                    new() { ForeignId = 456, Title = "Test Work", Books = new List<BookResource>
                    {
                        new() { ForeignId = 789, Title = "Test Edition", ImageUrl = image }
                    } }
                }
            });
            var proxy = CreateNativeRefreshProxy(native.Object, out _, compatibility);

            var result = proxy.RefreshAuthorInfo("hc:123");

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.TitleSlug, Is.EqualTo(authorSlug));
            Assert.That(result.Author.Links.Single().Name, Is.EqualTo(authorLink));
            Assert.That(result.Author.Images.Single().Url, Does.Contain(imageSize));
            Assert.That(result.Author.Series.Select(series => series.TitleSlug), Is.All.EqualTo(seriesSlug));
            foreach (var book in result.Author.Books)
            {
                Assert.That(book.TitleSlug, Is.EqualTo(bookSlug));
                Assert.That(book.Links.Single().Name, Is.EqualTo(bookLink));
                Assert.That(book.Editions.Single().TitleSlug, Is.EqualTo(editionSlug));
                Assert.That(book.Editions.Single().Links.Single().Name, Is.EqualTo(editionLink));
                Assert.That(book.Editions.Single().Images.Single().Url, Does.Contain(imageSize));
            }
        }

        private static BookInfoProxy CreateNativeRefreshProxy(IHardcoverMetadataProxy native, out RecordingHttpClient httpClient, bool compatibility = false)
        {
            httpClient = new RecordingHttpClient("{}");
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            ((ConfigServiceProxy)(object)config).BookshelfMatchCompatibility = compatibility;
            var preferences = new Mock<IAdditionalBookMetadataProxy>();
            preferences.Setup(x => x.ApplyAuthorImageSourcePreference(It.IsAny<Author>(), It.IsAny<Book>()))
                .Returns((Author author, Book book) => author);
            return new BookInfoProxy(httpClient, null, null, null, null, null, null, null,
                config, null, LogManager.GetCurrentClassLogger(), new CacheManager(), CreateHealthGate(config),
                preferences.Object, native);
        }

        [Test]
        public void should_always_use_golden_author_endpoint_and_etag_validation()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(payload);
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

            _ = proxy.RefreshAuthorInfo("hc:123", etag: "W/\"v0\"", forceRefresh: false);
            _ = proxy.RefreshAuthorInfo("hc:123", etag: "W/\"v0\"", forceRefresh: true);

            Assert.That(httpClient.Requests, Has.Count.EqualTo(2));

            var normalRequest = httpClient.Requests[0];
            Assert.That(normalRequest.Url.ToString(), Does.Contain("/api/v5/author?id="));
            Assert.That(normalRequest.Url.ToString(), Does.Not.Contain("/api/v5/authorsync/"));
            Assert.That(normalRequest.Headers.GetSingleValue("If-None-Match"), Is.EqualTo("W/\"v0\""));
            Assert.That(normalRequest.Headers.GetSingleValue("X-Force-Refresh"), Is.Null);

            var forcedRequest = httpClient.Requests[1];
            Assert.That(forcedRequest.Url.ToString(), Does.Contain("/api/v5/author?id="));
            Assert.That(forcedRequest.Url.ToString(), Does.Not.Contain("/api/v5/authorsync/"));
            Assert.That(forcedRequest.Url.ToString(), Does.Contain("snapshot="));
            Assert.That(forcedRequest.Headers.GetSingleValue("X-Force-Refresh"), Is.Null);
            Assert.That(forcedRequest.Headers.GetSingleValue("If-None-Match"), Is.EqualTo("W/\"v0\""));
        }

        [Test]
        public void should_map_nested_v5_author_dates_to_domain_status_without_using_server_status()
        {
            const string payload = @"{
  ""author"": {
    ""id"": ""hc:240859"",
    ""name"": ""Eno Raud"",
    ""sortName"": ""Eno Raud"",
    ""slug"": ""eno-raud"",
    ""birthDate"": ""1928-02-15"",
    ""deathDate"": ""1996-07-09"",
    ""status"": ""alive""
  },
  ""books"": [],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(payload);
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

            var result = proxy.RefreshAuthorInfo("hc:240859", forceRefresh: true);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.Born, Is.EqualTo(new DateTime(1928, 2, 15)));
            Assert.That(result.Author.Died, Is.EqualTo(new DateTime(1996, 7, 9)));
            Assert.That(result.Author.Status, Is.EqualTo(AuthorStatusType.Ended));
        }

        [Test]
        public void should_bypass_etag_validation_for_local_hydration_refresh_without_server_rebuild()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(payload);
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

            _ = proxy.RefreshAuthorInfo("hc:123", etag: "W/\"v0\"", forceRefresh: true, bypassEtag: true);

            var request = httpClient.Requests[0];
            Assert.That(request.Url.ToString(), Does.Contain("/api/v5/author?id="));
            Assert.That(request.Url.ToString(), Does.Not.Contain("/api/v5/authorsync/"));
            Assert.That(request.Url.ToString(), Does.Contain("snapshot="));
            Assert.That(request.Headers.GetSingleValue("X-Force-Refresh"), Is.Null);
            Assert.That(request.Headers.GetSingleValue("If-None-Match"), Is.Null);
        }

        [Test]
        public void should_use_scoped_v5_author_diff_endpoint()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [],
  ""series"": []
}";
            const string diffPayload = "{\"changed\":[{\"requestedId\":\"hc:123\",\"canonicalId\":\"hc:123\",\"etag\":\"W/\\\"v2\\\"\"}],\"deleted\":[],\"merged\":[]}";

            var httpClient = new RecordingHttpClient(payload, diffPayload);
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

            var response = proxy.GetBulkAuthorChanges(new List<NzbDrone.Core.MetadataSource.BookInfo.V5.V5AuthorETag>
            {
                new()
                {
                    RequestedId = "hc:123",
                    ETag = "W/\"v1\""
                }
            });

            Assert.That(response.Changed, Has.Count.EqualTo(1));

            var request = httpClient.Requests[^1];
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Url.ToString(), Does.Contain("/api/v5/authors/diff"));

            var body = Encoding.UTF8.GetString(request.ContentData);
            var json = JObject.Parse(body);
            Assert.That(json["items"], Is.Not.Null);
            Assert.That(json["authors"], Is.Null);
            Assert.That(json["items"]?[0]?["requestedId"]?.Value<string>(), Is.EqualTo("hc:123"));
            Assert.That(json["items"]?[0]?["etag"]?.Value<string>(), Is.EqualTo("W/\"v1\""));
        }

        [Test]
        public void should_skip_v5_author_refresh_when_metadata_server_circuit_is_open()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [],
  ""series"": []
}";

            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new RecordingHttpClient(payload);
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
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
                requestBuilder: null,
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: healthGate);

            var result = proxy.RefreshAuthorInfo("hc:123", etag: null, forceRefresh: true);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Error));
            Assert.That(result.Message, Does.Contain("temporarily unavailable"));
            Assert.That(httpClient.Requests, Is.Empty);
        }

        [Test]
        public void should_open_metadata_server_circuit_on_v5_bulk_diff_server_error()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [],
  ""series"": []
}";

            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new RecordingHttpClient(payload, executePayload: "gateway error", executeStatusCode: HttpStatusCode.ServiceUnavailable);
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var healthService = new MetadataServerHealthService(logger);
            var healthGate = new MetadataServerHealthGate(configService, healthService, logger);

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
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: healthGate);

            var response = proxy.GetBulkAuthorChanges(new List<NzbDrone.Core.MetadataSource.BookInfo.V5.V5AuthorETag>
            {
                new()
                {
                    RequestedId = "hc:123",
                    ETag = "W/\"v1\""
                }
            });

            Assert.That(response, Is.Null);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(healthService.TryBeginRequest(healthGate.SourceName, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));
        }

        [Test]
        public void should_preserve_duplicate_v5_book_pockets_from_author_payload()
        {
            const string payload = @"{
  ""author"": { ""id"": ""hc:123"", ""name"": ""Test Author"", ""sortName"": ""Author, Test"", ""slug"": ""test-author"", ""ratingAverage"": 0, ""ratingCount"": 0 },
  ""books"": [
    {
      ""id"": ""hc:first"",
      ""base_book_id"": ""hc:duplicate"",
      ""hardcoverBookId"": ""hc:shared-provider"",
      ""title"": ""First Pocket"",
      ""editions"": [
        { ""id"": ""hc:firstedition"", ""title"": ""First Edition"", ""languageCode"": ""eng"", ""formatType"": ""audiobook"", ""readingFormatId"": 2 }
      ]
    },
    {
      ""id"": ""hc:second"",
      ""base_book_id"": ""hc:duplicate"",
      ""hardcoverBookId"": ""hc:shared-provider"",
      ""title"": ""Second Pocket"",
      ""editions"": [
        { ""id"": ""hc:secondedition"", ""title"": ""Second Edition"", ""languageCode"": ""eng"", ""formatType"": ""ebook"", ""readingFormatId"": 3 }
      ]
    }
  ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(payload);
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

            var result = proxy.RefreshAuthorInfo("hc:123", etag: null, forceRefresh: true);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.Books, Has.Count.EqualTo(4));
            Assert.That(result.Author.Books.Count(b => b.MediaType == BookMediaType.Audiobook), Is.EqualTo(2));
            Assert.That(result.Author.Books.Count(b => b.MediaType == BookMediaType.Ebook), Is.EqualTo(2));
            Assert.That(result.Author.Books.Select(b => b.Title), Is.EquivalentTo(new[] { "First Pocket", "First Pocket", "Second Pocket", "Second Pocket" }));

            var editionIds = result.Author.Books.SelectMany(b => b.Editions).Select(e => e.ForeignEditionId).ToList();
            Assert.That(editionIds, Is.EquivalentTo(new[]
            {
                "hc:firstedition-audiobook",
                "hc:firstedition-ebook",
                "hc:secondedition-audiobook",
                "hc:secondedition-ebook"
            }));
        }

        [Test]
        public void should_discard_known_placeholder_and_keep_real_author_photos_from_v5()
        {
            const string placeholder = "https://assets.hardcover.app/author/910005/provider-default.jpg";
            const string goodreadsPhoto = "https://images.example/goodreads-author.jpg";
            const string audnexusPhoto = "https://images.example/audnexus-author.jpg";
            NzbDrone.Core.MediaCover.MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e");
            const string payload = @"{
  ""author"": {
    ""id"": ""hc:910005"",
    ""name"": ""Example Author"",
    ""sortName"": ""Author, Example"",
    ""slug"": ""example-author"",
    ""ratingAverage"": 0,
    ""ratingCount"": 0,
    ""photos"": [
      { ""provider"": ""hardcover"", ""isPrimary"": true, ""url"": ""https://assets.hardcover.app/author/910005/provider-default.jpg"" },
      { ""provider"": ""goodreads"", ""isPrimary"": false, ""url"": ""https://images.example/goodreads-author.jpg"" },
      { ""provider"": ""audnexus"", ""isPrimary"": false, ""url"": ""https://images.example/audnexus-author.jpg"" }
    ]
  },
  ""books"": [],
  ""series"": []
}";
            var httpClient = new RecordingHttpClient(payload);
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

            var result = proxy.RefreshAuthorInfo("hc:910005", forceRefresh: true);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Updated), result.Message);
            Assert.That(result.Author.Images.Select(image => image.Url), Is.EqualTo(new[]
            {
                goodreadsPhoto,
                audnexusPhoto
            }));
            Assert.That(result.Author.Images.All(image => image.Url != placeholder), Is.True);
        }

        [Test]
        public void should_preserve_existing_author_on_typed_terminal_refresh()
        {
            const string terminal = @"{
  ""code"": ""author_identity_ambiguous"",
  ""providerId"": ""hc:123"",
  ""message"": ""Identity evidence is ambiguous."",
  ""retryable"": false,
  ""reopenable"": true
}";

            var httpClient = new RecordingHttpClient(terminal, getStatusCode: HttpStatusCode.Conflict);
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

            var result = proxy.RefreshAuthorInfo("hc:123", etag: "W/\"served\"", forceRefresh: true);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.NotModified));
            Assert.That(result.ETag, Is.EqualTo("W/\"served\""));
            Assert.That(result.Reason, Is.Not.EqualTo(RefreshReason.NotFound));
        }

        [Test]
        public void should_surface_the_same_typed_terminal_from_sync_and_async_v5_clients()
        {
            const string terminal = @"{
  ""code"": ""author_identity_ambiguous"",
  ""providerId"": ""hc:123"",
  ""message"": ""Identity evidence is ambiguous."",
  ""retryable"": false,
  ""reopenable"": true
}";

            var httpClient = new RecordingHttpClient(terminal, getStatusCode: HttpStatusCode.Conflict);
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

            var sync = Assert.Throws<AuthorTerminalException>(() => proxy.GetAuthorInfo("hc:123", useCache: false));
            var async = Assert.ThrowsAsync<AuthorTerminalException>(async () => await proxy.GetAuthorInfoAsync("hc:123", useCache: false));

            Assert.That(sync.Code, Is.EqualTo("author_identity_ambiguous"));
            Assert.That(async.Code, Is.EqualTo(sync.Code));
            Assert.That(async.Reopenable, Is.True);
        }

        [Test]
        public void should_reject_unknown_author_terminal_conflict()
        {
            const string terminal = @"{
  ""code"": ""provider_redirect_future_state"",
  ""providerId"": ""hc:123"",
  ""message"": ""Unknown redirect state."",
  ""retryable"": false,
  ""reopenable"": true
}";

            var httpClient = new RecordingHttpClient(terminal, getStatusCode: HttpStatusCode.Conflict);
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

            Assert.Throws<BookInfoException>(() => proxy.GetAuthorInfo("hc:123", useCache: false));
        }

    }
}
