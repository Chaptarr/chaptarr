using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.ClientSchema;
using DryIoc;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.ImportLists.LazyLibrarianImport;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using HttpRequest = NzbDrone.Common.Http.HttpRequest;
using HttpResponse = NzbDrone.Common.Http.HttpResponse;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListCreationValidationFixture
    {
        private static readonly Type[] Providers =
        {
            typeof(HardcoverLibraryImportList), typeof(GoodreadsBookshelf),
            typeof(GoodreadsListImportList), typeof(GoodreadsSeriesImportList),
            typeof(ChaptarrImport), typeof(LazyLibrarianImport)
        };

        public static IEnumerable ProviderDatabases => Providers.SelectMany(provider => new[] { false, true }
            .Select(postgres => new TestCaseData(provider, postgres)));

        [OneTimeSetUp]
        public void InitializeSchemaBuilder()
        {
            using var container = new Container();
            container.RegisterInstance<ILocalizationService>(InterfaceProxy.Proxy<ILocalizationService>((method, args) => args[0]));
            SchemaBuilder.Initialize(container);
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public void modern_listed_only_should_project_legacy_value_and_allow_legacy_all_roundtrip(bool postgres, int sides)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, typeof(GoodreadsBookshelf));
                var definition = h.Definition(addMissing: false);
                definition.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                var settings = (DualMediaImportListSettings)definition.Settings;
                settings.MonitorAudiobooks = (sides & 1) != 0;
                settings.MonitorEbooks = (sides & 2) != 0;
                definition.MonitoringOptions.AudiobookMonitor = MonitorTypes.All;
                definition.MonitoringOptions.EbookMonitor = MonitorTypes.All;
                definition.MonitoringOptions.AudiobookMonitorListedBooks = true;
                definition.MonitoringOptions.EbookMonitorListedBooks = true;
                var saved = h.Factory.Create(definition);
                var json = JObject.Parse(STJson.ToJson(h.Resource(saved)));
                json.Remove("shouldMonitor");
                var modern = STJson.Deserialize<ImportListResource>(json.ToString());
                if ((sides & 1) != 0) modern.MonitoringOptions.AudiobookMonitor = MonitorTypes.SpecificBook;
                if ((sides & 2) != 0) modern.MonitoringOptions.EbookMonitor = MonitorTypes.SpecificBook;
                h.Controller.UpdateProvider(modern, forceSave: true);
                var readback = h.Repository.Get(saved.Id);
                Assert.That(readback.ShouldMonitor, Is.EqualTo(ImportListMonitorType.SpecificBook));
                Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo((sides & 1) != 0 ? MonitorTypes.SpecificBook : MonitorTypes.All));
                Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo((sides & 2) != 0 ? MonitorTypes.SpecificBook : MonitorTypes.All));
                h.ResetCounts();
                h.Controller.UpdateProvider(h.Resource(readback), forceSave: true);
                Assert.That(h.Updates, Is.Zero, "A GET/PUT roundtrip must not change a representable modern selection");
                var legacy = h.Resource(readback);
                legacy.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                h.Controller.UpdateProvider(legacy, forceSave: true);
                readback = h.Repository.Get(saved.Id);
                Assert.That(readback.ShouldMonitor, Is.EqualTo(ImportListMonitorType.EntireAuthor));
                Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                h.ResetCounts();
                h.Controller.UpdateProvider(h.Resource(readback), forceSave: true);
                Assert.That(h.Updates, Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void modern_nonrepresentable_policies_should_retain_legacy_value_and_survive_unchanged_save(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, typeof(GoodreadsBookshelf));
                foreach (var policy in new (MonitorTypes? Audio, MonitorTypes? Ebook, bool Listed)[]
                {
                    (MonitorTypes.SpecificBook, MonitorTypes.All, true),
                    (MonitorTypes.Missing, MonitorTypes.Missing, true),
                    (MonitorTypes.Existing, MonitorTypes.Existing, true),
                    (MonitorTypes.All, MonitorTypes.All, false),
                    (null, MonitorTypes.All, true)
                })
                {
                    var definition = h.Definition(addMissing: false);
                    definition.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                    definition.MonitoringOptions.AudiobookMonitor = MonitorTypes.All;
                    definition.MonitoringOptions.EbookMonitor = MonitorTypes.All;
                    definition.MonitoringOptions.AudiobookMonitorListedBooks = true;
                    definition.MonitoringOptions.EbookMonitorListedBooks = true;
                    var saved = h.Factory.Create(definition);
                    var json = JObject.Parse(STJson.ToJson(h.Resource(saved)));
                    json.Remove("shouldMonitor");
                    var resource = STJson.Deserialize<ImportListResource>(json.ToString());
                    resource.MonitoringOptions.AudiobookMonitor = policy.Audio;
                    resource.MonitoringOptions.EbookMonitor = policy.Ebook;
                    resource.MonitoringOptions.AudiobookMonitorListedBooks = policy.Listed;
                    resource.MonitoringOptions.EbookMonitorListedBooks = policy.Listed;
                    h.Controller.UpdateProvider(resource, forceSave: true);
                    var readback = h.Repository.Get(saved.Id);
                    Assert.That(readback.ShouldMonitor, Is.EqualTo(ImportListMonitorType.EntireAuthor));
                    Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(policy.Audio));
                    Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(policy.Ebook));
                    Assert.That(readback.MonitoringOptions.AudiobookMonitorListedBooks, Is.EqualTo(policy.Listed));
                    Assert.That(readback.MonitoringOptions.EbookMonitorListedBooks, Is.EqualTo(policy.Listed));
                    h.ResetCounts();
                    h.Controller.UpdateProvider(h.Resource(readback), forceSave: true);
                    Assert.That(h.Updates, Is.Zero, "An unrepresentable legacy projection must not normalize modern choices");
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void legacy_monitor_switch_should_translate_omitted_or_unchanged_modern_options(bool postgres, bool echoModern)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, typeof(GoodreadsBookshelf));
                var definition = h.Definition(addMissing: false);
                definition.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                definition.MonitoringOptions.AudiobookMonitor = MonitorTypes.All;
                definition.MonitoringOptions.EbookMonitor = MonitorTypes.All;
                var saved = h.Factory.Create(definition);
                var resource = h.Resource(saved);
                resource.ShouldMonitor = ImportListMonitorType.SpecificBook;
                if (!echoModern) resource.MonitoringOptions = null;
                h.Controller.UpdateProvider(resource, forceSave: true);
                var readback = h.Repository.Get(saved.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(readback.ShouldMonitor, Is.EqualTo(ImportListMonitorType.SpecificBook));
                    Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.SpecificBook));
                    Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.SpecificBook));
                    Assert.That(readback.MonitoringOptions.AudiobookMonitorListedBooks, Is.True);
                    Assert.That(readback.MonitoringOptions.EbookMonitorListedBooks, Is.True);
                    Assert.That(readback.MonitoringOptions.AddMissingAuthors, Is.False);
                    Assert.That(h.SyncCommands, Has.Count.EqualTo(1));
                });
                h.ResetCounts();
                h.Controller.UpdateProvider(h.Resource(readback), forceSave: true);
                Assert.That(h.Updates, Is.Zero, "A repeated unchanged legacy save must not write or resync");

                var modernJson = JObject.Parse(STJson.ToJson(h.Resource(readback)));
                modernJson.Remove("shouldMonitor");
                var modern = STJson.Deserialize<ImportListResource>(modernJson.ToString());
                modern.MonitoringOptions.EbookMonitor = MonitorTypes.Existing;
                modern.MonitoringOptions.EbookMonitorListedBooks = false;
                h.Controller.UpdateProvider(modern, forceSave: true);
                readback = h.Repository.Get(saved.Id);
                Assert.That(readback.ShouldMonitor, Is.EqualTo(ImportListMonitorType.SpecificBook), "Omission must retain a non-default legacy value");
                Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(readback.MonitoringOptions.EbookMonitorListedBooks, Is.False);
                Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.SpecificBook));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_modern_side_should_win_while_legacy_switch_translates_other_side(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, typeof(GoodreadsBookshelf));
                var definition = h.Definition(addMissing: false);
                definition.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                definition.MonitoringOptions.AudiobookMonitor = MonitorTypes.All;
                definition.MonitoringOptions.EbookMonitor = MonitorTypes.All;
                var saved = h.Factory.Create(definition);
                var resource = h.Resource(saved);
                resource.ShouldMonitor = ImportListMonitorType.None;
                resource.MonitoringOptions.AudiobookMonitor = MonitorTypes.Missing;
                resource.MonitoringOptions.AudiobookMonitorListedBooks = true;
                h.Controller.UpdateProvider(resource, forceSave: true);
                var readback = h.Repository.Get(saved.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.Missing));
                    Assert.That(readback.MonitoringOptions.AudiobookMonitorListedBooks, Is.True);
                    Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.None));
                    Assert.That(readback.MonitoringOptions.EbookMonitorListedBooks, Is.False);
                });

                resource = h.Resource(readback);
                var json = JObject.Parse(STJson.ToJson(resource));
                json.Remove("shouldMonitor");
                resource = STJson.Deserialize<ImportListResource>(json.ToString());
                resource.MonitoringOptions.EbookMonitor = MonitorTypes.Existing;
                resource.MonitoringOptions.EbookMonitorListedBooks = true;
                h.Controller.UpdateProvider(resource, forceSave: true);
                readback = h.Repository.Get(saved.Id);
                Assert.That(readback.ShouldMonitor, Is.EqualTo(ImportListMonitorType.None));
                Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(readback.MonitoringOptions.EbookMonitorListedBooks, Is.True);
                Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.Missing));

                resource = h.Resource(readback);
                resource.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                resource.MonitoringOptions.AudiobookMonitor = null;
                resource.MonitoringOptions.AudiobookMonitorListedBooks = null;
                h.Controller.UpdateProvider(resource, forceSave: true);
                readback = h.Repository.Get(saved.Id);
                Assert.That(readback.MonitoringOptions.AudiobookMonitor, Is.Null, "Explicit inheritance is a modern edit");
                Assert.That(readback.MonitoringOptions.AudiobookMonitorListedBooks, Is.Null);
                Assert.That(readback.MonitoringOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
            });
        }

        [TestCase(ImportListMonitorType.None, MonitorTypes.None, false)]
        [TestCase(ImportListMonitorType.SpecificBook, MonitorTypes.SpecificBook, true)]
        [TestCase(ImportListMonitorType.EntireAuthor, MonitorTypes.All, true)]
        public void legacy_creation_should_translate_only_omitted_modern_sides(ImportListMonitorType legacy, MonitorTypes expected, bool listed)
        {
            var mapper = new ImportListResourceMapper();
            var resource = STJson.Deserialize<ImportListResource>("{\"shouldMonitor\":\"" + legacy + "\",\"monitoringOptions\":{\"ebookMonitor\":null,\"ebookMonitorListedBooks\":false}}");
            var model = mapper.ToModel(resource);
            model.PreserveMonitoringOptions(null);
            Assert.That(model.MonitoringOptions.AudiobookMonitor, Is.EqualTo(expected));
            Assert.That(model.MonitoringOptions.AudiobookMonitorListedBooks, Is.EqualTo(listed));
            Assert.That(model.MonitoringOptions.EbookMonitor, Is.Null);
            Assert.That(model.MonitoringOptions.EbookMonitorListedBooks, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void partial_monitoring_options_should_preserve_saved_properties_and_honor_explicit_null(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, typeof(GoodreadsBookshelf));
                var definition = h.Definition(addMissing: false);
                definition.MonitoringOptions = new ImportListMonitoringOptions
                {
                    AddMissingAuthors = false, ResumePausedAuthors = true,
                    AudiobookMonitored = false, EbookMonitored = true,
                    AudiobookMonitorListedBooks = true, EbookMonitorListedBooks = false,
                    AudiobookMonitor = MonitorTypes.All, EbookMonitor = MonitorTypes.Existing,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.New, EbookMonitorNewItems = NewItemMonitorTypes.None
                };
                var saved = h.Factory.Create(definition);
                var resource = h.Resource(saved);
                resource.MonitoringOptions = STJson.Deserialize<ImportListMonitoringOptions>("{\"audiobookMonitor\":\"missing\",\"ebookMonitored\":null}");
                h.ResetCounts();
                h.ValidateResource(resource, "PUT");
                h.Controller.UpdateProvider(resource, forceSave: true);
                var readback = h.Repository.Get(saved.Id).GetMonitoringOptions();
                Assert.Multiple(() =>
                {
                    Assert.That(readback.AddMissingAuthors, Is.False);
                    Assert.That(readback.ResumePausedAuthors, Is.True);
                    Assert.That(readback.AudiobookMonitored, Is.False);
                    Assert.That(readback.EbookMonitored, Is.Null);
                    Assert.That(readback.AudiobookMonitorListedBooks, Is.True);
                    Assert.That(readback.EbookMonitorListedBooks, Is.False);
                    Assert.That(readback.AudiobookMonitor, Is.EqualTo(MonitorTypes.Missing));
                    Assert.That(readback.EbookMonitor, Is.EqualTo(MonitorTypes.Existing));
                    Assert.That(readback.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(readback.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(h.Updates, Is.EqualTo(1));
                    Assert.That(h.RootReads + h.QualityReads + h.MetadataReads, Is.Zero);
                });
                resource = h.Resource(h.Repository.Get(saved.Id));
                resource.MonitoringOptions = STJson.Deserialize<ImportListMonitoringOptions>("{\"resumePausedAuthors\":false,\"audiobookMonitor\":null}");
                h.Controller.UpdateProvider(resource, forceSave: true);
                readback = h.Repository.Get(saved.Id).GetMonitoringOptions();
                Assert.That(readback.ResumePausedAuthors, Is.False);
                Assert.That(readback.AudiobookMonitor, Is.Null);
                Assert.That(readback.EbookMonitor, Is.EqualTo(MonitorTypes.Existing));

                resource = h.Resource(h.Repository.Get(saved.Id));
                resource.MonitoringOptions = new ImportListMonitoringOptions();
                h.ResetCounts();
                h.Controller.UpdateProvider(resource, forceSave: true);
                Assert.That(h.Updates, Is.Zero, "An empty partial options object must not rewrite the saved definition");
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void monitoring_only_should_save_test_and_remain_available_without_creation_fields(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                var definition = h.Definition(addMissing: false);
                Set(definition.Settings, "AudiobookTags", new List<int> { 1 });
                Set(definition.Settings, "EbookTags", new List<int> { 2 });
                if (definition.Settings is ChaptarrSettings connector)
                {
                    connector.TagIds = new[] { 900 };
                }
                var resource = h.Resource(definition);
                h.ValidateResource(resource, "POST");
                Assert.DoesNotThrow(() => h.Controller.Test(resource));
                Assert.DoesNotThrow(() => h.Controller.CreateProvider(resource));
                var stored = h.Repository.All().Single();
                Assert.Multiple(() =>
                {
                    Assert.That(stored.GetMonitoringOptions().AddMissingAuthors, Is.False);
                    Assert.That(h.Factory.AutomaticAddEnabled(false).Select(list => list.Definition.Id), Does.Contain(stored.Id));
                    Assert.That(h.RootReads, Is.Zero);
                    Assert.That(h.QualityReads + h.MetadataReads, Is.Zero);
                    Assert.That((IEnumerable<int>)Get(stored.Settings, "AudiobookTags"), Is.EquivalentTo(new[] { 1 }));
                    Assert.That((IEnumerable<int>)Get(stored.Settings, "EbookTags"), Is.EquivalentTo(new[] { 2 }));
                });

                var hidden = h.Definition(addMissing: false);
                Set(hidden.Settings, "AudiobookRootFolderPath", @"C:\absent".AsOsAgnostic());
                Set(hidden.Settings, "EbookRootFolderPath", h.AudioPath);
                Set(hidden.Settings, "AudiobookQualityProfileId", 999999);
                Set(hidden.Settings, "EbookMetadataProfileId", 999999);
                Assert.DoesNotThrow(() => h.Controller.Test(h.Resource(hidden)));
                Assert.That(h.RootReads + h.QualityReads + h.MetadataReads, Is.Zero);

                resource = h.Resource(stored);
                resource.MonitoringOptions = null;
                h.ResetCounts();
                Assert.DoesNotThrow(() => h.Controller.Test(resource));
                Assert.That(h.ExistingGets, Is.EqualTo(1), "Validation must reuse the row already loaded for source settings");
                Assert.That(h.RootReads, Is.Zero);
                h.ResetCounts();
                h.ValidateResource(resource, "PUT");
                Assert.DoesNotThrow(() => h.Controller.UpdateProvider(resource, forceSave: true));
                Assert.Multiple(() =>
                {
                    Assert.That(h.Repository.Get(stored.Id).GetMonitoringOptions().AddMissingAuthors, Is.False);
                    Assert.That(h.ExistingGets, Is.EqualTo(2), "One existing settings read and one response read");
                    Assert.That(h.RootReads, Is.Zero);
                });

                resource.MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true };
                h.ResetCounts();
                Assert.Throws<ValidationException>(() => h.Controller.UpdateProvider(resource, forceSave: true));
                Assert.Multiple(() =>
                {
                    Assert.That(h.Repository.Get(stored.Id).GetMonitoringOptions().AddMissingAuthors, Is.False);
                    Assert.That(h.ExistingGets, Is.EqualTo(1));
                    Assert.That(h.RootReads, Is.Zero);
                });

                stored.MonitoringOptions = null;
                h.Factory.Update(stored);
                Assert.That(h.Repository.Get(stored.Id).GetMonitoringOptions().AddMissingAuthors, Is.False);

                var invalidSource = h.Definition(addMissing: false);
                h.InvalidateSource(invalidSource.Settings);
                Assert.Throws<ValidationException>(() => h.Controller.Test(h.Resource(invalidSource)), "Add off must still validate the source");
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void adding_should_validate_selected_roots_once_and_preserve_force_save_requiredness(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                var definition = h.Definition(addMissing: true);
                h.SelectRoots(definition);
                var resource = h.Resource(definition);
                h.ResetCounts();
                Assert.DoesNotThrow(() => h.Controller.Test(resource));
                Assert.That(h.RootReads, Is.EqualTo(1), "Both selected formats share one root snapshot");
                h.ResetCounts();
                h.ValidateResource(resource, "POST");
                Assert.DoesNotThrow(() => h.Controller.CreateProvider(resource));
                Assert.That(h.RootReads, Is.EqualTo(1));
                var stored = h.Repository.All().Single();
                Assert.Multiple(() =>
                {
                    Assert.That(Get(stored.Settings, "AudiobookRootFolderPath"), Is.EqualTo(h.AudioPath));
                    Assert.That(Get(stored.Settings, "EbookRootFolderPath"), Is.EqualTo(h.EbookPath));
                    Assert.That(Get(stored.Settings, "AudiobookQualityProfileId"), Is.EqualTo(0));
                    Assert.That(Get(stored.Settings, "EbookMetadataProfileId"), Is.EqualTo(0));
                });

                foreach (var path in new[] { h.EbookPath, h.UnconfiguredPath, @"C:\absent".AsOsAgnostic() })
                {
                    var wrong = h.Definition(addMissing: true);
                    h.SelectRoots(wrong);
                    Set(wrong.Settings, "AudiobookRootFolderPath", path);
                    h.ResetCounts();
                    Assert.Throws<ValidationException>(() => h.Controller.Test(h.Resource(wrong)));
                    Assert.That(h.RootReads, Is.EqualTo(1));
                }

                var single = h.Definition(addMissing: true);
                Set(single.Settings, "MonitorEbooks", false);
                Set(single.Settings, "AudiobookRootFolderPath", h.AudioPath);
                Set(single.Settings, "EbookRootFolderPath", h.AudioPath);
                Assert.DoesNotThrow(() => h.Controller.Test(h.Resource(single)), "An inactive format must not require its hidden root");

                var missing = h.Resource(h.Definition(addMissing: true));
                h.ResetCounts();
                Assert.Throws<ValidationException>(() => h.Controller.CreateProvider(missing));
                Assert.Throws<ValidationException>(() => h.Controller.CreateProvider(missing, forceSave: true));
                Assert.Throws<ValidationException>(() => h.Controller.Test(missing));
                Assert.That(h.RootReads, Is.Zero, "Missing fields are rejected without a root catalog query");
                missing.Id = stored.Id;
                Assert.Throws<ValidationException>(() => h.Controller.UpdateProvider(missing, forceSave: true));
                Assert.That(h.Repository.All(), Has.Count.EqualTo(1));

                var neither = h.Definition(addMissing: false);
                Set(neither.Settings, "MonitorAudiobooks", false);
                Set(neither.Settings, "MonitorEbooks", false);
                Assert.Throws<ValidationException>(() => h.Controller.Test(h.Resource(neither)));
            });
        }

        [Test]
        public void creation_settings_are_validated_on_enabled_and_disabled_saves(
            [Values] bool postgres, [Values] bool enabled, [Values] bool update)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, typeof(ChaptarrImport));
                var definition = h.Definition(addMissing: true);
                definition.EnableAutomaticAdd = enabled;
                h.SelectRoots(definition);
                if (update) definition = h.Factory.Create(definition);
                var original = update ? h.Repository.Get(definition.Id).ToJson() : null;
                foreach (var field in new[] { "AudiobookRootFolderPath", "EbookRootFolderPath", "AudiobookQualityProfileId", "EbookQualityProfileId", "AudiobookMetadataProfileId", "EbookMetadataProfileId" })
                {
                    var invalid = STJson.Deserialize<ImportListResource>(STJson.ToJson(h.Resource(definition)));
                    var value = field.EndsWith("Path") ? (object)"relative/path" : 999;
                    invalid.Fields.Single(item => item.Name.Equals(field, StringComparison.OrdinalIgnoreCase)).Value = value;
                    var error = Assert.Throws<ValidationException>(() =>
                    {
                        h.ValidateResource(invalid, update ? "PUT" : "POST");
                        if (update) h.Controller.UpdateProvider(invalid, forceSave: true);
                        else h.Controller.CreateProvider(invalid, forceSave: true);
                    });
                    Assert.That(error.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(failure => failure.PropertyName == field));
                    Assert.That(h.Repository.All(), Has.Count.EqualTo(update ? 1 : 0));
                    if (update) Assert.That(h.Repository.Get(definition.Id).ToJson(), Is.EqualTo(original));
                }
                var valid = h.Resource(definition);
                h.ValidateResource(valid, update ? "PUT" : "POST");
                Assert.DoesNotThrow(() =>
                {
                    if (update) h.Controller.UpdateProvider(valid, forceSave: true);
                    else h.Controller.CreateProvider(valid, forceSave: true);
                });
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void relative_creation_paths_are_rejected_even_for_a_forced_save(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                foreach (var field in new[] { "AudiobookRootFolderPath", "EbookRootFolderPath" })
                {
                    var definition = h.Definition(addMissing: true);
                    h.SelectRoots(definition);
                    Set(definition.Settings, field, "relative/library");
                    var error = Assert.Throws<ValidationException>(() => h.Controller.CreateProvider(h.Resource(definition), forceSave: true));
                    Assert.That(error.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(failure =>
                        failure.PropertyName == field && failure.ErrorMessage.Contains("Invalid Path")));
                    Assert.That(h.Repository.All(), Is.Empty);
                }
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void shared_schema_and_destination_options_should_preserve_source_filters(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                var definition = h.Definition(addMissing: false);
                var fields = h.Resource(definition).Fields;
                var expected = new[]
                {
                    "monitorAudiobooks", "monitorEbooks", "audiobookRootFolderPath", "ebookRootFolderPath",
                    "audiobookQualityProfileId", "ebookQualityProfileId", "audiobookMetadataProfileId", "ebookMetadataProfileId",
                    "audiobookTags", "ebookTags"
                };
                Assert.That(fields.Select(field => field.Name), Is.SupersetOf(expected));
                Assert.That(fields.Where(field => expected.Contains(field.Name)).Select(field => field.Name).Distinct().ToList(), Has.Count.EqualTo(10));
                Assert.That(fields.Where(field => expected.Contains(field.Name) && field.Advanced).Select(field => field.Name),
                    Is.EquivalentTo(expected.Where(name => name.EndsWith("ProfileId", StringComparison.Ordinal))));
                Assert.That(h.Factory.GetDefaultDefinitions().Single().Enable, Is.False, "A template cannot add before its creation fields are configured");
                Assert.That(Get(Activator.CreateInstance(h.Provider.ConfigContract), "MonitorAudiobooks"), Is.True);
                Assert.That(Get(Activator.CreateInstance(h.Provider.ConfigContract), "MonitorEbooks"), Is.True);

                var audio = h.Options(definition, "getAudiobookQualityProfiles");
                var ebook = h.Options(definition, "getEbookQualityProfiles");
                Assert.That(audio.Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 0, 11 }));
                Assert.That(ebook.Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 0, 12 }));
                Assert.That((string)audio[0]["LocalizationKey"], Is.EqualTo("UseRootFolderDefaults"));
                Assert.That(h.Options(definition, "getAudiobookMetadataProfiles").Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 0, 21, 23 }));
                Assert.That(h.Options(definition, "getEbookMetadataProfiles").Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 0, 22, 23 }));
                Assert.That(h.Options(definition, "getLocalTags").Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 1 }));

                if (definition.Settings is ChaptarrSettings source)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(h.Options(definition, "getTags").Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 900 }));
                        Assert.That(h.Options(definition, "getProfiles").Select(token => (int)token["Value"]), Is.EquivalentTo(new[] { 901 }));
                        Assert.That(h.Options(definition, "getRootFolders").Select(token => (string)token["value"]), Is.EquivalentTo(new[] { "/remote/books" }));
                        Assert.That(fields.Single(field => field.Name == "tagIds").SelectOptionsProviderAction, Is.EqualTo("getTags"));
                        Assert.That(fields.Single(field => field.Name == "audiobookTags").SelectOptionsProviderAction, Is.EqualTo("getLocalTags"));
                    });
                    source.ApiKey = string.Empty;
                    Assert.That(h.Options(definition, "getLocalTags"), Has.Count.EqualTo(1));
                    Assert.That(h.Options(definition, "getTags"), Is.Empty);
                }
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void creation_profile_overrides_should_reject_missing_and_wrong_media_even_when_forced(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                var initial = h.Definition(addMissing: true);
                h.SelectRoots(initial);
                h.Factory.Create(initial);
                foreach (var change in new[]
                {
                    (Field: "AudiobookQualityProfileId", Id: 999999),
                    (Field: "EbookMetadataProfileId", Id: 999999),
                    (Field: "AudiobookQualityProfileId", Id: 12),
                    (Field: "EbookMetadataProfileId", Id: 21),
                    (Field: "AudiobookQualityProfileId", Id: -1)
                })
                {
                    var definition = h.Repository.Get(initial.Id);
                    Set(definition.Settings, change.Field, change.Id);
                    var resource = h.Resource(definition);
                    h.ResetCounts();
                    Assert.Throws<ValidationException>(() => h.Controller.UpdateProvider(resource, forceSave: true), change.Field + "=" + change.Id);
                    Assert.That(Get(h.Repository.Get(initial.Id).Settings, change.Field), Is.EqualTo(0));
                    Assert.That(h.RootReads, Is.Zero, "Force save keeps its full-root-test bypass");
                    h.ResetCounts();
                    Assert.Throws<ValidationException>(() => h.Controller.Test(resource));
                    Assert.That(h.QualityReads, Is.LessThanOrEqualTo(1));
                    Assert.That(h.MetadataReads, Is.LessThanOrEqualTo(1));
                    h.ResetCounts();
                    Assert.Throws<ValidationException>(() => h.Controller.Test(resource, forceTest: true));
                    Assert.That(h.QualityReads, Is.LessThanOrEqualTo(1));
                    Assert.That(h.MetadataReads, Is.LessThanOrEqualTo(1));
                    h.ResetCounts();
                    Assert.That(h.Factory.Test(definition).IsValid, Is.False, "Direct factory callers use the same override validator");
                }

                var valid = h.Repository.Get(initial.Id);
                Set(valid.Settings, "AudiobookQualityProfileId", 11);
                Set(valid.Settings, "EbookQualityProfileId", 12);
                Set(valid.Settings, "AudiobookMetadataProfileId", 23);
                Set(valid.Settings, "EbookMetadataProfileId", 22);
                h.ResetCounts();
                Assert.DoesNotThrow(() => h.Controller.UpdateProvider(h.Resource(valid), forceSave: true));
                Assert.That(h.QualityReads, Is.EqualTo(1));
                Assert.That(h.MetadataReads, Is.EqualTo(1));
                h.ResetCounts();
                Assert.DoesNotThrow(() => h.Controller.Test(h.Resource(valid)));
                Assert.Multiple(() =>
                {
                    Assert.That(h.QualityReads, Is.EqualTo(1));
                    Assert.That(h.MetadataReads, Is.EqualTo(1));
                    Assert.That(h.RootReads, Is.EqualTo(1));
                });
                h.ResetCounts();
                var unchanged = STJson.Deserialize<ImportListResource>(STJson.ToJson(h.Controller.GetAll().Single()));
                Assert.DoesNotThrow(() => h.Controller.UpdateProvider(unchanged));
                Assert.That(h.QualityReads + h.MetadataReads + h.RootReads, Is.Zero, "Unchanged Save does not retest creation profiles");
                Assert.That(h.Updates, Is.Zero);
                Assert.That(h.SyncCommands, Is.Empty, "No unchanged-save ProviderUpdatedEvent reaches the real sync handler");
                valid.Name += " changed";
                h.ResetCounts();
                Assert.DoesNotThrow(() => h.Controller.UpdateProvider(h.Resource(valid)));
                Assert.That(h.QualityReads, Is.EqualTo(1), "Changed Save still validates profiles");
                Assert.That(h.MetadataReads, Is.EqualTo(1));
                Assert.That(h.Updates, Is.EqualTo(1));
                Assert.That(h.SyncCommands, Has.Count.EqualTo(1));
                valid.MonitoringOptions.AddMissingAuthors = false;
                Set(valid.Settings, "AudiobookQualityProfileId", 999999);
                Set(valid.Settings, "EbookMetadataProfileId", 999999);
                h.ResetCounts();
                Assert.DoesNotThrow(() => h.Controller.UpdateProvider(h.Resource(valid), forceSave: true));
                Assert.That(h.QualityReads + h.MetadataReads, Is.Zero, "Monitoring existing formats ignores hidden creation overrides");
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void saved_creation_profile_references_should_prevent_deletion_even_when_not_active(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                var definition = h.Definition(addMissing: false);
                definition.EnableAutomaticAdd = false;
                Set(definition.Settings, "MonitorAudiobooks", false);
                Set(definition.Settings, "MonitorEbooks", false);
                h.Repository.Insert(definition);
                var deleted = new List<int>();
                var listReads = 0;
                var lists = InterfaceProxy.Proxy<IImportListFactory>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo("All"));
                    listReads++;
                    return h.Repository.All().ToList();
                });
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, args) => new List<Author>());
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, args) => new List<RootFolder>());
                var qualityRepository = InterfaceProxy.Proxy<IProfileRepository>((method, args) =>
                {
                    if (method.Name == "Delete")
                    {
                        deleted.Add((int)args[0]);
                        return null;
                    }

                    return new QualityProfile { Id = (int)args[0], Name = "Referenced profile" };
                });
                var metadataRepository = InterfaceProxy.Proxy<IMetadataProfileRepository>((method, args) =>
                {
                    if (method.Name == "Delete")
                    {
                        deleted.Add((int)args[0]);
                        return null;
                    }

                    if (method.Name == "All")
                    {
                        return new[] { new MetadataProfile { Id = 21 }, new MetadataProfile { Id = 22 }, new MetadataProfile { Id = 24 } };
                    }

                    return new MetadataProfile { Id = (int)args[0], Name = "Referenced profile" };
                });
                var quality = new QualityProfileService(qualityRepository, authors, lists, null, roots, null, LogManager.GetCurrentClassLogger());
                var metadata = new MetadataProfileService(metadataRepository, authors, null, null, null, lists, roots, null, null, LogManager.GetCurrentClassLogger());
                foreach (var reference in new[]
                {
                    (Field: "AudiobookQualityProfileId", Id: 11, Quality: true),
                    (Field: "EbookQualityProfileId", Id: 12, Quality: true),
                    (Field: "AudiobookMetadataProfileId", Id: 21, Quality: false),
                    (Field: "EbookMetadataProfileId", Id: 22, Quality: false)
                })
                {
                    Set(definition.Settings, reference.Field, reference.Id);
                    h.Repository.Update(definition);
                    listReads = 0;
                    if (reference.Quality)
                    {
                        Assert.Throws<QualityProfileInUseException>(() => quality.Delete(reference.Id));
                    }
                    else
                    {
                        Assert.Throws<MetadataProfileInUseException>(() => metadata.Delete(reference.Id));
                    }

                    Assert.Multiple(() =>
                    {
                        Assert.That(deleted, Does.Not.Contain(reference.Id));
                        Assert.That(listReads, Is.EqualTo(1));
                    });
                    Set(definition.Settings, reference.Field, 0);
                    h.Repository.Update(definition);
                    Assert.DoesNotThrow(() => { if (reference.Quality) quality.Delete(reference.Id); else metadata.Delete(reference.Id); });
                    Assert.That(deleted, Does.Contain(reference.Id));
                }

                definition.QualityProfileId = 31;
                definition.MetadataProfileId = 41;
                h.Repository.Update(definition);
                Assert.Throws<QualityProfileInUseException>(() => quality.Delete(31));
                Assert.Throws<MetadataProfileInUseException>(() => metadata.Delete(41));
            });
        }

        [TestCaseSource(nameof(ProviderDatabases))]
        public void health_should_check_only_enabled_creation_roots_and_preserve_author_roots(Type providerType, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, providerType);
                var definition = h.Factory.Create(h.Definition(addMissing: false));
                var authorPaths = new List<KeyValuePair<int, string>>();
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, args) => authorPaths);
                var probed = new List<string>();
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.FolderExists)));
                    var path = (string)args[0];
                    probed.Add(path);
                    return path.PathEquals(h.AudioPath) || path.PathEquals(h.EbookPath);
                });
                var localization = InterfaceProxy.Proxy<ILocalizationService>((method, args) => (string)args[0] == "None" ? "None" : "{0}");
                var listCheck = new ImportListRootFolderCheck(h.Factory, disk, localization);
                var rootCheck = new RootFolderCheck(authors, h.Factory, disk, (IRootFolderService)h.GetService(typeof(IRootFolderService)), localization);

                void Check(HealthCheckResult listResult, HealthCheckResult rootResult, params string[] expectedProbes)
                {
                    h.Repository.Update(definition);
                    definition = h.Repository.Get(definition.Id);
                    probed.Clear();
                    h.ResetCounts();
                    var listHealth = listCheck.Check();
                    Assert.Multiple(() =>
                    {
                        Assert.That(listHealth.Type, Is.EqualTo(listResult));
                        Assert.That(probed, Is.EquivalentTo(expectedProbes));
                        Assert.That(h.RootReads, Is.Zero);
                    });
                    if (listResult == HealthCheckResult.Error)
                    {
                        Assert.That(listHealth.Message, Does.Contain("None"));
                    }

                    probed.Clear();
                    h.ResetCounts();
                    var rootHealth = rootCheck.Check();
                    Assert.Multiple(() =>
                    {
                        Assert.That(rootHealth.Type, Is.EqualTo(rootResult));
                        Assert.That(probed, Is.EquivalentTo(authorPaths.Any() ? new[] { h.UnconfiguredPath } : expectedProbes));
                        Assert.That(h.RootReads, Is.EqualTo(1));
                        Assert.That(h.QualityReads + h.MetadataReads, Is.Zero);
                    });
                    if (rootResult == HealthCheckResult.Error)
                    {
                        Assert.That(rootHealth.Message, Does.Contain(h.UnconfiguredPath));
                    }
                }

                Check(HealthCheckResult.Ok, HealthCheckResult.Ok);
                var hidden = @"C:\hidden-missing".AsOsAgnostic();
                definition.RootFolderPath = hidden;
                Set(definition.Settings, "AudiobookRootFolderPath", hidden);
                Set(definition.Settings, "EbookRootFolderPath", hidden);
                Check(HealthCheckResult.Ok, HealthCheckResult.Ok);

                definition.MonitoringOptions.AddMissingAuthors = true;
                Set(definition.Settings, "MonitorEbooks", false);
                Set(definition.Settings, "AudiobookRootFolderPath", " " + h.AudioPath + " ");
                Check(HealthCheckResult.Ok, HealthCheckResult.Ok, h.AudioPath);

                Set(definition.Settings, "MonitorEbooks", true);
                Set(definition.Settings, "EbookRootFolderPath", null);
                Check(HealthCheckResult.Error, HealthCheckResult.Ok, h.AudioPath);

                definition.EnableAutomaticAdd = false;
                Set(definition.Settings, "AudiobookRootFolderPath", null);
                Check(HealthCheckResult.Ok, HealthCheckResult.Ok);

                definition.EnableAutomaticAdd = true;
                definition.MonitoringOptions.AddMissingAuthors = false;
                authorPaths.Add(new KeyValuePair<int, string>(1, Path.Combine(h.UnconfiguredPath, "Existing author")));
                Check(HealthCheckResult.Ok, HealthCheckResult.Error);
            });
        }

        private static object Get(object settings, string name) => settings.GetType().GetProperty(name)?.GetValue(settings);

        private static void Set(object settings, string name, object value)
        {
            var property = settings.GetType().GetProperty(name);
            if (property != null)
            {
                property.SetValue(settings, value);
            }
        }

        private sealed class Harness : IServiceProvider
        {
            private readonly Dictionary<Type, object> _services = new();
            public readonly ImportListRepository Repository;
            public readonly ImportListFactory Factory;
            public readonly ImportListController Controller;
            public readonly IImportList Provider;
            public readonly string AudioPath = @"C:\list-audio".AsOsAgnostic();
            public readonly string EbookPath = @"C:\list-ebook".AsOsAgnostic();
            public readonly string UnconfiguredPath = @"C:\list-empty".AsOsAgnostic();
            public int RootReads;
            public int QualityReads;
            public int MetadataReads;
            public int ExistingGets;
            public int Updates;
            public List<ImportListSyncCommand> SyncCommands = new();

            public Harness(IMainDatabase database, Type providerType)
            {
                var logger = LogManager.GetCurrentClassLogger();
                var handler = new ImportListUpdatedHandler(InterfaceProxy.Proxy<IManageCommandQueue>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo("Push"));
                    SyncCommands.Add((ImportListSyncCommand)args[0]);
                    return new CommandModel();
                }));
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                {
                    if (args[0] is ProviderUpdatedEvent<IImportList> updated)
                    {
                        Updates++;
                        handler.Handle(updated);
                    }
                    return null;
                });
                var config = InterfaceProxy.Proxy<IConfigService>((method, args) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var roots = new RootFolderRepository(database, events);
                roots.Insert(AuthorLibraryServiceSpecificBookMonitoringFixture.BuildAudiobookRoot(AudioPath));
                roots.Insert(AuthorLibraryServiceSpecificBookMonitoringFixture.BuildEbookRoot(EbookPath));
                roots.Insert(new RootFolder { Path = UnconfiguredPath, FolderType = FolderType.Mixed });
                var rootService = new RootFolderService(roots, null, null, null, logger);
                var rootProxy = InterfaceProxy.Proxy<IRootFolderService>((method, args) =>
                {
                    if (method.Name == nameof(IRootFolderService.All))
                    {
                        RootReads++;
                    }

                    return Invoke(method, rootService, args);
                });
                var qualityProfiles = new List<QualityProfile>
                {
                    new() { Id = 11, Name = "Audio", ProfileType = ProfileType.Audiobook },
                    new() { Id = 12, Name = "Ebook", ProfileType = ProfileType.Ebook }
                };
                var quality = InterfaceProxy.Proxy<IQualityProfileService>((method, args) =>
                {
                    if (method.Name is "All" or "GetByType")
                    {
                        QualityReads++;
                        return method.Name == "All" ? qualityProfiles : qualityProfiles.Where(p => p.ProfileType == (ProfileType)args[0]).ToList();
                    }

                    return method.Name == "Exists" ? qualityProfiles.Any(p => p.Id == (int)args[0]) : qualityProfiles.SingleOrDefault(p => p.Id == (int)args[0]);
                });
                var metadataProfiles = new List<MetadataProfile>
                {
                    new() { Id = 21, Name = "Audio", ProfileType = MetadataProfileType.Audiobook },
                    new() { Id = 22, Name = "Ebook", ProfileType = MetadataProfileType.Ebook },
                    new() { Id = 23, Name = "General", ProfileType = MetadataProfileType.General }
                };
                var metadata = InterfaceProxy.Proxy<IMetadataProfileService>((method, args) =>
                {
                    if (method.Name == "All")
                    {
                        MetadataReads++;
                        return metadataProfiles;
                    }

                    return method.Name == "Exists" ? metadataProfiles.Any(p => p.Id == (int)args[0]) : metadataProfiles.SingleOrDefault(p => p.Id == (int)args[0]);
                });
                var tags = InterfaceProxy.Proxy<ITagService>((method, args) => new List<Tag> { new() { Id = 1, Label = "Local destination" } });
                var status = InterfaceProxy.Proxy<IImportListStatusService>((method, args) => method.ReturnType == typeof(void) ? null : Activator.CreateInstance(method.ReturnType));
                var http = InterfaceProxy.Proxy<IHttpClient>((method, args) =>
                {
                    var request = (HttpRequest)args[0];
                    var content = providerType == typeof(HardcoverLibraryImportList)
                        ? "{\"data\":{\"me\":[{\"id\":1,\"username\":\"fixture\"}],\"user_books\":[]}}"
                        : providerType == typeof(GoodreadsBookshelf)
                            ? "<GoodreadsResponse><shelves><user_shelf><name>want-to-read</name></user_shelf></shelves><reviews /></GoodreadsResponse>"
                            : "[{\"BookID\":\"gr:123\",\"BookName\":\"Fixture Book\",\"AuthorName\":\"Fixture Author\"}]";
                    return new HttpResponse(request, new HttpHeader { ContentType = providerType == typeof(GoodreadsBookshelf) ? "application/xml" : "application/json" }, content, HttpStatusCode.OK);
                });
                var source = InterfaceProxy.Proxy<IChaptarrV1Proxy>((method, args) => method.Name switch
                {
                    "Test" => null,
                    "GetTags" => new List<ChaptarrTag> { new() { Id = 900, Label = "Remote source" } },
                    "GetProfiles" => new List<ChaptarrProfile> { new() { Id = 901, Name = "Remote profile" } },
                    "GetRootFolders" => new List<ChaptarrRootFolder> { new() { Path = "/remote/books" } },
                    _ => throw new InvalidOperationException(method.Name)
                });
                Add(logger); Add(config); Add(status); Add(http); Add(source);
                Add(rootProxy); Add<IRootFolderSettingsResolver>(new RootFolderSettingsResolver(rootProxy));
                Add(new Lazy<IRootFolderService>(() => rootProxy));
                Add(quality); Add(metadata); Add(tags);
                Add(new Lazy<IQualityProfileService>(() => quality));
                Add(new Lazy<IMetadataProfileService>(() => metadata));
                Add(new Lazy<ITagService>(() => tags));
                Add(InterfaceProxy.Proxy<IParsingService>((method, args) => throw new InvalidOperationException(method.Name)));
                Add(InterfaceProxy.Proxy<IProvideListInfo>((method, args) => null));
                Add(InterfaceProxy.Proxy<IProvideSeriesInfo>((method, args) => null));
                Add(InterfaceProxy.Proxy<IHardcoverLibraryImportListStateRepository>((method, args) => null));
                Provider = (IImportList)Construct(providerType);
                _services[providerType] = Provider;
                Repository = new ImportListRepository(database, events);
                Factory = new ImportListFactory(status, Repository, new[] { Provider }, this, events,
                    InterfaceProxy.Proxy<IHardcoverIdentityService>((method, args) => false), config, logger);
                Add(InterfaceProxy.Proxy<IImportListFactory>((method, args) =>
                {
                    if (method.Name == "Get" && args.Length == 1 && args[0] is int)
                    {
                        ExistingGets++;
                    }

                    return Invoke(method, Factory, args);
                }));
                Add(new QualityProfileExistsValidator(quality));
                Add(new MetadataProfileExistsValidator(metadata));
                Controller = (ImportListController)Construct(typeof(ImportListController));
                Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            }

            public object GetService(Type serviceType) => _services.TryGetValue(serviceType, out var value) ? value : throw new InvalidOperationException(serviceType.FullName);
            private void Add<T>(T service) => _services[typeof(T)] = service;
            private object Construct(Type type) => type.GetConstructors().Single().Invoke(type.GetConstructors().Single().GetParameters().Select(p => GetService(p.ParameterType)).ToArray());

            public ImportListDefinition Definition(bool addMissing)
            {
                var settings = (IImportListSettings)Activator.CreateInstance(Provider.ConfigContract);
                switch (settings)
                {
                    case HardcoverLibraryImportListSettings hc: hc.ApiToken = "fixture-token"; break;
                    case GoodreadsBookshelfImportListSettings gr: gr.UserId = "123"; gr.BookshelfIds = new[] { "want-to-read" }; break;
                    case GoodreadsListImportListSettings gr: gr.ListId = 123; break;
                    case GoodreadsSeriesImportListSettings gr: gr.SeriesId = 123; break;
                    case ChaptarrSettings source: source.BaseUrl = "http://source.invalid"; source.ApiKey = "fixture-key"; break;
                    case LazyLibrarianImportSettings source: source.ApiKey = "fixture-key"; break;
                }

                return new ImportListDefinition
                {
                    Name = "Validation " + Guid.NewGuid().ToString("N"), Implementation = Provider.GetType().Name,
                    ConfigContract = Provider.ConfigContract.Name, Settings = settings, EnableAutomaticAdd = true,
                    MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = addMissing }
                };
            }

            public ImportListResource Resource(ImportListDefinition definition)
            {
                var resource = ImportListController.ResourceMapper.ToResource(definition);
                foreach (var field in resource.Fields)
                {
                    var property = definition.Settings.GetType().GetProperty(field.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    field.Value = property.GetValue(definition.Settings);
                }

                return STJson.Deserialize<ImportListResource>(resource.ToJson());
            }
            public void SelectRoots(ImportListDefinition definition)
            {
                Set(definition.Settings, "AudiobookRootFolderPath", AudioPath);
                Set(definition.Settings, "EbookRootFolderPath", EbookPath);
            }

            public void InvalidateSource(IProviderConfig settings)
            {
                switch (settings)
                {
                    case HardcoverLibraryImportListSettings hc: hc.ApiToken = null; break;
                    case GoodreadsBookshelfImportListSettings gr: gr.UserId = null; break;
                    case GoodreadsListImportListSettings gr: gr.ListId = 0; break;
                    case GoodreadsSeriesImportListSettings gr: gr.SeriesId = 0; break;
                    case ChaptarrSettings source: source.ApiKey = null; break;
                    case LazyLibrarianImportSettings source: source.ApiKey = null; break;
                }
            }

            public JArray Options(ImportListDefinition definition, string action) => (JArray)JObject.FromObject(Factory.RequestAction(definition, action, new Dictionary<string, string>()))["options"];
            public void ResetCounts() { RootReads = 0; QualityReads = 0; MetadataReads = 0; ExistingGets = 0; Updates = 0; SyncCommands.Clear(); }
            public void ValidateResource(ImportListResource resource, string method)
            {
                Controller.Request.Method = method;
                Invoke(typeof(ImportListController).GetMethod("ValidateResource", BindingFlags.Instance | BindingFlags.NonPublic), Controller, new object[] { resource, false, false });
            }

            private static object Invoke(MethodInfo method, object instance, object[] arguments)
            {
                try
                {
                    return method.Invoke(instance, arguments);
                }
                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
            }
        }
    }
}
