using System;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabNarratorMetadataClientFixture
    {
        private class IndexerHttpClientProxy : DispatchProxy
        {
            public int ExecuteCount { get; private set; }
            public Func<HttpRequest, HttpResponse> Response { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerHttpClient.Execute) && args?[0] is HttpRequest request)
                {
                    ExecuteCount++;
                    if (Response != null)
                    {
                        return Response(request);
                    }

                    var headers = new HttpHeader { ContentType = "text/plain" };
                    return new HttpResponse(request, headers, "Narration: Steven Pacey\nLength: 22 hours", HttpStatusCode.OK);
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        [TestCase(100, "Invalid API key", typeof(ApiKeyException), HttpStatusCode.OK)]
        [TestCase(100, "Invalid API key", typeof(ApiKeyException), HttpStatusCode.BadRequest)]
        [TestCase(500, "Request limit reached", typeof(RequestLimitReachedException), HttpStatusCode.OK)]
        [TestCase(500, "Request limit reached", typeof(RequestLimitReachedException), HttpStatusCode.BadRequest)]
        public void provider_errors_should_escape_without_endpoint_fallback_or_negative_caching(
            int code, string description, Type error, HttpStatusCode status)
        {
            var http = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var proxy = (IndexerHttpClientProxy)(object)http;
            proxy.Response = request => new HttpResponse(request, new HttpHeader { ContentType = "application/xml" },
                $"<error code=\"{code}\" description=\"{description}\" />", status);
            var settings = new NewznabSettings { BaseUrl = "https://indexer.example", ApiPath = "/api", ApiKey = "test-key" };
            var client = new NewznabNarratorMetadataClient(http, settings, 42, TimeSpan.Zero, new CacheManager());
            var release = new ReleaseInfo { DownloadUrl = "https://indexer.example/api?t=get&id=release-123" };

            Assert.Throws(error, () => client.TryPopulate(release));
            Assert.That(proxy.ExecuteCount, Is.EqualTo(1));
            Assert.Throws(error, () => client.TryPopulate(release));
            Assert.That(proxy.ExecuteCount, Is.EqualTo(2));
        }

        [TestCase("https://api.nzb.life/getnzb/e457b6ab64a00ddf0905d70ff634c9e2.nzb&i=321976&r=abc", "e457b6ab64a00ddf0905d70ff634c9e2")]
        [TestCase("https://drunkenslug.com/api?t=get&id=9ca52909ba9b9e5e6758d815fef4ecda&apikey=abc", "9ca52909ba9b9e5e6758d815fef4ecda")]
        public void should_extract_release_id_from_download_url(string downloadUrl, string expected)
        {
            var release = new ReleaseInfo { DownloadUrl = downloadUrl };

            var id = NewznabNarratorMetadataClient.ExtractReleaseId(release);

            Assert.That(id, Is.EqualTo(expected));
        }

        [TestCase("https://www.nzb.life/details/e457b6ab64a00ddf0905d70ff634c9e2", "e457b6ab64a00ddf0905d70ff634c9e2")]
        [TestCase("https://api.nzb.life/details/0bf4dd2cadc907000962372c710a75d5", "0bf4dd2cadc907000962372c710a75d5")]
        public void should_extract_release_id_from_guid_details_url(string guid, string expected)
        {
            var release = new ReleaseInfo { Guid = guid };

            var id = NewznabNarratorMetadataClient.ExtractReleaseId(release);

            Assert.That(id, Is.EqualTo(expected));
        }

        [TestCase("22 hrs and 37 mins", "22h 37m")]
        [TestCase("22h 37m", "22h 37m")]
        [TestCase("1357 minutes", "22h 37m")]
        [TestCase("22:37:00", "22h 37m")]
        [TestCase("22 hours", "22h")]
        public void should_normalize_duration_formats(string raw, string expected)
        {
            var normalized = NewznabNarratorMetadataClient.NormalizeDuration(raw);

            Assert.That(normalized, Is.EqualTo(expected));
        }

        [TestCase("１２:３０")]
        [TestCase("١٢ hours")]
        [TestCase("99999999999 minutes")]
        [TestCase("99999999 hours")]
        [TestCase("35791394 hours 2147483647 minutes")]
        public void should_preserve_unparseable_duration_without_losing_other_nfo_metadata(string duration)
        {
            var metadata = NewznabNarratorMetadataClient.ParseNfo(
                $"Duration: {duration}\nNarration: Steven Pacey\nGraphic Audio");

            Assert.Multiple(() =>
            {
                Assert.That(metadata.Duration, Is.EqualTo(duration));
                Assert.That(metadata.Narrator, Is.EqualTo("Steven Pacey"));
                Assert.That(metadata.IsGraphicAudio, Is.True);
            });
        }

        [Test]
        public void should_parse_nfo_for_narrator_and_duration()
        {
            var nfo = @"Audiobook

Arthur:  Joe Abercrombie

Book:  Before They Are Hanged

Series: The First Law Book Two

Length: 22 hrs and 37 mins

Narration: Steven Pacey

Quality: Good
";

            var metadata = NewznabNarratorMetadataClient.ParseNfo(nfo);

            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata.Narrator, Is.EqualTo("Steven Pacey"));
            Assert.That(metadata.Duration, Is.EqualTo("22h 37m"));
        }

        [Test]
        public void should_parse_nfo_with_angle_bracket_delimiters()
        {
            var nfo = @"Audiobook

Length > 22 hrs and 37 mins
Narration > Steven Pacey
";

            var metadata = NewznabNarratorMetadataClient.ParseNfo(nfo);

            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata.Narrator, Is.EqualTo("Steven Pacey"));
            Assert.That(metadata.Duration, Is.EqualTo("22h 37m"));
        }

        [TestCase("Publisher: Graphic.Audio", true)]
        [TestCase("Publisher: ＧｒａｐｈｉｃＡｕｄｉｏ", true)]
        [TestCase("Description: Graphic Audiobook", false)]
        [TestCase("Narration: Full Cast", false)]
        [TestCase("Narration: Karina Castillo", false)]
        public void nfo_brand_evidence_uses_the_shared_bounded_vocabulary(string line, bool expected)
        {
            var metadata = NewznabNarratorMetadataClient.ParseNfo("Narration: Steven Pacey\n" + line);
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata.IsGraphicAudio, Is.EqualTo(expected));
            Assert.That(metadata.Narrator, Is.EqualTo("Steven Pacey"));
        }

        [Test]
        public void should_detect_graphic_audio_from_nfo()
        {
            var nfo = @"Audiobook

Narration: Graphic Audio
";

            var metadata = NewznabNarratorMetadataClient.ParseNfo(nfo);

            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata.IsGraphicAudio, Is.True);
        }

        [Test]
        public void should_read_legacy_enhanced_search_settings_and_write_narrator_metadata_settings()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var settings = JsonSerializer.Deserialize<NewznabSettings>(@"{
  ""enableEnhancedSearching"": true,
  ""enhancedSearchingBaseUrl"": ""https://prowlarr.example"",
  ""enhancedSearchingApiKey"": ""legacy-key""
}", options);

            Assert.That(settings.EnableNarratorMetadata, Is.True);
            Assert.That(settings.NarratorMetadataBaseUrl, Is.EqualTo("https://prowlarr.example"));
            Assert.That(settings.NarratorMetadataApiKey, Is.EqualTo("legacy-key"));

            var serialized = JsonSerializer.Serialize(settings, options);

            Assert.That(serialized, Does.Contain("enableNarratorMetadata"));
            Assert.That(serialized, Does.Contain("narratorMetadataBaseUrl"));
            Assert.That(serialized, Does.Contain("narratorMetadataApiKey"));
            Assert.That(serialized, Does.Not.Contain("enableEnhancedSearching"));
            Assert.That(serialized, Does.Not.Contain("enhancedSearchingBaseUrl"));
            Assert.That(serialized, Does.Not.Contain("enhancedSearchingApiKey"));
        }

        [Test]
        public void should_share_metadata_cache_across_transient_indexer_instances()
        {
            var cacheManager = new CacheManager();
            var httpClient = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var proxy = (IndexerHttpClientProxy)(object)httpClient;
            var settings = new NewznabSettings
            {
                BaseUrl = "https://indexer.example",
                ApiPath = "/api",
                ApiKey = "test-key"
            };

            NewznabNarratorMetadataClient CreateClient()
            {
                return new NewznabNarratorMetadataClient(
                    httpClient,
                    settings,
                    indexerId: 42,
                    rateLimit: TimeSpan.Zero,
                    cacheManager);
            }

            ReleaseInfo CreateRelease()
            {
                return new ReleaseInfo
                {
                    DownloadUrl = "https://indexer.example/api?t=get&id=release-123&apikey=test-key"
                };
            }

            var first = CreateRelease();
            var second = CreateRelease();

            Assert.That(CreateClient().TryPopulate(first), Is.True);
            Assert.That(CreateClient().TryPopulate(second), Is.True);
            Assert.That(first.Narrator, Is.EqualTo("Steven Pacey"));
            Assert.That(second.Narrator, Is.EqualTo("Steven Pacey"));
            Assert.That(proxy.ExecuteCount, Is.EqualTo(1));
        }
    }
}
