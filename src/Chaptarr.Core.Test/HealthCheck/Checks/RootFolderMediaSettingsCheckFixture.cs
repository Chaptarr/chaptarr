using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
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
            mixed.SetAudiobookSettings(CompleteSettings());
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
        public void mixed_root_should_require_an_unconfigured_second_side()
        {
            var mixed = new RootFolder { Name = "Mixed", FolderType = FolderType.Mixed };
            mixed.SetAudiobookSettings(CompleteSettings());

            var result = CreateSubject(mixed).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(result.Message, Does.Contain("Mixed (Ebook)"));
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

        private class CheckDependency : DispatchProxy
        {
            public Func<MethodInfo, object[], object> InvokeMethod { get; set; }

            public static T Create<T>(Func<MethodInfo, object[], object> invoke)
                where T : class
            {
                var proxy = DispatchProxy.Create<T, CheckDependency>();
                ((CheckDependency)(object)proxy).InvokeMethod = invoke;
                return proxy;
            }

            protected override object Invoke(MethodInfo method, object[] args) => InvokeMethod(method, args);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void root_check_should_read_roots_once_and_check_both_media_paths(bool ebookMissing)
        {
            var audio = @"C:\Audiobooks".AsOsAgnostic();
            var ebook = @"C:\Ebooks".AsOsAgnostic();
            var roots = new List<RootFolder> { new() { Path = audio }, new() { Path = ebook } };
            var rootReads = 0;
            var repository = CheckDependency.Create<IRootFolderRepository>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IRootFolderRepository.All)));
                rootReads++;
                return roots;
            });
            var rootService = new RootFolderService(repository, null, null, null, LogManager.GetCurrentClassLogger());
            var authors = CheckDependency.Create<IAuthorService>((method, args) =>
                new[] { "Ernest Cline", "Frank Herbert" }.SelectMany((name, index) =>
                    new[] { audio, ebook }.Select(path => new KeyValuePair<int, string>(index + 1, Path.Combine(path, name)))).ToList());
            var probed = new List<string>();
            var disk = CheckDependency.Create<IDiskProvider>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.FolderExists)));
                probed.Add((string)args[0]);
                return !ebookMissing || !((string)args[0]).PathEquals(ebook);
            });
            var lists = CheckDependency.Create<IImportListFactory>((method, args) => new List<ImportListDefinition>());

            var result = new RootFolderCheck(authors, lists, disk, rootService, new StubLocalizationService()).Check();

            Assert.Multiple(() =>
            {
                Assert.That(rootReads, Is.EqualTo(1));
                Assert.That(probed, Is.EquivalentTo(new[] { audio, ebook }));
                Assert.That(result.Type, Is.EqualTo(ebookMissing ? HealthCheckResult.Error : HealthCheckResult.Ok));
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
