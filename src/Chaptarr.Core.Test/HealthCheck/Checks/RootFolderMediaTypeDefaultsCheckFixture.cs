using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class RootFolderMediaTypeDefaultsCheckFixture
    {
        private class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> RootFolders { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IRootFolderService.All) => RootFolders,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private sealed class StubLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                return phrase switch
                {
                    "RootFolderMissingMediaTypeDefaultsSingleMessage" => "Root folder is missing media type defaults and will silently fail to add new authors/books: {0}",
                    "RootFolderMissingMediaTypeDefaultsMultipleMessage" => "Root folders are missing media type defaults and will silently fail to add new authors/books: {0}",
                    _ => phrase
                };
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return GetLocalizedString(phrase);
            }
        }

        private static RootFolder MixedRootFolder(string path, bool hasAudiobookSettings, bool hasEbookSettings)
        {
            var rootFolder = new RootFolder { Path = path, FolderType = FolderType.Mixed };
            SetSettings(rootFolder, hasAudiobookSettings, hasEbookSettings);
            return rootFolder;
        }

        private static void SetSettings(RootFolder rootFolder, bool hasAudiobookSettings, bool hasEbookSettings)
        {
            if (hasAudiobookSettings)
            {
                rootFolder.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1, MonitorExisting = 1 });
            }

            if (hasEbookSettings)
            {
                rootFolder.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1, MonitorExisting = 1 });
            }
        }

        private static RootFolderMediaTypeDefaultsCheck CreateSubject(params RootFolder[] rootFolders)
        {
            var proxy = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)proxy).RootFolders = new List<RootFolder>(rootFolders);

            return new RootFolderMediaTypeDefaultsCheck(proxy, new StubLocalizationService());
        }

        [Test]
        public void should_be_healthy_when_mixed_root_folder_has_both_defaults_configured()
        {
            var result = CreateSubject(MixedRootFolder(@"C:\books", true, true)).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void should_warn_when_mixed_root_folder_is_missing_audiobook_defaults()
        {
            var result = CreateSubject(MixedRootFolder(@"C:\books", false, true)).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(result.Message, Does.Contain(@"C:\books"));
        }

        [Test]
        public void should_warn_when_mixed_root_folder_is_missing_ebook_defaults()
        {
            var result = CreateSubject(MixedRootFolder(@"C:\books", true, false)).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(result.Message, Does.Contain(@"C:\books"));
        }

        [Test]
        public void should_ignore_missing_ebook_defaults_on_an_audiobook_only_root_folder()
        {
            var rootFolder = new RootFolder { Path = @"C:\audiobooks", FolderType = FolderType.Audiobook };
            SetSettings(rootFolder, hasAudiobookSettings: true, hasEbookSettings: false);

            var result = CreateSubject(rootFolder).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void should_ignore_missing_audiobook_defaults_on_an_ebook_only_root_folder()
        {
            var rootFolder = new RootFolder { Path = @"C:\ebooks", FolderType = FolderType.Ebook };
            SetSettings(rootFolder, hasAudiobookSettings: false, hasEbookSettings: true);

            var result = CreateSubject(rootFolder).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void should_warn_when_audiobook_settings_exist_but_have_no_quality_profile()
        {
            var rootFolder = new RootFolder { Path = @"C:\books", FolderType = FolderType.Mixed };
            rootFolder.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = null, MetadataProfileId = 1, MonitorExisting = 1 });
            rootFolder.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1, MonitorExisting = 1 });

            var result = CreateSubject(rootFolder).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
        }

        [Test]
        public void should_warn_when_ebook_settings_exist_but_have_no_metadata_profile()
        {
            var rootFolder = new RootFolder { Path = @"C:\books", FolderType = FolderType.Mixed };
            rootFolder.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1, MonitorExisting = 1 });
            rootFolder.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 0, MonitorExisting = 1 });

            var result = CreateSubject(rootFolder).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
        }

        [Test]
        public void should_warn_when_stored_settings_json_is_corrupt()
        {
            var rootFolder = new RootFolder { Path = @"C:\books", FolderType = FolderType.Audiobook };
            rootFolder.AudiobookSettings = "not valid json";

            var result = CreateSubject(rootFolder).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
        }

        [Test]
        public void should_list_every_incomplete_root_folder_in_a_combined_message()
        {
            var result = CreateSubject(
                MixedRootFolder(@"C:\books-one", false, true),
                MixedRootFolder(@"C:\books-two", true, false)).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(result.Message, Does.Contain(@"C:\books-one"));
            Assert.That(result.Message, Does.Contain(@"C:\books-two"));
        }
    }
}
