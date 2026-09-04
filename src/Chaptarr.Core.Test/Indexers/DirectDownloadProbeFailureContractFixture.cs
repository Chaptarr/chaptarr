using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadProbeFailureContractFixture
    {
        [Test]
        public void should_try_all_configured_sources_in_order_and_redact_secrets_when_every_probe_fails()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(
                url => url.StartsWith("https://primary.example/search", StringComparison.Ordinal),
                request => throw new WebException("primary timeout for key real-secret", WebExceptionStatus.Timeout));
            transport.AddRoute(
                url => url.StartsWith("https://secondary.example/index.php", StringComparison.Ordinal),
                request => throw new WebException("secondary timeout for key real-secret", WebExceptionStatus.Timeout));

            var subject = new DirectDownloadSourceProbeService(transport.CreateClient(), LogManager.GetCurrentClassLogger());

            var exception = Assert.ThrowsAsync<DirectDownloadProbeException>(async () => await subject.ProbeAsync(new DirectDownloadProbeRequest
            {
                SourceUrls = new[] { "https://primary.example", "https://secondary.example" },
                ApiKey = "real-secret",
                Author = "Frank Herbert",
                Title = "Dune",
                Isbn = "9780441172719",
                RequestTimeout = TimeSpan.FromMilliseconds(50),
                MaxResponseBytes = 4096
            }));

            Assert.That(transport.RequestedUrls.First(), Does.StartWith("https://primary.example/search"));
            Assert.That(transport.RequestedUrls.Any(url => url.StartsWith("https://secondary.example/index.php", StringComparison.Ordinal)), Is.True);
            Assert.That(exception.Message, Does.Not.Contain("real-secret"));
        }
    }
}
