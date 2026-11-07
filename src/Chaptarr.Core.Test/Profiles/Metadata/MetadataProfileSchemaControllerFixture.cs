using Moq;
using NUnit.Framework;
using MetadataApi = Chaptarr.Api.V1.Profiles.Metadata;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public class MetadataProfileSchemaControllerFixture
    {
        [Test]
        public void should_use_bookshelf_default_language_for_migrated_installations()
        {
            var configService = new Mock<IConfigService>();
            configService.SetupGet(config => config.BookshelfMatchCompatibility).Returns(true);
            var controller = new MetadataApi.MetadataProfileSchemaController(configService.Object);

            Assert.That(controller.GetAll().AllowedLanguages, Is.EqualTo("eng"));
        }

        [Test]
        public void should_keep_native_default_language_for_new_installations()
        {
            var configService = new Mock<IConfigService>();
            configService.SetupGet(config => config.BookshelfMatchCompatibility).Returns(false);
            var controller = new MetadataApi.MetadataProfileSchemaController(configService.Object);

            Assert.That(controller.GetAll().AllowedLanguages, Is.Empty);
        }
    }
}
