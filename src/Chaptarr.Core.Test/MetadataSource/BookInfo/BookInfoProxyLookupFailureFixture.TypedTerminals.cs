using System;
using System.Net;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Reflection;
using LazyCache;
using Chaptarr.Core.Test.Common;
using NLog;
using System.Text;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    public partial class BookInfoProxyLookupFailureFixture
    {
        [Test]
        public void metadata_lookup_callers_and_declared_refusal_handling_require_an_outcome_gate()
        {
            var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "src", "Chaptarr.Core.Test", "Chaptarr.Core.Test.csproj")))
            {
                current = current.Parent;
            }

            Assert.That(current, Is.Not.Null, "The source contract needs the repository source, as do existing architecture checks.");
            var callers = new Dictionary<string, int>();
            var refusals = new Dictionary<string, int>();
            var callerPattern = new Regex(@"\.(GetAuthorInfoAsync|GetAuthorInfo|RefreshAuthorInfo|RefreshAuthorInfoAsync|RefreshAuthorInfoV5BulkAsync|GetBookInfo|GetWorkInfo|GetEditionInfo|SearchForNewBook|SearchForNewAuthor|SearchForNewEntity)\s*\(");
            var refusalPattern = new Regex(@"AuthorTerminalException|WorkRescueTerminalException|InvalidProviderIdException|catch\s*\(NzbDroneClientException|is NzbDroneClientException");
            foreach (var sourceDirectory in new[] { "NzbDrone.Core", "Chaptarr.Http", "Chaptarr.Api.V1" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(current.FullName, "src", sourceDirectory), "*.cs", SearchOption.AllDirectories))
                {
                    var lines = File.ReadAllLines(file);
                    var path = Path.GetRelativePath(current.FullName, file).Replace('\\', '/');
                    var callerCount = lines.Count(line => callerPattern.IsMatch(line));
                    var refusalCount = lines.Count(line => refusalPattern.IsMatch(line));
                    if (callerCount > 0) callers.Add(path, callerCount);
                    if (refusalCount > 0) refusals.Add(path, refusalCount);
                }
            }

            var expectedCallers = new Dictionary<string, int>
            {
                ["src/NzbDrone.Core/Books/Commands/ProcessPendingImportsCommandHandler.cs"] = 1,
                ["src/NzbDrone.Core/Books/Services/AddBookService.cs"] = 2,
                ["src/NzbDrone.Core/Books/Services/AuthorLibraryService.cs"] = 2,
                ["src/NzbDrone.Core/Books/Services/NarratorCoverageService.cs"] = 3,
                ["src/NzbDrone.Core/Books/Services/PendingImportService.cs"] = 1,
                ["src/NzbDrone.Core/Books/Services/RefreshAuthorService.cs"] = 2,
                ["src/NzbDrone.Core/Books/Services/RefreshAuthorService.Identity.cs"] = 1,
                ["src/NzbDrone.Core/Books/Services/RefreshBookService.cs"] = 4,
                ["src/NzbDrone.Core/Books/Services/SeriesImportService.cs"] = 1,
                ["src/NzbDrone.Core/ImportLists/ImportListSyncService.cs"] = 2,
                ["src/NzbDrone.Core/MetadataSource/BookInfo/BookInfoProxy.cs"] = 1,
                ["src/Chaptarr.Api.V1/Author/AuthorLookupController.cs"] = 1,
                ["src/Chaptarr.Api.V1/Books/BookLookupController.cs"] = 1,
                ["src/Chaptarr.Api.V1/Search/SearchController.cs"] = 2,
            };
            var expectedRefusals = new Dictionary<string, int>
            {
                // The broad refusal inventory also sees category-local restore validation; no metadata call is made.
                ["src/NzbDrone.Core/Configuration/SettingsBackups/SettingsBackupService.cs"] = 1,
                ["src/NzbDrone.Core/Books/Commands/ProcessPendingImportsCommandHandler.cs"] = 7,
                ["src/NzbDrone.Core/Books/Events/PendingAuthorImportEvents.cs"] = 2,
                ["src/NzbDrone.Core/Books/Services/PendingAuthorImportService.cs"] = 1,
                ["src/NzbDrone.Core/Books/Services/RefreshBookService.cs"] = 3,
                ["src/NzbDrone.Core/Exceptions/AuthorTerminalException.cs"] = 2,
                ["src/NzbDrone.Core/Exceptions/InvalidProviderIdException.cs"] = 3,
                ["src/NzbDrone.Core/Exceptions/WorkRescueTerminalException.cs"] = 2,
                ["src/NzbDrone.Core/ImportLists/ImportListSyncService.cs"] = 1,
                ["src/NzbDrone.Core/MetadataSource/BookInfo/BookInfoProxy.cs"] = 14,
                ["src/NzbDrone.Core/MetadataSource/BookInfo/RefreshResult.cs"] = 1,
                ["src/NzbDrone.Core/MetadataSource/Hardcover/HardcoverSearchClient.cs"] = 1,
                ["src/Chaptarr.Api.V1/Books/BookLookupController.cs"] = 1,
                ["src/Chaptarr.Http/Authentication/PlexAuthenticationController.cs"] = 1,
                ["src/Chaptarr.Http/ErrorManagement/ChaptarrErrorPipeline.cs"] = 1,
            };
            Assert.Multiple(() =>
            {
                Assert.That(callers, Is.EquivalentTo(expectedCallers), "New or removed metadata callers require a declared/temporary/unexpected outcome gate.");
                Assert.That(refusals, Is.EquivalentTo(expectedRefusals), "Changed concrete refusal handling requires the consumer outcome matrix to be reviewed.");
            });
        }

        [Test]
        public void unexpected_lookup_redirects_remain_retryable_without_reading_a_legacy_author_catalog(
            [Values(LookupKind.Work, LookupKind.Edition, LookupKind.Legacy)] LookupKind kind,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther,
                HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect)] HttpStatusCode status)
        {
            var author = new Author { GoodreadsAuthorId = "gr:58", Name = "Fixture Author" };
            var book = new Book { GoodreadsWorkId = "gr:3634639", MediaType = mediaType, Author = author };
            var selected = new Edition { ForeignEditionId = "gr:44767458", Monitored = true, Book = book };
            book.Editions = new List<Edition> { selected };
            author.Books = new List<Book> { book };
            var client = new RecordingHttpClient(request => new HttpResponse(request,
                new HttpHeader { ["Location"] = "http://metadata/api/v5/author/gr:58" }, "", status));
            var proxy = BookInfoProxyCanonicalLookupFixture.CreateProxy(client);
            var cache = (CachingService)typeof(BookInfoProxy).GetField("_authorCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(proxy);
            cache.GetOrAdd("gr:58", () => author, new LazyCacheEntryOptions { Size = 1 });

            var error = Assert.Throws<BookInfoException>(() => Lookup(proxy, kind, mediaType));
            Assert.Multiple(() =>
            {
                Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                Assert.That(error.InnerException, Is.TypeOf<HttpException>());
                Assert.That(((HttpException)error.InnerException).Response.StatusCode, Is.EqualTo(status));
                Assert.That(error.Message, Does.Not.Contain("http://metadata/api/v5/author/"));
                Assert.That(book.Editions.Single(), Is.SameAs(selected));
                Assert.That(selected.Monitored, Is.True);
            });
            AssertRequests(client, kind);
        }

        [Test]
        public void import_list_work_mapping_consumes_only_declared_per_book_rejections(
            [Values("work_terminal", "bad_id", "missing", "author_terminal", "unavailable", "unexpected")] string kind,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Exception failure = kind switch
            {
                "work_terminal" => new WorkRescueTerminalException("gr:3634639", "Declared work stop"),
                "bad_id" => new InvalidProviderIdException("gr:3634639", "Invalid requested identifier"),
                "missing" => new BookNotFoundException("gr:3634639"),
                "author_terminal" => new AuthorTerminalException("author_no_primary_works", "gr:58", null, "Declared author stop", false),
                "unavailable" => new BookInfoException("Metadata is unavailable"),
                _ => new InvalidOperationException("Unexpected metadata bug")
            };
            var metadataCalls = 0;
            var metadata = InterfaceProxy.Proxy<IProvideBookInfo>((calledMethod, args) =>
            {
                metadataCalls++;
                Assert.That(calledMethod.Name, Is.EqualTo(nameof(IProvideBookInfo.GetWorkInfo)));
                Assert.That(args, Is.EqualTo(new object[] { "gr:3634639", mediaType, "gr:58" }));
                throw failure;
            });
            var service = new ImportListSyncService(null, null, null, metadata, null, null, null,
                null, null, null, null, null, LogManager.GetCurrentClassLogger(), null);
            var type = typeof(ImportListSyncService);
            var lookup = Activator.CreateInstance(type.GetNestedType("ImportListLocalLookup", BindingFlags.NonPublic), true);
            var stats = Activator.CreateInstance(type.GetNestedType("ImportListSyncStats", BindingFlags.NonPublic), true);
            var mappings = new ConcurrentDictionary<string, ImportListBookIdentityCache>();
            var report = new ImportListItemInfo { BookProviderId = "gr:3634639", AuthorProviderId = "gr:58", Book = "Fixture Work" };
            var method = type.GetMethod("MapBookReportWithMappings", BindingFlags.Instance | BindingFlags.NonPublic);
            var arguments = new object[] { report, lookup, mappings, stats, new[] { mediaType } };
            var rejected = kind == "work_terminal" || kind == "bad_id" || kind == "missing";
            if (rejected)
            {
                Assert.That(method.Invoke(service, arguments), Is.Null);
                Assert.That(stats.GetType().GetField("EvidenceRejections").GetValue(stats), Is.EqualTo(kind == "missing" ? 0 : 1));
            }
            else
            {
                var invocation = Assert.Throws<TargetInvocationException>(() => method.Invoke(service, arguments));
                Assert.That(invocation.InnerException, Is.SameAs(failure));
            }

            Assert.That(report.AuthorProviderId, Is.Null);
            Assert.That(mappings, Is.Empty);
            Assert.That(stats.GetType().GetField("WorkLookups").GetValue(stats), Is.EqualTo(1));
            Assert.That(metadataCalls, Is.EqualTo(1));
        }

        [Test]
        public void untyped_lookup_errors_remain_unavailable_and_do_not_promote_the_body(
            [Values(LookupKind.Work, LookupKind.Edition, LookupKind.Legacy)] LookupKind kind,
            [Values(HttpStatusCode.BadRequest, HttpStatusCode.Conflict)] HttpStatusCode status,
            [Values("null", "<html>proxy diagnostic</html>", "{\"code\":\"gateway_error\"}")] string body)
        {
            var client = new RecordingHttpClient(request => Response(request, status, body));
            var exception = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(exception.InnerException, Is.TypeOf<HttpException>());
            Assert.That(((HttpException)exception.InnerException).Response.StatusCode, Is.EqualTo(status));
            Assert.That(exception.Message, Does.Contain(((int)status).ToString()));
            Assert.That(exception.Message, Does.Not.Contain(body));
            AssertRequests(client, kind);
        }

        [TestCase(HttpStatusCode.BadRequest, "{\"code\":\"unknown_code\",\"providerId\":\"gr:3634639\",\"retryable\":false}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"code\":\"invalid_provider_id\",\"providerId\":\"gr:999\",\"retryable\":false}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"code\":\"invalid_provider_id\",\"providerId\":\"gr:3634639\"}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"code\":\"invalid_provider_id\",\"providerId\":\"gr:3634639\",\"retryable\":true}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"code\":\"invalid_provider_id\",\"providerId\":\"gr:3634639\",\"retryable\":\"false\"}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"code\":42,\"providerId\":\"gr:3634639\",\"retryable\":false}")]
        [TestCase(HttpStatusCode.BadRequest, "{\"code\":\"work_rescue_terminal\",\"providerId\":\"gr:3634639\",\"retryable\":false}")]
        [TestCase(HttpStatusCode.Conflict, "{\"code\":\"invalid_provider_id\",\"providerId\":\"gr:3634639\",\"retryable\":false}")]
        [TestCase(HttpStatusCode.Conflict, "{\"code\":\"work_rescue_terminal\",\"providerId\":\"gr:999\",\"retryable\":false}")]
        [TestCase(HttpStatusCode.Conflict, "{\"code\":\"work_rescue_terminal\",\"providerId\":\"gr:3634639\",\"retryable\":true}")]
        [TestCase(HttpStatusCode.Conflict, "{\"code\":\"work_rescue_terminal\",\"providerId\":\"gr:3634639\",\"retryable\":null}")]
        [TestCase(HttpStatusCode.Conflict, "{not JSON")]
        public void incomplete_foreign_or_mismatched_declared_envelopes_are_retryable(HttpStatusCode status, string body)
        {
            var client = new RecordingHttpClient(request => Response(request, status, body));
            var exception = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), LookupKind.Work));
            Assert.That(exception.InnerException, Is.TypeOf<HttpException>());
            Assert.That(exception.Message, Does.Not.Contain(body));
            AssertRequests(client, LookupKind.Work);
        }

        [Test]
        public void a_work_stop_envelope_cannot_terminalize_an_edition_lookup()
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.Conflict,
                TypedLookupPayload("work_rescue_terminal", "gr:44767458", "Declared work stop")));
            var exception = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), LookupKind.Edition));
            Assert.That(exception.InnerException, Is.TypeOf<HttpException>());
            AssertRequests(client, LookupKind.Edition);
        }

        [TestCase(HttpStatusCode.BadRequest, "invalid_provider_id")]
        [TestCase(HttpStatusCode.Conflict, "work_rescue_terminal")]
        public void a_non_json_content_type_cannot_declare_a_stop(HttpStatusCode status, string code)
        {
            var client = new RecordingHttpClient(request => new HttpResponse(request, new HttpHeader { ContentType = "text/html" },
                TypedLookupPayload(code, "gr:3634639", "Untrusted body"), status));
            var exception = Assert.Throws<BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), LookupKind.Work));
            Assert.That(exception.Message, Does.Not.Contain("Untrusted body"));
            AssertRequests(client, LookupKind.Work);
        }

        [TestCase(LookupKind.Work, HttpStatusCode.BadRequest, "invalid_provider_id")]
        [TestCase(LookupKind.Edition, HttpStatusCode.BadRequest, "invalid_provider_id")]
        [TestCase(LookupKind.Work, HttpStatusCode.Conflict, "work_rescue_terminal")]
        [TestCase(LookupKind.Legacy, HttpStatusCode.Conflict, "work_rescue_terminal")]
        public void declared_diagnostics_are_bounded_without_splitting_unicode(LookupKind kind, HttpStatusCode status, string code)
        {
            var message = string.Concat(System.Linq.Enumerable.Repeat("😀", 300));
            var providerId = kind == LookupKind.Edition ? "gr:44767458" : "gr:3634639";
            var client = new RecordingHttpClient(request => Response(request, status, TypedLookupPayload(code, providerId, message)));
            Exception exception = status == HttpStatusCode.BadRequest
                ? Assert.Throws<InvalidProviderIdException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind))
                : Assert.Throws<WorkRescueTerminalException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(Encoding.UTF8.GetByteCount(exception.Message), Is.LessThanOrEqualTo(512));
            Assert.That(exception.Message, Does.Contain("😀"));
            Assert.That(char.IsHighSurrogate(exception.Message[^1]), Is.False);
            AssertRequests(client, kind);
        }
    }
}
