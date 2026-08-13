using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadGrabApiOnlyFixture
    {
        private const string Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string BaseUrl = "https://catalog.example";
        private const string InfoUrl = $"{BaseUrl}/md5/{Md5}";

        [Test]
        public async Task grab_api_success_returns_resolved_url()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterFastDownloadApi(transport, "https://cdn.example/book.epub");
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Success));
            Assert.That(result.ResolvedUrl, Is.EqualTo("https://cdn.example/book.epub"));
        }

        [Test]
        public async Task grab_api_success_does_not_scrape_detail_page()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterFastDownloadApi(transport, "https://cdn.example/book.epub");
            RegisterDetailPage(transport, "<a href=\"/get/book.epub\">GET</a>");
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(transport.RequestedUrls.Any(u => u.Contains("/md5/")), Is.False,
                "API-only grab must not scrape the detail page");
        }

        [Test]
        public async Task grab_api_success_does_not_invoke_browser_resolver()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterFastDownloadApi(transport, "https://cdn.example/book.epub");
            var browserResolver = new ThrowingBrowserResolver();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient(), browserResolver);

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Success));
            Assert.That(browserResolver.WasInvoked, Is.False, "Browser resolver must not be invoked during API-only grab");
        }

        [Test]
        public async Task grab_api_429_returns_unavailable_without_scraping()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}", "application/json")));
            RegisterDetailPage(transport, "<a href=\"/get/book.epub\">GET</a>");
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(result.ResolvedUrl, Is.Null);
            Assert.That(transport.RequestedUrls.Any(u => u.Contains("/md5/")), Is.False,
                "Must not fall back to detail page scraping on 429");
        }

        [Test]
        public async Task grab_api_401_returns_unavailable_without_scraping()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.Unauthorized,
                    "{\"error\":\"unauthorized\"}", "application/json")));
            RegisterDetailPage(transport, "<a href=\"/get/book.epub\">GET</a>");
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(transport.RequestedUrls.Any(u => u.Contains("/md5/")), Is.False);
        }

        [Test]
        public async Task grab_no_api_key_returns_unavailable_without_scraping()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterDetailPage(transport, "<a href=\"/get/book.epub\">GET</a>");
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, null, "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(result.Reason, Does.Contain("API key"));
            Assert.That(transport.RequestedUrls.Any(u => u.Contains("/md5/")), Is.False,
                "Must not scrape detail page when no API key");
        }

        [Test]
        public async Task grab_whitespace_api_key_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "   ", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(result.Reason, Does.Contain("API key"));
        }

        [Test]
        public async Task grab_api_transport_failure_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => throw new System.Net.WebException("Connection timed out", System.Net.WebExceptionStatus.Timeout));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_api_unavailable_reason_never_leaks_api_key()
        {
            var secretKey = "SUPER-SECRET-KEY-12345";
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.Unauthorized,
                    "{\"error\":\"unauthorized\"}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, secretKey, "CatalogPage");

            Assert.That(result.Reason, Does.Not.Contain(secretKey),
                "Unavailable reason must not leak the API key");
        }

        [Test]
        public async Task grab_api_request_uses_bounded_domain_rotation()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":null}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            var apiCalls = transport.RequestedUrls.Where(u => u.Contains("fast_download.json")).ToList();
            Assert.That(apiCalls.Count, Is.LessThanOrEqualTo(5),
                "Should stop rotating domain indices after bounded attempts");
            Assert.That(apiCalls.Count, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task grab_api_stops_rotation_on_first_success()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=0"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":null}", "application/json")));
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=1"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":\"https://cdn2.example/book.epub\"}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Success));
            Assert.That(result.ResolvedUrl, Is.EqualTo("https://cdn2.example/book.epub"));
            var apiCalls = transport.RequestedUrls.Where(u => u.Contains("fast_download.json")).ToList();
            Assert.That(apiCalls.Count, Is.EqualTo(2),
                "Should stop after first successful domain_index");
        }

        [Test]
        public async Task grab_api_includes_referer_and_user_agent()
        {
            HttpHeader capturedHeaders = null;
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request =>
                {
                    capturedHeaders = request.Headers;
                    return Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                        "{\"download_url\":\"https://cdn.example/book.epub\"}", "application/json"));
                });
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(capturedHeaders, Is.Not.Null);
            Assert.That(capturedHeaders["User-Agent"], Does.Contain("Chaptarr"));
            Assert.That(capturedHeaders["Referer"], Is.EqualTo("https://catalog.example/"));
        }

        [Test]
        public async Task grab_api_url_in_query_but_redacted_in_reason()
        {
            var secretKey = "MY-API-KEY-99999";
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => throw new System.Net.WebException("timeout", System.Net.WebExceptionStatus.Timeout));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, secretKey, "CatalogPage");

            Assert.That(transport.RequestedUrls.Any(u => u.Contains($"key={secretKey}")), Is.True,
                "Key must be in URL for API call");
            Assert.That(result.Reason, Does.Not.Contain(secretKey),
                "Key must not appear in result reason");
        }

        [Test]
        public async Task grab_malformed_json_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "not-json-at-all", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_null_download_url_in_api_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":null}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_empty_download_url_in_api_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":\"\"}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_unsafe_url_scheme_in_api_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":\"javascript:alert(1)\"}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable),
                "Non-HTTP scheme must be rejected");
        }

        [Test]
        public async Task grab_file_scheme_url_in_api_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":\"file:///etc/passwd\"}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_non_catalog_info_url_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(
                "https://catalog.example/some/other/path", "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(result.Reason, Does.Contain("identifier"));
        }

        [Test]
        public async Task grab_mirror_source_resolves_to_get_link()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://mirror.example/mirror1",
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "<a href=\"/get/book.epub\">GET</a>", "text/html; charset=utf-8")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(
                "https://mirror.example/mirror1", null, "MirrorIndex");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Success));
            Assert.That(result.ResolvedUrl, Is.EqualTo("https://mirror.example/get/book.epub"));
        }

        [Test]
        public async Task grab_unknown_source_returns_not_applicable()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(
                "https://downloads.example/book.epub", "test-key", "UnknownSource");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.NotApplicable));
            Assert.That(result.ResolvedUrl, Is.EqualTo("https://downloads.example/book.epub"));
        }

        [Test]
        public async Task grab_null_url_returns_not_applicable()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(null, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.NotApplicable));
        }

        [Test]
        public async Task grab_whitespace_url_returns_not_applicable()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync("   ", "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.NotApplicable));
        }

        [Test]
        public async Task grab_api_429_does_not_invoke_browser_resolver()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null}", "application/json")));
            var browserResolver = new ThrowingBrowserResolver();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient(), browserResolver);

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(browserResolver.WasInvoked, Is.False,
                "Browser resolver must not be invoked during API-only grab even on 429");
        }

        [Test]
        public async Task grab_no_api_key_does_not_invoke_browser_resolver()
        {
            var transport = new DirectDownloadTestHttp();
            var browserResolver = new ThrowingBrowserResolver();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient(), browserResolver);

            var result = await resolver.TryResolveGrabAsync(InfoUrl, null, "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(browserResolver.WasInvoked, Is.False,
                "Browser resolver must not be invoked during API-only grab even without API key");
        }

        [Test]
        public async Task grab_api_empty_response_body_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_api_html_challenge_response_returns_unavailable()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "<html><head><title>DDoS Protection</title></head><body>challenge</body></html>",
                    "text/html; charset=utf-8")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
        }

        [Test]
        public async Task grab_api_returns_first_valid_url_across_domains()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=0"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    "{\"download_url\":\"https://cdn1.example/book.epub\"}", "application/json")));
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.TryResolveGrabAsync(InfoUrl, "test-key", "CatalogPage");

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Success));
            Assert.That(result.ResolvedUrl, Is.EqualTo("https://cdn1.example/book.epub"));
        }

        private static void RegisterFastDownloadApi(DirectDownloadTestHttp transport, string downloadUrl)
        {
            transport.AddRoute(
                url => url.Contains("fast_download.json"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK,
                    $"{{\"download_url\":\"{downloadUrl}\"}}", "application/json")));
        }

        private static void RegisterDetailPage(DirectDownloadTestHttp transport, string html)
        {
            transport.AddRoute(
                url => url.Contains("/md5/"),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK, html, "text/html; charset=utf-8")));
        }

        private static HttpResponse BuildResponse(HttpRequest request, HttpStatusCode statusCode, string content, string contentType)
        {
            var headers = new HttpHeader();
            headers.ContentType = contentType;
            return new HttpResponse(request, headers, content, statusCode);
        }

        private sealed class ThrowingBrowserResolver : IBrowserDownloadResolver
        {
            public bool WasInvoked { get; private set; }

            public Task<bool> IsAvailableAsync()
            {
                WasInvoked = true;
                throw new InvalidOperationException("Browser resolver must not be invoked during API-only grab");
            }

            public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl)
            {
                WasInvoked = true;
                throw new InvalidOperationException("Browser resolver must not be invoked during API-only grab");
            }
        }
    }
}
