using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class SuggestedAuthorImportCoordinatorFixture
    {
        [Test]
        public void allowed_missing_settings_should_keep_a_complete_side_and_disable_an_incomplete_side()
        {
            var root = new RootFolder
            {
                Path = "/library".AsOsAgnostic(),
                FolderType = FolderType.Mixed
            };
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 10,
                MetadataProfileId = 20,
                Monitored = true
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                Monitored = true
            });

            var result = SuggestedAuthorImportCoordinator.TryBuildMonitoringConfig(
                new SuggestedAuthorImportConfigRequest
                {
                    AuthorName = "Test Author",
                    FilePaths = new List<string>
                    {
                        "/library/Test Author/Audio.mp3".AsOsAgnostic(),
                        "/library/Test Author/Text.epub".AsOsAgnostic()
                    },
                    FixedRootFolder = root,
                    AllowMissingMediaSettings = true
                },
                null,
                null,
                null,
                out var config,
                out var error);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True, error);
                Assert.That(config.CreateAudiobook, Is.True);
                Assert.That(config.AudiobookRootFolderPath, Is.EqualTo(root.Path));
                Assert.That(config.CreateEbook, Is.False);
                Assert.That(config.EbookRootFolderPath, Is.Null);
            });
        }

        [Test]
        public void allowed_missing_settings_should_fail_when_no_detected_side_is_configured()
        {
            var root = new RootFolder
            {
                Path = "/library".AsOsAgnostic(),
                FolderType = FolderType.Mixed
            };

            var result = SuggestedAuthorImportCoordinator.TryBuildMonitoringConfig(
                new SuggestedAuthorImportConfigRequest
                {
                    AuthorName = "Test Author",
                    FilePaths = new[] { "/library/Test Author/Text.epub".AsOsAgnostic() },
                    FixedRootFolder = root,
                    AllowMissingMediaSettings = true
                },
                null,
                null,
                null,
                out var config,
                out var error);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(config, Is.Null);
                Assert.That(error, Does.Contain("complete quality and metadata profile defaults"));
            });
        }
    }
}
