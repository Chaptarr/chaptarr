using System;
using System.Net;
using System.Net.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.MetadataSource
{
    [TestFixture]
    public class MetadataServerHealthServiceFixture
    {
        private const string Source = "https://api2.chaptarr.com";

        [Test]
        public void should_open_short_cooldown_then_allow_single_probe()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("edge returned 525"));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));

            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(service.TryBeginRequest(Source, out _), Is.False);
        }

        [Test]
        public void optional_request_check_should_not_claim_half_open_probe()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("edge returned 525"));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.False);
            Assert.That(status.ProbeInProgress, Is.False);
            Assert.That(service.TryBeginRequest(Source, out _), Is.True,
                "the optional request must leave the recovery probe available for load-bearing metadata work");
        }

        [Test]
        public void should_escalate_failed_probe_then_reset_after_success()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportFailure(Source, new WebException("first failure"));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("probe failed"));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(175, 180));

            service.ReportSuccess(Source);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(0));
            Assert.That(status.EscalationLevel, Is.EqualTo(0));
        }

        [Test]
        public void manual_reset_should_clear_open_circuit_and_allow_immediate_requests()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportFailure(Source, new WebException("edge returned 525"));
            Assert.That(service.TryBeginRequest(Source, out _), Is.False);

            service.Reset(Source);

            var status = service.GetStatus(Source);
            Assert.That(status.IsHealthy, Is.True);
            Assert.That(status.IsRateLimited, Is.False);
            Assert.That(status.ConsecutiveFailures, Is.Zero);
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("still down"));
            Assert.That(service.TryBeginRequest(Source, out _), Is.False, "a post-reset failure must reopen the circuit");
        }

        [Test]
        public void should_use_short_cooldown_for_rate_limit_without_retry_after()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source);

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));
        }

        [Test]
        public void should_clamp_rate_limit_window_when_caller_supplies_large_retry_after()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source, TimeSpan.FromHours(24));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter, Is.LessThanOrEqualTo(MetadataServerHealthService.MaxRateLimitRetryAfter));
            Assert.That(retryAfter.TotalSeconds, Is.InRange(895, 900));
        }

        [Test]
        public void should_clamp_large_numeric_retry_after()
        {
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(RateLimitedResponse("86400"));

            Assert.That(retryAfter, Is.EqualTo(MetadataServerHealthService.MaxRateLimitRetryAfter));
        }

        [Test]
        public void should_clamp_large_date_retry_after()
        {
            var header = DateTime.UtcNow.AddHours(6).ToString("R");
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(RateLimitedResponse(header));

            Assert.That(retryAfter, Is.EqualTo(MetadataServerHealthService.MaxRateLimitRetryAfter));
        }

        [Test]
        public void should_open_circuit_for_modern_http_transport_failures()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, LogManager.GetCurrentClassLogger());

            gate.ReportException(new HttpRequestException("connection refused"));

            Assert.That(service.GetStatus(gate.SourceName).IsHealthy, Is.False);
            Assert.That(gate.TryBeginRequest(out _), Is.False);
        }

        [Test]
        public void cached_response_should_release_probe_without_marking_server_healthy()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, LogManager.GetCurrentClassLogger());
            service.ReportFailure(gate.SourceName, new HttpRequestException("connection refused"));
            var status = service.GetStatus(gate.SourceName);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(gate.TryBeginRequest(out _), Is.True);

            var headers = new HttpHeader();
            headers.Add("X-Cache-Status", "HIT");
            gate.ReportResponse(new HttpResponse(new HttpRequest(Source), headers, string.Empty, HttpStatusCode.OK));

            Assert.That(status.IsHealthy, Is.False);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(1));
            Assert.That(gate.TryBeginRequest(out _), Is.True);
        }

        [Test]
        public void should_floor_negative_numeric_retry_after_at_zero()
        {
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(RateLimitedResponse("-1"));

            Assert.That(retryAfter, Is.EqualTo(TimeSpan.Zero));
        }

        private static HttpResponse RateLimitedResponse(string retryAfter)
        {
            var headers = new HttpHeader();
            headers.Add("Retry-After", retryAfter);

            return new HttpResponse(new HttpRequest(Source), headers, string.Empty, HttpStatusCode.TooManyRequests);
        }
    }
}
