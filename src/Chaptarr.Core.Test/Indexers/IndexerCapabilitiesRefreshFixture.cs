using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture(false)]
    [TestFixture(true)]
    public class IndexerCapabilitiesRefreshFixture
    {
        private readonly bool _torrent;

        public IndexerCapabilitiesRefreshFixture(bool torrent)
        {
            _torrent = torrent;
        }

        [TestCase("success")]
        [TestCase("timeout")]
        [TestCase("invalid-key")]
        [TestCase("invalid-xml")]
        [TestCase("rate-limited")]
        [TestCase("server-error")]
        [TestCase("feed-invalid-key")]
        public void test_reads_live_caps_and_only_success_replaces_the_search_cache(string outcome)
        {
            var calls = new List<string>();
            var fresh = false;
            var http = Proxy<IIndexerHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                if (method.Name == nameof(IIndexerHttpClient.Get))
                {
                    calls.Add("caps");
                    Assert.That(request.Url.FullUri, Does.Contain("t=caps"));
                    if (fresh && outcome == "timeout") throw new TimeoutException("Indexer unavailable");
                    var body = fresh && outcome == "invalid-key" ? "<error code=\"100\" description=\"Incorrect user credentials\"/>"
                        : fresh && outcome == "invalid-xml" ? "not XML"
                        : fresh && outcome == "rate-limited" ? "<error code=\"500\" description=\"Request limit reached\"/>"
                        : fresh && outcome == "server-error" ? "<error code=\"900\" description=\"Server unavailable\"/>"
                        : $"<caps><limits default=\"50\" max=\"50\"/><searching><search available=\"yes\" supportedParams=\"q\"/></searching>" +
                          $"<categories><category id=\"7000\" name=\"Books\"><subcat id=\"7020\" name=\"{(fresh ? "Current ebooks" : "Old ebooks")}\"/></category></categories></caps>";
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, body, HttpStatusCode.OK);
                }

                if (method.Name == nameof(IIndexerHttpClient.Execute))
                {
                    calls.Add("nfo");
                    Assert.That(request.Url.FullUri, Does.Contain("t=info"));
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" },
                        "<error code=\"300\" description=\"No such item\"/>", HttpStatusCode.OK);
                }

                Assert.That(method.Name, Is.EqualTo(nameof(IIndexerHttpClient.ExecuteAsync)));
                calls.Add("feed");
                if (outcome == "feed-invalid-key")
                    return Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/rss+xml" },
                        "<error code=\"100\" description=\"Incorrect user credentials\"/>", HttpStatusCode.OK));
                var mime = _torrent ? "application/x-bittorrent" : "application/x-nzb";
                return Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/rss+xml" },
                    $"<rss version=\"2.0\"><channel><title>Fixture</title><item><title>Frank Herbert - Dune EPUB</title>" +
                    "<guid>https://indexer.example/details/book</guid><link>https://indexer.example/details/book</link>" +
                    $"<enclosure url=\"https://indexer.example/download/book\" length=\"5000000\" type=\"{mime}\"/>" +
                    "<pubDate>Wed, 28 Oct 2026 12:00:00 +0000</pubDate></item></channel></rss>", HttpStatusCode.OK));
            });
            var factory = Proxy<IIndexerHttpClientFactory>((_, args) =>
            {
                Assert.That((int?)args[0], Is.EqualTo(7));
                return http;
            });
            var cache = new CacheManager();
            var capabilities = new NewznabCapabilitiesProvider(cache, factory, LogManager.GetCurrentClassLogger());
            NewznabSettings settings = _torrent ? new TorznabSettings() : new NewznabSettings();
            settings.BaseUrl = "https://indexer.example";
            settings.ApiKey = "fixture-key";
            settings.Categories = new[] { 7000, 7020 };
            var selectedCategories = settings.Categories.ToArray();
            var old = capabilities.GetCapabilities(settings, 7);
            Assert.That(capabilities.GetCapabilities(settings, 7), Is.SameAs(old));
            Assert.That(calls, Is.EqualTo(new[] { "caps" }), "Ordinary queries retain the seven-day cache");
            fresh = true;
            IIndexer indexer = _torrent
                ? new Torznab(capabilities, factory, null, null, null, cache, LogManager.GetCurrentClassLogger())
                : new Newznab(capabilities, factory, null, null, null, cache, LogManager.GetCurrentClassLogger());
            indexer.Definition = new IndexerDefinition { Id = 1, Name = "Fixture", Settings = settings, ProxyId = 7 };

            var result = indexer.Test();

            Assert.That(settings.Categories, Is.EqualTo(selectedCategories), "Test must not edit the user's selection");
            var current = capabilities.GetCapabilities(settings, 7);
            if (outcome == "success")
            {
                Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
                Assert.That(calls, Is.EqualTo(new[] { "caps", "feed", "nfo", "caps" }), "Validate the feed first, retain the NFO probe, then refresh capabilities");
                Assert.That(current.Categories.Single().Subcategories.Single().Name, Is.EqualTo("Current ebooks"));
                Assert.That(indexer.RequestAction("newznabCategories", new Dictionary<string, string>()).ToJson(), Does.Contain("Current ebooks"));
                Assert.That(calls.Count, Is.EqualTo(4));
            }
            else
            {
                Assert.That(result.IsValid, Is.False, "A failed live Test cannot pass using stale capabilities");
                Assert.That(current, Is.SameAs(old), "Failure preserves the last good ordinary-search cache");
                Assert.That(calls, Is.EqualTo(outcome == "feed-invalid-key"
                    ? new[] { "caps", "feed" } : new[] { "caps", "feed", "nfo", "caps" }));
                if (outcome == "feed-invalid-key") Assert.That(result.Errors.Select(error => error.ErrorMessage), Has.Some.Contains("Invalid API Key"));
                else Assert.That(result.Errors.Select(error => error.ErrorMessage), Has.Some.StartsWith(outcome == "rate-limited"
                    ? "Request limit reached:" : "Indexer capabilities could not be read:"));
            }
        }
        [TestCase("<error code=\"500\" description=\"Request limit reached\"/>")]
        [TestCase("<error code=\"900\" description=\"Server unavailable\"/>")]
        [TestCase("<caps><limits default=\"invalid\" max=\"100\"/></caps>")]
        public void ordinary_lookup_caches_fallback_briefly_then_retries(string invalidResponse)
        {
            var calls = 0;
            var http = Proxy<IIndexerHttpClient>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IIndexerHttpClient.Get)));
                var body = ++calls == 1 ? invalidResponse : "<caps><limits default=\"42\" max=\"42\"/></caps>";
                return new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "application/xml" }, body, HttpStatusCode.OK);
            });
            var now = DateTime.UtcNow;
            DateTime expiry = default;
            NewznabCapabilities cached = null;
            var entry = Proxy<ICached<NewznabCapabilities>>((method, args) =>
            {
                if (method.Name == "Find") return now < expiry ? cached : null;
                Assert.That(method.Name, Is.EqualTo("Set"));
                cached = (NewznabCapabilities)args[1];
                var lifetime = (TimeSpan)args[2];
                Assert.That(lifetime, Is.EqualTo(calls == 1 ? TimeSpan.FromMinutes(5) : TimeSpan.FromDays(7)));
                expiry = now + lifetime;
                return null;
            });
            var provider = new NewznabCapabilitiesProvider(Proxy<ICacheManager>((_, _) => entry),
                Proxy<IIndexerHttpClientFactory>((_, _) => http), LogManager.GetCurrentClassLogger());
            NewznabSettings settings = _torrent ? new TorznabSettings() : new NewznabSettings();
            settings.BaseUrl = "https://indexer.example";
            var fallback = provider.GetCapabilities(settings, null);
            Assert.That(fallback.DefaultPageSize, Is.EqualTo(100));
            Assert.That(provider.GetCapabilities(settings, null), Is.SameAs(fallback));
            Assert.That(calls, Is.EqualTo(1));
            now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
            var recovered = provider.GetCapabilities(settings, null);
            Assert.That(recovered.DefaultPageSize, Is.EqualTo(42));
            Assert.That(provider.GetCapabilities(settings, null), Is.SameAs(recovered));
            Assert.That(calls, Is.EqualTo(2));
        }
        [Test]
        public void forced_lookup_bypasses_fallback_and_failure_does_not_replace_it()
        {
            var calls = 0;
            var http = Proxy<IIndexerHttpClient>((_, args) =>
            {
                calls++;
                return new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "application/xml" },
                    "<caps><limits default=\"invalid\" max=\"100\"/></caps>", HttpStatusCode.OK);
            });
            var provider = new NewznabCapabilitiesProvider(new CacheManager(), Proxy<IIndexerHttpClientFactory>((_, _) => http), LogManager.GetCurrentClassLogger());
            NewznabSettings settings = _torrent ? new TorznabSettings() : new NewznabSettings();
            settings.BaseUrl = "https://indexer.example";
            var fallback = provider.GetCapabilities(settings, null);
            Assert.Throws<FormatException>(() => provider.GetCapabilities(settings, null, forceRefresh: true));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(provider.GetCapabilities(settings, null), Is.SameAs(fallback));
            Assert.That(calls, Is.EqualTo(2));
        }
    }
}
