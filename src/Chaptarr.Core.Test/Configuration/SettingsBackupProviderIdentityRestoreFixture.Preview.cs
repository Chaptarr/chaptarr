using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Tags;

namespace Chaptarr.Core.Test.Configuration
{
    public partial class SettingsBackupProviderIdentityRestoreFixture
    {
        [TestCase(false, "valid")]
        [TestCase(true, "valid")]
        [TestCase(false, "tag")]
        [TestCase(true, "tag")]
        [TestCase(false, "overlap")]
        [TestCase(true, "overlap")]
        [TestCase(false, "indexer-missing")]
        [TestCase(true, "indexer-missing")]
        [TestCase(false, "indexer-added")]
        [TestCase(true, "indexer-added")]
        [TestCase(false, "indexer-removed")]
        [TestCase(true, "indexer-removed")]
        [TestCase(false, "empty")]
        [TestCase(true, "empty")]
        [TestCase(false, "null")]
        [TestCase(true, "null")]
        [TestCase(false, "merge")]
        [TestCase(true, "merge")]
        public void profile_preview_lists_exactly_the_rows_overwrite_will_delete(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var oldTag = context.Tags.Add(new Tag { Label = "existing" });
                if (!context.Delays.All().Any(profile => profile.Id == 1)) context.Delays.Add(new DelayProfile { EnableTorrent = true }, int.MaxValue);
                var oldDelay = context.Delays.Add(new DelayProfile { Tags = new() { oldTag.Id }, EnableTorrent = true }, 1);
                var oldRelease = context.Releases.Add(new ReleaseProfile { Required = new() { "existing" } });
                var oldFormat = context.Formats.Insert(new CustomFormat { Name = "Existing format", Specifications = new() });
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.Profiles },
                    Tags = new() { new TagBackup { Id = 801, Label = "first" }, new TagBackup { Id = 802, Label = "second" } },
                    CustomFormats = new() { new CustomFormatBackup { OriginalId = 900, Name = "Restored format", Specifications = new() } },
                    DelayProfiles = new() { new DelayProfile { Id = 50, Order = 0, EnableTorrent = true, Tags = new() { 801 } } },
                    ReleaseProfiles = new() { new ReleaseProfile { Id = 60, Required = new() { "restored" }, Tags = new() { 801 } } }
                };
                if (scenario == "tag")
                {
                    package.DelayProfiles[0].Tags.Add(999);
                    package.ReleaseProfiles[0].Tags.Add(999);
                }
                if (scenario == "overlap") package.DelayProfiles.Add(new DelayProfile { Id = 51, Order = 1, EnableTorrent = true, Tags = new() { 801, 802 } });
                if (scenario.StartsWith("indexer-"))
                {
                    package.ReleaseProfiles[0].IndexerId = 700;
                    package.IndexerNames[700] = "Restored indexer";
                    if (scenario == "indexer-added")
                    {
                        package.Categories.Add(SettingsBackupCategory.Indexers);
                        package.Indexers.Add(new IndexerDefinitionBackup
                        {
                            OriginalId = 700, Name = "Restored indexer", Implementation = "Newznab", ConfigContract = "NewznabSettings",
                            Settings = JsonSerializer.SerializeToElement(new NewznabSettings { BaseUrl = "https://indexer.example" }, STJson.GetSerializerSettings())
                        });
                    }
                    if (scenario == "indexer-removed")
                    {
                        package.Categories.Add(SettingsBackupCategory.Indexers);
                        var current = context.AddIndexer();
                        package.IndexerNames[700] = current.Name;
                    }
                }
                if (scenario is "empty" or "null")
                {
                    package.CustomFormats = new();
                    package.DelayProfiles = scenario == "empty" ? new() : null;
                    package.ReleaseProfiles = scenario == "empty" ? new() : null;
                }
                var mode = scenario == "merge" ? SettingsBackupRestoreMode.Merge : SettingsBackupRestoreMode.Overwrite;
                var expected = new List<string>();
                if (mode == SettingsBackupRestoreMode.Overwrite)
                {
                    if (scenario is not ("empty" or "null")) expected.Add("Custom format: Existing format");
                    if (scenario is not ("tag" or "overlap" or "null")) expected.Add($"Delay profile: existing (#{oldDelay.Id})");
                    if (scenario is not ("tag" or "indexer-missing" or "indexer-removed" or "null")) expected.Add($"Release profile: +existing (#{oldRelease.Id})");
                    if (scenario == "indexer-removed") expected.Add("Indexer: " + context.Indexers.All().Single().Name);
                }
                var preview = context.Restore(package, mode, preview: true);
                Assert.That(preview.WouldRemove, Is.EquivalentTo(expected));
                Assert.That(context.Tags.All(), Has.Count.EqualTo(1), "Preview must not create tags");
                Assert.That(context.Formats.Find(oldFormat.Id), Is.Not.Null);
                Assert.That(context.Delays.All().Any(profile => profile.Id == oldDelay.Id), Is.True);
                Assert.That(context.Releases.All().Any(profile => profile.Id == oldRelease.Id), Is.True);
                Assert.That(context.Config, Is.Empty, "Preview must not save settings");
                if (mode == SettingsBackupRestoreMode.Overwrite)
                    Assert.That(preview.Warnings, Has.Some.Contains("Quality and metadata profiles are merged by name"));
                var restored = context.Restore(package, mode);
                Assert.That(restored.WouldRemove, Is.EquivalentTo(preview.WouldRemove));
                Assert.That(context.Formats.Find(oldFormat.Id) == null, Is.EqualTo(expected.Contains("Custom format: Existing format")));
                Assert.That(context.Delays.All().All(profile => profile.Id != oldDelay.Id), Is.EqualTo(expected.Contains($"Delay profile: existing (#{oldDelay.Id})")));
                Assert.That(context.Releases.All().All(profile => profile.Id != oldRelease.Id), Is.EqualTo(expected.Contains($"Release profile: +existing (#{oldRelease.Id})")));
                Assert.That(context.Delays.All().Any(profile => profile.Id == 1), Is.True, "The default delay profile survives");
                Assert.That(restored.Warnings, Is.Unique);
            });
        }

        [Test]
        public void connection_preview_equals_actual_deletions_and_keeps_retained_row_ids(
            [Values] bool postgres, [Values] bool empty, [Values] SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                NotificationDefinition Add(string name) => context.Notifications.Insert(new NotificationDefinition
                {
                    Name = name, Implementation = "AudioBookShelf", ConfigContract = "AudioBookShelfSettings",
                    OnGrab = true, Settings = new AudioBookShelfSettings { Host = "localhost", Port = 80, ApiKey = "synthetic" }
                });
                var retained = Add("Retained connection");
                var removed = Add("Old connection");
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.Connections },
                    Connections = new()
                };
                if (!empty) package.Connections.Add(new NotificationDefinitionBackup
                {
                    Name = retained.Name, Implementation = retained.Implementation, ConfigContract = retained.ConfigContract,
                    OnGrab = false, Settings = JsonSerializer.SerializeToElement(retained.Settings, retained.Settings.GetType(), STJson.GetSerializerSettings())
                });
                var before = context.Notifications.All().ToList();
                var expected = mode == SettingsBackupRestoreMode.Overwrite
                    ? (empty ? before : new() { removed }) : new List<NotificationDefinition>();
                var preview = context.Restore(package, mode, preview: true);
                Assert.That(preview.WouldRemove, Is.EquivalentTo(expected.Select(item => "Connection: " + item.Name)));
                Assert.That(context.Notifications.All().Select(item => item.Id), Is.EquivalentTo(before.Select(item => item.Id)));
                Assert.That(context.Notifications.Get(retained.Id).OnGrab, Is.True, "Preview never updates a connection");
                var actual = context.Restore(package, mode);
                var remaining = context.Notifications.All().Select(item => item.Id).ToHashSet();
                Assert.That(actual.WouldRemove, Is.EquivalentTo(preview.WouldRemove));
                Assert.That(before.Where(item => !remaining.Contains(item.Id)).Select(item => "Connection: " + item.Name),
                    Is.EquivalentTo(preview.WouldRemove));
                if (!empty)
                {
                    Assert.That(remaining, Does.Contain(retained.Id), "Overwrite updates the existing row; it does not recreate it");
                    Assert.That(context.Notifications.Get(retained.Id).OnGrab, Is.EqualTo(mode != SettingsBackupRestoreMode.Overwrite));
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void remote_mapping_preview_includes_selected_category_once_and_keeps_skipped_paths(bool postgres, bool removeClient)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var client = context.Clients.Insert(Client("Client to remove"));
                var removed = context.AddMapping(client.Id);
                var skipped = context.Mappings.Insert(new RemotePathMapping { Host = "other.example", RemotePath = "/keep", LocalPath = "/old" });
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.RemotePathMappings },
                    RemotePathMappings = new() { new RemotePathMappingBackup { Host = skipped.Host, RemotePath = skipped.RemotePath, LocalPath = context.Folder + "/missing" } }
                };
                if (removeClient) package.Categories.Add(SettingsBackupCategory.DownloadClients);
                var expected = new List<string> { "Remote path mapping: original.example:/remote → /local" };
                if (removeClient) expected.Add("Download client: Client to remove");
                var preview = context.Restore(package, preview: true);
                Assert.That(preview.WouldRemove, Is.EquivalentTo(expected));
                Assert.That(context.Mappings.All(), Has.Count.EqualTo(2));
                Assert.That(context.Restore(package).WouldRemove, Is.EquivalentTo(expected));
                Assert.That(context.Mappings.Find(removed.Id), Is.Null);
                Assert.That(context.Mappings.Get(skipped.Id).LocalPath, Is.EqualTo("/old"));
            });
        }

        [TestCase(false, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(true, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(false, SettingsBackupRestoreMode.Merge)]
        [TestCase(true, SettingsBackupRestoreMode.Merge)]
        public void proxy_preview_lists_only_removed_names_and_an_empty_backup_keeps_them(bool postgres, SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var kept = context.Proxies.Insert(ProxyDefinition("Existing proxy"));
                var removed = context.Proxies.Insert(ProxyDefinition("Old proxy"));
                var package = new SettingsBackupPackage { Categories = new() { SettingsBackupCategory.Proxies } };
                Assert.That(context.Restore(package, mode, preview: true).WouldRemove, Is.Empty);
                context.Restore(package, mode);
                Assert.That(context.Proxies.All(), Has.Count.EqualTo(2));
                package.Proxies.Add(ProxyBackup("EXISTING PROXY"));
                var expected = mode == SettingsBackupRestoreMode.Overwrite ? new[] { "Proxy: Old proxy" } : Array.Empty<string>();
                Assert.That(context.Restore(package, mode, preview: true).WouldRemove, Is.EquivalentTo(expected));
                Assert.That(context.Proxies.All(), Has.Count.EqualTo(2));
                Assert.That(context.Restore(package, mode).WouldRemove, Is.EquivalentTo(expected));
                Assert.That(context.Proxies.Find(removed.Id) == null, Is.EqualTo(mode == SettingsBackupRestoreMode.Overwrite));
                Assert.That(context.Proxies.Find(kept.Id), Is.Not.Null);
            });
        }
    }
}
