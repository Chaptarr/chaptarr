using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfSettingsLegacyMigrationFixture
    {
        private static readonly JsonSerializerOptions SerializerSettings = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        static AudioBookShelfSettingsLegacyMigrationFixture()
        {
            SerializerSettings.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, true));
            SerializerSettings.Converters.Add(new STJTimeSpanConverter());
            SerializerSettings.Converters.Add(new STJUtcConverter());
        }

        [Test]
        public void should_migrate_legacy_server_url_http()
        {
            var settings = JsonSerializer.Deserialize<AudioBookShelfSettings>("{\"serverUrl\":\"http://example.com\"}", SerializerSettings);

            Assert.That(settings.Host, Is.EqualTo("example.com"));
            Assert.That(settings.Port, Is.EqualTo(80));
            Assert.That(settings.UseSsl, Is.False);
            Assert.That(settings.UrlBase, Is.Null);
            Assert.That(settings.ServerUrl, Is.Null);
        }

        [Test]
        public void should_migrate_legacy_server_url_https()
        {
            var settings = JsonSerializer.Deserialize<AudioBookShelfSettings>("{\"serverUrl\":\"https://example.com\"}", SerializerSettings);

            Assert.That(settings.Host, Is.EqualTo("example.com"));
            Assert.That(settings.Port, Is.EqualTo(443));
            Assert.That(settings.UseSsl, Is.True);
            Assert.That(settings.UrlBase, Is.Null);
            Assert.That(settings.ServerUrl, Is.Null);
        }

        [Test]
        public void should_migrate_legacy_server_url_with_port_and_urlbase()
        {
            var settings = JsonSerializer.Deserialize<AudioBookShelfSettings>("{\"serverUrl\":\"https://example.com:8443/abs/\"}", SerializerSettings);

            Assert.That(settings.Host, Is.EqualTo("example.com"));
            Assert.That(settings.Port, Is.EqualTo(8443));
            Assert.That(settings.UseSsl, Is.True);
            Assert.That(settings.UrlBase, Is.EqualTo("/abs"));
            Assert.That(settings.ServerUrl, Is.Null);
        }

        [Test]
        public void should_prefer_host_over_legacy_server_url_when_mixed()
        {
            var settings = JsonSerializer.Deserialize<AudioBookShelfSettings>("{\"host\":\"real\",\"port\":1234,\"useSsl\":true,\"serverUrl\":\"http://legacy:80/x\"}", SerializerSettings);

            Assert.That(settings.Host, Is.EqualTo("real"));
            Assert.That(settings.Port, Is.EqualTo(1234));
            Assert.That(settings.UseSsl, Is.True);
            Assert.That(settings.ServerUrl, Is.Null);
        }
    }
}

