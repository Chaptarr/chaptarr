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

            public string GetCurrentLocale() => "en";

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
                Urls = "https://203.0.113.18",
                ApiKey = "real-secret"
            });

            Assert.That(schema.Single(field => field.Name == "apiKey").Value, Is.EqualTo("********"));
        }

        [Test]
        public void should_keep_existing_api_key_when_placeholder_is_saved()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = "https://203.0.113.18",
                ApiKey = "real-secret"
            };

            SchemaBuilder.ReadFromSchema(new()
            {
                new Field { Name = "urls", Value = "https://203.0.113.18\nhttps://203.0.113.26" },
                new Field { Name = "apiKey", Value = "********" }
            }, settings);

            Assert.That(settings.ApiKey, Is.EqualTo("real-secret"));
            Assert.That(settings.Urls, Is.EqualTo("https://203.0.113.18\nhttps://203.0.113.26"));
        }

        [Test]
        public void should_accept_bookshelfng_tag_array_values_for_the_urls_schema_field()
        {
            var settings = new DirectDownloadSettings();
            var urls = JsonSerializer.Deserialize<JsonElement>(
                "[\"https://primary.example\",\"https://secondary.example\"]");

            SchemaBuilder.ReadFromSchema(new()
            {
                new Field { Name = "urls", Value = urls }
            }, settings);

            Assert.That(settings.Urls, Is.EqualTo("https://primary.example\nhttps://secondary.example"));
            Assert.That(DirectDownloadSettings.NormalizeUrls(settings.Urls), Is.EqualTo(new[]
            {
                "https://primary.example",
                "https://secondary.example"
            }));
        }

        [Test]
        public void should_normalize_urls_by_trimming_and_deduplicating_while_preserving_order()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = " https://203.0.113.18/ \r\n\r\nhttps://203.0.113.26\nhttps://203.0.113.18/ ",
                EnableSlowFallback = true
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.True, () => string.Join(" | ", result.Errors.Select(error => error.ErrorMessage)));
            Assert.That(settings.Urls, Is.EqualTo("https://203.0.113.18\nhttps://203.0.113.26"));
            Assert.That(settings.BaseUrl, Is.EqualTo("https://203.0.113.18"));
        }

        [TestCase("")]
        [TestCase("   \r\n  ")]
        [TestCase("https://203.0.113.18\nftp://203.0.113.26")]
        [TestCase("https://203.0.113.18\nhttps://user:pass@203.0.113.26")]
        [TestCase("https://203.0.113.18\nnot-a-url")]
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
                Urls = " https://203.0.113.18/ \nhttps://203.0.113.26 ",
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
            Assert.That(restored.Urls, Is.EqualTo("https://203.0.113.18\nhttps://203.0.113.26"));
            Assert.That(restored.BaseUrl, Is.EqualTo("https://203.0.113.18"));
        }

        [Test]
        public void should_load_legacy_array_url_settings_and_preserve_order()
        {
            const string json = """
                {"urls":["https://203.0.113.31/","https://203.0.113.26"],"apiKey":"legacy-key","enableSlowFallback":true}
                """;

            var restored = STJson.Deserialize<DirectDownloadSettings>(json);

            Assert.That(restored.Urls, Is.EqualTo("https://203.0.113.31\nhttps://203.0.113.26"));
            Assert.That(restored.BaseUrl, Is.EqualTo("https://203.0.113.31"));
            Assert.That(restored.ApiKey, Is.EqualTo("legacy-key"));
            Assert.That(restored.EnableSlowFallback, Is.True);
            Assert.That(STJson.ToJson(restored), Does.Contain("\"urls\": \"https://203.0.113.31\\nhttps://203.0.113.26\""));
        }

        [Test]
        public void should_reject_non_string_entries_in_legacy_url_arrays()
        {
            const string json = """
                {"urls":["https://203.0.113.31",7]}
                """;

            Assert.Throws<System.Text.Json.JsonException>(() => STJson.Deserialize<DirectDownloadSettings>(json));
        }

        [TestCase(null, false, false, Description = "neither API key nor fallback is invalid")]
        [TestCase("real-key", false, true, Description = "API-only is valid")]
        [TestCase(null, true, true, Description = "fallback-only is valid")]
        [TestCase("real-key", true, true, Description = "both configured is valid")]
        [TestCase("   ", false, false, Description = "whitespace-only API key counts as absent, no fallback is invalid")]
        [TestCase("\t\n", false, false, Description = "whitespace (tabs/newlines) API key counts as absent, no fallback is invalid")]
        public void should_validate_api_key_or_fallback_matrix(string apiKey, bool enableSlowFallback, bool expectedValid, string description = null)
        {
            var settings = new DirectDownloadSettings
            {
                Urls = "https://203.0.113.18",
                ApiKey = apiKey,
                EnableSlowFallback = enableSlowFallback
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.EqualTo(expectedValid),
                $"Expected valid={expectedValid} for apiKey='{apiKey}', fallback={enableSlowFallback}. Errors: {string.Join("; ", result.Errors.Select(e => e.ErrorMessage))}");
        }

        [Test]
        public void should_reject_neither_api_key_nor_fallback_with_specific_error()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = "https://203.0.113.18",
                ApiKey = null,
                EnableSlowFallback = false
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Some.Contains("At least one download method is required"));
        }

        [Test]
        public void should_not_mask_absent_whitespace_api_key_in_schema()
        {
            var schema = SchemaBuilder.ToSchema(new DirectDownloadSettings
            {
                Urls = "https://203.0.113.18",
                ApiKey = "   "
            });

            var apiKeyField = schema.Single(field => field.Name == "apiKey");
            Assert.That(apiKeyField.Value, Is.Null, "Whitespace-only API key is absent and must not be masked as a real secret");
        }

        [Test]
        public void should_preserve_masked_key_when_whitespace_is_saved()
        {
            var settings = new DirectDownloadSettings
            {
                Urls = "https://203.0.113.18",
                ApiKey = "real-secret"
            };

            SchemaBuilder.ReadFromSchema(new()
            {
                new Field { Name = "urls", Value = "https://203.0.113.18" },
                new Field { Name = "apiKey", Value = "********" }
            }, settings);

            Assert.That(settings.ApiKey, Is.EqualTo("real-secret"));
        }
    }
}
