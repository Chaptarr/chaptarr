using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.ClientSchema;
using DryIoc;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.ImportLists.LazyLibrarianImport;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider.Events;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListBulkCommonSettingsFixture
    {
        private static readonly Type[] Providers =
        {
            typeof(HardcoverLibraryImportList), typeof(GoodreadsBookshelf), typeof(GoodreadsListImportList),
            typeof(GoodreadsSeriesImportList), typeof(ChaptarrImport), typeof(LazyLibrarianImport)
        };
        public static IEnumerable<TestCaseData> ProviderDatabases => Enumerable.Range(0, Providers.Length)
            .SelectMany(provider => new[] { false, true }.Select(postgres => new TestCaseData(provider, postgres)));
        public static IEnumerable<TestCaseData> CompatibleSelections => new[]
        {
            (1, FolderType.Audiobook, ProfileType.Audiobook, MetadataProfileType.Audiobook),
            (2, FolderType.Ebook, ProfileType.Ebook, MetadataProfileType.Ebook),
            (3, FolderType.Audiobook, ProfileType.Audiobook, MetadataProfileType.Audiobook),
            (3, FolderType.Ebook, ProfileType.Ebook, MetadataProfileType.Ebook),
            (3, FolderType.Mixed, ProfileType.Audiobook, MetadataProfileType.General),
            (3, FolderType.Mixed, ProfileType.Ebook, MetadataProfileType.General),
            (1, FolderType.Mixed, ProfileType.Audiobook, MetadataProfileType.General),
            (2, FolderType.Mixed, ProfileType.Ebook, MetadataProfileType.General)
        }.SelectMany(selection => new[] { false, true }.Select(postgres => new TestCaseData(postgres, selection.Item1, selection.Item2, selection.Item3, selection.Item4)));
        public static IEnumerable<TestCaseData> InvalidSelections => new[]
        {
            "missingQuality", "negativeQuality", "missingMetadata", "negativeMetadata", "missingRoot", "emptyRoot", "whitespaceRoot",
            "unconfiguredMixed", "incompatibleRoot", "incompatibleQuality", "incompatibleMetadata"
        }.SelectMany(selection => new[] { false, true }.Select(postgres => new TestCaseData(postgres, selection)));

        [OneTimeSetUp]
        public void InitializeSchemaBuilder()
        {
            using var container = new Container();
            container.RegisterInstance<ILocalizationService>(InterfaceProxy.Proxy<ILocalizationService>((method, args) => args[0]));
            SchemaBuilder.Initialize(container);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_enable_rejects_missing_creation_roots_without_saving(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var list = h.Insert(0, 1, addMissing: true);
                list.EnableAutomaticAdd = false;
                ((DualMediaImportListSettings)list.Settings).AudiobookRootFolderPath = null;
                h.Repository.Update(list);
                Assert.Throws<ValidationException>(() => h.Save(new ImportListBulkResource { EnableAutomaticAdd = true }, list));
                Assert.That(h.Repository.Get(list.Id).EnableAutomaticAdd, Is.False);
                Assert.That(h.Repository.BulkWrites, Is.Zero);
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void explicit_bulk_edit_should_reach_persisted_common_settings_and_sync_builder(int provider, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var definition = h.Insert(provider, 1, addMissing: false);
                var original = (DualMediaImportListSettings)definition.Settings;
                var options = definition.GetMonitoringOptions().ToJson();
                h.Save(new ImportListBulkResource { EnableAutomaticAdd = false, RootFolderPath = h.NewAudio, QualityProfileId = 21, MetadataProfileId = 22 }, definition);
                var stored = h.Repository.Get(definition.Id);
                var settings = (DualMediaImportListSettings)stored.Settings;
                var config = h.Build(stored);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.EnableAutomaticAdd, Is.False);
                    Assert.That(settings.AudiobookRootFolderPath, Is.EqualTo(h.NewAudio));
                    Assert.That(settings.AudiobookQualityProfileId, Is.EqualTo(21));
                    Assert.That(settings.AudiobookMetadataProfileId, Is.EqualTo(22));
                    Assert.That(config.AudiobookRootFolderPath, Is.EqualTo(h.NewAudio));
                    Assert.That(config.AudiobookQualityProfileId, Is.EqualTo(21));
                    Assert.That(config.AudiobookMetadataProfileId, Is.EqualTo(22));
                    Assert.That(stored.RootFolderPath, Is.EqualTo(h.OldAudio));
                    Assert.That(stored.QualityProfileId, Is.EqualTo(11));
                    Assert.That(stored.MetadataProfileId, Is.EqualTo(12));
                    Assert.That(stored.GetMonitoringOptions().ToJson(), Is.EqualTo(options));
                    Assert.That(settings.MonitorEbooks, Is.False);
                    Assert.That(settings.EbookRootFolderPath, Is.EqualTo(h.OldEbook));
                    Assert.That(settings.EbookQualityProfileId, Is.EqualTo(31));
                    Assert.That(settings.EbookMetadataProfileId, Is.EqualTo(32));
                    Assert.That(settings.EbookTags, Is.EqualTo(original.EbookTags));
                    Assert.That(settings.AudiobookTags, Is.EqualTo(original.AudiobookTags));
                    Assert.That(stored.Tags, Is.EquivalentTo(definition.Tags));
                    Assert.That(config.AudiobookMonitored, Is.False);
                    Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                    Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(h.Repository.BulkWrites, Is.EqualTo(1));
                    Assert.That(h.UpdatedEvents, Has.Count.EqualTo(1));
                });
            });
        }

        [TestCaseSource(nameof(CompatibleSelections))]
        public void typed_selections_should_change_only_enabled_compatible_formats(bool postgres, int sides, FolderType rootType, ProfileType qualityType, MetadataProfileType metadataType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var definition = h.Insert(4, sides, addMissing: true);
                var options = definition.GetMonitoringOptions().ToJson();
                var root = rootType == FolderType.Mixed ? h.Mixed : rootType == FolderType.Audiobook ? h.NewAudio : h.NewEbook;
                var quality = qualityType == ProfileType.Audiobook ? 21 : 41;
                var metadata = metadataType == MetadataProfileType.General ? 99 : metadataType == MetadataProfileType.Audiobook ? 22 : 42;
                h.Save(new ImportListBulkResource { RootFolderPath = root, QualityProfileId = quality, MetadataProfileId = metadata }, definition);
                var stored = h.Repository.Get(definition.Id);
                var settings = (DualMediaImportListSettings)stored.Settings;
                var audio = (sides & 1) != 0;
                var ebook = (sides & 2) != 0;
                Assert.Multiple(() =>
                {
                    Assert.That(settings.AudiobookRootFolderPath, Is.EqualTo(audio && rootType != FolderType.Ebook ? root : h.OldAudio));
                    Assert.That(settings.EbookRootFolderPath, Is.EqualTo(ebook && rootType != FolderType.Audiobook ? root : h.OldEbook));
                    Assert.That(settings.AudiobookQualityProfileId, Is.EqualTo(audio && qualityType == ProfileType.Audiobook ? quality : 11));
                    Assert.That(settings.EbookQualityProfileId, Is.EqualTo(ebook && qualityType == ProfileType.Ebook ? quality : 31));
                    Assert.That(settings.AudiobookMetadataProfileId, Is.EqualTo(audio && metadataType != MetadataProfileType.Ebook ? metadata : 12));
                    Assert.That(settings.EbookMetadataProfileId, Is.EqualTo(ebook && metadataType != MetadataProfileType.Audiobook ? metadata : 32));
                    Assert.That(settings.MonitorAudiobooks, Is.EqualTo(audio));
                    Assert.That(settings.MonitorEbooks, Is.EqualTo(ebook));
                    Assert.That(stored.GetMonitoringOptions().ToJson(), Is.EqualTo(options));
                    Assert.That(settings.AudiobookTags, Is.EqualTo(new[] { 101 }));
                    Assert.That(settings.EbookTags, Is.EqualTo(new[] { 102 }));
                    Assert.That(h.RootReads, Is.EqualTo(1));
                    Assert.That(h.QualityReads, Is.EqualTo(1));
                    Assert.That(h.MetadataReads, Is.EqualTo(1));
                });
                var config = h.Build(stored);
                Assert.That(config.CreateAudiobook, Is.EqualTo(audio));
                Assert.That(config.CreateEbook, Is.EqualTo(ebook));
                Assert.That(config.AudiobookQualityProfileId, Is.EqualTo(audio ? (int?)settings.AudiobookQualityProfileId : null));
                Assert.That(config.EbookQualityProfileId, Is.EqualTo(ebook ? (int?)settings.EbookQualityProfileId : null));
            });
        }

        [TestCaseSource(nameof(InvalidSelections))]
        public void invalid_or_incompatible_selection_should_reject_whole_request_before_mutation_or_write(bool postgres, string selection)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.Insert(4, 1, addMissing: false);
                var second = h.Insert(0, selection == "unconfiguredMixed" ? 3 : 2, addMissing: true);
                var before = new[] { Snapshot(first), Snapshot(second) };
                var payload = new ImportListBulkResource { EnableAutomaticAdd = false };
                var field = selection switch
                {
                    "missingQuality" or "negativeQuality" or "incompatibleQuality" => nameof(payload.QualityProfileId),
                    "missingMetadata" or "negativeMetadata" or "incompatibleMetadata" => nameof(payload.MetadataProfileId),
                    _ => nameof(payload.RootFolderPath)
                };
                switch (selection)
                {
                    case "missingQuality": payload.QualityProfileId = 999999; break;
                    case "negativeQuality": payload.QualityProfileId = -1; break;
                    case "missingMetadata": payload.MetadataProfileId = 999999; break;
                    case "negativeMetadata": payload.MetadataProfileId = -1; break;
                    case "missingRoot": payload.RootFolderPath = @"C:\Absent".AsOsAgnostic(); break;
                    case "emptyRoot": payload.RootFolderPath = string.Empty; break;
                    case "whitespaceRoot": payload.RootFolderPath = " \t "; break;
                    case "unconfiguredMixed": payload.RootFolderPath = h.PartialMixed; break;
                    case "incompatibleRoot": payload.RootFolderPath = h.NewAudio; break;
                    case "incompatibleQuality": payload.QualityProfileId = 21; break;
                    case "incompatibleMetadata": payload.MetadataProfileId = 22; break;
                }
                var failure = Assert.Throws<ValidationException>(() => h.Save(payload, first, second));
                Assert.Multiple(() =>
                {
                    Assert.That(failure.Errors.Select(error => error.PropertyName), Does.Contain(field));
                    Assert.That(h.Repository.BulkWrites, Is.Zero);
                    Assert.That(h.UpdatedEvents, Is.Empty);
                    Assert.That(h.Loaded.OrderBy(definition => definition.Id).Select(Snapshot), Is.EqualTo(before));
                    Assert.That(new[] { Snapshot(h.Repository.Get(first.Id)), Snapshot(h.Repository.Get(second.Id)) }, Is.EqualTo(before));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_zero_should_clear_enabled_overrides_without_catalog_reads_and_use_root_defaults(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var definition = h.Insert(4, 3, addMissing: false);
                h.Save(new ImportListBulkResource { QualityProfileId = 0, MetadataProfileId = 0 }, definition);
                var stored = h.Repository.Get(definition.Id);
                var settings = (DualMediaImportListSettings)stored.Settings;
                Assert.Multiple(() =>
                {
                    Assert.That(settings.AudiobookQualityProfileId, Is.Zero);
                    Assert.That(settings.EbookQualityProfileId, Is.Zero);
                    Assert.That(settings.AudiobookMetadataProfileId, Is.Zero);
                    Assert.That(settings.EbookMetadataProfileId, Is.Zero);
                    Assert.That(h.QualityReads + h.MetadataReads + h.RootReads, Is.Zero);
                    Assert.That(stored.GetMonitoringOptions().AddMissingAuthors, Is.False);
                });
                var config = h.Build(stored);
                Assert.Multiple(() =>
                {
                    Assert.That(config.AudiobookQualityProfileId, Is.EqualTo(21));
                    Assert.That(config.AudiobookMetadataProfileId, Is.EqualTo(22));
                    Assert.That(config.EbookQualityProfileId, Is.EqualTo(41));
                    Assert.That(config.EbookMetadataProfileId, Is.EqualTo(42));
                    Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                    Assert.That(config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.Existing));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void omitted_creation_fields_should_preserve_retained_values_without_catalog_reads(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var definition = h.Insert(4, 3, addMissing: false);
                var before = Snapshot(definition);
                h.Save(new ImportListBulkResource { EnableAutomaticAdd = false }, definition);
                definition.EnableAutomaticAdd = false;
                Assert.Multiple(() =>
                {
                    Assert.That(Snapshot(h.Repository.Get(definition.Id)), Is.EqualTo(Snapshot(definition)));
                    Assert.That(Snapshot(definition), Is.Not.EqualTo(before));
                    Assert.That(h.QualityReads + h.MetadataReads + h.RootReads, Is.Zero);
                    Assert.That(h.Repository.BulkWrites, Is.EqualTo(1));
                });
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 64)]
        [TestCase(true, 64)]
        public void compatible_bulk_should_use_at_most_three_snapshots_and_one_existing_writer(bool postgres, int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var definitions = Enumerable.Range(0, count).Select(index => h.Insert(index % Providers.Length, 3, index % 2 == 0)).ToArray();
                h.Save(new ImportListBulkResource { RootFolderPath = h.Mixed, QualityProfileId = 21, MetadataProfileId = 99 }, definitions);
                Assert.Multiple(() =>
                {
                    Assert.That(h.RootReads, Is.EqualTo(1));
                    Assert.That(h.QualityReads, Is.EqualTo(1));
                    Assert.That(h.MetadataReads, Is.EqualTo(1));
                    Assert.That(h.Repository.BulkWrites, Is.EqualTo(1));
                    Assert.That(h.UpdatedEvents, Has.Count.EqualTo(count));
                });
                var stored = h.Repository.Get(definitions.Select(definition => definition.Id)).ToList();
                foreach (var definition in stored)
                {
                    var settings = (DualMediaImportListSettings)definition.Settings;
                    Assert.Multiple(() =>
                    {
                        Assert.That(settings.AudiobookRootFolderPath, Is.EqualTo(h.Mixed));
                        Assert.That(settings.EbookRootFolderPath, Is.EqualTo(h.Mixed));
                        Assert.That(settings.AudiobookQualityProfileId, Is.EqualTo(21));
                        Assert.That(settings.EbookQualityProfileId, Is.EqualTo(31));
                        Assert.That(settings.AudiobookMetadataProfileId, Is.EqualTo(99));
                        Assert.That(settings.EbookMetadataProfileId, Is.EqualTo(99));
                    });
                }
            });
        }

        private static string Snapshot(ImportListDefinition definition)
        {
            var settings = (DualMediaImportListSettings)definition.Settings;
            return new
            {
                definition.EnableAutomaticAdd, definition.RootFolderPath, definition.QualityProfileId, definition.MetadataProfileId,
                definition.MonitoringOptions, definition.Tags, settings.MonitorAudiobooks, settings.MonitorEbooks,
                settings.AudiobookRootFolderPath, settings.EbookRootFolderPath, settings.AudiobookQualityProfileId,
                settings.EbookQualityProfileId, settings.AudiobookMetadataProfileId, settings.EbookMetadataProfileId,
                settings.AudiobookTags, settings.EbookTags
            }.ToJson();
        }

        private sealed class CountedRepository : ImportListRepository
        {
            public int BulkWrites;
            public CountedRepository(IMainDatabase database, IEventAggregator events) : base(database, events) { }
            public override void UpdateMany(IList<ImportListDefinition> definitions)
            {
                BulkWrites++;
                base.UpdateMany(definitions);
            }
        }

        private sealed class Harness : IServiceProvider
        {
            private readonly Dictionary<Type, object> _services = new();
            private readonly IRootFolderService _roots;
            private readonly IEventAggregator _events;
            public readonly CountedRepository Repository;
            public readonly ImportListController Controller;
            public readonly List<ProviderUpdatedEvent<IImportList>> UpdatedEvents = new();
            public List<ImportListDefinition> Loaded = new();
            public readonly string OldAudio = @"C:\OldAudio".AsOsAgnostic();
            public readonly string NewAudio = @"C:\NewAudio".AsOsAgnostic();
            public readonly string OldEbook = @"C:\OldEbook".AsOsAgnostic();
            public readonly string NewEbook = @"C:\NewEbook".AsOsAgnostic();
            public readonly string Mixed = @"C:\Mixed".AsOsAgnostic();
            public readonly string PartialMixed = @"C:\PartialMixed".AsOsAgnostic();
            public int RootReads;
            public int QualityReads;
            public int MetadataReads;
            private int _nextName;

            public Harness(IMainDatabase database)
            {
                var logger = LogManager.GetCurrentClassLogger();
                _events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                {
                    if (args.FirstOrDefault() is ProviderUpdatedEvent<IImportList> updated) UpdatedEvents.Add(updated);
                    return null;
                });
                Repository = new CountedRepository(database, _events);
                var rootRepository = new RootFolderRepository(database, _events);
                foreach (var item in new[] { (OldAudio, FolderType.Audiobook), (NewAudio, FolderType.Audiobook),
                    (OldEbook, FolderType.Ebook), (NewEbook, FolderType.Ebook), (Mixed, FolderType.Mixed), (PartialMixed, FolderType.Mixed) })
                {
                    var root = new RootFolder { Name = item.Item1, Path = item.Item1, FolderType = item.Item2 };
                    if (item.Item2 != FolderType.Ebook) root.SetAudiobookSettings(new MediaTypeSettings
                    {
                        QualityProfileId = 21, MetadataProfileId = 22, Monitored = true,
                        MonitorExistingMode = MonitorTypes.All, MonitorNewItems = NewItemMonitorTypes.All, Tags = new List<int> { 201 }
                    });
                    if (item.Item2 != FolderType.Audiobook && item.Item1 != PartialMixed) root.SetEbookSettings(new MediaTypeSettings
                    {
                        QualityProfileId = 41, MetadataProfileId = 42, Monitored = false,
                        MonitorExistingMode = MonitorTypes.None, MonitorNewItems = NewItemMonitorTypes.None, Tags = new List<int> { 202 }
                    });
                    rootRepository.Insert(root);
                }
                var rootService = new RootFolderService(rootRepository, null, null, null, logger);
                _roots = InterfaceProxy.Proxy<IRootFolderService>((method, args) =>
                {
                    if (method.Name == "All") RootReads++;
                    return Invoke(method, rootService, args);
                });
                var quality = InterfaceProxy.Proxy<IQualityProfileService>((method, args) =>
                {
                    if (method.Name != "All") throw new InvalidOperationException(method.Name);
                    QualityReads++;
                    return new List<QualityProfile>
                    {
                        new() { Id = 11, ProfileType = ProfileType.Audiobook }, new() { Id = 21, ProfileType = ProfileType.Audiobook },
                        new() { Id = 31, ProfileType = ProfileType.Ebook }, new() { Id = 41, ProfileType = ProfileType.Ebook }
                    };
                });
                var metadata = InterfaceProxy.Proxy<IMetadataProfileService>((method, args) =>
                {
                    if (method.Name != "All") throw new InvalidOperationException(method.Name);
                    MetadataReads++;
                    return new List<MetadataProfile>
                    {
                        new() { Id = 12, ProfileType = MetadataProfileType.Audiobook }, new() { Id = 22, ProfileType = MetadataProfileType.Audiobook },
                        new() { Id = 32, ProfileType = MetadataProfileType.Ebook }, new() { Id = 42, ProfileType = MetadataProfileType.Ebook },
                        new() { Id = 99, ProfileType = MetadataProfileType.General }
                    };
                });
                var config = InterfaceProxy.Proxy<IConfigService>((method, args) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var status = InterfaceProxy.Proxy<IImportListStatusService>((method, args) => throw new InvalidOperationException(method.Name));
                Add(logger); Add(_roots); Add(config); Add(status);
                Add(new Lazy<IRootFolderService>(() => _roots));
                Add(new Lazy<IQualityProfileService>(() => quality));
                Add(new Lazy<IMetadataProfileService>(() => metadata));
                var providers = Providers.Select(type => (IImportList)Construct(type)).ToArray();
                foreach (var provider in providers) _services[provider.GetType()] = provider;
                var factory = new ImportListFactory(status, Repository, providers, this, _events,
                    InterfaceProxy.Proxy<IHardcoverIdentityService>((method, args) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null), config, logger);
                Add(InterfaceProxy.Proxy<IImportListFactory>((method, args) =>
                {
                    var result = Invoke(method, factory, args);
                    if (method.Name == "Get" && args[0] is IEnumerable<int>) Loaded = ((IEnumerable<ImportListDefinition>)result).ToList();
                    return result;
                }));
                Controller = (ImportListController)Construct(typeof(ImportListController));
                Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            }

            public object GetService(Type type) => _services.TryGetValue(type, out var service) ? service : null;
            private void Add<T>(T service) => _services[typeof(T)] = service;
            private object Construct(Type type) => type.GetConstructors().Single().Invoke(type.GetConstructors().Single().GetParameters().Select(parameter => GetService(parameter.ParameterType)).ToArray());
            private static object Invoke(MethodInfo method, object target, object[] args)
            {
                try { return method.Invoke(target, args); }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }

            public ImportListDefinition Insert(int provider, int sides, bool addMissing)
            {
                var instance = (IImportList)GetService(Providers[provider]);
                var settings = (DualMediaImportListSettings)Activator.CreateInstance(instance.ConfigContract);
                settings.MonitorAudiobooks = (sides & 1) != 0;
                settings.MonitorEbooks = (sides & 2) != 0;
                settings.AudiobookRootFolderPath = OldAudio; settings.EbookRootFolderPath = OldEbook;
                settings.AudiobookQualityProfileId = 11; settings.AudiobookMetadataProfileId = 12;
                settings.EbookQualityProfileId = 31; settings.EbookMetadataProfileId = 32;
                settings.AudiobookTags = new List<int> { 101 }; settings.EbookTags = new List<int> { 102 };
                return Repository.Insert(new ImportListDefinition
                {
                    Name = "Bulk common " + provider + " " + ++_nextName,
                    Implementation = Providers[provider].Name, Settings = settings, EnableAutomaticAdd = true,
                    RootFolderPath = OldAudio, QualityProfileId = 11, MetadataProfileId = 12, Tags = new HashSet<int> { 100 },
                    MonitoringOptions = new ImportListMonitoringOptions
                    {
                        AddMissingAuthors = addMissing, AudiobookMonitored = false, EbookMonitored = true,
                        AudiobookMonitor = MonitorTypes.Missing, EbookMonitor = MonitorTypes.Existing,
                        AudiobookMonitorNewItems = NewItemMonitorTypes.New, EbookMonitorNewItems = NewItemMonitorTypes.None
                    }
                });
            }

            public void Save(ImportListBulkResource resource, params ImportListDefinition[] definitions)
            {
                resource.Ids = definitions.Select(definition => definition.Id).ToList();
                var result = Controller.UpdateProvider(resource);
                Assert.That(result.Result, Is.TypeOf<AcceptedResult>());
            }

            public MonitoringConfig Build(ImportListDefinition definition)
            {
                var service = new ImportListSyncService(null, null, null, null, null, null, null, null,
                    _roots, new RootFolderSettingsResolver(_roots), _events, null, LogManager.GetCurrentClassLogger(), null);
                return (MonitoringConfig)typeof(ImportListSyncService).GetMethod("BuildConfigFromImportList", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(service, new object[] { definition, "Bulk common fixture author", null });
            }
        }
    }
}
