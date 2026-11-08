using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class SettingsBackupProfilesRestoreFixture
    {
        private readonly List<DelayProfile> _delays = new();
        private readonly List<ReleaseProfile> _releases = new();
        private readonly List<QualityDefinition> _qualities = new();
        private readonly List<IndexerDefinition> _indexers = new();
        private SettingsBackupService _service;
        private int _nextId;

        [SetUp]
        public void SetUp()
        {
            _nextId = 100;
            _delays.Clear();
            _releases.Clear();
            _qualities.Clear();
            _indexers.Clear();
            _delays.Add(new DelayProfile { Id = 1, Order = int.MaxValue, EnableTorrent = true });
            _service = new SettingsBackupService(null, null, null, null,
                Service<IIndexerFactory, IndexerDefinition>(_indexers), null, null, null, null, null, null, null, null, null,
                Service<IDelayProfileService, DelayProfile>(_delays), Service<IReleaseProfileService, ReleaseProfile>(_releases),
                Service<IQualityDefinitionService, QualityDefinition>(_qualities), mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
        }

        private TService Service<TService, TModel>(List<TModel> rows) where TService : class where TModel : ModelBase
        {
            return InterfaceProxy.Proxy<TService>((method, args) =>
            {
                if (method.Name == "All") return rows.ToList();
                if (method.Name == "Delete")
                {
                    var ids = args[0] is IEnumerable<int> batch ? batch.ToHashSet() : new HashSet<int> { (int)args[0] };
                    rows.RemoveAll(row => ids.Contains(row.Id));
                    return null;
                }
                if (method.Name == "UpdateMany")
                {
                    foreach (var row in (List<TModel>)args[0])
                    {
                        rows.RemoveAll(existing => existing.Id == row.Id);
                        rows.Add(row);
                    }
                    return null;
                }
                var model = (TModel)args[0];
                if (method.Name is "Add" or "Create")
                {
                    model.Id = ++_nextId;
                    if (model is DelayProfile delay) delay.Order = args.Length > 1 && args[1] is int order ? order : rows.Count;
                }
                else if (method.Name == "Update") rows.RemoveAll(row => row.Id == model.Id);
                else throw new NotImplementedException(method.Name);
                rows.Add(model);
                return method.ReturnType == typeof(void) ? null : model;
            });
        }

        private SettingsBackupRestoreResult Restore(SettingsBackupPackage source, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Merge)
        {
            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(JsonSerializer.Serialize(source, STJson.GetSerializerSettings()), STJson.GetSerializerSettings());
            var result = new SettingsBackupRestoreResult();
            foreach (var method in new[] { "RestoreDelayProfiles", "RestoreReleaseProfiles" })
            {
                typeof(SettingsBackupService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_service, new object[] { package, package.Tags.ToDictionary(tag => tag.Id, tag => tag.Id + 1000), mode, result });
            }
            typeof(SettingsBackupService).GetMethod("RestoreQualitySizes", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_service, new object[] { package, result, mode });
            return result;
        }

        [Test]
        public void merge_must_preserve_existing_positions_and_append_in_backup_order()
        {
            _delays.Add(new DelayProfile { Id = 2, Order = 9, Tags = new() { 1001 } });
            var package = new SettingsBackupPackage
            {
                Tags = new() { new TagBackup { Id = 1, Label = "existing" }, new TagBackup { Id = 2, Label = "second" }, new TagBackup { Id = 3, Label = "third" } },
                DelayProfiles = new()
                {
                    new DelayProfile { Id = 9, Order = 2, Tags = new() { 3 } },
                    new DelayProfile { Id = 8, Order = 1, Tags = new() { 2 } },
                    new DelayProfile { Id = 7, Order = 0, Tags = new() { 1 }, UsenetDelay = 15 }
                }
            };
            Restore(package);
            Restore(package);
            Assert.That(_delays.Where(profile => profile.Id != 1).OrderBy(profile => profile.Order).Select(profile => (profile.Order, profile.Tags.Single())),
                Is.EqualTo(new[] { (9, 1001), (10, 1002), (11, 1003) }));
        }

        [Test]
        public void overwriting_a_delay_profile_cohort_must_delete_and_reorder_once()
        {
            var calls = new Dictionary<string, int>();
            var profiles = Enumerable.Range(1, 21).Select(id => new DelayProfile { Id = id, Order = id }).ToList();
            var repository = InterfaceProxy.Proxy<IDelayProfileRepository>((method, args) =>
            {
                calls[method.Name] = calls.GetValueOrDefault(method.Name) + 1;
                if (method.Name == "DeleteMany")
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    profiles.RemoveAll(profile => ids.Contains(profile.Id));
                    return null;
                }
                if (method.Name == "All") return profiles;
                if (method.Name == "UpdateMany") return null;
                throw new NotImplementedException(method.Name);
            });
            var service = new DelayProfileService(repository, new NzbDrone.Common.Cache.CacheManager());
            _service = new SettingsBackupService(null, null, null, null, null, null, null, null, null, null, null, null, null, null, delayProfileService: service, releaseProfileService: null, qualityDefinitionService: null, mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
            Restore(new SettingsBackupPackage { DelayProfiles = new() }, SettingsBackupRestoreMode.Overwrite);
            Assert.That(profiles.Single().Id, Is.EqualTo(1));
            Assert.That(calls, Is.EquivalentTo(new Dictionary<string, int> { ["DeleteMany"] = 1, ["All"] = 2, ["UpdateMany"] = 1 }));
        }

        [Test]
        public void merge_should_remap_references_without_changing_existing_enabled_choice()
        {
            _indexers.Add(new IndexerDefinition { Id = 24, Name = "my indexer" });
            _indexers.Add(new IndexerDefinition { Id = 23, Name = "My INDEXER" });
            _releases.Add(new ReleaseProfile { Id = 7, IndexerId = 23, Enabled = false, Required = new() { "B", "A" }, Tags = new() { 1004 } });
            var package = new SettingsBackupPackage
            {
                Tags = new() { new TagBackup { Id = 4, Label = "spoken" } },
                IndexerNames = new() { [89] = "my indexer" },
                ReleaseProfiles = new() { new ReleaseProfile { IndexerId = 89, Enabled = true, Required = new() { "a", "b" }, Tags = new() { 4 } } },
                DelayProfiles = new() { new DelayProfile { Id = 8, Tags = new() { 4 }, EnableTorrent = true, TorrentDelay = 30 } }
            };
            Restore(package);
            Restore(package);
            Assert.That(_releases, Has.Count.EqualTo(1));
            Assert.That(_releases.Single().Id, Is.EqualTo(7));
            Assert.That(_releases.Single().Enabled, Is.False);
            Assert.That(_releases.Single().IndexerId, Is.EqualTo(23));
            Assert.That(_delays, Has.Count.EqualTo(2));
            Assert.That(_delays.Single(row => row.Id != 1).Tags, Is.EquivalentTo(new[] { 1004 }));
        }

        [Test]
        public void merge_should_skip_partial_tag_overlap_with_named_warning()
        {
            _delays.Add(new DelayProfile { Id = 4, Tags = new() { 1001, 1002 }, EnableTorrent = true });
            var result = Restore(new SettingsBackupPackage
            {
                Tags = new() { new TagBackup { Id = 1, Label = "overlapping" }, new TagBackup { Id = 3, Label = "new" } },
                DelayProfiles = new() { new DelayProfile { Id = 8, Tags = new() { 1, 3 }, EnableTorrent = true } }
            });
            Assert.That(_delays, Has.Count.EqualTo(2));
            Assert.That(result.Warnings.Single(), Does.Contain("overlapping").And.Contain("overlap"));
        }

        [Test]
        public void merge_should_not_let_a_skipped_profile_block_a_later_valid_profile()
        {
            _delays.Add(new DelayProfile { Id = 4, Tags = new() { 1002, 1003 }, EnableTorrent = true });
            var result = Restore(new SettingsBackupPackage
            {
                Tags = Enumerable.Range(1, 4).Select(id => new TagBackup { Id = id, Label = $"tag-{id}" }).ToList(),
                DelayProfiles = new()
                {
                    new DelayProfile { Id = 8, Order = 0, Tags = new() { 1, 2 }, EnableTorrent = true },
                    new DelayProfile { Id = 9, Order = 1, Tags = new() { 1, 4 }, EnableTorrent = true }
                }
            });
            Assert.That(result.Warnings.Single(), Does.Contain("Skipped delay profile 8"));
            Assert.That(_delays, Has.Count.EqualTo(3));
            Assert.That(_delays.Any(profile => profile.Tags.SetEquals(new[] { 1001, 1004 })), Is.True);
        }

        [TestCase(1, 2)]
        [TestCase(4, 9)]
        public void overwrite_should_replace_routing_profiles_preserve_default_id_and_backup_priority(int firstOrder, int secondOrder)
        {
            _delays.Add(new DelayProfile { Id = 44, Tags = new() { 19 } });
            _releases.Add(new ReleaseProfile { Id = 8, Required = new() { "old" } });
            var package = new SettingsBackupPackage
            {
                Tags = new() { new TagBackup { Id = 1, Label = "first" }, new TagBackup { Id = 2, Label = "second" } },
                DelayProfiles = new()
                {
                    new DelayProfile { Id = 20, Order = secondOrder, Tags = new() { 2 }, EnableTorrent = true },
                    new DelayProfile { Id = 1, Order = int.MaxValue, TorrentDelay = 60, EnableTorrent = true },
                    new DelayProfile { Id = 10, Order = firstOrder, Tags = new() { 1 }, EnableTorrent = true }
                },
                ReleaseProfiles = new() { new ReleaseProfile { Required = new() { "new" } } }
            };
            Restore(package, SettingsBackupRestoreMode.Overwrite);
            Assert.That(_delays.Single(row => row.Id == 1).TorrentDelay, Is.EqualTo(60));
            Assert.That(_delays.OrderBy(row => row.Order).SelectMany(row => row.Tags), Is.EqualTo(new[] { 1001, 1002 }));
            Assert.That(_releases.Single().Required, Is.EqualTo(new[] { "new" }));
            Assert.That(_delays.Where(row => row.Id != 1).OrderBy(row => row.Order).Select(row => row.Order), Is.EqualTo(new[] { firstOrder, secondOrder }));
            Restore(package, SettingsBackupRestoreMode.Overwrite);
            Assert.That(_delays, Has.Count.EqualTo(3));
            Assert.That(_releases, Has.Count.EqualTo(1));
        }

        [Test]
        public void missing_indexer_must_skip_its_profile_instead_of_widening_to_all_indexers()
        {
            var result = Restore(new SettingsBackupPackage
            {
                IndexerNames = new() { [8] = "Absent indexer" },
                ReleaseProfiles = new() { new ReleaseProfile { IndexerId = 8 } }
            });
            Assert.That(_releases, Is.Empty);
            Assert.That(result.Warnings.Single(), Does.Contain("Absent indexer"));
        }

        [TestCase(SettingsBackupRestoreMode.Merge)]
        [TestCase(SettingsBackupRestoreMode.Overwrite)]
        public void restore_mode_should_control_existing_delays_and_quality_sizes(SettingsBackupRestoreMode mode)
        {
            _delays.Single().EnableTorrent = false;
            _delays.Single().TorrentDelay = 0;
            _qualities.Add(new QualityDefinition(Quality.MP3) { Id = 42, Title = "", MinSize = 0, MaxSize = 10 });
            Restore(new SettingsBackupPackage
            {
                DelayProfiles = new() { new DelayProfile { Id = 1, EnableTorrent = true, TorrentDelay = 60 } },
                QualityDefinitions = new() { new QualityDefinition(Quality.MP3) { Title = "MP3 backup", MinSize = 5, MaxSize = 30 } }
            }, mode);
            Assert.That(_delays.Single().EnableTorrent, Is.EqualTo(mode == SettingsBackupRestoreMode.Overwrite));
            Assert.That(_delays.Single().TorrentDelay, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? 0 : 60));
            Assert.That(_qualities.Single().Id, Is.EqualTo(42));
            Assert.That(_qualities.Single().Title, Is.EqualTo("MP3 backup"));
            Assert.That(_qualities.Single().MinSize, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? 0 : 5));
            Assert.That(_qualities.Single().MaxSize, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? 10 : 30));
        }

        [Test]
        public void older_backup_must_not_delete_sections_it_never_carried()
        {
            _releases.Add(new ReleaseProfile { Id = 8 });
            Restore(new SettingsBackupPackage(), SettingsBackupRestoreMode.Overwrite);
            Assert.That(_releases.Single().Id, Is.EqualTo(8));
            Assert.That(_delays.Single().Id, Is.EqualTo(1));
        }

        [Test]
        public void indexer_overwrite_should_warn_about_profiles_that_may_be_kept_by_a_partial_restore()
        {
            _indexers.AddRange(new[]
            {
                new IndexerDefinition { Id = 23, Name = "My INDEXER", Implementation = "Newznab" },
                new IndexerDefinition { Id = 24, Name = "Referenced" },
                new IndexerDefinition { Id = 25, Name = "Unreferenced" }
            });
            _releases.AddRange(new[] { new ReleaseProfile { Id = 1, IndexerId = 23 }, new ReleaseProfile { Id = 2, IndexerId = 24 } });
            var package = new SettingsBackupPackage { Indexers = new()
            {
                new IndexerDefinitionBackup { OriginalId = 89, Name = "my indexer", Implementation = "Newznab", ConfigContract = "NewznabSettings",
                    Settings = JsonSerializer.SerializeToElement(new NzbDrone.Core.Indexers.Newznab.NewznabSettings { BaseUrl = "https://example.invalid", ApiKey = "synthetic" }, STJson.GetSerializerSettings()) }
            } };
            var result = new SettingsBackupRestoreResult();
            typeof(SettingsBackupService).GetMethod("RestoreIndexers", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_service, new object[] { package, new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, int>(), SettingsBackupRestoreMode.Overwrite, result });
            Assert.That(_indexers.Single(row => row.Name == "my indexer").Id, Is.EqualTo(23));
            Assert.That(_releases.Single(row => row.Id == 1).IndexerId, Is.EqualTo(23));
            Assert.That(_indexers.Any(row => row.Id == 24), Is.False);
            Assert.That(_releases.Single(row => row.Id == 2).IndexerId, Is.EqualTo(24));
            Assert.That(_indexers.Any(row => row.Id == 25), Is.False);
            Assert.That(result.Warnings.Any(warning => warning.Contains("Referenced") && warning.Contains("no longer apply")), Is.True);
        }

        [TestCase(false, "none")]
        [TestCase(true, "none")]
        [TestCase(false, "tag")]
        [TestCase(true, "tag")]
        [TestCase(false, "indexer")]
        [TestCase(true, "indexer")]
        [TestCase(false, "overlap")]
        [TestCase(true, "overlap")]
        public void overwrite_should_keep_only_the_affected_profile_section_and_apply_valid_entries(bool postgres, string unresolved)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((_, _) => null);
                var delays = new DelayProfileService(new DelayProfileRepository(database, events), new NzbDrone.Common.Cache.CacheManager());
                var releases = new ReleaseProfileService(new ReleaseProfileRepository(database, events), LogManager.GetCurrentClassLogger());
                if (!delays.All().Any(profile => profile.Id == 1))
                    Assert.That(delays.Add(new DelayProfile { EnableTorrent = true }, int.MaxValue).Id, Is.EqualTo(1));
                var keptDelay = delays.Add(new DelayProfile { Tags = new() { 1004 }, TorrentDelay = 17, EnableTorrent = true }, 5);
                var updatedDelay = delays.Add(new DelayProfile { Tags = new() { 1001 }, TorrentDelay = 19, EnableTorrent = true }, 2);
                var keptRelease = releases.Add(new ReleaseProfile { Required = new() { "keep" }, Tags = new() { 1004 }, IndexerId = 23 });
                var updatedRelease = releases.Add(new ReleaseProfile { Required = new() { "update" }, Tags = new() { 1001 }, Enabled = false });
                _indexers.Add(new IndexerDefinition { Id = 23, Name = "Existing indexer" });
                _service = new SettingsBackupService(null, null, null, null,
                    Service<IIndexerFactory, IndexerDefinition>(_indexers), null, null, null, null, null, null, null, null, null,
                    delays, releases, Service<IQualityDefinitionService, QualityDefinition>(_qualities), mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
                var package = new SettingsBackupPackage
                {
                    Tags = new() { new TagBackup { Id = 1, Label = "first" }, new TagBackup { Id = 2, Label = "second" }, new TagBackup { Id = 3, Label = "third" } },
                    IndexerNames = new() { [8] = "Absent indexer" },
                    DelayProfiles = new()
                    {
                        new DelayProfile { Id = 10, Order = 0, Tags = new() { 1 }, EnableTorrent = true, TorrentDelay = 60 },
                        new DelayProfile { Id = 11, Order = 1, Tags = new() { 2 }, EnableTorrent = true, TorrentDelay = 90 },
                        new DelayProfile { Id = 1, Order = int.MaxValue, EnableTorrent = true, TorrentDelay = 30 }
                    },
                    ReleaseProfiles = new()
                    {
                        new ReleaseProfile { Required = new() { "update" }, Tags = new() { 1 }, Enabled = true },
                        new ReleaseProfile { Required = new() { "new" }, Tags = new() { 2 } }
                    }
                };
                if (unresolved is "tag" or "overlap")
                    package.DelayProfiles.Add(new DelayProfile { Id = 12, Order = 3, Tags = unresolved == "tag" ? new() { 99 } : new() { 1, 3 }, EnableTorrent = true });
                if (unresolved is "tag" or "indexer")
                    package.ReleaseProfiles.Add(new ReleaseProfile { Id = 13, Required = new() { "unavailable" }, Tags = unresolved == "tag" ? new() { 99 } : new(), IndexerId = unresolved == "indexer" ? 8 : 0 });
                var keepDelays = unresolved is "tag" or "overlap";
                var keepReleases = unresolved is "tag" or "indexer";
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var result = Restore(package, SettingsBackupRestoreMode.Overwrite);
                    var storedDelays = delays.All();
                    var storedReleases = releases.All();
                    Assert.That(storedDelays.Any(profile => profile.Id == keptDelay.Id), Is.EqualTo(keepDelays));
                    Assert.That(storedReleases.Any(profile => profile.Id == keptRelease.Id), Is.EqualTo(keepReleases));
                    Assert.That(storedDelays, Has.Count.EqualTo(keepDelays ? 4 : 3));
                    Assert.That(storedReleases, Has.Count.EqualTo(keepReleases ? 3 : 2));
                    Assert.That(storedDelays.Single(profile => profile.Id == 1).TorrentDelay, Is.EqualTo(30));
                    var updated = storedDelays.Single(profile => profile.Tags.SetEquals(new[] { 1001 }));
                    Assert.That(updated.TorrentDelay, Is.EqualTo(60));
                    Assert.That(updated.Order, Is.EqualTo(keepDelays ? 2 : 0));
                    Assert.That(storedDelays.Single(profile => profile.Tags.SetEquals(new[] { 1002 })).Order, Is.EqualTo(keepDelays ? 6 : 1));
                    Assert.That(storedReleases.Single(profile => profile.Required.Contains("update")).Enabled, Is.True);
                    Assert.That(storedReleases.Any(profile => profile.Required.Contains("unavailable")), Is.False);
                    if (keepDelays)
                    {
                        Assert.That(updated.Id, Is.EqualTo(updatedDelay.Id));
                        Assert.That(storedDelays.Single(profile => profile.Id == keptDelay.Id).TorrentDelay, Is.EqualTo(17));
                        Assert.That(result.Warnings, Has.Some.Contains("Skipped delay profile 12"));
                        Assert.That(result.Warnings, Has.Some.Contains("Kept existing delay profiles"));
                    }
                    if (keepReleases)
                    {
                        Assert.That(storedReleases.Single(profile => profile.Required.Contains("update")).Id, Is.EqualTo(updatedRelease.Id));
                        Assert.That(storedReleases.Single(profile => profile.Id == keptRelease.Id).IndexerId, Is.EqualTo(23));
                        Assert.That(result.Warnings, Has.Some.Contains("Skipped release profile 13"));
                        Assert.That(result.Warnings, Has.Some.Contains("Kept existing release profiles"));
                    }
                    if (unresolved == "indexer") Assert.That(result.Warnings, Has.Some.Contains("Absent indexer"));
                    if (unresolved == "none") Assert.That(result.Warnings, Is.Empty);
                }
            });
        }

        [Test]
        public void unknown_provider_should_be_named_and_skipped_while_supported_provider_is_restored()
        {
            var package = new SettingsBackupPackage
            {
                Indexers = new()
                {
                    new IndexerDefinitionBackup { Name = "Unavailable provider", ConfigContract = "NotInstalledSettings", Implementation = "NotInstalled", Settings = JsonSerializer.SerializeToElement(new { }) },
                    new IndexerDefinitionBackup { Name = "Supported provider", ConfigContract = "NewznabSettings", Implementation = "Newznab",
                        Settings = JsonSerializer.SerializeToElement(new NzbDrone.Core.Indexers.Newznab.NewznabSettings { BaseUrl = "https://example.invalid", ApiKey = "synthetic" }, STJson.GetSerializerSettings()) }
                }
            };
            package.Categories = new() { SettingsBackupCategory.Indexers };
            var folder = Path.Combine(Path.GetTempPath(), "settings-provider-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "providers.chaptarr-settings-backup.json");
                File.WriteAllText(file, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "synthetic passphrase"), STJson.GetSerializerSettings()));
                IDiskProvider disk = OsInfo.IsWindows ? new NzbDrone.Windows.Disk.DiskProvider()
                    : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, LogManager.GetCurrentClassLogger());
                var service = new SettingsBackupService(InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => folder), disk, null, null,
                    Service<IIndexerFactory, IndexerDefinition>(_indexers), null, null, null, null, null, null, null, null, null, delayProfileService: null, releaseProfileService: null, qualityDefinitionService: null, mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
                var result = service.RestoreBackup(new SettingsBackupRestoreRequest
                {
                    FilePath = file, Passphrase = "synthetic passphrase", Categories = package.Categories.ToHashSet(), Mode = SettingsBackupRestoreMode.Merge
                });
                Assert.That(result.Warnings.Single(), Does.Contain("Unavailable provider").And.Contain("NotInstalledSettings"));
                Assert.That(result.Applied, Does.Contain("Indexers"));
                Assert.That(_indexers.Single().Name, Is.EqualTo("Supported provider"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void quality_sizes_should_use_destination_identity_and_bulk_update()
        {
            _qualities.Add(new QualityDefinition(Quality.EPUB) { Id = 17, MinSize = 0, MaxSize = 100 });
            Restore(new SettingsBackupPackage { QualityDefinitions = new() { new QualityDefinition(Quality.EPUB) { Id = 82, MinSize = 1, MaxSize = 7 } } }, SettingsBackupRestoreMode.Overwrite);
            Assert.That(_qualities.Single().Id, Is.EqualTo(17));
            Assert.That(_qualities.Single().MinSize, Is.EqualTo(1));
            Assert.That(_qualities.Single().MaxSize, Is.EqualTo(7));
        }
    }
}
