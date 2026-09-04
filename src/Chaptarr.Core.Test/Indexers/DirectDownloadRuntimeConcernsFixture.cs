using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadRuntimeConcernsFixture
    {
        [Test]
        public async Task validate_api_key_returns_empty_outcome_when_key_is_null()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", null);

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.EmptyKey));
            Assert.That(result.Message, Does.Contain("No API key"));
            Assert.That(transport.RequestedUrls, Is.Empty, "Should not make any HTTP calls for empty key");
        }

        [Test]
        public async Task validate_api_key_returns_empty_outcome_when_key_is_whitespace()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "   ");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.EmptyKey));
        }

        [Test]
        public async Task validate_api_key_returns_valid_when_api_returns_download_url()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "{\"download_url\":\"https://downloads.example/book.epub\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "real-secret");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.Valid));
            Assert.That(result.Message, Does.Contain("valid"));
            Assert.That(transport.RequestedUrls.Single(), Does.Contain("key=real-secret"));
        }

        [Test]
        public async Task validate_api_key_returns_valid_when_download_url_is_null_but_no_error()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "{\"download_url\":null}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "test-key");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.Valid));
        }

        [Test]
        public async Task validate_api_key_returns_no_downloads_remaining_when_api_returns_no_downloads_error()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "exhausted-key");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.NoDownloadsRemaining));
            Assert.That(result.Message, Does.Contain("No downloads left"));
            Assert.That(result.Message, Does.Not.Contain("exhausted-key"), "API key must not leak into messages");
        }

        [Test]
        public async Task validate_api_key_returns_invalid_when_api_returns_401()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.Unauthorized,
                    "{\"error\":\"unauthorized\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "bad-key-1234");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.InvalidOrExpired));
            Assert.That(result.Message, Does.Not.Contain("bad-key-1234"), "API key must not leak into messages");
        }

        [Test]
        public async Task validate_api_key_returns_invalid_when_response_is_html_challenge()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "<html><head><title>DDoS Protection</title></head><body>challenge</body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "key-that-triggers-challenge");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.InvalidOrExpired));
            Assert.That(result.Message, Does.Contain("non-JSON"));
            Assert.That(result.Message, Does.Not.Contain("key-that-triggers-challenge"), "API key must not leak into messages");
        }

        [Test]
        public async Task validate_api_key_returns_transient_when_network_times_out()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => throw new System.Net.WebException("Connection timed out", System.Net.WebExceptionStatus.Timeout));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "any-key");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.TransientFailure));
            Assert.That(result.Message, Does.Not.Contain("any-key"), "API key must not leak into transient failure messages");
        }

        [Test]
        public async Task validate_api_key_returns_invalid_when_api_returns_invalid_key_error()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "{\"error\":\"invalid key provided\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "wrong-secret");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.InvalidOrExpired));
            Assert.That(result.Message, Does.Contain("invalid key"));
            Assert.That(result.Message, Does.Not.Contain("wrong-secret"));
        }

        [Test]
        public async Task validate_api_key_returns_invalid_when_api_returns_invalid_secret_key_with_200()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "{\"download_url\":null,\"error\":\"Invalid secret key\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "bad-key-AAAA");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.InvalidOrExpired));
            Assert.That(result.Message, Does.Contain("Invalid secret key").IgnoreCase);
            Assert.That(result.Message, Does.Not.Contain("bad-key-AAAA"), "API key must not leak into messages");
        }

        [Test]
        public async Task validate_api_key_returns_invalid_when_api_returns_401_with_invalid_secret_key()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.Unauthorized,
                    "{\"download_url\":null,\"error\":\"Invalid secret key\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "expired-key-1234");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.InvalidOrExpired));
            Assert.That(result.Message, Does.Not.Contain("expired-key-1234"), "API key must not leak into messages");
        }

        [Test]
        public async Task validate_api_key_includes_api_key_in_url_query_but_redacts_in_messages()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => throw new System.Net.WebException("timeout", System.Net.WebExceptionStatus.Timeout));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "SUPER-SECRET-KEY-12345");

            Assert.That(transport.RequestedUrls.Single(), Does.Contain("key=SUPER-SECRET-KEY-12345"), "Key must be in URL for API call");
            Assert.That(result.Message, Does.Not.Contain("SUPER-SECRET-KEY-12345"), "Key must not appear in result message");
        }

        [Test]
        public async Task validate_api_key_probe_uses_impossible_md5_to_avoid_consuming_downloads()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "{\"download_url\":null}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            await resolver.ValidateApiKeyAsync("https://catalog.example", "test-key");

            var probeUrl = transport.RequestedUrls.Single();
            Assert.That(probeUrl, Does.Contain("md5=00000000000000000000000000000000"), "Probe must use an impossible md5 to avoid matching real files");
        }

        [Test]
        public async Task validate_api_key_message_never_leaks_key_across_all_outcomes()
        {
            var secretKey = "LEAK-TEST-KEY-98765";

            var scenarios = new (string label, HttpStatusCode status, string body)[]
            {
                ("Valid", HttpStatusCode.OK, "{\"download_url\":\"https://downloads.example/book.epub\"}"),
                ("NoDownloadsRemaining", HttpStatusCode.TooManyRequests, "{\"download_url\":null,\"error\":\"No downloads left\"}"),
                ("InvalidOrExpired_401", HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}"),
                ("InvalidOrExpired_SecretKey", HttpStatusCode.OK, "{\"download_url\":null,\"error\":\"Invalid secret key\"}"),
                ("InvalidOrExpired_HtmlChallenge", HttpStatusCode.OK, "<html><head><title>DDoS Protection</title></head><body>challenge</body></html>")
            };

            foreach (var (label, status, body) in scenarios)
            {
                var transport = new DirectDownloadTestHttp();
                var contentType = body.StartsWith("<", StringComparison.Ordinal) ? "text/html; charset=utf-8" : "application/json";
                transport.AddRoute(
                    url => url.Contains("fast_download.json", StringComparison.Ordinal),
                    request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, status, body, contentType)));

                var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());
                var result = await resolver.ValidateApiKeyAsync("https://catalog.example", secretKey);

                Assert.That(result.Message, Does.Not.Contain(secretKey), $"API key must not leak in {label} ({result.Outcome}) message");
            }
        }

        [Test]
        public async Task api_request_includes_referer_and_user_agent_headers()
        {
            HttpHeader capturedHeaders = null;
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request =>
                {
                    capturedHeaders = request.Headers;
                    return Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                        request,
                        HttpStatusCode.OK,
                        "{\"download_url\":\"https://downloads.example/book.epub\"}",
                        "application/json"));
                });

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            await resolver.ValidateApiKeyAsync("https://catalog.example", "test-key");

            Assert.That(capturedHeaders, Is.Not.Null);
            Assert.That(capturedHeaders["User-Agent"], Does.Contain("Chaptarr"));
            Assert.That(capturedHeaders["Referer"], Is.EqualTo("https://catalog.example/"));
        }

        [Test]
        public async Task detail_page_request_includes_referer_and_user_agent_headers()
        {
            HttpHeader capturedHeaders = null;
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request =>
                {
                    capturedHeaders = request.Headers;
                    return Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                        request,
                        HttpStatusCode.OK,
                        "<html><body><a href=\"/slow_download/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0/0\" class=\"js-download-link\">Slow Partner Server #1</a></body></html>",
                        "text/html; charset=utf-8"));
                });

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            await resolver.TryResolveAsync(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                null,
                "CatalogPage");

            Assert.That(capturedHeaders, Is.Not.Null);
            Assert.That(capturedHeaders["User-Agent"], Does.Contain("Chaptarr"));
            Assert.That(capturedHeaders["Referer"], Is.EqualTo("https://catalog.example/"));
        }

        [Test]
        public async Task fast_download_api_skips_detail_page_on_429_response()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}",
                    "application/json")));
            transport.AddRoute(
                url => url == "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "<html><body><a href=\"/get/book.epub\">GET</a></body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "test-key",
                "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://catalog.example/get/book.epub"), "Should fall back to detail page scraping on 429");
            Assert.That(transport.RequestedUrls.Count(url => url.Contains("fast_download.json")), Is.GreaterThanOrEqualTo(1));
            Assert.That(transport.RequestedUrls.Count(url => url.Contains("fast_download.json")), Is.LessThanOrEqualTo(5), "Should stop rotating domain indices after bounded attempts");
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("/md5/")), Is.True, "Should have scraped detail page");
        }
    }
}
