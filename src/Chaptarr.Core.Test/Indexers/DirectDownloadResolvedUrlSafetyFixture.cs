using System;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadResolvedUrlSafetyFixture
    {
        [TestCase("file:///tmp/dune.epub")]
        [TestCase("data:text/plain,ebook")]
        [TestCase("javascript:alert(1)")]
        [TestCase("/download/dune.epub")]
        [TestCase("https://user:pass@downloads.example/dune.epub")]
        public async Task search_should_return_info_url_even_when_fast_download_would_return_unsafe_url(string unsafeResolvedUrl)
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(
                transport,
                "https://primary.example",
                "9780441172719",
                "Dune",
                "9780441172719",
                unsafeResolvedUrl);
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
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/md5/"), "Search should return info URL regardless of fast_download response");
        }

        [TestCase("file:///tmp/dune.epub")]
        [TestCase("data:text/plain,ebook")]
        [TestCase("javascript:alert(1)")]
        [TestCase("https://user:pass@downloads.example/dune.epub")]
        public async Task search_should_return_mirror_url_even_when_mirror_page_would_resolve_to_unsafe_url(string unsafeResolvedUrl)
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(
                transport,
                "https://mirror.example",
                "Dune",
                "9780441172719",
                unsafeResolvedUrl,
                false,
                $"<a href=\"{unsafeResolvedUrl}\">GET</a>");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://mirror.example" },
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 16 * 1024
            });

            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/mirror"), "Search should return mirror URL regardless of mirror page content");
        }

        [Test]
        public async Task should_preserve_safe_info_url_behavior_when_catalog_download_url_is_missing()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(
                transport,
                "https://primary.example",
                "9780441172719",
                "Dune",
                "9780441172719",
                null,
                "{}",
                true);
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
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases[0].DownloadUrl, Is.EqualTo("https://primary.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }
}
