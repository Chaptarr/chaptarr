using System.Linq;
using System.Text.Json;
using Chaptarr.Http.ClientSchema;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers.DirectDownload;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class DirectDownloadSettingsFixture
    {
        private sealed class TestLocalizationService : ILocalizationService
        {
            public System.Collections.Generic.Dictionary<string, string> GetLocalizationDictionary()
            {
                return new();
            }

            public string GetLocalizedString(string phrase)
            {
                return phrase;
            }

            public string GetLocalizedString(string phrase, System.Collections.Generic.Dictionary<string, object> tokens)
            {
                return phrase;
            }
        }

        [SetUp]
        public void SetUp()
        {
            typeof(SchemaBuilder)
                .GetField("_localizationService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .SetValue(null, new TestLocalizationService());
        }

        [Test]
        public void should_mask_api_key_in_schema()
        {
            var schema = SchemaBuilder.ToSchema(new DirectDownloadSettings
            {
                Urls = "https://downloads.example.com",
                ApiKey = "real-secret"
            });

            Assert.That(schema.Single(field => field.Name == "apiKey").Value, Is.EqualTo("********"));
        }

        [Test]
        public void should_keep_existing_api_key_when_placeholder_is_saved()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = "https://downloads.example.com",
                ApiKey = "real-secret"
            };

            SchemaBuilder.ReadFromSchema(new()
            {
                new Field { Name = "urls", Value = "https://downloads.example.com\nhttps://mirror.example.com" },
                new Field { Name = "apiKey", Value = "********" }
            }, settings);

            Assert.That(settings.ApiKey, Is.EqualTo("real-secret"));
            Assert.That(settings.Urls, Is.EqualTo("https://downloads.example.com\nhttps://mirror.example.com"));
        }

        [Test]
        public void should_normalize_urls_by_trimming_and_deduplicating_while_preserving_order()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = " https://downloads.example.com/ \r\n\r\nhttps://mirror.example.com\nhttps://downloads.example.com/ "
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.True, () => string.Join(" | ", result.Errors.Select(error => error.ErrorMessage)));
            Assert.That(settings.Urls, Is.EqualTo("https://downloads.example.com\nhttps://mirror.example.com"));
            Assert.That(settings.BaseUrl, Is.EqualTo("https://downloads.example.com"));
        }

        [TestCase("")]
        [TestCase("   \r\n  ")]
        [TestCase("https://downloads.example.com\nftp://mirror.example.com")]
        [TestCase("https://downloads.example.com\nhttps://user:pass@mirror.example.com")]
        [TestCase("https://downloads.example.com\nnot-a-url")]
        public void should_reject_invalid_url_input(string urls)
        {
            var settings = new DirectDownloadSettings
            {
                Urls = urls
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void should_omit_empty_api_key_from_json_and_preserve_normalized_urls_round_trip()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = " https://downloads.example.com/ \nhttps://mirror.example.com ",
                ApiKey = "  "
            };

            settings.Validate();

            var json = STJson.ToJson(settings);
            var restored = (DirectDownloadSettings)JsonSerializer.Deserialize(
                json,
                ProviderConfigTypeCache.Find(nameof(DirectDownloadSettings)),
                STJson.GetSerializerSettings());

            Assert.That(json, Does.Not.Contain("apiKey"));
            Assert.That(json, Does.Not.Contain("baseUrl"));
            Assert.That(restored.ApiKey, Is.Null);
            Assert.That(restored.Urls, Is.EqualTo("https://downloads.example.com\nhttps://mirror.example.com"));
            Assert.That(restored.BaseUrl, Is.EqualTo("https://downloads.example.com"));
        }
    }
}
