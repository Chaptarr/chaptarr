using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Web;
using Chaptarr.Core.Test.Http;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class CalibreRetryFixture
    {
        private static CalibreProxy Reader(IHttpClient http, ICalibreLibraryGate gate = null) =>
            new CalibreProxy(http, null, Proxy<IRemotePathMappingService>((_, args) => (OsPath)args[1]), null,
                null, null, new CacheManager(), null,
                gate ?? new CalibreLibraryGate(new HttpServiceHealth(LogManager.GetCurrentClassLogger())), LogManager.GetCurrentClassLogger());


        [Test]
        public void test_connection_returns_validation_instead_of_http_500(
            [Values(HttpRequestError.ConnectionError, HttpRequestError.NameResolutionError, HttpRequestError.SecureConnectionError)] HttpRequestError kind,
            [Values("", "ajax/library-info", "cdb/cmd/saved_searches")] string failedPath)
        {
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                if (RequestPath(request) == failedPath) throw new HttpRequestException(kind, "Cannot connect");
                return SuccessfulTestResponse(request);
            });
            var error = Assert.Throws<FluentValidation.ValidationException>(() => Reader(http).Test(new CalibreSettings { Host = "localhost", Library = "books" }));
            Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo("Unable to connect"));
        }

        [TestCase(false)]
        [TestCase(true)]
        [TestCase(true, HttpStatusCode.Unauthorized)]
        [TestCase(true, HttpStatusCode.Forbidden)]
        [TestCase(true, HttpStatusCode.ProxyAuthenticationRequired, true)]
        [TestCase(false, HttpStatusCode.BadGateway, true)]
        public void connection_test_logs_one_warning_with_debug_stack_and_actionable_reason(bool proxyFailure, HttpStatusCode proxyStatus = HttpStatusCode.ProxyAuthenticationRequired, bool httpResponse = false)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}: ${message} ${exception:format=tostring}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
            logs.Configuration = config;
            var logger = logs.GetLogger("calibre-test");
            Exception cause = new HttpRequestException(proxyFailure ? HttpRequestError.ProxyTunnelError : HttpRequestError.ConnectionError,
                "Connection refused (127.0.0.1:37729)", statusCode: proxyFailure ? proxyStatus : null);
            var reader = new CalibreProxy(Proxy<IHttpClient>((_, args) => httpResponse
                ? new HttpResponse((HttpRequest)args[0], new HttpHeader(), "", proxyStatus)
                : ThrowConnectionFailure(cause)), null, null, null, null, null,
                new CacheManager(), null, new CalibreLibraryGate(new HttpServiceHealth(logger)), logger);
            var error = Assert.Throws<FluentValidation.ValidationException>(() => reader.Test(new CalibreSettings { Host = "localhost" }));
            if (proxyFailure && proxyStatus == HttpStatusCode.ProxyAuthenticationRequired)
            {
                Assert.That(error.Errors.Single().PropertyName, Is.EqualTo("Host"));
                Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo("Proxy returned HTTP 407 ProxyAuthenticationRequired: Check the proxy settings."));
            }
            else if (httpResponse)
            {
                Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo("Calibre is unavailable: HTTP 502 BadGateway"));
            }
            else
            {
                var failure = (NzbDroneValidationFailure)error.Errors.Single();
                Assert.That(failure.DetailedDescription, Is.EqualTo(proxyFailure
                    ? $"Proxy returned HTTP {(int)proxyStatus} {proxyStatus}: Check the proxy settings."
                    : "Connection refused (127.0.0.1:37729): Verify the hostname and port."));
            }
            Assert.That(messages.Logs.Count(line => line.StartsWith("Warn:")), Is.EqualTo(1));
            var debug = messages.Logs.Single(line => line.StartsWith("Debug:"));
            Assert.That(debug, Does.Contain(httpResponse ? nameof(HttpException) : cause.GetType().Name)
                .And.Contain(httpResponse ? "TestCalibre" : nameof(ThrowConnectionFailure)));
            var failureMessage = error.Errors.Single();
            var loggedMessage = failureMessage is NzbDroneValidationFailure detailed
                ? HttpTransportErrors.JoinMessages(detailed.ErrorMessage, detailed.DetailedDescription)
                : failureMessage.ErrorMessage;
            Assert.That(messages.Logs.Single(line => line.StartsWith("Warn:")).TrimEnd(), Is.EqualTo("Warn: Unable to test Calibre: " + loggedMessage));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ThrowConnectionFailure(Exception cause) => throw cause;

        [TestCase(HttpStatusCode.Unauthorized, false)]
        [TestCase(HttpStatusCode.Forbidden, false)]
        [TestCase(HttpStatusCode.Unauthorized, true)]
        [TestCase(HttpStatusCode.Forbidden, true)]
        public void tunnel_refusal_is_a_connection_failure_in_the_gate(HttpStatusCode status, bool wrapped)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new CalibreLibraryGate(health);
            Exception error = new HttpRequestException(HttpRequestError.ProxyTunnelError, "Tunnel refused", statusCode: status);
            if (wrapped) error = new CalibreException("Request failed", error);
            Assert.That(gate.ReportException("library", error, libraryRequest: true), Is.EqualTo(HttpServiceResult.ConnectionFailure));
            Assert.That(health.GetStatus("library").LastErrorMessage, Is.EqualTo($"Proxy returned HTTP {(int)status} {status}"));
            Assert.That(gate.CanBegin("library", out _), Is.False);
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void destination_refusal_has_one_diagnosis(HttpStatusCode status)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new CalibreLibraryGate(health);
            var error = new CalibreException("Request failed", new HttpException(
                new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "", status)));
            Assert.That(gate.ReportException("library", error, libraryRequest: true), Is.EqualTo(HttpServiceResult.AccessDenied));
            Assert.That(health.GetStatus("library").LastErrorMessage, Is.EqualTo($"Calibre refused access: HTTP {(int)status} {status}"));
        }

        [Test]
        public void invalid_response_reason_is_not_prefixed_twice()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new CalibreLibraryGate(health);
            gate.ReportException("library", new CalibreInvalidResponseException("Calibre did not confirm the change"));
            Assert.That(health.GetStatus("library").LastErrorMessage, Is.EqualTo("Calibre did not confirm the change"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void html_backoff_keeps_its_known_diagnosis(bool wrapped)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new CalibreLibraryGate(health);
            var error = new UnexpectedHtmlContentException(new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "<html>login</html>"));
            gate.ReportException("library", wrapped ? new CalibreException("Request failed", error) : error, libraryRequest: true);
            Assert.That(health.GetStatus("library").LastErrorMessage, Is.EqualTo("Calibre returned a web page instead of API data (HTTP 200)"));
            Assert.That(gate.CanBegin("library", out _), Is.False);
        }

        [Test]
        public void missing_library_backoff_keeps_the_diagnosis_without_a_second_calibre_prefix()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new CalibreLibraryGate(health);
            var error = new HttpException(new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "missing", HttpStatusCode.NotFound));
            gate.ReportException("library", error, libraryRequest: true);
            Assert.That(health.GetStatus("library").LastErrorMessage, Is.EqualTo("Calibre library not found (HTTP 404)"));
            Assert.That(gate.CanBegin("library", out _), Is.False);
        }

        [TestCase("unauthorized", "", "Authentication failed: check the username and password", "Username")]
        [TestCase("unauthorized", "cdb/cmd/saved_searches", "Authentication failed: check the username and password", "Username")]
        [TestCase("unauthorized", "ajax/library-info", "Authentication failed: check the username and password", "Username")]
        [TestCase("forbidden", "cdb/cmd/saved_searches", "Chaptarr needs write access. Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html", "Username")]
        [TestCase("redirect", "", "Unknown exception: Redirect loop", "")]
        [TestCase("server", "ajax/library-info", "HTTP 500 InternalServerError", "")]
        [TestCase("gateway", "ajax/library-info", "Calibre is unavailable: HTTP 502 BadGateway", "")]
        [TestCase("html", "ajax/library-info", "Calibre returned a web page instead of API data (HTTP 200)", "")]
        [TestCase("forbidden", "ajax/library-info", "Calibre refused access: HTTP 403 Forbidden", "")]
        [TestCase("forbidden", "", "Calibre refused access: HTTP 403 Forbidden", "")]
        [TestCase("gateway", "", "Calibre is unavailable: HTTP 502 BadGateway", "")]
        [TestCase("proxy", "", "Proxy returned HTTP 407 ProxyAuthenticationRequired: Check the proxy settings.", "Host")]
        [TestCase("proxy", "cdb/cmd/saved_searches", "Proxy returned HTTP 407 ProxyAuthenticationRequired: Check the proxy settings.", "Host")]
        [TestCase("proxy", "ajax/library-info", "Proxy returned HTTP 407 ProxyAuthenticationRequired: Check the proxy settings.", "Host")]
        [TestCase("missing", "", "Could not connect", "Host")]
        [TestCase("non-calibre", "", "Not a valid Calibre content server.  See https://manual.calibre-ebook.com/server.html", "Port")]
        [TestCase("no-content", "", "HTTP 204 NoContent", "")]
        [TestCase("restricted", "cdb/cmd/saved_searches", "Cannot use the command-line db interface with a user who has per library restrictions: Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html", "Username")]
        [TestCase("gateway", "cdb/cmd/saved_searches", "Calibre is unavailable: HTTP 502 BadGateway", "")]
        [TestCase("json", "ajax/library-info", "Calibre returned an invalid library list", "")]
        [TestCase("missing", "cdb/cmd/saved_searches", "Calibre's write API answered HTTP 404 NotFound. Check the Calibre version and whether the reverse proxy permits /cdb requests.", "")]
        [TestCase("redirect-response", "cdb/cmd/saved_searches", "Calibre's write API answered HTTP 302 Found.", "")]
        [TestCase("server", "cdb/cmd/saved_searches", "Calibre's write API answered HTTP 500 InternalServerError.", "")]
        [TestCase("empty", "ajax/library-info", "Calibre returned an invalid library list", "")]
        [TestCase("moved", "cdb/cmd/saved_searches", "Calibre's write API answered HTTP 301 MovedPermanently.", "")]
        public void test_failures_are_validation_errors_at_every_step(string kind, string failedPath, string expected, string property)
        {
            var requests = new List<HttpRequest>();
            var http = Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                requests.Add(request);
                var path = RequestPath(request);
                if (path == "cdb/cmd/saved_searches") Assert.That(method.Name, Is.EqualTo("Post"));
                if (path != failedPath) return SuccessfulTestResponse(request);
                if (kind == "redirect") throw new WebException("Redirect loop", WebExceptionStatus.ProtocolError);
                if (kind == "json") throw new JsonReaderException("Invalid json");
                if (kind == "empty") return new HttpResponse<CalibreLibraryInfo>(new HttpResponse(request, new HttpHeader(), "{}"));
                var status = kind switch
                {
                    "unauthorized" => HttpStatusCode.Unauthorized,
                    "forbidden" or "restricted" => HttpStatusCode.Forbidden,
                    "proxy" => HttpStatusCode.ProxyAuthenticationRequired,
                    "no-content" => HttpStatusCode.NoContent,
                    "missing" => HttpStatusCode.NotFound,
                    "redirect-response" => HttpStatusCode.Found,
                    "moved" => HttpStatusCode.MovedPermanently,
                    "server" => HttpStatusCode.InternalServerError,
                    "gateway" => HttpStatusCode.BadGateway,
                    _ => HttpStatusCode.OK
                };
                var response = kind == "restricted"
                    ? new HttpResponse(request, new HttpHeader { ContentType = "text/plain; charset=UTF-8" },
                        "Cannot use the command-line db interface with a user who has per library restrictions", status)
                    : new HttpResponse(request, new HttpHeader(), kind == "non-calibre" ? "Welcome to nginx!" : "<html>login</html>", status);
                if (kind == "html") throw new UnexpectedHtmlContentException(response);
                if (path == "ajax/library-info") throw new HttpException(response);
                return response;
            });
            var error = Assert.Throws<FluentValidation.ValidationException>(() => Reader(http).Test(new CalibreSettings { Host = "localhost", Library = "books" }));
            Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo(expected));
            Assert.That(error.Errors.Single().PropertyName, Is.EqualTo(property));
            foreach (var request in requests.Where(request => RequestPath(request) != ""))
            {
                Assert.That(request.LogResponseContent, Is.True);
                if (RequestPath(request) == "ajax/library-info") Assert.That(request.LogHttpError, Is.False);
            }
        }

        private static string RequestPath(HttpRequest request) => new Uri(request.Url.ToString()).AbsolutePath.TrimStart('/');

        private static object SuccessfulTestResponse(HttpRequest request) => RequestPath(request) == "ajax/library-info"
            ? new HttpResponse<CalibreLibraryInfo>(new HttpResponse(request, new HttpHeader(), "{\"library_map\":{\"books\":\"Books\",\"kids\":\"Kids\"},\"default_library\":\"books\"}"))
            : new HttpResponse(request, new HttpHeader(), "<title>calibre</title>");

        [TestCase("kids", "kids")]
        [TestCase("", "books")]
        [TestCase(null, "books")]
        public void write_probe_uses_the_resolved_library(string selected, string expected)
        {
            var paths = new List<string>();
            var settings = new CalibreSettings { Host = "localhost", Library = selected };
            var reader = Reader(Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                paths.Add(RequestPath(request));
                if (RequestPath(request) == "cdb/cmd/saved_searches")
                {
                    Assert.That(method.Name, Is.EqualTo("Post"));
                    Assert.That(HttpUtility.ParseQueryString(new Uri(request.Url.ToString()).Query)["library_id"], Is.EqualTo(expected));
                    Assert.That(settings.Library, Is.EqualTo(expected));
                }
                return SuccessfulTestResponse(request);
            }));
            reader.Test(settings);
            Assert.That(paths, Is.EqualTo(new[] { "", "ajax/library-info", "cdb/cmd/saved_searches" }));
        }

        [Test]
        public void invalid_library_is_rejected_before_the_write_probe()
        {
            var paths = new List<string>();
            var reader = Reader(Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                paths.Add(RequestPath(request));
                return SuccessfulTestResponse(request);
            }));
            var error = Assert.Throws<FluentValidation.ValidationException>(() => reader.Test(new CalibreSettings { Host = "localhost", Library = "typo" }));
            Assert.That(error.Errors.Single().PropertyName, Is.EqualTo("Library"));
            Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo("Not a valid library in calibre"));
            Assert.That(paths, Is.EqualTo(new[] { "", "ajax/library-info" }));
        }

        [TestCase("Anonymous users are not allowed to make changes", "text/plain", "Anonymous users are not allowed to make changes: Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        [TestCase("The user reader does not have permission to make changes", "text/plain; charset=UTF-8", "The user reader does not have permission to make changes: Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        [TestCase("Cannot use the command-line db interface with a user who has per library restrictions", "text/plain", "Cannot use the command-line db interface with a user who has per library restrictions: Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        [TestCase("  Cannot use restrictions \r\nsecond line", "Text/Plain", "Cannot use restrictions: Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        [TestCase("<html>nginx denied this request</html>", "text/html", "Chaptarr needs write access. Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        [TestCase("", "text/plain", "Chaptarr needs write access. Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        [TestCase("first line\r\nsecond line", "text/plain", "first line: Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html")]
        public void write_refusal_keeps_plain_text_reason_and_remedy(string body, string contentType, string expected)
        {
            AssertWriteRefusal(body, contentType, expected);
        }

        [TestCase("long")]
        [TestCase("emoji")]
        [TestCase("exact")]
        public void write_refusal_is_bounded_in_utf8_without_splitting_a_character(string kind)
        {
            var body = kind == "emoji" ? new string('x', 299) + "😀" : new string('x', kind == "long" ? 102000 : 300);
            var expected = kind == "emoji" ? new string('x', 299) + "… " : new string('x', 300) + (kind == "long" ? "… " : ": ");
            AssertWriteRefusal(body, "text/plain", expected + "Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html");
        }

        private static void AssertWriteRefusal(string body, string contentType, string expected)
        {
            var requests = new List<string>();
            var reader = Reader(Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                requests.Add(RequestPath(request));
                return RequestPath(request) == "cdb/cmd/saved_searches"
                    ? new HttpResponse(request, new HttpHeader { ContentType = contentType }, body, HttpStatusCode.Forbidden)
                    : SuccessfulTestResponse(request);
            }));
            var error = Assert.Throws<FluentValidation.ValidationException>(() => reader.Test(new CalibreSettings { Host = "localhost" }));
            Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo(expected));
            Assert.That(error.Errors.Single().PropertyName, Is.EqualTo("Username"));
            Assert.That(error.Message.Length, Is.LessThan(600), "The API pipeline logs this validation exception.");
            Assert.That(error.Message.Count(c => c == '\n'), Is.LessThanOrEqualTo(1));
            Assert.That(requests, Is.EqualTo(new[] { "", "ajax/library-info", "cdb/cmd/saved_searches" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void proxy_authentication_has_one_reason_and_a_connection_failure(bool tunnel)
        {
            Exception error = tunnel
                ? new HttpRequestException(HttpRequestError.ProxyTunnelError, "Proxy refused", statusCode: HttpStatusCode.ProxyAuthenticationRequired)
                : new HttpException(new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "Proxy refused", HttpStatusCode.ProxyAuthenticationRequired));
            error = new CalibreException("Request failed", error);
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new CalibreLibraryGate(health);
            Assert.That(gate.ReportException("library", error, libraryRequest: true), Is.EqualTo(HttpServiceResult.ConnectionFailure));
            Assert.That(health.GetStatus("library").LastErrorMessage, Is.EqualTo("Proxy returned HTTP 407 ProxyAuthenticationRequired"));
            Assert.That(gate.CanBegin("library", out _), Is.False);
        }

        [TestCase(HttpStatusCode.Unauthorized, true, true)]
        [TestCase(HttpStatusCode.Forbidden, true, true)]
        [TestCase(HttpStatusCode.BadGateway, false, false)]
        public void diagnosis_helpers_unwrap_calibre_exceptions(HttpStatusCode status, bool denied, bool known)
        {
            var error = new CalibreException("Request failed", new HttpException(
                new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "", status)));
            Assert.That(CalibreLibraryGate.IsAccessDenied(error), Is.EqualTo(denied));
            Assert.That(CalibreLibraryGate.HasKnownDiagnosis(error), Is.EqualTo(known));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void only_library_reads_call_a_404_a_missing_library(bool library)
        {
            var reader = Reader(Proxy<IHttpClient>((_, args) => throw new HttpException(new HttpResponse((HttpRequest)args[0], new HttpHeader(), "missing", HttpStatusCode.NotFound))));
            var error = Assert.Catch<CalibreException>(() =>
            {
                if (library) reader.GetBooks(new() { 42 }, new CalibreSettings { Host = "localhost" });
                else reader.GetBook(42, new CalibreSettings { Host = "localhost" });
            });
            Assert.That(error.Message, Does.Contain("HTTP 404"));
            Assert.That(error.Message.Contains("Calibre library not found"), Is.EqualTo(library));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void only_bulk_delete_urls_are_redacted(bool bulk)
        {
            var calls = 0;
            var reader = Reader(Proxy<IHttpClient>((_, args) =>
            {
                calls++;
                var request = (HttpRequest)args[0];
                Assert.That(request.RedactUrlInLogs, Is.EqualTo(bulk));
                return new HttpResponse(request, new HttpHeader(), "{}");
            }));
            var settings = new CalibreSettings { Host = "localhost" };
            if (bulk) reader.DeleteBooks(new() { new() { CalibreId = 42 } }, settings);
            else reader.DeleteBook(new BookFile { CalibreId = 42 }, settings);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void deleting_without_known_ids_sends_no_request()
        {
            var http = Proxy<IHttpClient>((_, _) => throw new AssertionException("No request expected"));
            Reader(http).DeleteBooks(new List<BookFile>(), new CalibreSettings());
            Reader(http).DeleteBooks(new List<BookFile> { new() { CalibreId = 0 } }, new CalibreSettings());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void book_read_errors_and_html_never_expose_id_urls(bool html)
        {
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                var response = new HttpResponse(request, new HttpHeader(), html ? "<html>login</html>" : "bad", html ? HttpStatusCode.OK : HttpStatusCode.BadGateway);
                throw html ? new UnexpectedHtmlContentException(response) : new HttpException(response);
            });
            foreach (var many in new[] { false, true })
            {
                var error = Assert.Catch<CalibreException>(() =>
                {
                    if (many) Reader(http).GetBooks(Enumerable.Range(100000, 750).ToList(), new CalibreSettings { Host = "localhost", Library = "books" });
                    else Reader(http).GetBook(100000, new CalibreSettings { Host = "localhost", Library = "books" });
                });
                Assert.That(error.Message, Does.Not.Contain("ajax/book").And.Not.Contain("100000").And.Not.Contain("ids=")); // absent-literal: Test book ID must not leak through bulk-request errors.
                if (many) Assert.That(error.ToString(), Does.Not.Contain("ids=").And.Not.Contain("100000")); // absent-literal: Test book ID must not leak through bulk-request errors.
                Assert.That(error.Message.Length, Is.LessThan(200));
                Assert.That(error.InnerException.Message.Length, Is.LessThan(350));
            }
        }

        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("")]
        public void empty_success_body_is_a_page_failure(string payload)
        {
            var http = Proxy<IHttpClient>((_, args) =>
                new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse((HttpRequest)args[0], new HttpHeader(), payload)));
            Assert.Throws<CalibreInvalidResponseException>(() => Reader(http).GetBooks(new() { 1 }, new CalibreSettings { Host = "localhost" }));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void all_null_page_warns_only_for_multiple_requested_books(int count)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}: ${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
            logs.Configuration = configuration;
            var logger = logs.GetLogger("calibre-page");
            var ids = Enumerable.Range(1, count).ToList();
            var payload = ids.ToDictionary(id => id, _ => (CalibreBook)null).ToJson();
            var http = Proxy<IHttpClient>((_, args) =>
                new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse((HttpRequest)args[0], new HttpHeader(), payload)));
            var proxy = new CalibreProxy(http, null, null, null, null, null, new CacheManager(), null,
                new CalibreLibraryGate(new HttpServiceHealth(logger)), logger);
            var settings = new CalibreSettings { Host = "localhost", Library = "books" };
            Assert.That(proxy.GetBooks(ids, settings), Is.Empty);
            if (count == 1)
            {
                Assert.That(messages.Logs, Is.Empty);
            }
            else
            {
                Assert.That(messages.Logs, Is.EqualTo(new[] { $"Warn: Calibre {CalibreLibraryIdentity.GetKey(settings)}: all 2 requested books are deleted or restricted" }));
            }
        }

        [Test]
        public void oversized_pages_fail_before_any_http_request()
        {
            var proxy = Reader(Proxy<IHttpClient>((_, _) => throw new AssertionException("HTTP must not be called")));
            Assert.Throws<ArgumentException>(() => proxy.GetBooks(Enumerable.Range(1, CalibreProxy.PageSize + 1).ToList(), new CalibreSettings { Host = "localhost" }));
        }

        [Test]
        public void page_request_disables_http_error_url_logging()
        {
            var proxy = Reader(Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                Assert.That(request.LogHttpError, Is.False);
                Assert.That(request.LogResponseContent, Is.False);
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(), "{\"1\":null}"));
            }));
            proxy.GetBooks(new() { 1 }, new CalibreSettings { Host = "localhost" });
        }

        [TestCase("<html>Login</html>")]
        [TestCase("")]
        [TestCase("{}")]
        [TestCase("{\"1\":null}")]
        public void invalid_write_confirmation_does_not_claim_acceptance_or_start_embedding(string body)
        {
            var calls = 0;
            var proxy = Reader(Proxy<IHttpClient>((method, args) =>
            {
                calls++;
                Assert.That(method.Name, Is.EqualTo("Execute"));
                return new HttpResponse((HttpRequest)args[0], new HttpHeader(), body);
            }));
            var author = new Author { Name = "Author" };
            var file = new BookFile
            {
                CalibreId = 1,
                Author = author,
                Edition = new Edition { Title = "Book", Language = "en", Book = new Book { Author = author } }
            };
            var error = Assert.Catch<CalibreException>(() => proxy.SetFields(file, new CalibreSettings { Host = "localhost" }, false, true));
            Assert.That(error, body == "{\"1\":null}" ? Is.TypeOf<CalibreException>() : Is.TypeOf<CalibreInvalidResponseException>());
            Assert.That(error.WriteAccepted, Is.False);
            Assert.That(error.Message, Does.Contain(body == "{\"1\":null}" ? "Calibre reports this book no longer exists" : "Calibre did not confirm the change"));
            Assert.That(calls, Is.EqualTo(1), "An unconfirmed write must not be replayed or embedded");
        }

        [TestCase(true, "<html>login</html>")]
        [TestCase(false, "<html>login</html>")]
        [TestCase(true, "{\"1\":null}")]
        public void format_add_and_remove_require_a_confirmed_response(bool add, string body)
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "format bytes");
                var requests = new List<HttpRequest>();
                var http = Proxy<IHttpClient>((_, args) =>
                {
                    var request = (HttpRequest)args[0]; requests.Add(request);
                    return new HttpResponse(request, new HttpHeader(), body);
                });
                var subject = Reader(http);
                var settings = new CalibreSettings { Host = "localhost", Library = "books" };
                void Write()
                {
                    if (add) subject.AddAndConvert(new BookFile { Path = path, CalibreId = 1 }, settings);
                    else subject.RemoveFormats(1, new[] { "EPUB" }, settings);
                }
                var error = Assert.Catch<CalibreException>(Write);
                Assert.That(error.Message, Is.EqualTo(body.Contains("null") ? "Calibre reports this book no longer exists" : "Calibre did not confirm the change"));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(System.Text.Encoding.UTF8.GetString(requests[0].ContentData), Does.Contain(add ? "added_formats" : "removed_formats"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("format bytes"));
            }
            finally { File.Delete(path); }
        }

        [TestCase("{\"7\":null}", true)]
        [TestCase("{\"7\":{}}", true)]
        [TestCase("{\"8\":null}", false)]
        [TestCase("{\"7\":false}", false)]
        [TestCase("<html>login</html>", false)]
        public void removing_formats_accepts_only_the_requested_book_or_its_explicit_absence(string body, bool success)
        {
            var requests = 0;
            var proxy = Reader(Proxy<IHttpClient>((_, args) =>
            {
                requests++;
                return new HttpResponse((HttpRequest)args[0], new HttpHeader(), body);
            }));
            void Remove() => proxy.RemoveFormats(7, new[] { "EPUB" }, new CalibreSettings { Host = "localhost" });
            if (success) Assert.DoesNotThrow(Remove);
            else Assert.Throws<CalibreInvalidResponseException>(Remove);
            Assert.That(requests, Is.EqualTo(1));
        }

        [TestCase(200)]
        [TestCase(302)]
        public void redirect_is_not_accepted_even_if_its_body_looks_like_calibre(int status)
        {
            var proxy = Reader(Proxy<IHttpClient>((_, args) => new HttpResponse((HttpRequest)args[0],
                new HttpHeader(), "{\"7\":{}}", (HttpStatusCode)status)));
            void Remove() => proxy.RemoveFormats(7, new[] { "EPUB" }, new CalibreSettings { Host = "localhost" });
            if (status == 200) Assert.DoesNotThrow(Remove);
            else Assert.Throws<HttpException>(Remove);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void invalid_json_is_identified_at_the_response_reader(bool retry)
        {
            var attempts = 0;
            var proxy = Reader(Proxy<IHttpClient>((_, args) =>
            {
                if (++attempts == 1 && retry) throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse((HttpRequest)args[0], new HttpHeader(), "not json"));
            }));
            Assert.Throws<CalibreInvalidResponseException>(() => proxy.GetBooks(new() { 7 }, new CalibreSettings { Host = "localhost" }));
            Assert.That(attempts, Is.EqualTo(retry ? 2 : 1));
        }

        [Test]
        public void missing_format_map_is_safe_to_inspect()
        {
            Assert.That(CalibreProxy.GetOriginalFormat(null), Is.Null);
            var http = Proxy<IHttpClient>((_, args) => new HttpResponse<Dictionary<int, CalibreBook>>(
                new HttpResponse((HttpRequest)args[0], new HttpHeader(), "{\"1\":{\"application_id\":1,\"format_metadata\":null}}")));
            Assert.That(Reader(http).GetBooks(new() { 1 }, new CalibreSettings { Host = "localhost" }).Single().Id, Is.EqualTo(1));
        }

        [Test]
        public void reads_one_supplied_page_and_ignores_null_books_from_calibre()
        {
            var pages = new List<int[]>();
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                var query = HttpUtility.ParseQueryString(new Uri(request.Url.ToString()).Query);
                var ids = query["ids"].Split(',').Select(int.Parse).ToArray(); pages.Add(ids);
                var books = ids.ToDictionary(id => id, id => id == 2 ? null : new CalibreBook { Id = id, Formats = new() });
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(), books.ToJson()));
            });
            var rows = Reader(http).GetBooks(Enumerable.Range(1, 750).Concat(new[] { 1 }).ToList(), new CalibreSettings { Host = "localhost" });
            Assert.That(pages.Select(page => page.Length), Is.EqualTo(new[] { 750 }));
            Assert.That(rows.Select(book => book.Id), Is.EqualTo(Enumerable.Range(1, 750).Where(id => id != 2)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void only_successful_connection_test_clears_that_library_backoff(bool success)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger()); var gate = new CalibreLibraryGate(health);
            var settings = new CalibreSettings { Host = "localhost", Library = "books" };
            var other = new CalibreSettings { Host = "localhost", Library = "other" };
            gate.ReportException(gate.GetKey(settings), RetagMediaScopeFixture.ResponseError(504));
            gate.ReportException(gate.GetKey(other), RetagMediaScopeFixture.ResponseError(504));
            var http = Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                if (request.Url.ToString().Contains("library-info"))
                    return new HttpResponse<CalibreLibraryInfo>(new HttpResponse(request, new HttpHeader(), "{\"default_library\":\"books\",\"library_map\":{\"books\":\"Books\"}}"));
                return new HttpResponse(request, new HttpHeader(), success ? "<title>calibre</title>" : "unrecognized");
            });
            if (success)
            {
                Reader(http, gate).Test(settings);
            }
            else
            {
                Assert.Catch(() => Reader(http, gate).Test(settings));
            }
            Assert.That(gate.TryBegin(gate.GetKey(settings), out _), Is.EqualTo(success));
            Assert.That(gate.TryBegin(gate.GetKey(other), out _), Is.False);
        }

        [Test]
        public void successful_test_without_identity_does_not_report_success_under_a_null_key()
        {
            var gate = Proxy<ICalibreLibraryGate>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ICalibreLibraryGate.GetKey)));
                return null;
            });
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                if (request.Url.ToString().Contains("library-info"))
                    return new HttpResponse<CalibreLibraryInfo>(new HttpResponse(request, new HttpHeader(), "{\"default_library\":\"books\",\"library_map\":{\"books\":\"Books\"}}"));
                return new HttpResponse(request, new HttpHeader(), "<title>calibre</title>");
            });
            Reader(http, gate).Test(new CalibreSettings { Host = "localhost", Library = "books" });
        }

        [TestCase("refused", 2)]
        [TestCase("dns", 2)]
        [TestCase("timeout", 1)]
        [TestCase("gateway", 1)]
        [TestCase("500", 1)]
        [TestCase("cancelled", 1)]
        public async Task retry_is_at_the_failed_request_not_the_whole_book(string failure, int expected)
        {
            var error = failure == "cancelled" ? new OperationCanceledException("fixture cancellation") : failure is "refused" or "dns" or "timeout"
                ? await HttpTransportErrorsFixture.CaptureTransportFailure(failure == "refused" ? "closed-port" : failure)
                : RetagMediaScopeFixture.ResponseError(failure == "gateway" ? 504 : 500);
            var directory = Path.Combine(Path.GetTempPath(), "calibre-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "book.epub"); File.WriteAllText(path, "fixture");
                var counts = new Dictionary<string, int>();
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    var request = (HttpRequest)args[0];
                    var step = request.Url.ToString().Contains("set-fields") ? "set" : method.Name == "Get" ? "get" : "embed";
                    counts.TryGetValue(step, out var count); counts[step] = count + 1;
                    if (step == "embed")
                    {
                        throw error;
                    }
                    if (step == "get")
                    {
                        var book = new CalibreBook { Formats = new() { ["epub"] = new CalibreBookFormat { Path = path } } };
                        return new HttpResponse<CalibreBook>(new HttpResponse(request, new HttpHeader(), book.ToJson()));
                    }
                    return new HttpResponse(request, new HttpHeader(), "{\"1\":{}}");
                });
                var proxy = new CalibreProxy(http, null,
                    Proxy<IRemotePathMappingService>((_, args) => (OsPath)args[1]),
                    Proxy<IRootFolderWatchingService>((_, _) => null), null, null, new CacheManager(),
                    Proxy<IFileMutationSafetyService>((_, _) => false), new CalibreLibraryGate(new HttpServiceHealth(LogManager.GetCurrentClassLogger())), LogManager.GetCurrentClassLogger());
                var author = new Author { Name = "Author" };
                var file = new BookFile
                {
                    Path = path,
                    Author = author,
                    CalibreId = 1,
                    Edition = new Edition { Title = "Title", Language = "en", Book = new Book { Author = author } }
                };
                if (failure == "cancelled")
                {
                    Assert.Throws<OperationCanceledException>(() => proxy.SetFields(file, new CalibreSettings { Host = "localhost", Library = "books" }, false, true));
                }
                else
                {
                    Assert.That(Assert.Throws<CalibreException>(() => proxy.SetFields(file, new CalibreSettings { Host = "localhost", Library = "books" }, false, true)).WriteAccepted, Is.True);
                }
                Assert.That(counts["set"], Is.EqualTo(1), "Earlier successful writes must not be replayed");
                Assert.That(counts["get"], Is.EqualTo(1)); Assert.That(counts["embed"], Is.EqualTo(expected));
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task refused_read_retries_once_and_can_recover(bool preview)
        {
            var error = await HttpTransportErrorsFixture.CaptureTransportFailure("closed-port");
            var calls = 0;
            var http = Proxy<IHttpClient>((_, args) =>
            {
                if (++calls == 1)
                {
                    throw error;
                }
                var response = new HttpResponse((HttpRequest)args[0], new HttpHeader(), preview ? "{\"1\":{\"application_id\":1,\"format_metadata\":{}}}" : "{\"application_id\":1,\"format_metadata\":{}}");
                return preview ? (object)new HttpResponse<Dictionary<int, CalibreBook>>(response) : new HttpResponse<CalibreBook>(response);
            });
            var proxy = new CalibreProxy(http, null, null, null, null, null, new CacheManager(), null, new CalibreLibraryGate(new HttpServiceHealth(LogManager.GetCurrentClassLogger())), LogManager.GetCurrentClassLogger());
            var settings = new CalibreSettings { Host = "localhost" };
            if (preview)
            {
                Assert.That(proxy.GetBooks(new() { 1 }, settings).Single().Id, Is.EqualTo(1));
            }
            else
            {
                Assert.That(proxy.GetBook(1, settings).Id, Is.EqualTo(1));
            }
            Assert.That(calls, Is.EqualTo(2));
        }
    }
}
