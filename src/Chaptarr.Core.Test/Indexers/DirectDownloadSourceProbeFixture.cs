using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadSourceProbeFixture
    {
        [Test]
        public async Task should_return_primary_source_results_without_probning_fallback_source()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(transport, "https://203.0.113.30", "9780441172719", "Dune", "9780441172719", "https://203.0.113.19/files/dune.epub");
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(transport, "https://203.0.113.33", "9780441172719", "Dune Messiah", "9780441172696", "https://203.0.113.20/files/dune-messiah.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.30", "https://203.0.113.33" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedSourceUrl, Is.EqualTo("https://203.0.113.30/"));
            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.CatalogPage));
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().DownloadProtocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://203.0.113.33", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public async Task should_fail_over_to_secondary_source_after_primary_timeout()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal),
                request => throw new WebException("Http request timed out", WebExceptionStatus.Timeout));
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://203.0.113.33", "Dune", "9780441172719", "https://203.0.113.33/get/dune.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.30", "https://203.0.113.33" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedSourceUrl, Is.EqualTo("https://203.0.113.33/"));
            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.MirrorIndex));
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/mirror"), "Download URL should be the mirror URI for grab-time resolution");
            Assert.That(transport.RequestedUrls.First(), Does.StartWith("https://203.0.113.30/search"));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://203.0.113.33/index.php", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_fallback_from_isbn_to_title_when_identifier_search_returns_no_results()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://203.0.113.33", "Dune", "9780441172719", "https://203.0.113.33/get/dune.epub", emptyIsbnSearch: true);
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.33" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(transport.RequestedUrls.Count(url => url.Contains("req=9780441172719", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(transport.RequestedUrls.Count(url => url.Contains("req=Dune", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(result.Releases.Single().Isbn, Is.EqualTo("9780441172719"));
        }

        [Test]
        public async Task should_canonicalize_isbn_before_searching_and_in_results()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://203.0.113.33", "Dune", "9780441172719", "https://203.0.113.33/get/dune.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.33" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "978-0-441-17271-9 ",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(transport.RequestedUrls.Count(url => url.Contains("req=9780441172719", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("req=978-0-441-17271-9", StringComparison.Ordinal)), Is.False);
            Assert.That(result.Releases.Single().Isbn, Is.EqualTo("9780441172719"));
        }

        [Test]
        public async Task should_continue_to_next_family_on_same_source_when_cached_family_fails()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.11/search", StringComparison.Ordinal),
                request => throw new WebException("cached family timeout", WebExceptionStatus.Timeout));
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://203.0.113.11", "Dune", "9780441172719", "https://203.0.113.11/get/dune.epub");
            var runtimeCache = new DirectDownloadSourceRuntimeCache();
            runtimeCache.Set("https://203.0.113.11/", DirectDownloadSourceFamily.CatalogPage);
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger(), runtimeCache);

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.11" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.MirrorIndex));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://203.0.113.11/index.php", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_continue_to_next_family_on_same_source_when_cached_family_returns_no_results()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.10/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, HttpStatusCode.OK, "<html><body>empty</body></html>", "text/html; charset=utf-8")));
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://203.0.113.10", "Dune", "9780441172719", "https://203.0.113.10/get/dune.epub");
            var runtimeCache = new DirectDownloadSourceRuntimeCache();
            runtimeCache.Set("https://203.0.113.10/", DirectDownloadSourceFamily.CatalogPage);
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger(), runtimeCache);

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.10" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.MirrorIndex));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://203.0.113.10/index.php", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_preserve_catalog_release_with_info_url_as_download_url_for_grab_time_resolution()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "<div class=\"flex  pt-3 pb-3 border-b last:border-b-0 border-gray-100\"><a href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" class=\"custom-a block mr-2 sm:mr-4 hover:opacity-80\"><div>cover</div></a><div class=\"max-w-full overflow-hidden flex flex-col justify-around\"><div><div class=\"line-clamp-[2] overflow-hidden break-words text-[9px] text-gray-500 font-mono\">lgli/Frank Herbert - Dune (1965, Chilton).epub</div><a href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" class=\"line-clamp-[3] overflow-hidden break-words js-vim-focus custom-a text-[#2563eb] inline-block outline-offset-[-2px] outline-2 rounded-[3px] focus:outline font-semibold text-lg leading-[1.2] hover:opacity-80 mt-1\">Dune</a><a href=\"/search?q=Frank Herbert\" class=\"line-clamp-[2] overflow-hidden break-words block custom-a text-sm hover:opacity-70 leading-[1.2] mt-1\">Frank Herbert</a><a href=\"/search?q=Chilton\" class=\"line-clamp-[2] overflow-hidden break-words block custom-a text-sm hover:opacity-70 leading-[1.2] mt-1\">Chilton, 1965</a></div><div><div class=\"line-clamp-[5]\"></div></div><div class=\"text-gray-800 dark:text-slate-400 font-semibold text-sm leading-[1.2] mt-2\">English [en] · EPUB · 1.2MB · 1965 · 📕 Book (fiction)</div></div></div>",
                    "text/html; charset=utf-8")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.30" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedSourceUrl, Is.EqualTo("https://203.0.113.30/"));
            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.CatalogPage));
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().Isbn, Is.EqualTo("9780441172719"));
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/md5/"), "Download URL should be the info URI for grab-time resolution");
            Assert.That(result.Releases.Single().DownloadUrl, Is.EqualTo(result.Releases.Single().InfoUrl), "Download URL should equal info URL before grab-time resolution");
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://203.0.113.30/md5/", StringComparison.Ordinal)), Is.False, "Search should not call detail pages");
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("fast_download.json", StringComparison.Ordinal)), Is.False, "Search should not call fast_download API");
        }

        [Test]
        public async Task should_not_append_content_book_parameter_to_catalog_search_url()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(transport, "https://203.0.113.30", "9780441172719", "Dune", "9780441172719", "https://203.0.113.19/files/dune.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.30" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.Releases, Has.Count.EqualTo(1));
            var catalogRequest = transport.RequestedUrls.First(url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal));
            Assert.That(catalogRequest, Does.Not.Contain("content=book"));
            Assert.That(catalogRequest, Does.Contain("q=9780441172719"));
        }

        [TestCase("KEPUB", "Dune.kepub", "kepub")]
        [TestCase("MOBI", "Dune.mobi", "mobi")]
        [TestCase("AZW3", "Dune.azw3", "azw3")]
        public async Task should_search_catalog_for_bookshelf_ebook_formats(string format, string filename, string expectedExtension)
        {
            const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var transport = new DirectDownloadTestHttp();
            var html = $"<div><a class=\"js-vim-focus\" href=\"/md5/{md5}\">Dune</a><div class=\"text-gray-800 font-semibold\">English · {format} · 1.2 MB · 1965</div><div class=\"font-mono\">{filename}</div></div>";
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, html, "text/html; charset=utf-8")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.30" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.CatalogPage));
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().Title, Does.EndWith($"[{expectedExtension}]"));
            Assert.That(result.Releases.Single().Categories, Is.EqualTo(new[] { 7020 }));
            var catalogRequest = transport.RequestedUrls.Single(url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal));
            Assert.That(Uri.UnescapeDataString(catalogRequest), Does.Contain("ext=epub,kepub,mobi,azw3,pdf"));
        }

        [Test]
        public async Task should_unwrap_comment_hidden_partial_matches_for_catalog_adapter()
        {
            var transport = new DirectDownloadTestHttp();
            // Simulate Anna's Archive HTML where partial matches are wrapped in HTML comments
            var commentWrappedHtml = "<html><body><!-- <div><a class=\"js-vim-focus\" href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\">Dune</a><div class=\"font-semibold\">EPUB · 1.2 MB · 1965</div><div class=\"font-mono\">Dune.epub</div></div> --></body></html>";
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, commentWrappedHtml, "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url == "https://203.0.113.30/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, "<a class=\"js-md5-codes-tabs-tab\"><span>ISBN</span><span>9780441172719</span></a>", "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.30/dyn/api/fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, "{\"download_url\":\"https://203.0.113.19/files/dune.epub\"}", "application/json")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.30" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().Title, Does.Contain("Dune"));
        }

        [TestCase("ftp://203.0.113.17?key=real-secret")]
        [TestCase("file:///tmp/dune.epub?key=real-secret")]
        [TestCase("data:text/plain,ebook?key=real-secret")]
        [TestCase("javascript:alert(1)")]
        [TestCase("https://user:pass@203.0.113.17/path?key=real-secret")]
        [TestCase("http://127.0.0.1?key=real-secret")]
        [TestCase("http://169.254.10.20?key=real-secret")]
        [TestCase("http://192.168.1.10?key=real-secret")]
        public void should_reject_unsafe_source_urls_and_redact_keys(string sourceUrl)
        {
            var subject = new DirectDownloadSourceProbeService(new DirectDownloadTestHttp().CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { sourceUrl },
                ApiKey = "real-secret",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 1024
            }));

            Assert.That(exception.Message, Does.Not.Contain("real-secret"));
        }

        [Test]
        public void should_fail_when_redirect_target_is_blocked()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.32/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, HttpStatusCode.Found, string.Empty, "text/html; charset=utf-8", "http://169.254.169.254/latest/meta-data/")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.32" },
                Title = "Dune",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 1024
            }));

            Assert.That(exception.Message, Does.Contain("not allowed"));
        }

        [TestCase("http://127.0.0.1/download")]
        [TestCase("http://localhost/download")]
        [TestCase("http://169.254.10.20/download")]
        [TestCase("http://192.168.1.10/download")]
        [TestCase("http://100.64.0.10/download")]
        public void should_fail_when_redirect_target_enters_blocked_network_ranges(string location)
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.32/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, HttpStatusCode.Found, string.Empty, "text/html; charset=utf-8", location)));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.32" },
                Title = "Dune",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 1024
            }));

            Assert.That(exception.Message, Does.Contain("not allowed"));
        }

        [Test]
        public void should_fail_when_response_body_exceeds_limit()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://203.0.113.32/search", StringComparison.Ordinal) || url.StartsWith("https://203.0.113.32/index.php", StringComparison.Ordinal),
                async request =>
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 4096));
                    await request.ResponseStream.WriteAsync(bytes, 0, bytes.Length);
                    return new HttpResponse(request, new HttpHeader { ContentType = "text/html; charset=utf-8" }, Array.Empty<byte>(), HttpStatusCode.OK);
                });
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.32" },
                Title = "Dune",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 128
            }));

            Assert.That(exception.Message, Does.Contain("maximum response size"));
        }

    }
}
