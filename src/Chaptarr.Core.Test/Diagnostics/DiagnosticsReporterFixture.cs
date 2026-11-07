using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using Chaptarr.Diagnostics;

namespace Chaptarr.Core.Test.Diagnostics
{
    [TestFixture]
    public class DiagnosticsReporterFixture
    {
        private string _previousEnabled;
        private string _previousEndpoint;
        private string _previousToken;
        private string _previousLegacyEndpoint;
        private string _previousLegacyToken;
        private string _previousLegacyEnabled;

        [SetUp]
        public void SetUp()
        {
            _previousEnabled = Environment.GetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_ENABLED");
            _previousEndpoint = Environment.GetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT");
            _previousToken = Environment.GetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_AUTH_TOKEN");
            _previousLegacyEndpoint = Environment.GetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT");
            _previousLegacyToken = Environment.GetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN");
            _previousLegacyEnabled = Environment.GetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_ENABLED");
            ClearEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_ENABLED", _previousEnabled);
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT", _previousEndpoint);
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_AUTH_TOKEN", _previousToken);
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT", _previousLegacyEndpoint);
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN", _previousLegacyToken);
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_ENABLED", _previousLegacyEnabled);
        }

        [Test]
        public async Task should_send_nothing_unless_the_operator_explicitly_enables_it()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT", "https://collector.example/v1/metrics");
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_AUTH_TOKEN", "test-secret");
            var handler = new CapturingHandler();
            using var reporter = new DiagnosticsReporter(LogManager.GetLogger(nameof(DiagnosticsReporterFixture)), handler);

            reporter.Start();
            reporter.Record("application.started");
            await reporter.StopAsync();

            Assert.That(handler.RequestCount, Is.Zero);
        }

        [Test]
        public async Task should_send_only_aggregated_allowlisted_events_to_the_configured_https_endpoint()
        {
            SetValidEnvironment("https://collector.example/v1/metrics", "test-secret");
            var handler = new CapturingHandler();
            using var reporter = new DiagnosticsReporter(LogManager.GetLogger(nameof(DiagnosticsReporterFixture)), handler);

            reporter.Start();
            reporter.Record("book.imported");
            reporter.Record("book.imported");
            reporter.Record("download.failed");
            reporter.Record("/private/library/Secret Title.epub");
            await reporter.StopAsync();

            Assert.That(handler.RequestCount, Is.EqualTo(1));
            Assert.That(handler.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.RequestUri, Is.EqualTo(new Uri("https://collector.example/v1/metrics")));
            Assert.That(handler.Authorization, Is.EqualTo(new AuthenticationHeaderValue("Bearer", "test-secret")));
            Assert.That(handler.Body, Does.Not.Contain("test-secret"));
            Assert.That(handler.Body, Does.Not.Contain("Secret Title"));
            Assert.That(handler.Body, Does.Not.Contain("/private/library"));

            using var payload = JsonDocument.Parse(handler.Body);
            var resource = payload.RootElement
                .GetProperty("resourceMetrics")[0]
                .GetProperty("resource");
            Assert.That(resource.GetProperty("attributes")[0].GetProperty("value").GetProperty("stringValue").GetString(), Is.EqualTo("chaptarrng"));

            var dataPoints = payload.RootElement
                .GetProperty("resourceMetrics")[0]
                .GetProperty("scopeMetrics")[0]
                .GetProperty("metrics")[0]
                .GetProperty("sum")
                .GetProperty("dataPoints");
            Assert.That(dataPoints.GetArrayLength(), Is.EqualTo(2));
            Assert.That(FindCount(dataPoints, "book.imported"), Is.EqualTo("2"));
            Assert.That(FindCount(dataPoints, "download.failed"), Is.EqualTo("1"));
        }

        [Test]
        [TestCase("http://collector.example/v1/metrics")]
        [TestCase("https://user:password@collector.example/v1/metrics")]
        [TestCase("https://collector.example/v1/metrics?token=secret")]
        [TestCase("https://collector.example/v1/metrics#fragment")]
        [TestCase("https://collector.example/custom-endpoint")]
        public async Task should_send_nothing_when_endpoint_is_invalid(string endpoint)
        {
            SetValidEnvironment(endpoint, "test-secret");
            var handler = new CapturingHandler();
            using var reporter = new DiagnosticsReporter(LogManager.GetLogger(nameof(DiagnosticsReporterFixture)), handler);

            reporter.Start();
            reporter.Record("application.started");
            await reporter.StopAsync();

            Assert.That(handler.RequestCount, Is.Zero);
        }

        [Test]
        public async Task should_accept_legacy_credentials_when_a_migrated_install_keeps_them()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_ENABLED", "true");
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT", "https://collector.example/v1/metrics");
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN", "legacy-secret");
            var handler = new CapturingHandler();
            using var reporter = new DiagnosticsReporter(LogManager.GetLogger(nameof(DiagnosticsReporterFixture)), handler);

            reporter.Start();
            reporter.Record("application.started");
            await reporter.StopAsync();

            Assert.That(handler.RequestCount, Is.EqualTo(1));
            Assert.That(handler.Authorization, Is.EqualTo(new AuthenticationHeaderValue("Bearer", "legacy-secret")));
            Assert.That(handler.Body, Does.Not.Contain("legacy-secret"));
        }

        [Test]
        public async Task should_let_an_explicit_current_disable_override_the_legacy_enable_flag()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_ENABLED", "false");
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_ENABLED", "true");
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT", "https://collector.example/v1/metrics");
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN", "legacy-secret");
            var handler = new CapturingHandler();
            using var reporter = new DiagnosticsReporter(LogManager.GetLogger(nameof(DiagnosticsReporterFixture)), handler);

            reporter.Start();
            reporter.Record("application.started");
            await reporter.StopAsync();

            Assert.That(handler.RequestCount, Is.Zero);
        }

        private static string FindCount(JsonElement dataPoints, string eventName)
        {
            foreach (var point in dataPoints.EnumerateArray())
            {
                if (point.GetProperty("attributes")[0].GetProperty("value").GetProperty("stringValue").GetString() == eventName)
                {
                    return point.GetProperty("asInt").GetString();
                }
            }

            return null;
        }

        private void SetValidEnvironment(string endpoint, string token)
        {
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_ENABLED", "true");
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT", endpoint);
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_AUTH_TOKEN", token);
        }

        private void ClearEnvironment()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_ENABLED", null);
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT", null);
            Environment.SetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_ENABLED", null);
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }
            public HttpMethod Method { get; private set; }
            public Uri RequestUri { get; private set; }
            public AuthenticationHeaderValue Authorization { get; private set; }
            public string Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                Method = request.Method;
                RequestUri = request.RequestUri;
                Authorization = request.Headers.Authorization;
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }
}
