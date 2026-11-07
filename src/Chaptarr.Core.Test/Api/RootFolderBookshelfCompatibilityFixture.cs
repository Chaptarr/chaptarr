using System.Collections.Generic;
using Chaptarr.Api.V1.RootFolders;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class RootFolderBookshelfCompatibilityFixture
    {
        [Test]
        public void legacy_root_defaults_should_apply_to_both_formats_when_folder_type_is_omitted()
        {
            var resource = new RootFolderResource
            {
                Name = "Books",
                Path = "/books",
                DefaultMetadataProfileId = 23,
                DefaultQualityProfileId = 17,
                DefaultMonitorOption = MonitorTypes.Missing,
                DefaultNewItemMonitorOption = NewItemMonitorTypes.New,
                DefaultTags = new HashSet<int> { 5, 9 }
            };

            var model = RootFolderResourceMapper.ToModel(resource);
            var audiobook = model.GetAudiobookSettings();
            var ebook = model.GetEbookSettings();

            Assert.That(model.FolderType, Is.EqualTo(FolderType.Mixed));
            AssertLegacyDefaults(audiobook);
            AssertLegacyDefaults(ebook);

            var response = model.ToResource();
            Assert.That(response.DefaultMetadataProfileId, Is.EqualTo(23));
            Assert.That(response.DefaultQualityProfileId, Is.EqualTo(17));
            Assert.That(response.DefaultMonitorOption, Is.EqualTo(MonitorTypes.Missing));
            Assert.That(response.DefaultNewItemMonitorOption, Is.EqualTo(NewItemMonitorTypes.New));
        }

        [Test]
        public void format_specific_root_settings_should_take_precedence_over_legacy_defaults()
        {
            var resource = new RootFolderResource
            {
                Name = "Books",
                Path = "/books",
                DefaultQualityProfileId = 17,
                Audiobook = new MediaTypeSettingsResource { QualityProfileId = 31 },
                Ebook = new MediaTypeSettingsResource { QualityProfileId = 41 }
            };

            var model = RootFolderResourceMapper.ToModel(resource);

            Assert.That(model.GetAudiobookSettings().QualityProfileId, Is.EqualTo(31));
            Assert.That(model.GetEbookSettings().QualityProfileId, Is.EqualTo(41));
        }

        private static void AssertLegacyDefaults(MediaTypeSettings settings)
        {
            Assert.That(settings.QualityProfileId, Is.EqualTo(17));
            Assert.That(settings.MetadataProfileId, Is.EqualTo(23));
            Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
            Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(settings.Tags, Is.EquivalentTo(new[] { 5, 9 }));
        }
    }
}
