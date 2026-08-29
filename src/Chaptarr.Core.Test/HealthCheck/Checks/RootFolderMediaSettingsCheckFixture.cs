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
    public class RootFolderMediaSettingsCheckFixture
    {
        private class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> RootFolders { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IRootFolderService.All) => RootFolders,
                    _ => throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}")
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
                    "RootFolderMediaSettingsIncomplete" => "Root folder media defaults are incomplete: {0}. Configure both a quality profile and metadata profile for each listed format; Chaptarr will skip that format until it is complete.",
                    "NoConfiguredMediaType" => "no configured media type",
                    _ => phrase
                };
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return GetLocalizedString(phrase);
            }
        }

        [Test]
        public void should_be_healthy_for_complete_single_type_and_mixed_roots()
        {
            var audiobook = new RootFolder { Name = "Audio", FolderType = FolderType.Audiobook };
            audiobook.SetAudiobookSettings(CompleteSettings());

            var mixed = new RootFolder { Name = "Mixed", FolderType = FolderType.Mixed };
            mixed.SetEbookSettings(CompleteSettings());

            var result = CreateSubject(audiobook, mixed).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void should_warn_for_each_required_or_partially_configured_side()
        {
            var audiobook = new RootFolder { Name = "Audio", FolderType = FolderType.Audiobook };
            audiobook.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1 });

            var mixed = new RootFolder { Name = "Mixed", FolderType = FolderType.Mixed };
            mixed.SetEbookSettings(new MediaTypeSettings { MetadataProfileId = 2 });

            var result = CreateSubject(audiobook, mixed).Check();

            Assert.Multiple(() =>
            {
                Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
                Assert.That(result.Message, Does.Contain("Audio (Audiobook)"));
                Assert.That(result.Message, Does.Contain("Mixed (Ebook)"));
                Assert.That(result.Message, Does.Contain("skip that format"));
            });
        }

        [Test]
        public void mixed_root_should_not_require_an_unconfigured_second_side()
        {
            var mixed = new RootFolder { Name = "Mixed", FolderType = FolderType.Mixed };
            mixed.SetAudiobookSettings(CompleteSettings());

            var result = CreateSubject(mixed).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void mixed_root_should_warn_when_neither_side_is_configured()
        {
            var mixed = new RootFolder { Name = "Mixed", FolderType = FolderType.Mixed };

            var result = CreateSubject(mixed).Check();

            Assert.Multiple(() =>
            {
                Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
                Assert.That(result.Message, Does.Contain("Mixed (no configured media type)"));
            });
        }

        private static MediaTypeSettings CompleteSettings()
        {
            return new MediaTypeSettings
            {
                QualityProfileId = 1,
                MetadataProfileId = 2
            };
        }

        private static RootFolderMediaSettingsCheck CreateSubject(params RootFolder[] rootFolders)
        {
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolders = new List<RootFolder>(rootFolders);

            return new RootFolderMediaSettingsCheck(
                rootFolderService,
                new RootFolderSettingsResolver(rootFolderService),
                new StubLocalizationService());
        }
    }
}
