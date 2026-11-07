using NzbDrone.Core.Configuration;
using NUnit.Framework;

namespace NzbDrone.Core.Test.Configuration
{
    [TestFixture]
    public class ConfigServiceMetadataServerUrlFixture
    {
        [Test]
        public void saved_chaptarr_metadata_url_should_take_precedence_over_legacy_environment_value()
        {
            var url = ConfigService.ResolveMetadataServerUrl(
                "https://metadata.chaptarr.example",
                "https://metadata.bookshelf.example");

            Assert.That(url, Is.EqualTo("https://metadata.chaptarr.example"));
        }

        [Test]
        public void legacy_metadata_url_should_be_used_when_chaptarr_has_no_saved_value()
        {
            var url = ConfigService.ResolveMetadataServerUrl(
                string.Empty,
                "https://metadata.bookshelf.example");

            Assert.That(url, Is.EqualTo("https://metadata.bookshelf.example"));
        }

        [Test]
        public void metadata_server_should_keep_the_chaptarr_default_when_no_legacy_value_exists()
        {
            var url = ConfigService.ResolveMetadataServerUrl(string.Empty, null);

            Assert.That(url, Is.EqualTo("https://api2.chaptarr.com"));
        }

        [Test]
        public void captured_source_fallback_should_apply_after_source_environment_is_removed()
        {
            Assert.That(ConfigService.ResolveMetadataServerUrl("", null, "https://source-default.example"),
                Is.EqualTo("https://source-default.example"));
        }

        [Test]
        public void saved_and_current_environment_values_should_override_captured_source_fallback()
        {
            Assert.That(ConfigService.ResolveMetadataServerUrl("https://saved.example", "https://environment.example", "https://source.example"),
                Is.EqualTo("https://saved.example"));
            Assert.That(ConfigService.ResolveMetadataServerUrl("", "https://environment.example", "https://source.example"),
                Is.EqualTo("https://environment.example"));
        }
    }
}
