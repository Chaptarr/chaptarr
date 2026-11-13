using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpServiceHealthFixture
    {
        private const string Source = "https://api2.chaptarr.com";

        [TestCase(false)]
        [TestCase(true)]
        public void metadata_429_uses_shared_rate_limit_and_retry_after(bool thrown)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            var response = RateLimitedResponse("180");
            if (thrown)
            {
                gate.ReportException(new TooManyRequestsException(response.Request, response));
            }
            else
            {
                gate.ReportResponse(response);
            }
            var state = health.GetStatus(gate.SourceName);
            Assert.That(state.IsRateLimited, Is.True); Assert.That(state.ConsecutiveFailures, Is.Zero);
            Assert.That(gate.TryBeginRequest(out var delay), Is.False);
            Assert.That(delay.TotalSeconds, Is.InRange(175, 180));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void zero_retry_after_is_not_replaced_by_default(bool thrown)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var response = RateLimitedResponse("0");
            health.ReportResult(Source, thrown ? null : response, thrown ? new TooManyRequestsException(response.Request, response) : null);
            Assert.That(health.GetStatus(Source).IsRateLimited, Is.True);
            Assert.That(health.CanBeginRequest(Source, out _), Is.True);
            Assert.That(health.GetStatus(Source).ProbeInProgress, Is.False);
        }

        [TestCase(401, false)]
        [TestCase(401, true)]
        [TestCase(403, false)]
        [TestCase(403, true)]
        public void calibre_access_refusal_policy_does_not_change_metadata_policy(int status, bool thrown)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            var response = new HttpResponse(new HttpRequest(Source), new HttpHeader(), "refused", (HttpStatusCode)status);
            if (thrown)
            {
                gate.ReportException(new HttpException(response));
            }
            else
            {
                gate.ReportResponse(response);
            }
            Assert.That(gate.TryBeginRequest(out _), Is.True);
            Assert.That(health.GetStatus(gate.SourceName).IsHealthy, Is.True);
            Assert.That(health.ReportResult("Calibre fixture", response, responseFailure: HttpServiceResult.AccessDenied), Is.EqualTo(HttpServiceResult.AccessDenied));
            Assert.That(health.CanBeginRequest("Calibre fixture", out _), Is.False);
        }

        [Test]
        public void http_date_retry_after_is_culture_independent(
            [Values("fr-FR", "de-DE", "th-TH", "ar-SA", "fa-IR", "ja-JP")] string culture,
            [Values("R", "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM d HH:mm:ss yyyy")] string format)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var header = DateTimeOffset.UtcNow.AddMinutes(2).ToString(format, CultureInfo.InvariantCulture);
                var response = RateLimitedResponse(header);
                Assert.That(HttpServiceHealth.GetRetryAfter(response).Value.TotalSeconds, Is.InRange(115, 120), header);
                Assert.That(new TooManyRequestsException(response.Request, response).RetryAfter.TotalSeconds, Is.InRange(115, 120), header);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void padded_asctime_uses_the_http_calendar()
        {
            var response = RateLimitedResponse("Tue Jan  5 12:00:00 2027");
            var expected = new DateTimeOffset(2027, 1, 5, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UtcNow;
            Assert.That(RetryAfterParser.GetDelay(response).Value.TotalSeconds, Is.EqualTo(expected.TotalSeconds).Within(2));
        }

        [TestCase(522, "HTTP 522")]
        [TestCase(503, "HTTP 503 ServiceUnavailable")]
        public void http_reasons_do_not_repeat_unnamed_status_numbers(int status, string expected)
        {
            var error = new HttpRequestException("failure", null, (HttpStatusCode)status);
            Assert.That(HttpTransportErrors.GetStatusMessage(exception: error), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("garbage")]
        [TestCase("-1")]
        [TestCase("-5")]
        [TestCase("1.5")]
        [TestCase("2027-01-19T12:00:00Z")]
        public void invalid_or_absent_retry_after_keeps_the_callers_default_policy(string header)
        {
            var response = RateLimitedResponse(header);
            if (header == null)
            {
                response.Headers.Remove("Retry-After");
            }
            Assert.That(new TooManyRequestsException(response.Request, response).RetryAfter, Is.EqualTo(TimeSpan.Zero));
            Assert.That(HttpServiceHealth.GetRetryAfter(response), Is.Null);
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            health.ReportResult(Source, response);
            Assert.That(health.CanBeginRequest(Source, out var delay), Is.False);
            Assert.That(delay.TotalSeconds, Is.InRange(55, 60));
        }

        [TestCase(HttpServiceResult.Response)]
        [TestCase(HttpServiceResult.Inconclusive)]
        [TestCase(HttpServiceResult.RateLimited)]
        [TestCase(HttpServiceResult.ConnectionFailure)]
        [TestCase(HttpServiceResult.Outage)]
        public void shared_classifier_returns_the_request_outcome(HttpServiceResult expected)
        {
            var response = RateLimitedResponse("60");
            Exception exception = expected switch
            {
                HttpServiceResult.Response => new HttpException(new HttpResponse(response.Request, new HttpHeader(), "denied", HttpStatusCode.Forbidden)),
                HttpServiceResult.Inconclusive => new InvalidOperationException("local failure"),
                HttpServiceResult.RateLimited => new TooManyRequestsException(response.Request, response),
                HttpServiceResult.ConnectionFailure => new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)),
                _ => new WebException("request timed out", WebExceptionStatus.Timeout)
            };
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            Assert.That(health.ReportResult(Source, exception: exception), Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void metadata_status_message_keeps_its_existing_text(bool thrown)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            var response = new HttpResponse(new HttpRequest("https://example.test/private/path"), new HttpHeader(), "", HttpStatusCode.ServiceUnavailable);
            if (thrown)
            {
                gate.ReportException(new HttpException(response));
            }
            else
            {
                gate.ReportResponse(response);
            }
            Assert.That(health.GetStatus(gate.SourceName).LastErrorMessage, Is.EqualTo("Metadata server returned 503 ServiceUnavailable"));
        }

        [Test]
        public void unclassified_failure_releases_probe_without_clearing_outage()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            health.ReportFailure(gate.SourceName, new TimeoutException());
            var status = health.GetStatus(gate.SourceName); status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            Assert.That(gate.TryBeginRequest(out _), Is.True);
            gate.ReportException(new InvalidOperationException("fixture local failure"));
            Assert.That(status.ProbeInProgress, Is.False); Assert.That(status.IsHealthy, Is.False);
            Assert.That(gate.TryBeginRequest(out _), Is.True);
        }

        [Test]
        public void latest_failure_replaces_the_previous_trigger()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            health.ReportResult(Source, exception: new TimeoutException(), context: "known.epub");
            health.ReportResult(Source, exception: new TimeoutException());
            Assert.That(health.GetStatus(Source).FailureContext, Is.Null);
        }

        [Test]
        public void status_precedes_transport_fallback_in_the_shared_classifier()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            Assert.That(health.ReportResult(Source, exception: new HttpRequestException("server refused", null, HttpStatusCode.InternalServerError)), Is.EqualTo(HttpServiceResult.Response));
            Assert.That(health.GetStatus(Source).IsHealthy, Is.True);
            Assert.That(health.ReportResult(Source, exception: new HttpRequestException("gateway", null, HttpStatusCode.BadGateway)), Is.EqualTo(HttpServiceResult.Outage));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void metadata_500_response_and_exception_open_the_backoff(bool thrown)
        {
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("http://localhost/");
            var response = new HttpResponse(request, new HttpHeader(), "failed", HttpStatusCode.InternalServerError);
            if (thrown)
            {
                gate.ReportException(new HttpException(response));
            }
            else
            {
                gate.ReportResponse(response);
            }
            Assert.That(gate.TryBeginRequest(out var delay), Is.False);
            Assert.That(delay.TotalSeconds, Is.InRange(55, 60));
            Assert.That(service.GetStatus(gate.SourceName).ConsecutiveFailures, Is.EqualTo(1));
        }

        [Test]
        public void scheduling_check_shares_request_eligibility_without_stealing_recovery()
        {
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
            service.ReportFailure(Source, new WebException("unavailable"));
            Assert.That(service.CanBeginRequest(Source, out var wait), Is.False);
            Assert.That(wait.TotalSeconds, Is.InRange(55, 60));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
            Assert.That(status.ProbeInProgress, Is.False);
            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.False, "optional work still cannot probe");
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(service.CanBeginRequest(Source, out _), Is.False);
            Assert.That(service.TryBeginRequest(Source, out _), Is.False);
            status.ProbeStartedAt = DateTime.UtcNow.AddMinutes(-3);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True, "an abandoned probe can recover");
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            service.ReportSuccess(Source);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
        }

        [Test]
        public void should_open_short_cooldown_then_allow_single_probe()
        {
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());

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
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());

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
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());

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
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());

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
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source);

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));
        }

        [Test]
        public void should_clamp_rate_limit_window_when_caller_supplies_large_retry_after()
        {
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source, TimeSpan.FromHours(24));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter, Is.LessThanOrEqualTo(HttpServiceHealth.MaxRateLimitRetryAfter));
            Assert.That(retryAfter.TotalSeconds, Is.InRange(895, 900));
        }

        [Test]
        public void should_clamp_large_numeric_retry_after()
        {
            var retryAfter = HttpServiceHealth.GetRetryAfter(RateLimitedResponse("86400"));

            Assert.That(retryAfter, Is.EqualTo(HttpServiceHealth.MaxRateLimitRetryAfter));
        }

        [Test]
        public void should_clamp_large_date_retry_after()
        {
            var header = DateTime.UtcNow.AddHours(6).ToString("R");
            var retryAfter = HttpServiceHealth.GetRetryAfter(RateLimitedResponse(header));

            Assert.That(retryAfter, Is.EqualTo(HttpServiceHealth.MaxRateLimitRetryAfter));
        }

        [Test]
        public void should_open_circuit_for_modern_http_transport_failures()
        {
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, LogManager.GetCurrentClassLogger());

            gate.ReportException(new HttpRequestException("connection refused"));

            Assert.That(service.GetStatus(gate.SourceName).IsHealthy, Is.False);
            Assert.That(gate.TryBeginRequest(out _), Is.False);
        }

        [Test]
        public void cached_response_should_release_probe_without_marking_server_healthy()
        {
            var service = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
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
        public void invalid_negative_retry_after_is_not_an_http_delta()
        {
            var retryAfter = HttpServiceHealth.GetRetryAfter(RateLimitedResponse("-1"));

            Assert.That(retryAfter, Is.Null);
        }

        [TestCase("th-TH")]
        [TestCase("ar-SA")]
        [TestCase("fa-IR")]
        public void rate_limit_log_uses_the_invariant_calendar(string culture)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}: ${message}" };
            var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, messages); logs.Configuration = config;
            var health = new HttpServiceHealth(logs.GetLogger("HealthTest"));
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                health.ReportRateLimited("fixture", TimeSpan.FromSeconds(60));
                var expected = health.GetStatus("fixture").RateLimitedUntil.Value.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
                Assert.That(messages.Logs.Single(line => line.Contains("is rate limited until")), Does.EndWith(expected));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [TestCase("The SSL connection could not be established, see inner exception.", "Broken pipe")]
        [TestCase("An error occurred while sending the request.", "Connection reset by peer")]
        public void shared_health_warning_includes_the_inner_transport_reason(string outer, string inner)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}: ${message}" };
            var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, messages); logs.Configuration = config;
            var health = new HttpServiceHealth(logs.GetLogger("HealthTest"));
            health.ReportResult("fixture", exception: new HttpRequestException(HttpRequestError.SecureConnectionError, outer, new IOException("transport wrapper", new IOException(inner))));
            Assert.That(messages.Logs.Single(line => line.StartsWith("Warn:")), Does.Contain(inner).And.Contain(outer));
            Assert.That(health.GetStatus("fixture").LastErrorMessage, Does.Contain(inner));
        }


        [Test]
        public void metadata_html_does_not_claim_recovery()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            health.ReportFailure(gate.SourceName, new HttpRequestException("offline"));
            var state = health.GetStatus(gate.SourceName);
            state.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            Assert.That(gate.TryBeginRequest(out _), Is.True);
            gate.ReportException(new UnexpectedHtmlContentException(new HttpResponse(new HttpRequest(Source), new HttpHeader(), "<html>login</html>")));
            Assert.That(state.IsHealthy, Is.False);
            Assert.That(state.ProbeInProgress, Is.False);
            Assert.That(state.LastSuccess, Is.Null);
        }

        [TestCase(HttpServiceResult.Response)]
        [TestCase(HttpServiceResult.RateLimited)]
        [TestCase(HttpServiceResult.Outage)]
        public void response_override_rejects_unrelated_policy(HttpServiceResult result)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            Assert.Throws<ArgumentOutOfRangeException>(() => health.ReportResult("fixture", responseFailure: result));
            Assert.That(health.GetStatus("fixture").IsHealthy, Is.True);
        }

        [Test]
        public void shared_parser_clamps_past_dates_before_caller_policy()
        {
            Assert.That(RetryAfterParser.GetDelay(RateLimitedResponse("Wed, 01 Jan 2020 00:00:00 GMT")), Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void proxy_authentication_response_backs_off_without_changing_connect_only_retry()
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var response = new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "", HttpStatusCode.ProxyAuthenticationRequired);
            var error = new HttpException(response);
            Assert.That(health.ReportResult("proxy", exception: error), Is.EqualTo(HttpServiceResult.ConnectionFailure));
            Assert.That(health.CanBeginRequest("proxy", out _), Is.False);
            Assert.That(HttpTransportErrors.IsConnectionNotEstablished(error), Is.False, "An HTTP response must not add an automatic resend.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void proxy_tunnel_failure_backs_off_both_services_and_identifies_the_proxy(bool tunnel)
        {
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            Exception error = tunnel
                ? new HttpRequestException(HttpRequestError.ProxyTunnelError, "Proxy CONNECT rejected", statusCode: HttpStatusCode.ProxyAuthenticationRequired)
                : new HttpException(new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "", HttpStatusCode.ProxyAuthenticationRequired));
            Assert.That(health.ReportResult("fixture", exception: error), Is.EqualTo(HttpServiceResult.ConnectionFailure));
            Assert.That(health.CanBeginRequest("fixture", out _), Is.False);
            var metadata = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            metadata.ReportException(error);
            var calibre = new CalibreLibraryGate(health);
            calibre.ReportException("calibre-proxy", error);
            Assert.That(health.GetStatus(metadata.SourceName).LastErrorMessage, Does.Contain("Proxy returned HTTP 407 ProxyAuthenticationRequired"));
            Assert.That(health.GetStatus("calibre-proxy").LastErrorMessage, Is.EqualTo("Proxy returned HTTP 407 ProxyAuthenticationRequired"));

        }

        private static HttpResponse RateLimitedResponse(string retryAfter)
        {
            var headers = new HttpHeader();
            headers.Add("Retry-After", retryAfter);

            return new HttpResponse(new HttpRequest(Source), headers, string.Empty, HttpStatusCode.TooManyRequests);
        }
    }
}
