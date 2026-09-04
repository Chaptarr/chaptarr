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
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(transport, "https://primary.example", "9780441172719", "Dune", "9780441172719", "https://downloads.primary.example/files/dune.epub");
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(transport, "https://secondary.example", "9780441172719", "Dune Messiah", "9780441172696", "https://downloads.secondary.example/files/dune-messiah.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://primary.example", "https://secondary.example" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedSourceUrl, Is.EqualTo("https://primary.example/"));
            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.CatalogPage));
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().DownloadProtocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://secondary.example", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public async Task should_fail_over_to_secondary_source_after_primary_timeout()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://primary.example/search", StringComparison.Ordinal),
                request => throw new WebException("Http request timed out", WebExceptionStatus.Timeout));
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://secondary.example", "Dune", "9780441172719", "https://secondary.example/get/dune.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://primary.example", "https://secondary.example" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedSourceUrl, Is.EqualTo("https://secondary.example/"));
            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.MirrorIndex));
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/mirror"), "Download URL should be the mirror URI for grab-time resolution");
            Assert.That(transport.RequestedUrls.First(), Does.StartWith("https://primary.example/search"));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://secondary.example/index.php", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_fallback_from_isbn_to_title_when_identifier_search_returns_no_results()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://secondary.example", "Dune", "9780441172719", "https://secondary.example/get/dune.epub", emptyIsbnSearch: true);
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://secondary.example" },
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
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://secondary.example", "Dune", "9780441172719", "https://secondary.example/get/dune.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://secondary.example" },
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
                url => url.StartsWith("https://cached.example/search", StringComparison.Ordinal),
                request => throw new WebException("cached family timeout", WebExceptionStatus.Timeout));
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://cached.example", "Dune", "9780441172719", "https://cached.example/get/dune.epub");
            var runtimeCache = new DirectDownloadSourceRuntimeCache();
            runtimeCache.Set("https://cached.example/", DirectDownloadSourceFamily.CatalogPage);
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger(), runtimeCache);

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://cached.example" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.MirrorIndex));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://cached.example/index.php", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_continue_to_next_family_on_same_source_when_cached_family_returns_no_results()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://cached-empty.example/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, HttpStatusCode.OK, "<html><body>empty</body></html>", "text/html; charset=utf-8")));
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(transport, "https://cached-empty.example", "Dune", "9780441172719", "https://cached-empty.example/get/dune.epub");
            var runtimeCache = new DirectDownloadSourceRuntimeCache();
            runtimeCache.Set("https://cached-empty.example/", DirectDownloadSourceFamily.CatalogPage);
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger(), runtimeCache);

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://cached-empty.example" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.MirrorIndex));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://cached-empty.example/index.php", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_preserve_catalog_release_with_info_url_as_download_url_for_grab_time_resolution()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://primary.example/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    "<div class=\"flex  pt-3 pb-3 border-b last:border-b-0 border-gray-100\"><a href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" class=\"custom-a block mr-2 sm:mr-4 hover:opacity-80\"><div>cover</div></a><div class=\"max-w-full overflow-hidden flex flex-col justify-around\"><div><div class=\"line-clamp-[2] overflow-hidden break-words text-[9px] text-gray-500 font-mono\">lgli/Frank Herbert - Dune (1965, Chilton).epub</div><a href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" class=\"line-clamp-[3] overflow-hidden break-words js-vim-focus custom-a text-[#2563eb] inline-block outline-offset-[-2px] outline-2 rounded-[3px] focus:outline font-semibold text-lg leading-[1.2] hover:opacity-80 mt-1\">Dune</a><a href=\"/search?q=Frank Herbert\" class=\"line-clamp-[2] overflow-hidden break-words block custom-a text-sm hover:opacity-70 leading-[1.2] mt-1\">Frank Herbert</a><a href=\"/search?q=Chilton\" class=\"line-clamp-[2] overflow-hidden break-words block custom-a text-sm hover:opacity-70 leading-[1.2] mt-1\">Chilton, 1965</a></div><div><div class=\"line-clamp-[5]\"></div></div><div class=\"text-gray-800 dark:text-slate-400 font-semibold text-sm leading-[1.2] mt-2\">English [en] · EPUB · 1.2MB · 1965 · 📕 Book (fiction)</div></div></div>",
                    "text/html; charset=utf-8")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://primary.example" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.SelectedSourceUrl, Is.EqualTo("https://primary.example/"));
            Assert.That(result.SelectedFamily, Is.EqualTo(DirectDownloadSourceFamily.CatalogPage));
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().Isbn, Is.EqualTo("9780441172719"));
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/md5/"), "Download URL should be the info URI for grab-time resolution");
            Assert.That(result.Releases.Single().DownloadUrl, Is.EqualTo(result.Releases.Single().InfoUrl), "Download URL should equal info URL before grab-time resolution");
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://primary.example/md5/", StringComparison.Ordinal)), Is.False, "Search should not call detail pages");
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("fast_download.json", StringComparison.Ordinal)), Is.False, "Search should not call fast_download API");
        }

        [Test]
        public async Task should_not_append_content_book_parameter_to_catalog_search_url()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(transport, "https://primary.example", "9780441172719", "Dune", "9780441172719", "https://downloads.primary.example/files/dune.epub");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://primary.example" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.Releases, Has.Count.EqualTo(1));
            var catalogRequest = transport.RequestedUrls.First(url => url.StartsWith("https://primary.example/search", StringComparison.Ordinal));
            Assert.That(catalogRequest, Does.Not.Contain("content=book"));
            Assert.That(catalogRequest, Does.Contain("q=9780441172719"));
        }

        [Test]
        public async Task should_unwrap_comment_hidden_partial_matches_for_catalog_adapter()
        {
            var transport = new DirectDownloadTestHttp();
            // Simulate Anna's Archive HTML where partial matches are wrapped in HTML comments
            var commentWrappedHtml = "<html><body><!-- <div><a class=\"js-vim-focus\" href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\">Dune</a><div class=\"font-semibold\">EPUB · 1.2 MB · 1965</div><div class=\"font-mono\">Dune.epub</div></div> --></body></html>";
            transport.AddRoute(
                url => url.StartsWith("https://primary.example/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, commentWrappedHtml, "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url == "https://primary.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, "<a class=\"js-md5-codes-tabs-tab\"><span>ISBN</span><span>9780441172719</span></a>", "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url.StartsWith("https://primary.example/dyn/api/fast_download.json", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(
                    request, HttpStatusCode.OK, "{\"download_url\":\"https://downloads.primary.example/files/dune.epub\"}", "application/json")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://primary.example" },
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

        [TestCase("ftp://downloads.example?key=real-secret")]
        [TestCase("file:///tmp/dune.epub?key=real-secret")]
        [TestCase("data:text/plain,ebook?key=real-secret")]
        [TestCase("javascript:alert(1)")]
        [TestCase("https://user:pass@downloads.example/path?key=real-secret")]
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
                url => url.StartsWith("https://public.example/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, HttpStatusCode.Found, string.Empty, "text/html; charset=utf-8", "http://169.254.169.254/latest/meta-data/")));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://public.example" },
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
                url => url.StartsWith("https://public.example/search", StringComparison.Ordinal),
                request => Task.FromResult(DirectDownloadSourceProbeFixtureSupport.BuildResponse(request, HttpStatusCode.Found, string.Empty, "text/html; charset=utf-8", location)));
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://public.example" },
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
                url => url.StartsWith("https://public.example/search", StringComparison.Ordinal) || url.StartsWith("https://public.example/index.php", StringComparison.Ordinal),
                async request =>
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 4096));
                    await request.ResponseStream.WriteAsync(bytes, 0, bytes.Length);
                    return new HttpResponse(request, new HttpHeader { ContentType = "text/html; charset=utf-8" }, Array.Empty<byte>(), HttpStatusCode.OK);
                });
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://public.example" },
                Title = "Dune",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 128
            }));

            Assert.That(exception.Message, Does.Contain("maximum response size"));
        }

    }
}
