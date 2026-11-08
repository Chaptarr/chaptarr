using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture(false)]
    [TestFixture(true)]
    public class IndexerNfoTestIntegrationFixture
    {
        private readonly bool _torrent;
        public IndexerNfoTestIntegrationFixture(bool torrent) { _torrent = torrent; }

        [Test]
        public void schema_capabilities_can_be_read_before_a_definition_is_assigned()
        {
            var factory = Proxy<IIndexerHttpClientFactory>((_, _) => throw new InvalidOperationException("Opening schema must not request a client"));
            INarratorMetadataProvider provider = _torrent
                ? new Torznab(null, factory, null, null, null, new CacheManager(), LogManager.GetCurrentClassLogger())
                : new Newznab(null, factory, null, null, null, new CacheManager(), LogManager.GetCurrentClassLogger());
            Assert.That(provider.SupportsNarratorMetadata, Is.Null);
        }

        [TestCase("supported", true, 1)]
        [TestCase("no narrator", true, 1)]
        [TestCase("unsupported", false, 2)]
        [TestCase("timeout", null, 1)]
        [TestCase("missing", null, 1)]
        [TestCase("no nfo", null, 0)]
        public void regular_indexer_test_succeeds_independently_of_optional_nfo_support(string response, bool? expected, int nfoCalls)
        {
            var feedCalls = 0;
            var requests = new List<HttpRequest>();
            var proxyIds = new List<int?>();
            var mime = _torrent ? "application/x-bittorrent" : "application/x-nzb";
            var nfo = response == "no nfo" ? "0" : "1";
            var ns = _torrent ? "http://torznab.com/schemas/2015/feed" : "http://www.newznab.com/DTD/2010/feeds/attributes/";
            var feed = $"""
                <rss version="2.0" xmlns:newznab="{ns}">
                <channel><title>Fixture</title><item><title>Writer - A Book</title>
                <guid isPermaLink="true">https://indexer.example/details/feed-selected-id</guid>
                <link>https://indexer.example/details/feed-selected-id</link>
                <pubDate>Wed, 21 Oct 2026 12:00:00 +0000</pubDate>
                <enclosure url="https://indexer.example/api?t=get&amp;id=feed-selected-id" length="500000000" type="{mime}" />
                <newznab:attr name="nfo" value="{nfo}" /><newznab:attr name="category" value="3030" />
                <newznab:attr name="seeders" value="5" /><newznab:attr name="peers" value="10" />
                </item></channel></rss>
                """;
            var http = Proxy<IIndexerHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                if (method.Name == nameof(IIndexerHttpClient.ExecuteAsync))
                {
                    feedCalls++;
                    return Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/rss+xml" }, feed, HttpStatusCode.OK));
                }

                requests.Add(request);
                if (response == "timeout") throw new TimeoutException("NFO unavailable");
                var body = response switch
                {
                    "unsupported" => "<error code=\"203\" description=\"Function unavailable\"/>",
                    "missing" => "<error code=\"300\" description=\"No such item\"/>",
                    "no narrator" => "Release information without narration credits",
                    _ => "Narrator: A Reader"
                };
                return new HttpResponse(request, new HttpHeader { ContentType = body.StartsWith('<') ? "application/xml" : "text/plain" }, body,
                    response == "unsupported" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            });
            var factory = Proxy<IIndexerHttpClientFactory>((_, args) => { proxyIds.Add((int?)args[0]); return http; });
            var capabilities = Proxy<INewznabCapabilitiesProvider>((_, _) => new NewznabCapabilities());
            IIndexer indexer = _torrent
                ? new Torznab(capabilities, factory, null, null, null, new CacheManager(), LogManager.GetCurrentClassLogger())
                : new Newznab(capabilities, factory, null, null, null, new CacheManager(), LogManager.GetCurrentClassLogger());
            NewznabSettings settings = _torrent ? new TorznabSettings() : new NewznabSettings();
            settings.BaseUrl = "https://indexer.example";
            settings.ApiKey = "fixture-key";
            settings.EnableNarratorMetadata = false;
            indexer.Definition = new IndexerDefinition { Id = 1, Name = "Fixture", Settings = settings, ProxyId = 7 };

            var result = indexer.Test();

            Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
            Assert.That(((INarratorMetadataProvider)indexer).SupportsNarratorMetadata, Is.EqualTo(expected));
            Assert.That(settings.EnableNarratorMetadata, Is.False);
            Assert.That(feedCalls, Is.EqualTo(1));
            Assert.That(requests, Has.Count.EqualTo(nfoCalls));
            Assert.That(proxyIds, Is.All.EqualTo(7));
            foreach (var request in requests)
            {
                Assert.That(request.Url.FullUri, Does.Contain("id=feed-selected-id").And.Not.Contain("t=get&"));
            }
        }
    }
}
