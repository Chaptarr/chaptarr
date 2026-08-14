using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class LoggingMiddlewareFixture
    {
        private LoggingConfiguration _originalConfiguration;

        [SetUp]
        public void SetUp()
        {
            _originalConfiguration = LogManager.Configuration;
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _originalConfiguration;
            LogManager.ReconfigExistingLoggers();
        }

        [Test]
        public async Task should_sanitize_and_truncate_user_agent_in_http_trace_logs()
        {
            var memoryTarget = ConfigureLogging();
            var middleware = new LoggingMiddleware(_ => Task.CompletedTask);
            var context = CreateContext("/api/v1/books", "?page=1");
            context.Request.Headers["User-Agent"] = $"Browser\r\nTabbed\t{new string('a', 280)}TAILMARKER";

            await middleware.InvokeAsync(context);

            var combinedLogs = string.Join("\n", memoryTarget.Logs);

            Assert.That(combinedLogs, Does.Contain("Req:"));
            Assert.That(combinedLogs, Does.Contain("Res:"));
            Assert.That(combinedLogs, Does.Contain("Browser  Tabbed"));
            Assert.That(combinedLogs, Does.Not.Contain("\r"));
            Assert.That(combinedLogs, Does.Not.Contain("\t"));
            Assert.That(combinedLogs, Does.Not.Contain("TAILMARKER"));
        }

        [Test]
        public void should_log_response_and_rethrow_when_downstream_throws()
        {
            var memoryTarget = ConfigureLogging();
            var middleware = new LoggingMiddleware(_ => throw new InvalidOperationException("boom"));
            var context = CreateContext("/api/v1/books", string.Empty);

            var exception = Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

            Assert.That(exception.Message, Is.EqualTo("boom"));
            Assert.That(memoryTarget.Logs.Any(log => log.Contains("Req:", StringComparison.Ordinal)), Is.True);
            Assert.That(memoryTarget.Logs.Any(log => log.Contains("Res:", StringComparison.Ordinal)), Is.True);
        }

        [TestCase("/api/v1/health?apikey=secret&marker=PLAIN", "", false)]
        [TestCase("/ebook/api/v1/health?apikey=secret&marker=FACADE", "", true)]
        [TestCase("/chaptarr/api/v1/health?apikey=secret&marker=URLBASE", "/chaptarr", false)]
        [TestCase("/chaptarr/ebook/api/v1/health?apikey=secret&marker=COMBINED", "/chaptarr", true)]
        public async Task should_classify_normalized_api_requests_and_log_the_original_target(string rawTarget, string urlBase, bool isFacade)
        {
            var memoryTarget = ConfigureLogging();
            var services = new ServiceCollection().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            var normalizedPath = string.Empty;
            var normalizedQuery = string.Empty;

            app.UsePathBase(new PathString(urlBase));
            app.UseMiddleware<ServarrMediaTypeScopeMiddleware>();
            app.UseMiddleware<LoggingMiddleware>();
            app.Run(context =>
            {
                normalizedPath = context.Request.Path;
                normalizedQuery = context.Request.QueryString.Value;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });

            var context = CreateContextFromRawTarget(rawTarget);
            await app.Build()(context);

            var sanitizedTarget = rawTarget.Replace("secret", "<REDACTED>", StringComparison.Ordinal);
            var combinedLogs = string.Join("\n", memoryTarget.Logs);

            Assert.That(normalizedPath, Is.EqualTo("/api/v1/health"));
            Assert.That(normalizedQuery.Contains("mediaType=ebook", StringComparison.Ordinal), Is.EqualTo(isFacade));
            Assert.That(memoryTarget.Logs.Any(log => log.StartsWith("Http|Req:", StringComparison.Ordinal) && log.Contains(sanitizedTarget, StringComparison.Ordinal)), Is.True);
            Assert.That(memoryTarget.Logs.Any(log => log.StartsWith("Http|Res:", StringComparison.Ordinal) && log.Contains(sanitizedTarget, StringComparison.Ordinal)), Is.True);
            Assert.That(memoryTarget.Logs.Any(log => log.Contains($"Api|[GET] {sanitizedTarget}: 200.OK", StringComparison.Ordinal)), Is.True);
            Assert.That(combinedLogs, Does.Not.Contain("secret"));
            Assert.That(combinedLogs, Does.Not.Contain("mediaType=ebook"));
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memoryTarget = new MemoryTarget("memory")
            {
                Layout = "${logger}|${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, memoryTarget, "Http");
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memoryTarget, "Api");

            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();

            return memoryTarget;
        }

        private static DefaultHttpContext CreateContext(string path, string queryString)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(queryString);
            context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
            context.Response.StatusCode = StatusCodes.Status200OK;
            return context;
        }

        private static DefaultHttpContext CreateContextFromRawTarget(string rawTarget)
        {
            var queryIndex = rawTarget.IndexOf('?', StringComparison.Ordinal);
            var path = queryIndex < 0 ? rawTarget : rawTarget[..queryIndex];
            var query = queryIndex < 0 ? string.Empty : rawTarget[queryIndex..];
            var context = CreateContext(path, query);
            context.Features.Get<IHttpRequestFeature>().RawTarget = rawTarget;
            return context;
        }
    }
}
