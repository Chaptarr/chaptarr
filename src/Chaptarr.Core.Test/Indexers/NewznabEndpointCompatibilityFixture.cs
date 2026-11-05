using System.Net;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabEndpointCompatibilityFixture
    {
        [Test]
        public void should_route_capabilities_and_preserve_settings_and_key()
        {
            var http = new Mock<IIndexerHttpClient>(MockBehavior.Strict);
            http.Setup(client => client.Get(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest request) =>
                {
                    Assert.That(request.Url.FullUri, Is.EqualTo("https://api.nzb.life/api?t=caps&apikey=fixture-key"));
                    return new HttpResponse(request, new HttpHeader(), "<caps><limits default='50' max='100'/></caps>", HttpStatusCode.OK);
                });
            var factory = new Mock<IIndexerHttpClientFactory>();
            factory.Setup(value => value.GetClient(null)).Returns(http.Object);
            var provider = new NewznabCapabilitiesProvider(new CacheManager(), factory.Object, LogManager.GetCurrentClassLogger());
            var settings = new NewznabSettings { BaseUrl = "https://api.nzb.su", ApiPath = "/api", ApiKey = "fixture-key" };
            Assert.That(provider.GetCapabilities(settings, null).MaxPageSize, Is.EqualTo(100));
            Assert.That(settings.BaseUrl, Is.EqualTo("https://api.nzb.su"));
            http.Verify(client => client.Get(It.IsAny<HttpRequest>()), Times.Once);
        }

        [Test]
        public void should_route_pending_download_links()
        {
            var indexer = new Newznab(null, null, null, null, null, null, LogManager.GetCurrentClassLogger());
            var request = indexer.GetDownloadRequest("https://api.nzb.su/api?t=get&id=a%2Fb&apikey=fixture-key");
            Assert.That(request.Url.FullUri, Is.EqualTo("https://api.nzb.life/api?t=get&id=a%2Fb&apikey=fixture-key"));
        }

        [TestCase("https://api.nzb.su")]
        [TestCase("http://nzb.su")]
        [TestCase("https://www.nzb.su")]
        [TestCase("HTTPS://API.NZB.SU:443")]
        [TestCase("http://api.nzb.su:80")]
        public void should_route_retired_endpoints_without_reencoding_request_data(string origin)
        {
            const string tail = "/api?t=get&id=a%2Fb&apikey=fixture%2Bkey+value#fragment";
            Assert.That(NewznabEndpointCompatibility.Normalize(origin + tail), Is.EqualTo("https://api.nzb.life" + tail));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("/api.nzb.su/api")]
        [TestCase("https://api.nzb.su.example/api")]
        [TestCase("https://other.nzb.su/api")]
        [TestCase("https://api.nzb.life/api")]
        [TestCase("https://api.nzb.su:8443/api")]
        [TestCase("https://user:password@api.nzb.su/api")]
        [TestCase("ftp://api.nzb.su/api")]
        [TestCase("https://api.nzb.su\\@other.example/api")]
        public void should_leave_other_or_ambiguous_endpoints_unchanged(string url)
        {
            Assert.That(NewznabEndpointCompatibility.Normalize(url), Is.EqualTo(url));
        }
    }
}
