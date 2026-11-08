using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public partial class BookInfoProxyLookupFailureFixture
    {
        public enum LookupKind { Work, Edition, Legacy }
        public enum TransportKind { Http, Web, Managed }

        private sealed class RecordingHttpClient : IHttpClient
        {
            private readonly Func<HttpRequest, HttpResponse> _handler;
            public List<HttpRequest> Requests { get; } = new();

            public RecordingHttpClient(Func<HttpRequest, HttpResponse> handler) => _handler = handler;
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

        private static HttpResponse Response(HttpRequest request, HttpStatusCode status, string payload = "null") =>
            new(request, new HttpHeader { ContentType = "application/json", ["Retry-After"] = "0" }, payload, status);

        internal static string TypedLookupPayload(string code, string providerId, string message) =>
            JsonConvert.SerializeObject(new { code, providerId, message, retryable = false });

        private static Tuple<string, Book, List<Author>> Lookup(BookInfoProxy proxy, LookupKind kind, BookMediaType media = BookMediaType.Audiobook) => kind switch
        {
            LookupKind.Work => proxy.GetWorkInfo(" GR:3634639 ", media, "gr:58"),
            LookupKind.Legacy => proxy.GetBookInfo(" GR:3634639 ", media, "gr:58"),
            _ => proxy.GetEditionInfo(" GR-ED:44767458 ", media)
        };

        private static void AssertRequests(RecordingHttpClient client, LookupKind kind, int count = 1)
        {
            Assert.That(client.Requests, Has.Count.EqualTo(count));
            Assert.That(client.Requests.Select(request => Uri.UnescapeDataString(request.Url.ToString())),
                Is.All.EqualTo(kind == LookupKind.Edition
                    ? "http://metadata/api/v5/book/gr:44767458"
                    : "http://metadata/api/v5/work/gr:3634639?author=gr:58"));
        }

        [TestCase(LookupKind.Work)]
        [TestCase(LookupKind.Edition)]
        [TestCase(LookupKind.Legacy)]
        public void true_404_remains_not_found(LookupKind kind)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.NotFound));
            Assert.Throws<BookNotFoundException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work, "null")]
        [TestCase(LookupKind.Edition, "null")]
        [TestCase(LookupKind.Legacy, "null")]
        [TestCase(LookupKind.Work, "not JSON")]
        [TestCase(LookupKind.Edition, "not JSON")]
        [TestCase(LookupKind.Legacy, "not JSON")]
        public void pending_metadata_is_unavailable_before_payload_parsing(LookupKind kind, string payload)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.Accepted, payload));
            var error = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(error.InnerException, Is.Null);
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work, HttpStatusCode.InternalServerError)]
        [TestCase(LookupKind.Edition, HttpStatusCode.InternalServerError)]
        [TestCase(LookupKind.Legacy, HttpStatusCode.InternalServerError)]
        [TestCase(LookupKind.Work, HttpStatusCode.ServiceUnavailable)]
        [TestCase(LookupKind.Edition, HttpStatusCode.ServiceUnavailable)]
        [TestCase(LookupKind.Legacy, HttpStatusCode.ServiceUnavailable)]
        [TestCase(LookupKind.Work, HttpStatusCode.GatewayTimeout)]
        [TestCase(LookupKind.Edition, HttpStatusCode.GatewayTimeout)]
        [TestCase(LookupKind.Legacy, HttpStatusCode.GatewayTimeout)]
        [TestCase(LookupKind.Edition, HttpStatusCode.Conflict)]
        public void server_error_preserves_http_status_and_request_in_inner_exception(LookupKind kind, HttpStatusCode status)
        {
            var client = new RecordingHttpClient(request => Response(request, status, "upstream unavailable"));
            var error = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(error.InnerException, Is.TypeOf<HttpException>());
            var inner = (HttpException)error.InnerException;
            Assert.That(inner.Response.StatusCode, Is.EqualTo(status));
            Assert.That(inner.Request, Is.SameAs(client.Requests.Single()));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work, TransportKind.Http)]
        [TestCase(LookupKind.Edition, TransportKind.Http)]
        [TestCase(LookupKind.Legacy, TransportKind.Http)]
        [TestCase(LookupKind.Work, TransportKind.Web)]
        [TestCase(LookupKind.Edition, TransportKind.Web)]
        [TestCase(LookupKind.Legacy, TransportKind.Web)]
        [TestCase(LookupKind.Work, TransportKind.Managed)]
        [TestCase(LookupKind.Edition, TransportKind.Managed)]
        [TestCase(LookupKind.Legacy, TransportKind.Managed)]
        public void transport_failure_retains_the_original_exception(LookupKind kind, TransportKind failure)
        {
            Exception transport = null;
            var client = new RecordingHttpClient(request =>
            {
                transport = failure switch
                {
                    TransportKind.Http => new HttpException(Response(request, HttpStatusCode.BadGateway)),
                    TransportKind.Web => new WebException("Metadata timeout", WebExceptionStatus.Timeout),
                    _ => new HttpRequestException("Metadata connection failed")
                };
                throw transport;
            });
            var error = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error.InnerException, Is.SameAs(transport));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work, "not JSON")]
        [TestCase(LookupKind.Edition, "not JSON")]
        [TestCase(LookupKind.Legacy, "not JSON")]
        [TestCase(LookupKind.Work, "{}")]
        [TestCase(LookupKind.Edition, "{}")]
        [TestCase(LookupKind.Legacy, "{}")]
        [TestCase(LookupKind.Work, "null")]
        [TestCase(LookupKind.Edition, "null")]
        [TestCase(LookupKind.Legacy, "null")]
        public void malformed_response_retains_unavailable_classification_and_json_cause(LookupKind kind, string payload)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.OK, payload));
            var error = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(error.Message, Does.Contain("status=200"));
            Assert.That(error.Message, Does.Contain("application/json"));
            Assert.That(error.Message, Does.Contain(client.Requests.Single().Url.ToString()));
            if (payload == "not JSON") Assert.That(error.InnerException, Is.InstanceOf<JsonException>());
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work)]
        [TestCase(LookupKind.Edition)]
        [TestCase(LookupKind.Legacy)]
        public void exhausted_rate_limit_preserves_existing_three_requests(LookupKind kind)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.TooManyRequests));
            var error = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error.Message, Does.Contain("after 3 attempts"));
            AssertRequests(client, kind, 3);
        }

        [TestCase(LookupKind.Work)]
        [TestCase(LookupKind.Edition)]
        [TestCase(LookupKind.Legacy)]
        public void validation_400_remains_bad_request(LookupKind kind)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.BadRequest,
                TypedLookupPayload("invalid_provider_id", kind == LookupKind.Edition ? "gr:44767458" : "gr:3634639", "Invalid provider identifier")));
            var error = Assert.Throws<InvalidProviderIdException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error, Is.InstanceOf<NzbDroneClientException>());
            Assert.That((error as object as NzbDroneClientException)?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work)]
        [TestCase(LookupKind.Legacy)]
        public void declared_work_409_remains_terminal(LookupKind kind)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.Conflict, TypedLookupPayload("work_rescue_terminal", "gr:3634639", "Declared work rescue stop")));
            var error = Assert.Throws<WorkRescueTerminalException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(error.ProviderId, Is.EqualTo("gr:3634639"));
            Assert.That(error.Message, Is.EqualTo("Declared work rescue stop"));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work, BookMediaType.Audiobook)]
        [TestCase(LookupKind.Work, BookMediaType.Ebook)]
        [TestCase(LookupKind.Edition, BookMediaType.Audiobook)]
        [TestCase(LookupKind.Edition, BookMediaType.Ebook)]
        [TestCase(LookupKind.Legacy, BookMediaType.Audiobook)]
        [TestCase(LookupKind.Legacy, BookMediaType.Ebook)]
        public void successful_lookup_preserves_media_mapping_identity_and_author_hint(LookupKind kind, BookMediaType media)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.OK, BookInfoProxyCanonicalLookupFixture.SecondaryEditionAliasesPayload));
            var book = Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind, media).Item2;
            Assert.That(book.Title, Is.EqualTo("Dune"));
            Assert.That(book.MediaType, Is.EqualTo(media));
            Assert.That(book.GoodreadsWorkId, Is.EqualTo("gr:3634639"));
            Assert.That(book.Editions.Any(edition => BookEditionIdentity.EditionMatchesProviderId(edition, "gr-ed:44767458")), Is.True);
            AssertRequests(client, kind);
        }

        private sealed class ObservedRefresh : RefreshBookService
        {
            public int Saves { get; private set; }
            public int Deletes { get; private set; }
            public ObservedRefresh(IBookService books, BookInfoProxy metadata)
                : base(books, null, null, null, null, metadata, null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(), mainDatabase: null) { }
            protected override void SaveEntity(Book book) => Saves++;
            protected override void DeleteEntity(Book book, bool deleteFiles) => Deletes++;
        }

        [TestCase(HttpStatusCode.Accepted, "null")]
        [TestCase(HttpStatusCode.OK, "not JSON")]
        public void direct_refresh_unavailable_metadata_exits_before_save_or_delete(HttpStatusCode status, string payload)
        {
            var author = new Author { Id = 1, GoodreadsAuthorId = "gr:58" };
            var books = Enumerable.Range(1, 2).Select(id => new Book
            {
                Id = id, Title = "Saved local title", GoodreadsWorkId = "gr:" + (3634638 + id),
                Author = author, AuthorId = author.Id, MediaType = BookMediaType.Audiobook,
                Monitored = true, Editions = new List<Edition> { new() { Id = id, ForeignEditionId = "gr:44767458", Monitored = true } }
            }).ToList();
            var serviceCalls = new List<string>();
            var bookService = InterfaceProxy.Proxy<IBookService>((method, args) =>
            {
                serviceCalls.Add(method.Name);
                return method.Name == nameof(IBookService.GetBooks) ? books : throw new AssertionException(method.Name);
            });
            var client = new RecordingHttpClient(request => Response(request, status, payload));
            var service = new ObservedRefresh(bookService, BookInfoProxyCanonicalLookupFixture.CreateProxy(client));
            Assert.That(service.RefreshBookInfo(books[0]), Is.False);
            Assert.That(service.Saves, Is.Zero);
            Assert.That(service.Deletes, Is.Zero);
            Assert.That(serviceCalls, Is.Empty);
            Assert.That(books.Select(book => book.Title), Is.All.EqualTo("Saved local title"));
            Assert.That(books.All(book => book.Monitored && book.Editions.Single().Monitored), Is.True);
            AssertRequests(client, LookupKind.Work);
        }

        [TestCase(false, HttpStatusCode.Accepted, "null")]
        [TestCase(true, HttpStatusCode.Accepted, "null")]
        [TestCase(false, HttpStatusCode.OK, "not JSON")]
        [TestCase(true, HttpStatusCode.OK, "not JSON")]
        public void bulk_refresh_retains_failed_copies_logs_the_typed_error_and_persists_healthy_media(bool postgres, HttpStatusCode status, string payload)
        {
            AssertBulkRefreshRetainsFailedCopies(postgres, status, payload);
        }

        private static void AssertBulkRefreshRetainsFailedCopies(bool postgres, HttpStatusCode status, string payload, bool authorTerminal = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var factory = new LogFactory();
                var logs = new NLog.Targets.MemoryTarget { Layout = "${message}|${exception:format=ToString}" };
                var configuration = new NLog.Config.LoggingConfiguration(factory);
                configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, logs);
                factory.Configuration = configuration;
                var logger = factory.GetLogger(nameof(BookInfoProxyLookupFailureFixture));
                var published = new List<BookUpdatedEvent>();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is BookUpdatedEvent updated) published.Add(updated);
                    return null;
                });
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Frank Herbert", CleanName = "frankherbert", SortName = "herbertfrank",
                    TitleSlug = "frank-herbert", GoodreadsAuthorId = "gr:58",
                    Path = @"C:\Books\Frank Herbert".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var rows = new BookRepository(database, events);
                var editions = new EditionService(new EditionRepository(database, events), events, logger);
                var files = new MediaFileRepository(database, events);
                var books = Enumerable.Range(1, 3).Select(id => rows.Insert(new Book
                {
                    Title = "Saved local title", CleanTitle = "saved local title", TitleSlug = "dune-" + id,
                    GoodreadsWorkId = "gr:3634639", Author = author, AuthorId = author.Id,
                    MediaType = id == 3 ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    AnyEditionOk = true, AudiobookMonitored = id != 3, EbookMonitored = id == 3, Added = DateTime.UtcNow
                })).ToList();
                var pins = books.Select(book => new Edition
                {
                    BookId = book.Id, ForeignEditionId = "hc:edition:12250068", Title = "Saved edition",
                    ReadingFormatId = 1, Monitored = true, ManualAdd = true
                }).ToList();
                editions.InsertMany(pins);
                var file = files.Insert(new BookFile
                {
                    EditionId = pins[0].Id, Path = @"C:\Books\Dune.m4b".AsOsAgnostic(), Size = 100,
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new NzbDrone.Core.Qualities.QualityModel(), MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel()
                });
                Book Load(int id)
                {
                    var book = rows.Get(id);
                    book.Author = author;
                    book.Editions = editions.GetEditionRowsByBook(new[] { id });
                    book.BookFiles = files.GetFilesByBook(id);
                    return book;
                }
                var saved = new List<int>();
                var serviceBooks = InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooks)) return books.Select(book => Load(book.Id)).ToList();
                    if (method.Name == nameof(IBookService.GetExistingBooks)) return ((IEnumerable<int>)args[0]).Select(Load).ToList();
                    if (method.Name == nameof(IBookService.GetBook)) return Load((int)args[0]);
                    if (method.Name == nameof(IBookService.UpdateMany))
                    {
                        var changed = (List<Book>)args[0];
                        saved.AddRange(changed.Select(book => book.Id));
                        rows.UpdateMany(changed);
                        return null;
                    }
                    throw new AssertionException(method.Name);
                });
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, _) =>
                    method.Name == nameof(IAuthorService.GetAuthor) || method.Name == nameof(IAuthorService.FindByProviderId)
                        ? author : throw new AssertionException(method.Name));
                var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name switch
                {
                    nameof(IMediaFileService.GetFilesByBook) => files.GetFilesByBook((int)args[0]),
                    nameof(IMediaFileService.GetFilesByEdition) => files.GetFilesByEdition((int)args[0]),
                    _ => throw new AssertionException(method.Name)
                });
                var tags = InterfaceProxy.Proxy<IMetadataTagService>((_, _) => null);
                var clientRequests = 0;
                var client = new RecordingHttpClient(request =>
                {
                    var first = clientRequests++ == 0;
                    if (authorTerminal && request.Url.Path == "/api/v5/author")
                        return Response(request, HttpStatusCode.NotFound,
                            "{\"code\":\"author_no_primary_works\",\"providerId\":\"gr:58\",\"message\":\"Declared author stop\",\"retryable\":false,\"reopenable\":false}");
                    return Response(request, first && !authorTerminal ? status : HttpStatusCode.OK,
                        first && !authorTerminal
                            ? status == HttpStatusCode.BadRequest || status == HttpStatusCode.Conflict
                                ? TypedLookupPayload(status == HttpStatusCode.BadRequest ? "invalid_provider_id" : "work_rescue_terminal", "gr:3634639", payload)
                                : payload
                            : BookInfoProxyCanonicalLookupFixture.SecondaryEditionAliasesPayload);
                });
                var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(client);
                var authorCalls = 0;
                var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((_, _) =>
                    authorTerminal && authorCalls++ == 0 ? metadata.GetAuthorInfo("gr:58") : author);
                var service = new RefreshBookService(serviceBooks, authors, null, editions, authorInfo,
                    metadata,
                    new RefreshEditionService(editions, tags, null, database, logger), media, null, events, null,
                    new EditionSelector(logger), null, null, logger, mainDatabase: null);
                Assert.DoesNotThrow(() => service.Execute(new BulkRefreshBookCommand { BookIds = books.Select(book => book.Id).ToList() }));
                Assert.That(saved, Is.EqualTo(new[] { books[2].Id }));
                Assert.That(published.Select(updated => updated.Book.Id), Is.EqualTo(saved));
                Assert.That(Load(books[2].Id).Title, Is.EqualTo("Dune"));
                foreach (var book in books.Take(2))
                {
                    var retained = Load(book.Id);
                    Assert.That(retained.Title, Is.EqualTo("Saved local title"));
                    Assert.That(retained.IsMonitored(), Is.True);
                    Assert.That(retained.LastInfoSync, Is.Null);
                    Assert.That(retained.Editions.Single().Id, Is.EqualTo(pins.Single(pin => pin.BookId == book.Id).Id));
                    Assert.That(retained.Editions.Single().Monitored && retained.Editions.Single().ManualAdd, Is.True);
                }
                Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(pins[0].Id));
                var expectedType = authorTerminal ? nameof(AuthorTerminalException) :
                    status == HttpStatusCode.Conflict ? nameof(WorkRescueTerminalException) :
                    status == HttpStatusCode.BadRequest ? nameof(InvalidProviderIdException) : nameof(BookInfoException);
                var typedErrors = logs.Logs.Where(message => message.Contains(expectedType, StringComparison.Ordinal)).ToList();
                Assert.That(typedErrors, Has.Count.EqualTo(1));
                Assert.That(typedErrors.Single(), Does.Contain(authorTerminal ? "Declared author stop" :
                    status == HttpStatusCode.Conflict || status == HttpStatusCode.BadRequest ? payload : status == HttpStatusCode.Accepted ? "is not ready" : "Invalid JSON"));
                Assert.That(client.Requests, Has.Count.EqualTo(authorTerminal ? 3 : 2));
                Assert.That(client.Requests.Count(request => request.Url.Path == "/api/v5/author"), Is.EqualTo(authorTerminal ? 1 : 0));
                Assert.That(client.Requests.Where(request => request.Url.Path != "/api/v5/author")
                    .Select(request => Uri.UnescapeDataString(request.Url.ToString())),
                    Is.All.EqualTo("http://metadata/api/v5/work/gr:3634639?author=gr:58"));
            });
        }

        [TestCase(LookupKind.Work, HttpStatusCode.Accepted, "null")]
        [TestCase(LookupKind.Edition, HttpStatusCode.Accepted, "null")]
        [TestCase(LookupKind.Legacy, HttpStatusCode.Accepted, "null")]
        [TestCase(LookupKind.Work, HttpStatusCode.OK, "not JSON")]
        [TestCase(LookupKind.Edition, HttpStatusCode.OK, "not JSON")]
        [TestCase(LookupKind.Legacy, HttpStatusCode.OK, "not JSON")]
        public void narrator_coverage_keeps_its_existing_empty_result_for_unavailable_metadata(LookupKind kind, HttpStatusCode status, string payload)
        {
            var book = new Book
            {
                Id = 1, Title = "Saved local title", Author = new Author { GoodreadsAuthorId = "gr:58" },
                MediaType = BookMediaType.Audiobook, GoodreadsWorkId = kind == LookupKind.Work ? "gr:3634639" : null,
                HardcoverBookId = kind == LookupKind.Legacy ? "hc:312460" : null,
                Editions = kind == LookupKind.Edition ? new List<Edition> { new() { GoodreadsEditionId = 44767458, Monitored = true } } : new List<Edition>()
            };
            var books = InterfaceProxy.Proxy<IBookService>((method, _) => method.Name == nameof(IBookService.GetBooks)
                ? new List<Book> { book } : throw new AssertionException(method.Name));
            var links = InterfaceProxy.Proxy<ISeriesBookLinkService>((method, _) => method.Name == nameof(ISeriesBookLinkService.GetLinksBySeries)
                ? new List<SeriesBookLink> { new() { BookId = book.Id } } : throw new AssertionException(method.Name));
            var client = new RecordingHttpClient(request => Response(request, status, payload));
            var service = new NarratorCoverageService(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), null, books, links,
                new CacheManager(), LogManager.GetCurrentClassLogger());

            Assert.That(service.GetNarratorCoverageForSeries(new Series { Id = 1, Title = "Test series" }).GetAwaiter().GetResult(), Is.Empty);
            Assert.That(book.Title, Is.EqualTo("Saved local title"));
            Assert.That(client.Requests, Has.Count.EqualTo(1));
            Assert.That(Uri.UnescapeDataString(client.Requests.Single().Url.ToString()), Is.EqualTo(kind switch
            {
                LookupKind.Work => "http://metadata/api/v5/work/gr:3634639?author=gr:58",
                LookupKind.Edition => "http://metadata/api/v5/book/gr:44767458",
                _ => "http://metadata/api/v5/work/hc:312460"
            }));
        }
    }
}
