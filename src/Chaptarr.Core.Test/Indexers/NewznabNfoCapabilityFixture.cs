using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Parser.Model;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabNfoCapabilityFixture
    {
        private readonly List<HttpRequest> _requests = new();
        private CacheManager _cache;
        private NewznabSettings _settings;
        private Func<HttpRequest, HttpResponse> _respond;
        private IIndexerHttpClient _http;

        [SetUp]
        public void SetUp()
        {
            _requests.Clear();
            _cache = new CacheManager();
            _settings = new NewznabSettings { BaseUrl = "https://indexer.example", ApiKey = "fixture-key", EnableNarratorMetadata = true };
            _respond = request => Response(request, "Narrator: A Reader", HttpStatusCode.OK, "text/plain");
            _http = Proxy<IIndexerHttpClient>((method, args) =>
            {
                if (method.Name != nameof(IIndexerHttpClient.Execute)) throw new NotImplementedException(method.Name);
                var request = (HttpRequest)args[0];
                _requests.Add(request);
                return _respond(request);
            });
        }

        private NewznabNarratorMetadataClient Client(int? proxyId = null) => new(_http, _settings, 42, TimeSpan.Zero, _cache, proxyId);
        private static ReleaseInfo Release(string id = "current-id", bool? nfo = true) => new()
        {
            DownloadUrl = $"https://indexer.example/api?t=get&id={id}", HasNfo = nfo
        };
        private static HttpResponse Response(HttpRequest request, string body, HttpStatusCode status, string type = "application/xml") =>
            new(request, new HttpHeader { ContentType = type }, body, status);
        private static HttpResponse Error(HttpRequest request, int code, HttpStatusCode status) =>
            Response(request, $"<error code=\"{code}\" description=\"API response\" />", status);

        [TestCase(true)]
        [TestCase(false)]
        public void synthetic_feed_responses_preserve_nfo_capability_and_endpoint_selection(bool available)
        {
            var metadataHint = available
                ? "<newznab:attr name=\"nfo\" value=\"1\" /><newznab:attr name=\"info\" value=\"https://indexer.example/api?t=getnfo&amp;id=fixture-id\" />"
                : string.Empty;
            var feed = $$"""
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel>
                    <title>Example indexer</title>
                    <item>
                      <title>Example Writer - Example Book</title>
                      <guid isPermaLink="false">fixture-id</guid>
                      <link>https://indexer.example/api?t=get&amp;id=fixture-id</link>
                      <comments>https://indexer.example/details/fixture-id#comments</comments>
                      <pubDate>Sat, 01 Jan 2022 00:00:00 +0000</pubDate>
                      <enclosure url="https://indexer.example/api?t=get&amp;id=fixture-id" length="123456789" type="application/x-nzb" />
                      <newznab:attr name="size" value="123456789" />
                      {{metadataHint}}
                    </item>
                  </channel>
                </rss>
                """;
            var feedRequest = new HttpRequest("https://indexer.example/api?t=search&extended=1");
            var releases = new NewznabRssParser().ParseResponse(new IndexerResponse(new IndexerRequest(feedRequest),
                Response(feedRequest, feed, HttpStatusCode.OK, "application/rss+xml")));
            var release = releases.Single();
            _respond = request => available
                ? Response(request, "Example release information without narrator metadata.", HttpStatusCode.OK, "text/x-nfo;charset=UTF-8")
                : Error(request, 300, HttpStatusCode.MultipleChoices);
            var client = Client();

            client.Test(new[] { release });

            Assert.Multiple(() =>
            {
                Assert.That(NewznabNarratorMetadataClient.ExtractReleaseId(release), Is.EqualTo("fixture-id"));
                Assert.That(release.HasNfo, Is.EqualTo(available ? (bool?)true : null));
                Assert.That(client.SupportsMetadata, Is.EqualTo(available ? (bool?)true : null));
                Assert.That(_requests, Has.Count.EqualTo(1));
                Assert.That(_requests[0].Url.FullUri, Does.Contain("id=fixture-id")
                    .And.Contain(available ? "t=getnfo" : "t=info"));
            });
        }

        [TestCase(202, HttpStatusCode.OK)]
        [TestCase(203, HttpStatusCode.OK)]
        [TestCase(202, HttpStatusCode.BadRequest)]
        [TestCase(203, HttpStatusCode.NotFound)]
        public void explicit_unsupported_is_shared_across_releases_and_reopen_but_test_can_recover(int code, HttpStatusCode status)
        {
            _respond = request => Error(request, code, status);
            Client().Test(new[] { Release() });
            Assert.That(Client().SupportsMetadata, Is.False);
            Assert.That(_requests.Count, Is.EqualTo(2));
            Assert.That(Client().TryPopulate(Release("different-id")), Is.False);
            Assert.That(_requests.Count, Is.EqualTo(2));
            Assert.That(_settings.EnableNarratorMetadata, Is.True);
            _respond = request => Response(request, "Narrator: A Reader", HttpStatusCode.OK, "text/plain");
            Client().Test(new[] { Release() });
            Assert.That(Client().SupportsMetadata, Is.True);
            Assert.That(_requests.Count, Is.EqualTo(3));
        }

        [Test]
        public void uses_one_current_advertised_release_without_fetching_a_download()
        {
            Client().Test(new[] { Release("no-nfo", false), Release("unknown", null), Release("advertised", true) });
            Assert.That(_requests, Has.Count.EqualTo(1));
            Assert.That(_requests[0].Url.FullUri, Does.Contain("id=advertised").And.Contain("t=info").And.Not.Contain("t=get&"));
            Assert.That(Client().SupportsMetadata, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void opening_an_existing_configuration_does_not_test_or_change_its_choice(bool enabled)
        {
            _settings.EnableNarratorMetadata = enabled;
            Assert.That(Client().SupportsMetadata, Is.Null);
            Assert.That(Client().SupportsMetadata, Is.Null);
            Assert.That(_settings.EnableNarratorMetadata, Is.EqualTo(enabled));
            Assert.That(_requests, Is.Empty);
        }

        [Test]
        public void no_suitable_release_is_unknown_not_unsupported()
        {
            Client().Test(new[] { Release("absent", false), new ReleaseInfo { HasNfo = true } });
            Assert.That(_requests, Is.Empty);
            Assert.That(Client().SupportsMetadata, Is.Null);
        }

        [TestCase(HttpStatusCode.OK)]
        [TestCase(HttpStatusCode.NotFound)]
        public void missing_item_is_unknown_and_not_an_endpoint_failure(HttpStatusCode status)
        {
            _respond = request => Error(request, 300, status);
            Client().Test(new[] { Release() });
            Assert.That(Client().SupportsMetadata, Is.Null);
            Assert.That(_requests, Has.Count.EqualTo(1));
            Assert.That(_settings.EnableNarratorMetadata, Is.True);
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void transport_failure_does_not_disable_or_negative_cache_a_release(HttpStatusCode status)
        {
            _respond = request => Response(request, "temporarily unavailable", status, "text/plain");
            Assert.Throws<HttpException>(() => Client().Test(new[] { Release() }));
            Assert.That(Client().SupportsMetadata, Is.Null);
            _respond = request => Response(request, "Narrator: A Reader", HttpStatusCode.OK, "text/plain");
            Assert.That(Client().TryPopulate(Release()), Is.True);
            Assert.That(_requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void empty_nfo_or_html_does_not_prove_support()
        {
            _respond = request => Response(request, "<html>Sign in</html>", HttpStatusCode.OK, "text/html");
            Assert.Throws<InvalidOperationException>(() => Client().Test(new[] { Release() }));
            Assert.That(Client().SupportsMetadata, Is.Null);
            _respond = request => Response(request, "Narrator: A Reader", HttpStatusCode.OK, "text/plain");
            Assert.That(Client().TryPopulate(Release()), Is.True);
        }

        [Test]
        public void nfo_without_narrator_still_proves_support()
        {
            _respond = request => Response(request, "Release information without narrator", HttpStatusCode.OK, "text/plain");
            Client().Test(new[] { Release() });
            Assert.That(Client().SupportsMetadata, Is.True);
            Assert.That(_requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void support_follows_the_same_connection_when_a_new_indexer_gets_its_saved_id()
        {
            _respond = request => Error(request, 203, HttpStatusCode.OK);
            var unsaved = new NewznabNarratorMetadataClient(_http, _settings, 0, TimeSpan.Zero, _cache);
            unsaved.Test(new[] { Release() });
            Assert.That(Client().SupportsMetadata, Is.False);
            Assert.That(Client().TryPopulate(Release()), Is.False);
            Assert.That(_requests.Count, Is.EqualTo(2));
        }

        [TestCase("apiKey")]
        [TestCase("baseUrl")]
        [TestCase("apiPath")]
        [TestCase("overrideUrl")]
        [TestCase("overrideKey")]
        [TestCase("proxy")]
        public void old_unsupported_result_does_not_apply_to_changed_connection(string change)
        {
            _respond = request => Error(request, 203, HttpStatusCode.OK);
            Client().Test(new[] { Release() });
            switch (change)
            {
                case "apiKey": _settings.ApiKey = "changed-key"; break;
                case "baseUrl": _settings.BaseUrl = "https://changed.example"; break;
                case "apiPath": _settings.ApiPath = "/other-api"; break;
                case "overrideUrl": _settings.NarratorMetadataBaseUrl = "https://direct.example"; break;
                case "overrideKey": _settings.NarratorMetadataApiKey = "direct-key"; break;
            }

            var updated = Client(change == "proxy" ? 4 : null);
            Assert.That(updated.SupportsMetadata, Is.Null);
            _respond = request => Response(request, "Narrator: A Reader", HttpStatusCode.OK, "text/plain");
            Assert.That(updated.TryPopulate(Release()), Is.True);
        }

        [TestCase("info", "getnfo")]
        [TestCase("getnfo", "info")]
        public void supported_alternative_prevents_false_unsupported_and_duplicate_fallbacks(string first, string second)
        {
            var release = Release();
            release.NfoUrl = $"https://indexer.example/api?t={first}&id=current-id";
            _respond = request => request.Url.FullUri.Contains("t=" + first)
                ? Error(request, 203, HttpStatusCode.BadRequest)
                : Response(request, "Narrator: A Reader", HttpStatusCode.OK, "text/plain");
            Assert.That(Client().TryPopulate(release), Is.True);
            Assert.That(_requests, Has.Count.EqualTo(2));
            Assert.That(Client().SupportsMetadata, Is.True);
            Assert.That(_requests.Last().Url.FullUri, Does.Contain("t=" + second));
        }
    }
}
