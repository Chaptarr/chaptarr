using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadSearchLatencyFixture
    {
        [Test]
        public async Task search_should_cap_catalog_candidates_at_ten()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterCatalogSourceWithManyResults(transport, "https://catalog.example", 25);
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());
            var provider = BuildIndexer(probeService, "https://catalog.example", "test-key");

            var releases = await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "J.K. Rowling" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Harry Potter",
                BookIsbn = "9780747532743"
            });

            Assert.That(releases.Count, Is.LessThanOrEqualTo(10), $"Expected at most 10 candidates but got {releases.Count}");
            Assert.That(releases.Count, Is.GreaterThan(0));
        }

        [Test]
        public async Task search_should_not_call_detail_page_or_fast_download_api_per_result()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterCatalogSourceWithManyResults(transport, "https://catalog.example", 5);
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());
            var provider = BuildIndexer(probeService, "https://catalog.example", "test-key");

            await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "J.K. Rowling" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Harry Potter",
                BookIsbn = "9780747532743"
            });

            var detailPageCalls = transport.RequestedUrls
                .Count(url => url.Contains("/md5/", StringComparison.Ordinal));
            var fastDownloadCalls = transport.RequestedUrls
                .Count(url => url.Contains("fast_download.json", StringComparison.Ordinal));

            Assert.That(detailPageCalls, Is.EqualTo(0), $"Search should not call detail pages but made {detailPageCalls} calls");
            Assert.That(fastDownloadCalls, Is.EqualTo(0), $"Search should not call fast_download API but made {fastDownloadCalls} calls");
        }

        [Test]
        public async Task search_should_use_request_isbn_instead_of_fetching_detail_page()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterCatalogSourceWithManyResults(transport, "https://catalog.example", 3);
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());
            var provider = BuildIndexer(probeService, "https://catalog.example", "test-key");

            var releases = await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "J.K. Rowling" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Harry Potter",
                BookIsbn = "9780747532743"
            });

            Assert.That(releases.Count, Is.GreaterThan(0));
            foreach (var release in releases)
            {
                Assert.That(release.Isbn, Is.EqualTo("9780747532743"), "Search should use the request ISBN directly");
            }
        }

        [Test]
        public async Task search_should_set_download_url_to_info_uri_for_grab_time_resolution()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterCatalogSourceWithManyResults(transport, "https://catalog.example", 3);
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());
            var provider = BuildIndexer(probeService, "https://catalog.example", "test-key");

            var releases = await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "J.K. Rowling" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Harry Potter",
                BookIsbn = "9780747532743"
            });

            Assert.That(releases.Count, Is.GreaterThan(0));
            foreach (var release in releases)
            {
                Assert.That(release.DownloadUrl, Does.Contain("/md5/"), "Download URL should be the info URI for grab-time resolution");
                Assert.That(release.DownloadUrl, Is.EqualTo(release.InfoUrl), "Download URL should equal info URL before grab-time resolution");
            }
        }

        [Test]
        public async Task search_should_cap_mirror_candidates_at_ten()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterMirrorSourceWithManyResults(transport, "https://mirror.example", 20);
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());
            var provider = BuildIndexer(probeService, "https://mirror.example", "test-key");

            var releases = await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "J.K. Rowling" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Harry Potter",
                BookIsbn = "9780747532743"
            });

            Assert.That(releases.Count, Is.LessThanOrEqualTo(10), $"Expected at most 10 mirror candidates but got {releases.Count}");
        }

        [Test]
        public async Task mirror_search_should_not_call_per_result_mirror_pages()
        {
            var transport = new DirectDownloadTestHttp();
            RegisterMirrorSourceWithManyResults(transport, "https://mirror.example", 5);
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());
            var provider = BuildIndexer(probeService, "https://mirror.example", "test-key");

            await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "J.K. Rowling" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Harry Potter",
                BookIsbn = "9780747532743"
            });

            var mirrorPageCalls = transport.RequestedUrls
                .Where(url => url.Contains("/mirror", StringComparison.Ordinal) && !url.Contains("index.php", StringComparison.Ordinal) && !url.Contains("/search", StringComparison.Ordinal))
                .ToList();

            Assert.That(mirrorPageCalls.Count, Is.EqualTo(0), $"Mirror search should not call per-result mirror pages but made {mirrorPageCalls.Count} calls: [{string.Join(", ", mirrorPageCalls)}] URLs requested: [{string.Join(", ", transport.RequestedUrls)}]");
        }

        [Test]
        public async Task grab_time_resolver_should_resolve_catalog_info_url_to_download_url()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "{\"download_url\":\"https://downloads.example/book.epub\"}",
                    "application/json")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "test-api-key",
                "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://downloads.example/book.epub"));
        }

        [Test]
        public async Task grab_time_resolver_should_throw_when_api_key_missing_for_catalog_source()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var ex = Assert.ThrowsAsync<NzbDrone.Core.Exceptions.ReleaseDownloadException>(async () =>
                await resolver.TryResolveAsync(
                    "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    null,
                    "CatalogPage"));

            Assert.That(ex.Message, Does.Contain("no public download link"));
        }

        [Test]
        public async Task grab_time_resolver_should_resolve_detail_page_download_link_when_no_api_key()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "<html><body><a class=\"js-md5-codes-tabs-tab\"><span>ISBN</span><span>9780747532743</span></a><div><a href=\"/get/harry-potter.epub\">GET</a></div></body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                null,
                "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://catalog.example/get/harry-potter.epub"));
        }

        [Test]
        public async Task grab_time_resolver_should_resolve_aa_slow_download_link()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "<html><body><a href=\"/slow_download/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0/0\" rel=\"noopener noreferrer nofollow\" class=\"js-download-link\">Slow Partner Server #1</a></body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                null,
                "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://catalog.example/slow_download/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/0/0"));
        }

        [Test]
        public async Task grab_time_resolver_should_throw_when_no_api_key_and_no_download_link()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "<html><body><a class=\"js-md5-codes-tabs-tab\"><span>ISBN</span><span>9780747532743</span></a></body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var ex = Assert.ThrowsAsync<NzbDrone.Core.Exceptions.ReleaseDownloadException>(async () =>
                await resolver.TryResolveAsync(
                    "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    null,
                    "CatalogPage"));

            Assert.That(ex.Message, Does.Contain("no public download link"));
            Assert.That(ex.Message, Does.Contain("API key"));
        }

        [Test]
        public async Task grab_time_resolver_should_throw_when_detail_page_has_no_download_links()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://catalog.example/md5/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "<!DOCTYPE html><html><head><title>Book Detail</title></head><body><p>No download links available</p></body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var ex = Assert.ThrowsAsync<NzbDrone.Core.Exceptions.ReleaseDownloadException>(async () =>
                await resolver.TryResolveAsync(
                    "https://catalog.example/md5/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    null,
                    "CatalogPage"));

            Assert.That(ex.Message, Does.Contain("no public download link"));
        }

        [Test]
        public async Task grab_time_resolver_should_prefer_api_key_over_detail_page_scraping()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.Contains("fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "{\"download_url\":\"https://downloads.example/premium-book.epub\"}",
                    "application/json")));
            transport.AddRoute(
                url => url == "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "<html><body><a href=\"/get/free-book.epub\">GET</a></body></html>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "test-api-key",
                "CatalogPage");

            Assert.That(resolved, Is.EqualTo("https://downloads.example/premium-book.epub"));
        }

        [Test]
        public async Task grab_time_resolver_should_resolve_mirror_url_to_get_link()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url == "https://mirror.example/mirror1",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    System.Net.HttpStatusCode.OK,
                    "<a href=\"/get/book.epub\">GET</a>",
                    "text/html; charset=utf-8")));

            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://mirror.example/mirror1",
                null,
                "MirrorIndex");

            Assert.That(resolved, Is.EqualTo("https://mirror.example/get/book.epub"));
        }

        [Test]
        public async Task grab_time_resolver_should_return_original_url_for_unknown_source()
        {
            var transport = new DirectDownloadTestHttp();
            var resolver = new DirectDownloadGrabUrlResolver(transport.CreateClient());

            var resolved = await resolver.TryResolveAsync(
                "https://downloads.example/book.epub",
                "test-key",
                "UnknownSource");

            Assert.That(resolved, Is.EqualTo("https://downloads.example/book.epub"));
        }

        private static DirectDownloadIndexer BuildIndexer(DirectDownloadSourceProbeService probeService, string url, string apiKey)
        {
            return new DirectDownloadIndexer(null, null, null, null, probeService)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Id = 41,
                    Name = "Direct Download Test",
                    Priority = 7,
                    Settings = new DirectDownloadSettings
                    {
                        Urls = url,
                        ApiKey = apiKey
                    }
                }
            };
        }

        private static void RegisterCatalogSourceWithManyResults(DirectDownloadTestHttp transport, string baseUrl, int count)
        {
            transport.AddRoute(
                url => url.StartsWith($"{baseUrl}/search", StringComparison.Ordinal),
                request =>
                {
                    var items = string.Join("", Enumerable.Range(1, count).Select(i =>
                        $"<div><a class=\"js-vim-focus\" href=\"/md5/{new string('a', 28)}{i:x4}\">Book {i}</a>" +
                        "<div class=\"font-semibold\">EPUB · 1.2 MB · 2020</div>" +
                        $"<div class=\"font-mono\">Book{i}.epub</div></div>"));

                    return Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                        request,
                        System.Net.HttpStatusCode.OK,
                        $"<div>{items}</div>",
                        "text/html; charset=utf-8"));
                });
        }

        private static void RegisterMirrorSourceWithManyResults(DirectDownloadTestHttp transport, string baseUrl, int count)
        {
            transport.AddRoute(
                url => url.StartsWith($"{baseUrl}/index.php", StringComparison.Ordinal),
                request =>
                {
                    var rows = string.Join("", Enumerable.Range(1, count).Select(i =>
                        $"<tr><td><a href=\"ignored\">x</a>" +
                        $"<a href=\"book/index.php?md5={i}\">Book {i}</a></td>" +
                        $"<td>Author</td><td>Publisher</td><td>2020</td>" +
                        $"<td>English</td><td>300</td><td>1 MB</td><td>EPUB</td>" +
                        $"<td><a href=\"/mirror{i}\">Mirror {i}</a></td></tr>"));

                    return Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                        request,
                        System.Net.HttpStatusCode.OK,
                        $"<table></table><table><tr><th>Title</th></tr>{rows}</table>",
                        "text/html; charset=utf-8"));
                });
        }
    }
}
