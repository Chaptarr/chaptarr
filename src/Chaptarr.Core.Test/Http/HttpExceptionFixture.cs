using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Instrumentation.Sentry;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using Sentry;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpExceptionFixture
    {
        private const string ErrorBody = "{\"message\":\"synthetic provider error detail\",\"url\":\"https://example.test/api?apikey=synthetic-key\",\"dl\":\"synthetic-download-token\"}";

        private sealed class ResponseDispatcher(HttpStatusCode status, bool hasBody) : IHttpDispatcher
        {
            public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies) =>
                Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/json" },
                    hasBody ? Encoding.UTF8.GetBytes(ErrorBody) : null, status));
        }

        private sealed class RenderedFileTarget : NzbDroneFileTarget
        {
            public string Render(LogEventInfo logEvent)
            {
                var builder = new StringBuilder();
                RenderFormattedMessage(logEvent, builder);
                return builder.ToString();
            }
        }

        [TestCase(HttpStatusCode.BadRequest, false, false, true)]
        [TestCase(HttpStatusCode.BadGateway, false, true, true)]
        [TestCase(HttpStatusCode.TooManyRequests, false, false, true)]
        [TestCase(HttpStatusCode.Unauthorized, true, false, true)]
        [TestCase(HttpStatusCode.OK, true, false, true)]
        [TestCase(HttpStatusCode.OK, false, false, true)]
        [TestCase(HttpStatusCode.BadGateway, false, false, false)]
        public async Task should_log_error_bodies_once_at_trace_through_existing_output_cleanser(
            HttpStatusCode status, bool logResponse, bool suppressError, bool hasBody)
        {
            using var logs = new LogFactory();
            var memory = new MemoryTarget { Layout = "${level}|${message}" };
            var console = new MemoryTarget { Layout = new CleanseLayout { Inner = "${level}|${message}" } };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, console);
            logs.Configuration = configuration;
            var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(),
                null, new ResponseDispatcher(status, hasBody), logs.GetLogger("HttpClient"));
            var request = new HttpRequest("https://example.test/api")
            {
                LogResponseContent = logResponse,
                SuppressHttpError = suppressError
            };
            Exception failure = null;
            try
            {
                await client.ExecuteAsync(request);
            }
            catch (HttpException ex)
            {
                failure = ex;
            }

            var isError = (int)status >= 400;
            var bodies = memory.Logs.Where(line => line.StartsWith("Trace|Response content (")).ToList();
            Assert.That(failure != null, Is.EqualTo(isError && !suppressError));
            Assert.That(bodies, Has.Count.EqualTo(hasBody && (isError || logResponse) ? 1 : 0));
            Assert.That(memory.Logs.Count(line => line.StartsWith("Warn|")), Is.EqualTo(isError && !suppressError ? 1 : 0));
            Assert.That(memory.Logs.Where(line => !line.StartsWith("Trace|")), Has.None.Contains("synthetic provider error detail"));
            Assert.That(failure?.ToString() ?? string.Empty, Does.Not.Contain("synthetic provider error detail"));

            if (bodies.Count == 1)
            {
                Assert.That(bodies.Single(), Does.Contain(ErrorBody));
                var traceEvent = new LogEventInfo(LogLevel.Trace, "HttpClient", bodies.Single());
                using var file = new RenderedFileTarget { Layout = "${message}" };
                var sentry = SentryCleanser.CleanseEvent(new SentryEvent
                {
                    Message = new SentryMessage { Formatted = bodies.Single(), Message = bodies.Single(), Params = new[] { bodies.Single() } }
                });
                var breadcrumb = SentryCleanser.CleanseBreadcrumb(new Breadcrumb(bodies.Single(), "http", null, "HttpClient", BreadcrumbLevel.Debug));
                foreach (var output in new[] { console.Logs.Single(line => line.StartsWith("Trace|Response content (")),
                    file.Render(traceEvent), sentry.Message.Formatted, sentry.Message.Message, sentry.Message.Params.Single(), breadcrumb.Message })
                {
                    Assert.That(output, Does.Contain("synthetic provider error detail"));
                    Assert.That(output, Does.Not.Contain("synthetic-key").And.Not.Contain("synthetic-download-token"));
                    Assert.That(output, Does.Contain("apikey=(removed)").And.Contain("\"dl\":\"(removed)\""));
                }
            }
        }

        [Test]
        [NonParallelizable]
        public void metadata_lookup_does_not_repeat_the_shared_http_error_body()
        {
            var previous = LogManager.Configuration;
            var memory = new MemoryTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            LogManager.Configuration = configuration;
            try
            {
                var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(),
                    null, new ResponseDispatcher(HttpStatusCode.BadGateway, true), LogManager.GetLogger("HttpClient"));
                var proxy = MetadataSource.BookInfo.BookInfoProxyCanonicalLookupFixture.CreateProxy(client);
                Assert.Throws<NzbDrone.Core.MetadataSource.BookInfo.BookInfoException>(() => proxy.GetWorkInfo("gr:3634639", NzbDrone.Core.Books.BookMediaType.Audiobook, "gr:58"));
                Assert.That(memory.Logs.Count(line => line.Contains("synthetic provider error detail")), Is.EqualTo(1));
                Assert.That(memory.Logs.Single(line => line.Contains("synthetic provider error detail")), Does.StartWith("Trace|Response content ("));
            }
            finally { LogManager.Configuration = previous; }
        }

        [Test]
        public void should_sanitize_request_url_in_default_message()
        {
            var request = new HttpRequest("https://example.com/api?apikey=real-secret&query=value");
            var response = new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.Unauthorized);

            var exception = new HttpException(request, response);

            Assert.That(exception.Message, Does.Not.Contain("real-secret"));
            Assert.That(exception.Message, Does.Contain("apikey=(removed)"));
        }

        [Test]
        public void should_keep_http_error_summary_without_appending_full_response_body()
        {
            const string responseBody = "synthetic provider error detail";
            var request = new HttpRequest("https://indexer.example/api?apikey=synthetic-key");
            var headers = new HttpHeader
            {
                ContentType = "application/json"
            };
            var response = new HttpResponse(request, headers, responseBody, HttpStatusCode.BadGateway);

            var responseSummary = response.ToString();
            var exceptionSummary = new HttpException(request, response).ToString();
            var traceBody = CleanseLogMessage.Cleanse($"Response content ({response.ResponseData.Length} bytes): {response.Content}");

            Assert.That(responseSummary, Does.Not.Contain(responseBody));
            Assert.That(responseSummary, Does.Contain("502.BadGateway"));
            Assert.That(responseSummary, Does.Contain($"({response.ResponseData.Length} bytes)"));
            Assert.That(exceptionSummary, Does.Not.Contain(responseBody));
            Assert.That(exceptionSummary, Does.Contain("502:BadGateway"));
            Assert.That(traceBody, Does.Contain(responseBody));
        }
    }
}
