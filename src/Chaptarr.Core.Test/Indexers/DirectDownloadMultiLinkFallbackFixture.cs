using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadMultiLinkFallbackFixture
    {
        // --- Detail page multi-link enumeration ---

        [Test]
        public async Task detail_page_enumerates_all_slow_download_links_not_just_first()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            // Detail page with multiple slow_download links (8 partner servers, as AA serves)
            var detailHtml = $@"<html><body>
                <a href=""/slow_download/{md5}/0/0"" class=""js-download-link"">Slow Partner Server #1</a>
                <a href=""/slow_download/{md5}/0/1"" class=""js-download-link"">Slow Partner Server #2</a>
                <a href=""/slow_download/{md5}/0/2"" class=""js-download-link"">Slow Partner Server #3</a>
                <a href=""/slow_download/{md5}/0/3"" class=""js-download-link"">Slow Partner Server #4</a>
                <a href=""/slow_download/{md5}/0/4"" class=""js-download-link"">Slow Partner Server #5</a>
                <a href=""/slow_download/{md5}/0/5"" class=""js-download-link"">Slow Partner Server #6</a>
                <a href=""/slow_download/{md5}/0/6"" class=""js-download-link"">Slow Partner Server #7</a>
                <a href=""/slow_download/{md5}/0/7"" class=""js-download-link"">Slow Partner Server #8</a>
            </body></html>";

            transport.AddRoute(
                url => url == $"{baseUrl}/md5/{md5}",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, detailHtml, "text/html; charset=utf-8")));

            // All slow_download links return DDoS challenge (403 HTML)
            for (var i = 0; i < 8; i++)
            {
                var domainIndex = i;
                transport.AddRoute(
                    url => url.Contains($"/slow_download/{md5}/0/{domainIndex}"),
                    request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                        request,
                        HttpStatusCode.Forbidden,
                        "<html><head><title>DDoS-Guard</title></head><body>Checking your browser</body></html>",
                        "text/html; charset=utf-8")));
            }

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                $"{baseUrl}/md5/{md5}", null, "CatalogPage");

            // Should have tried ALL 8 slow_download links, not just the first
            var slowDownloadRequests = transport.RequestedUrls
                .Where(u => u.Contains("/slow_download/"))
                .ToList();
            Assert.That(slowDownloadRequests.Count, Is.EqualTo(8),
                "Should enumerate and try ALL 8 slow_download links from detail page, not just the first");
        }

        [Test]
        public async Task detail_page_tries_fast_download_links_before_slow_download_links()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            // Detail page with both fast and slow download links
            var detailHtml = $@"<html><body>
                <a href=""/fast_download/{md5}/0/0"" class=""js-download-link"">Fast Partner Server #1</a>
                <a href=""/fast_download/{md5}/0/1"" class=""js-download-link"">Fast Partner Server #2</a>
                <a href=""/slow_download/{md5}/0/0"" class=""js-download-link"">Slow Partner Server #1</a>
                <a href=""/slow_download/{md5}/0/1"" class=""js-download-link"">Slow Partner Server #2</a>
            </body></html>";

            transport.AddRoute(
                url => url == $"{baseUrl}/md5/{md5}",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, detailHtml, "text/html; charset=utf-8")));

            // Fast links return DDoS challenge
            transport.AddRoute(
                url => url.Contains("/fast_download/"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.Forbidden,
                    "<html><head><title>DDoS-Guard</title></head><body>challenge</body></html>",
                    "text/html; charset=utf-8")));

            // Slow links also return DDoS challenge
            transport.AddRoute(
                url => url.Contains("/slow_download/"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.Forbidden,
                    "<html><head><title>DDoS-Guard</title></head><body>challenge</body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            await resolver.TryResolveAsync(
                $"{baseUrl}/md5/{md5}", null, "CatalogPage");

            // Verify ordering: fast links requested before slow links
            var fastIndex = transport.RequestedUrls.FindIndex(u => u.Contains("/fast_download/"));
            var slowIndex = transport.RequestedUrls.FindIndex(u => u.Contains("/slow_download/"));

            Assert.That(fastIndex, Is.GreaterThanOrEqualTo(0), "Should have tried fast_download links");
            Assert.That(slowIndex, Is.GreaterThanOrEqualTo(0), "Should have tried slow_download links");
            Assert.That(fastIndex, Is.LessThan(slowIndex),
                "Should try fast_download links before slow_download links");
        }

        [Test]
        public async Task detail_page_returns_first_non_challenge_link()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            var detailHtml = $@"<html><body>
                <a href=""/slow_download/{md5}/0/0"" class=""js-download-link"">Slow #1</a>
                <a href=""/slow_download/{md5}/0/1"" class=""js-download-link"">Slow #2</a>
                <a href=""/slow_download/{md5}/0/2"" class=""js-download-link"">Slow #3</a>
            </body></html>";

            transport.AddRoute(
                url => url == $"{baseUrl}/md5/{md5}",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, detailHtml, "text/html; charset=utf-8")));

            // First two return DDoS challenge, third returns actual download
            transport.AddRoute(
                url => url.Contains($"/slow_download/{md5}/0/0"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.Forbidden,
                    "<html>DDoS-Guard</html>", "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url.Contains($"/slow_download/{md5}/0/1"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.Forbidden,
                    "<html>DDoS-Guard</html>", "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url.Contains($"/slow_download/{md5}/0/2"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK,
                    "binary-content", "application/epub+zip")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                $"{baseUrl}/md5/{md5}", null, "CatalogPage");

            Assert.That(resolved, Is.EqualTo($"{baseUrl}/slow_download/{md5}/0/2"),
                "Should return the first link that does not return a challenge page");
        }

        // --- Fast download API domain_index rotation ---

        [Test]
        public async Task fast_download_api_tries_multiple_domain_indices_when_first_returns_null()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=0"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK,
                    "{\"download_url\":null}", "application/json")));
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=1"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK,
                    "{\"download_url\":null}", "application/json")));
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=2"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK,
                    "{\"download_url\":\"https://cdn3.example/book.epub\"}", "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                $"{baseUrl}/md5/{md5}", "test-key", "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://cdn3.example/book.epub"),
                "Should try multiple domain indices and use the first that returns a valid URL");

            var apiCalls = transport.RequestedUrls.Where(u => u.Contains("fast_download.json")).ToList();
            Assert.That(apiCalls.Count, Is.EqualTo(3),
                "Should have tried domain_index 0, 1, and 2");
            Assert.That(apiCalls[0], Does.Contain("domain_index=0"));
            Assert.That(apiCalls[1], Does.Contain("domain_index=1"));
            Assert.That(apiCalls[2], Does.Contain("domain_index=2"));
        }

        [Test]
        public void fast_download_api_stops_rotating_on_429_quota_exhausted()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            // All domain indices return 429 (quota exhausted per-account, not per-domain)
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index="),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}", "application/json")));

            // Detail page has no download links
            transport.AddRoute(
                url => url == $"{baseUrl}/md5/{md5}",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, "<html><body>no links</body></html>", "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            Assert.ThrowsAsync<ReleaseDownloadException>(async () =>
            {
                await resolver.TryResolveAsync(
                    $"{baseUrl}/md5/{md5}", "test-key", "CatalogPage");
            });

            var apiCalls = transport.RequestedUrls.Where(u => u.Contains("fast_download.json")).ToList();
            Assert.That(apiCalls.Count, Is.LessThanOrEqualTo(MaxFastDownloadDomainRotations),
                "Should stop rotating domain indices after bounded attempts when all return 429");
        }

        private const int MaxFastDownloadDomainRotations = 5;

        [Test]
        public async Task fast_download_api_returns_first_valid_url_across_domains()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=0"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK,
                    "{\"download_url\":\"https://cdn1.example/book.epub\"}", "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                $"{baseUrl}/md5/{md5}", "test-key", "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://cdn1.example/book.epub"));
            var apiCalls = transport.RequestedUrls.Where(u => u.Contains("fast_download.json")).ToList();
            Assert.That(apiCalls.Count, Is.EqualTo(1),
                "Should stop after first successful domain_index");
        }

        // --- Quota classification ---

        [Test]
        public async Task validate_api_key_quota_exhausted_message_recommends_waiting()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "exhausted-key");

            Assert.That(result.Outcome, Is.EqualTo(ApiKeyValidationOutcome.NoDownloadsRemaining));
            // Message should recommend waiting or using another source
            Assert.That(result.Message, Does.Contain("wait").IgnoreCase,
                "Quota exhausted message should recommend waiting");
        }

        [Test]
        public async Task validate_api_key_quota_exhausted_message_does_not_leak_exact_quota_numbers()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var result = await resolver.ValidateApiKeyAsync("https://catalog.example", "secret-key-12345");

            // Should not leak the API key
            Assert.That(result.Message, Does.Not.Contain("secret-key-12345"));
            // Should not contain raw JSON
            Assert.That(result.Message, Does.Not.Contain("{\""));
        }

        // --- Combined: fallback when API exhausted then detail page also challenged ---

        [Test]
        public async Task full_fallback_api_exhausted_then_all_detail_links_challenged()
        {
            var transport = new DirectDownloadTestHttp();
            var md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var baseUrl = "https://catalog.example";

            // API returns 429 (quota exhausted)
            transport.AddRoute(
                url => url.Contains("fast_download.json") && url.Contains("domain_index=0"),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.TooManyRequests,
                    "{\"download_url\":null,\"error\":\"No downloads left\"}", "application/json")));

            // Detail page has slow download links
            var detailHtml = $@"<html><body>
                <a href=""/slow_download/{md5}/0/0"" class=""js-download-link"">Slow #1</a>
                <a href=""/slow_download/{md5}/0/1"" class=""js-download-link"">Slow #2</a>
                <a href=""/slow_download/{md5}/0/2"" class=""js-download-link"">Slow #3</a>
            </body></html>";
            transport.AddRoute(
                url => url == $"{baseUrl}/md5/{md5}",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, detailHtml, "text/html; charset=utf-8")));

            // All slow_download links return DDoS challenge
            for (var i = 0; i < 3; i++)
            {
                var idx = i;
                transport.AddRoute(
                    url => url.Contains($"/slow_download/{md5}/0/{idx}"),
                    request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                        request, HttpStatusCode.Forbidden,
                        "<html><head><title>DDoS-Guard</title></head><body>Checking your browser</body></html>",
                        "text/html; charset=utf-8")));
            }

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                $"{baseUrl}/md5/{md5}", "test-key", "CatalogPage");

            // When all links are DDoS-challenged, should return the last attempted URL
            // (caller will classify it as DDoS challenge)
            Assert.That(resolved, Is.Not.Null.And.Not.Empty,
                "Should return a URL even when all are challenged (transfer layer handles classification)");

            // Should have tried the API first, then all detail page links
            Assert.That(transport.RequestedUrls.Any(u => u.Contains("fast_download.json")), Is.True,
                "Should have tried fast_download API first");
            Assert.That(transport.RequestedUrls.Count(u => u.Contains("/slow_download/")), Is.EqualTo(3),
                "Should have tried all 3 slow_download links from detail page");
        }
    }
}
