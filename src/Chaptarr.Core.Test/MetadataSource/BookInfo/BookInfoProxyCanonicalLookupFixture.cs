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

        internal static BookInfoProxy CreateProxy(IHttpClient httpClient)
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

        internal const string SecondaryEditionAliasesPayload = """
        {
          "work": {
            "id": "hc:312460", "title": "Dune", "hardcoverBookId": "hc:312460",
            "goodreadsWorkId": "gr:3634639",
            "editions": [
              { "id": "hc:edition:12250068", "title": "Dune", "format": "physical", "readingFormatId": 1,
                "provider_ids_all": { "gr": ["gr:1000049", "gr:44767458"],
                  "hc": ["hc:edition:12250068", "hc:edition:12250069"] } },
              { "id": "hc:edition:90000000", "title": "Other edition", "format": "audiobook", "readingFormatId": 2,
                "provider_ids_all": { "gr": ["gr:80000000"], "hc": ["hc:edition:90000000"] } }
            ]
          },
          "editions": [
            { "id": "hc:edition:12250068", "title": "Dune", "format": "physical", "readingFormatId": 1,
              "provider_ids_all": { "gr": ["gr:1000049", "gr:44767458"],
                "hc": ["hc:edition:12250068", "hc:edition:12250069"] } },
            { "id": "hc:edition:90000000", "title": "Other edition", "format": "audiobook", "readingFormatId": 2,
              "provider_ids_all": { "gr": ["gr:80000000"], "hc": ["hc:edition:90000000"] } }
          ],
          "authors": [{ "id": 20, "name": "Frank Herbert", "providerIds": { "gr": "gr:58" } }],
          "series": []
        }
        """;

        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        public void exact_secondary_alias_keeps_canonical_identity_and_requested_media(BookMediaType mediaType, bool allEditions)
        {
            var client = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, SecondaryEditionAliasesPayload, HttpStatusCode.OK));
            var proxy = CreateProxy(client);
            var direct = proxy.GetEditionInfo("gr:44767458", mediaType).Item2;
            Assert.That(direct.Editions.Single(e => e.ForeignEditionId.Contains("12250068")).RemoteProviderIds, Does.Contain("gr:44767458"));
            foreach (var term in new[] { "gr-ed:44767458", "hc-ed:12250069", "hc:edition:12250069" })
            {
                var book = proxy.SearchForNewBook(term, null, allEditions, mediaType).Single();
                var edition = book.Editions.Single(e => BookEditionIdentity.EditionMatchesProviderId(e, term));
                Assert.Multiple(() =>
                {
                    Assert.That(book.MediaType, Is.EqualTo(mediaType));
                    Assert.That(edition.ForeignEditionId, Is.EqualTo("hc:edition:12250068-" + mediaType.ToString().ToLowerInvariant()));
                    Assert.That(edition.GoodreadsEditionId, Is.Null);
                    Assert.That(edition.HardcoverEditionId, Is.EqualTo("12250068"));
                    Assert.That(edition.ReadingFormatId, Is.EqualTo(1));
                    Assert.That(BookEditionIdentity.EditionMatchesProviderId(edition, "gr:3634639"), Is.False);
                    Assert.That(book.Editions, Has.Count.EqualTo(allEditions ? 2 : 1));
                    Assert.That(edition.RemoteProviderIds, Is.EquivalentTo(new[]
                        { "gr:1000049", "gr:44767458", "hc:edition:12250068", "hc:edition:12250069" }));
                });
            }
            Assert.That(client.Requests, Has.Count.EqualTo(4));
        }

        [Test]
        public void edition_aliases_are_copied_without_changing_pins_or_retaining_withdrawn_ids()
        {
            var source = CreateProxy(new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, SecondaryEditionAliasesPayload, HttpStatusCode.OK)))
                .SearchForNewBook("gr-ed:44767458", null, false, BookMediaType.Ebook).Single().Editions.Single();
            source.ManualAdd = true;
            var clone = RefreshEntityCopy.CloneEdition(source);
            Assert.That(BookEditionIdentity.EditionMatchesProviderId(clone, "gr-ed:44767458"), Is.True);
            clone.RemoteProviderIds.Remove("gr:44767458");
            Assert.That(source.RemoteProviderIds, Does.Contain("gr:44767458"));
            Assert.That(clone.ManualAdd, Is.True);
            var target = new Edition { ManualAdd = true, Monitored = true };
            target.UseMetadataFrom(source);
            Assert.That(BookEditionIdentity.EditionMatchesProviderId(target, "gr-ed:44767458"), Is.True);
            Assert.That(target.RemoteProviderIds, Is.Not.SameAs(source.RemoteProviderIds));
            source.RemoteProviderIds = new HashSet<string>();
            target.UseMetadataFrom(source);
            Assert.That(BookEditionIdentity.EditionMatchesProviderId(target, "gr-ed:44767458"), Is.False);
            Assert.That(target.ManualAdd, Is.True);
            Assert.That(target.Monitored, Is.True);
        }

        [TestCase("gr-ed:44767458", "gr:44767458")]
        [TestCase("hc-ed:12250069", "hc:12250069")]
        [TestCase("hc:edition:12250069", "hc:12250069")]
        [TestCase(" HC-ED:12250069 ", "hc:12250069")]
        [TestCase("hc:edition:12250069-audiobook", "hc:12250069")]
        [TestCase(" HC:EDITION:12250069-ebook ", "hc:12250069")]
        public void direct_edition_lookup_should_accept_the_same_identifiers_as_public_search(string term, string canonicalId)
        {
            var client = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, SecondaryEditionAliasesPayload, HttpStatusCode.OK));
            var proxy = CreateProxy(client);

            foreach (var mediaType in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                var direct = proxy.GetEditionInfo(term, mediaType).Item2;
                var search = proxy.SearchForNewBook(term, null, true, mediaType).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(direct.MediaType, Is.EqualTo(mediaType));
                    Assert.That(direct.HardcoverBookId, Is.EqualTo("hc:312460"));
                    Assert.That(direct.Editions.Any(e => BookEditionIdentity.EditionMatchesProviderId(e, term)), Is.True);
                    Assert.That(direct.Editions.Select(e => e.ForeignEditionId),
                        Is.EquivalentTo(search.Editions.Select(e => e.ForeignEditionId)));
                });
            }

            Assert.That(client.Requests, Has.Count.EqualTo(4));
            Assert.That(client.Requests.Select(req => Uri.UnescapeDataString(req.Url.ToString())),
                Is.All.EqualTo("http://metadata/api/v5/book/" + canonicalId));
        }

        [Test]
        public void malformed_legacy_edition_lookup_should_fail_before_contacting_metadata()
        {
            var client = new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called"));
            Assert.Throws<InvalidProviderIdException>(() => CreateProxy(client).GetEditionInfo("hc:edition:"));
            Assert.That(client.Requests, Is.Empty);
        }

        [TestCase("hc")]
        [TestCase("gr")]
        public void same_number_work_and_edition_lookups_should_keep_their_own_routes(string provider)
        {
            var client = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, "{}", HttpStatusCode.NotFound));
            var proxy = CreateProxy(client);

            Assert.Throws<BookNotFoundException>(() => proxy.GetWorkInfo(provider + ":123"));
            Assert.Throws<BookNotFoundException>(() => proxy.GetEditionInfo(provider + "-ed:123"));
            Assert.That(proxy.SearchForNewBook(provider + ":123", null), Is.Empty);
            Assert.That(proxy.SearchForNewBook(provider + "-ed:123", null), Is.Empty);

            Assert.That(client.Requests.Select(req => Uri.UnescapeDataString(req.Url.ToString())), Is.EqualTo(new[]
            {
                "http://metadata/api/v5/work/" + provider + ":123",
                "http://metadata/api/v5/book/" + provider + ":123",
                "http://metadata/api/v5/work/" + provider + ":123",
                "http://metadata/api/v5/book/" + provider + ":123"
            }));
        }

        [TestCase("hc-ed:123")]
        [TestCase("gr-ed:123")]
        [TestCase("hc:edition:123")]
        public void work_lookup_should_reject_an_explicit_edition_without_contacting_metadata(string term)
        {
            var client = new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called"));
            Assert.Throws<InvalidProviderIdException>(() => CreateProxy(client).GetWorkInfo(term));
            Assert.That(client.Requests, Is.Empty);
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
                },
                null
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

        [TestCase("goodreads-search")]
        [TestCase("hardcover")]
        [TestCase("audible")]
        public void provider_book_keys_should_preserve_signed_numeric_titles_with_unicode_padding(string provider)
        {
            const string title = "\u2003+00123\u2003";
            var proxy = CreateProxy(new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called")));
            var method = provider switch
            {
                "goodreads-search" => "CreateBookFromSearchResult",
                "hardcover" => "ConvertHardcoverBook",
                _ => "MapAudibleCatalogBookToDomain"
            };
            object input = provider switch
            {
                "goodreads-search" => new SearchJsonResource
                {
                    BookId = 90001, WorkId = 90002, Title = title,
                    Author = new AuthorJsonResource { Id = 90003, Name = "Fixture Author" }
                },
                "hardcover" => new NzbDrone.Core.MetadataSource.Hardcover.HardcoverBookResult { Id = "90001", Title = title },
                _ => new NzbDrone.Core.MetadataSource.Audible.Resources.AudibleCatalogBookResource
                {
                    Asin = "B00BOOK001", Title = title,
                    Authors = new List<NzbDrone.Core.MetadataSource.Audible.Resources.AudibleCatalogAuthorResource>
                    {
                        new() { Asin = "B00AUTH001", Name = "Fixture Author" }
                    }
                }
            };
            var mapper = typeof(BookInfoProxy).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            var book = (Book)mapper.Invoke(proxy, provider == "goodreads-search" ? new[] { input, null } : new[] { input });

            Assert.Multiple(() =>
            {
                Assert.That(book.Title, Is.EqualTo(title));
                Assert.That(book.CleanTitle, Is.EqualTo("+00123"));
            });
        }

        [Test]
        public void audible_book_key_should_derive_from_the_existing_full_stored_title()
        {
            var proxy = CreateProxy(new RecordingHttpClient(_ => throw new AssertionException("HTTP should not be called")));
            var mapper = typeof(BookInfoProxy).GetMethod("MapAudibleCatalogBookToDomain", BindingFlags.Instance | BindingFlags.NonPublic);
            var book = (Book)mapper.Invoke(proxy, new object[]
            {
                new NzbDrone.Core.MetadataSource.Audible.Resources.AudibleCatalogBookResource
                {
                    Asin = "B00BOOK001", Title = "The Book Title", Subtitle = "A Subtitle",
                    Authors = new List<NzbDrone.Core.MetadataSource.Audible.Resources.AudibleCatalogAuthorResource>
                    {
                        new() { Asin = "B00AUTH001", Name = "Fixture Author" }
                    }
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(book.Title, Is.EqualTo("The Book Title: A Subtitle"));
                Assert.That(book.Editions.Single().Title, Is.EqualTo(book.Title));
                Assert.That(book.CleanTitle, Is.EqualTo("thebooktitleasubtitle"));
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

        [TestCase("edition:58613424", "gr:58613424", BookMediaType.Audiobook)]
        [TestCase("gr-ed:58613424", "gr:58613424", BookMediaType.Ebook)]
        [TestCase("hc-ed:11111", "hc:11111", BookMediaType.Ebook)]
        [TestCase("hc:edition:11111", "hc:11111", BookMediaType.Audiobook)]
        public void explicit_edition_uses_canonical_lookup_and_keeps_requested_media(string term, string editionId, BookMediaType mediaType)
        {
            var http = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" },
                editionId.StartsWith("hc:") ? HardcoverWorkPayload : GoodreadsWorkPayload, HttpStatusCode.OK));
            var result = CreateProxy(http).SearchForNewBook(term, null, getAllEditions: false, mediaType: mediaType);
            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].MediaType, Is.EqualTo(mediaType));
                Assert.That(BookEditionIdentity.EditionMatchesProviderId(result[0].Editions.Single(), editionId), Is.True);
                Assert.That(result[0].Editions.Single().Monitored, Is.True);
                Assert.That(result[0].Editions.Single().Book, Is.SameAs(result[0]));
                Assert.That(http.Requests, Has.Count.EqualTo(1));
                Assert.That(Uri.UnescapeDataString(http.Requests[0].Url.ToString()), Does.Contain("/api/v5/book/" + editionId));
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void trimming_v5_edition_keeps_full_catalog_aliases_and_requested_media(BookMediaType mediaType)
        {
            var http = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, SecondaryEditionAliasesPayload, HttpStatusCode.OK));
            var proxy = CreateProxy(http);
            var trimmed = proxy.SearchForNewBook("gr-ed:44767458", null, getAllEditions: false, mediaType: mediaType).Single();
            var complete = proxy.SearchForNewBook("gr-ed:44767458", null, mediaType: mediaType).Single();
            var alias = proxy.SearchForNewBook("hc-ed:12250069", null, getAllEditions: false, mediaType: mediaType).Single();
            var edition = proxy.GetEditionInfo("gr:44767458", mediaType).Item2;
            var work = proxy.GetWorkInfo("gr:3634639", mediaType).Item2;
            Assert.Multiple(() =>
            {
                Assert.That(trimmed.MediaType, Is.EqualTo(mediaType));
                Assert.That(trimmed.Editions, Has.Count.EqualTo(1));
                Assert.That(trimmed.Editions.Single().Monitored, Is.True);
                Assert.That(trimmed.Editions.Single().Book, Is.SameAs(trimmed));
                Assert.That(BookEditionIdentity.EditionMatchesProviderId(trimmed.Editions.Single(), "gr:44767458"), Is.True);
                Assert.That(BookEditionIdentity.EditionMatchesProviderId(alias.Editions.Single(), "hc:12250069"), Is.True);
                Assert.That(complete.Editions, Has.Count.EqualTo(2));
                Assert.That(edition.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(complete.Editions.Select(e => e.ForeignEditionId)));
                Assert.That(work.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(complete.Editions.Select(e => e.ForeignEditionId)));
                Assert.That(edition.MediaType, Is.EqualTo(mediaType));
                Assert.That(work.MediaType, Is.EqualTo(mediaType));
                Assert.That(http.Requests, Has.Count.EqualTo(5));
            });
        }

        [Test]
        public void explicit_edition_not_in_response_does_not_become_a_work_request()
        {
            var http = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, HardcoverWorkPayload, HttpStatusCode.OK));
            Assert.That(CreateProxy(http).SearchForNewBook("hc-ed:383236", null, mediaType: BookMediaType.Ebook), Is.Empty);
            Assert.That(http.Requests, Has.Count.EqualTo(1));
            Assert.That(http.Requests[0].Url.ToString(), Does.Contain("/api/v5/book/"));
        }

        [Test]
        public void legacy_work_term_uses_the_same_canonical_work_lookup()
        {
            var http = new RecordingHttpClient(req => new HttpResponse(req,
                new HttpHeader { ContentType = "application/json" }, GoodreadsWorkPayload, HttpStatusCode.OK));
            var result = CreateProxy(http).SearchForNewBook("work:3046572", null);
            Assert.That(result.Select(book => book.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
            Assert.That(http.Requests, Has.Count.EqualTo(1));
            Assert.That(Uri.UnescapeDataString(http.Requests[0].Url.ToString()), Does.Contain("/api/v5/work/gr:3046572"));
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
        public void should_map_v5_work_author_dates_to_domain_status_without_using_server_status()
        {
            const string payload = @"{
  ""work"": { ""id"": ""gr:3046572"", ""title"": ""Harry Potter and the Goblet of Fire"", ""goodreadsWorkId"": ""gr:3046572"" },
  ""editions"": [ { ""id"": 10, ""title"": ""Harry Potter and the Goblet of Fire"", ""language"": ""en"", ""formatType"": ""audiobook"", ""providerIds"": { ""goodreads"": ""gr:58613424"" } } ],
  ""authors"": [ {
    ""name"": ""J.K. Rowling"",
    ""birthDate"": ""1965-07-31"",
    ""deathDate"": ""1996-07-09"",
    ""status"": ""alive"",
    ""providerIds"": { ""gr"": ""gr:1077326"" }
  } ],
  ""series"": []
}";

            var httpClient = new RecordingHttpClient(req =>
                new HttpResponse(req, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK));

            var proxy = CreateProxy(httpClient);

            var result = proxy.GetWorkInfo("gr:3046572", BookMediaType.Audiobook);
            var author = result.Item3.Single();

            Assert.That(author.Born, Is.EqualTo(new DateTime(1965, 7, 31)));
            Assert.That(author.Died, Is.EqualTo(new DateTime(1996, 7, 9)));
            Assert.That(author.Status, Is.EqualTo(AuthorStatusType.Ended));
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
            Assert.That(audio.GoodreadsEditionId, Is.Null);
            Assert.That(audio.RemoteProviderIds, Does.Contain("gr:61304420"));
            Assert.That(BookEditionIdentity.EditionMatchesProviderId(audio, "gr:61304420"), Is.True);
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
        public void should_report_unavailable_when_canonical_lookup_not_ready()
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

            var error = Assert.Throws<BookInfoException>(() => proxy.SearchForNewBook("hc:12345", author: null));

            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_preserve_declared_work_rescue_conflict_for_the_pending_request_handler()
        {
            const string reason = "Work rescue is blocked_safety_gate";
            var proxy = CreateProxy(new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader { ContentType = "application/json" },
                    BookInfoProxyLookupFailureFixture.TypedLookupPayload("work_rescue_terminal", "gr:12345", reason), HttpStatusCode.Conflict)));

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
