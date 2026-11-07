using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.Config;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    [NonParallelizable]
    public class MetadataProviderConfigResourceFixture
    {
        private static readonly string[] EnvironmentKeys =
        {
            "CHAPTARR_METADATA_SOURCES",
            "BOOKSHELF_METADATA_SOURCES",
            "HARDCOVER_AUTH",
            "HARDCOVER_API_KEY",
            "HARDCOVER",
            "HARDCOVER_NATIVE",
            "GOOGLE_BOOKS_API_KEY",
            "EUROPEANA_API_KEY",
            "HARDCOVER_APIFY_GOODREADS_ACTOR",
            "HARDCOVER_APIFY_TOKEN",
            "HARDCOVER_APIFY_GOODREADS_INPUT_TEMPLATE",
            "OPEN_LIBRARY_CONTACT_EMAIL"
        };

        private readonly Dictionary<string, string> _previousEnvironment = new();

        [SetUp]
        public void ClearProviderEnvironment()
        {
            foreach (var key in EnvironmentKeys)
            {
                _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        [TearDown]
        public void RestoreProviderEnvironment()
        {
            foreach (var pair in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        [Test]
        public void should_never_return_saved_provider_credentials()
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.GoogleBooksApiKey).Returns("google-secret");
            config.SetupGet(value => value.EuropeanaApiKey).Returns("europeana-secret");
            config.SetupGet(value => value.ApifyToken).Returns("apify-secret");
            config.SetupGet(value => value.ApifyGoodreadsActor).Returns("publisher~actor");
            config.SetupGet(value => value.ApifyGoodreadsInputTemplate).Returns("{\"queries\":[{{query}}]}");
            config.SetupGet(value => value.AdditionalMetadataSources).Returns("ndl");
            config.Setup(value => value.IsDefined("AdditionalMetadataSources")).Returns(true);

            var resource = MetadataProviderConfigResourceMapper.ToResource(config.Object);

            Assert.Multiple(() =>
            {
                Assert.That(resource.GoogleBooksApiKey, Is.Empty);
                Assert.That(resource.EuropeanaApiKey, Is.Empty);
                Assert.That(resource.ApifyToken, Is.Empty);
                Assert.That(resource.HasGoogleBooksApiKey, Is.True);
                Assert.That(resource.HasEuropeanaApiKey, Is.True);
                Assert.That(resource.HasApifyToken, Is.True);
                Assert.That(resource.ApifyGoodreadsActor, Is.EqualTo("publisher~actor"));
                Assert.That(resource.EnableNdl, Is.True);
                Assert.That(resource.EnableLoc, Is.False);
            });
        }

        [Test]
        public void should_preserve_new_settings_when_an_older_client_omits_them()
        {
            Dictionary<string, object> savedValues = null;
            var config = new Mock<IConfigService>();
            config.Setup(value => value.IsDefined(It.IsAny<string>())).Returns(false);
            config.Setup(value => value.SaveConfigDictionary(It.IsAny<Dictionary<string, object>>()))
                .Callback<Dictionary<string, object>>(values => savedValues = values);
            var controller = new MetadataProviderConfigController(config.Object);

            controller.SaveConfig(new MetadataProviderConfigResource { Id = 1 });

            Assert.That(savedValues, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(savedValues, Does.Not.ContainKey("MetadataCatalogSources"));
                Assert.That(savedValues, Does.Not.ContainKey("AdditionalMetadataSources"));
                Assert.That(savedValues, Does.Not.ContainKey("GoogleBooksApiKey"));
                Assert.That(savedValues, Does.Not.ContainKey("EuropeanaApiKey"));
                Assert.That(savedValues, Does.Not.ContainKey("ApifyToken"));
                Assert.That(savedValues, Does.Not.ContainKey("ApifyGoodreadsInputTemplate"));
            });
        }
    }
}
