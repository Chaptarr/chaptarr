using Chaptarr.Api.V1.Config;
using Prowlarr.Api.V1.Config;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ConfigurationBookshelfCompatibilityFixture
    {
        [Test]
        public void legacy_proxy_enabled_should_map_to_proxy_mode_only_when_new_mode_is_absent()
        {
            var legacy = new HostConfigResource { ProxyEnabled = true };
            legacy.ApplyBookshelfCompatibilityAliases();

            Assert.That(legacy.ProxyMode, Is.EqualTo(ProxyMode.ProxyEverything));

            var modern = new HostConfigResource
            {
                ProxyMode = ProxyMode.Disabled,
                ProxyEnabled = true
            };
            modern.ApplyBookshelfCompatibilityAliases();

            Assert.That(modern.ProxyMode, Is.EqualTo(ProxyMode.Disabled));
        }

        [Test]
        public void legacy_metadata_source_should_map_only_when_metadata_server_url_is_absent()
        {
            var legacy = new DevelopmentConfigResource { MetadataSource = "https://metadata.example/api" };
            legacy.ApplyBookshelfCompatibilityAliases();

            Assert.That(legacy.MetadataServerUrl, Is.EqualTo("https://metadata.example/api"));

            var modern = new DevelopmentConfigResource
            {
                MetadataServerUrl = string.Empty,
                MetadataSource = "https://old.example/api"
            };
            modern.ApplyBookshelfCompatibilityAliases();

            Assert.That(modern.MetadataServerUrl, Is.Empty);

            var clearLegacy = new DevelopmentConfigResource { MetadataSource = string.Empty };
            clearLegacy.ApplyBookshelfCompatibilityAliases();
            Assert.That(clearLegacy.MetadataServerUrl, Is.Empty);
        }

        [Test]
        public void legacy_new_author_root_paths_should_map_to_format_specific_defaults()
        {
            var legacy = new MediaManagementConfigResource
            {
                NewAuthorAudiobookRootFolderPath = "/audiobooks",
                NewAuthorEbookRootFolderPath = "/ebooks"
            };

            legacy.ApplyBookshelfCompatibilityAliases();

            Assert.That(legacy.DefaultAudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(legacy.DefaultEbookRootFolderPath, Is.EqualTo("/ebooks"));

            var modern = new MediaManagementConfigResource
            {
                DefaultAudiobookRootFolderPath = "/new-audiobooks",
                DefaultEbookRootFolderPath = "/new-ebooks",
                NewAuthorAudiobookRootFolderPath = "/old-audiobooks",
                NewAuthorEbookRootFolderPath = "/old-ebooks"
            };

            modern.ApplyBookshelfCompatibilityAliases();

            Assert.That(modern.DefaultAudiobookRootFolderPath, Is.EqualTo("/new-audiobooks"));
            Assert.That(modern.DefaultEbookRootFolderPath, Is.EqualTo("/new-ebooks"));
        }

        [Test]
        public void media_management_resource_should_expose_the_legacy_book_import_match_threshold()
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookImportMinimumMatchPercent).Returns(83);

            var resource = MediaManagementConfigResourceMapper.ToResource(config.Object);

            Assert.That(resource.BookImportMinimumMatchPercent, Is.EqualTo(83));
        }
    }
}
