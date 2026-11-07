using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadResolvedUrlSafetyFixture
    {
        [Test]
        public void should_reject_dns_results_that_resolve_to_private_addresses()
        {
            var exception = Assert.Throws<DirectDownloadProbeException>(() => DirectDownloadUrlSafety.EnsureSafeHost(
                new Uri("https://public.example"),
                "https://public.example",
                TimeSpan.FromMilliseconds(100),
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.20.30.40") })));

            Assert.That(exception.Message, Does.Contain("not allowed"));
        }

        [Test]
        public void should_fail_closed_when_dns_resolution_exceeds_the_validation_budget()
        {
            var exception = Assert.Throws<DirectDownloadProbeException>(() => DirectDownloadUrlSafety.EnsureSafeHost(
                new Uri("https://slow-dns.example"),
                "https://slow-dns.example",
                TimeSpan.FromMilliseconds(50),
                async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Array.Empty<IPAddress>();
                }));

            Assert.That(exception.Message, Does.Contain("DNS resolution timed out"));
        }

        [Test]
        public void should_fail_closed_when_dns_resolution_fails()
        {
            var exception = Assert.Throws<DirectDownloadProbeException>(() => DirectDownloadUrlSafety.EnsureSafeHost(
                new Uri("https://unresolvable.example"),
                "https://unresolvable.example",
                TimeSpan.FromMilliseconds(100),
                (_, _) => Task.FromException<IPAddress[]>(new InvalidOperationException("No DNS response"))));

            Assert.That(exception.Message, Does.Contain("could not be safely resolved"));
        }

        [TestCase("file:///tmp/dune.epub")]
        [TestCase("data:text/plain,ebook")]
        [TestCase("javascript:alert(1)")]
        [TestCase("/download/dune.epub")]
        [TestCase("https://user:pass@203.0.113.17/dune.epub")]
        public async Task search_should_return_info_url_even_when_fast_download_would_return_unsafe_url(string unsafeResolvedUrl)
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(
                transport,
                "https://203.0.113.30",
                "9780441172719",
                "Dune",
                "9780441172719",
                unsafeResolvedUrl);
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
            Assert.That(result.Releases.Single().DownloadUrl, Does.Contain("/md5/"), "Search should return info URL regardless of fast_download response");
        }

        [TestCase("file:///tmp/dune.epub")]
        [TestCase("data:text/plain,ebook")]
        [TestCase("javascript:alert(1)")]
        [TestCase("https://user:pass@203.0.113.17/dune.epub")]
        public async Task search_should_return_mirror_url_even_when_mirror_page_would_resolve_to_unsafe_url(string unsafeResolvedUrl)
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterMirrorSource(
                transport,
                "https://203.0.113.25",
                "Dune",
                "9780441172719",
                unsafeResolvedUrl,
                false,
                $"<a href=\"{unsafeResolvedUrl}\">GET</a>");
            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var result = await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://203.0.113.25" },
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
                "https://203.0.113.30",
                "9780441172719",
                "Dune",
                "9780441172719",
                null,
                "{}",
                true);
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
            Assert.That(result.Releases, Has.Count.EqualTo(1));
            Assert.That(result.Releases[0].DownloadUrl, Is.EqualTo("https://203.0.113.30/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }
}
