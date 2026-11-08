using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using Chaptarr.Api.V1.DownloadClient;
using Chaptarr.Http.ClientSchema;
using DryIoc;
using NzbDrone.Core.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Tags;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    [NonParallelizable]
    public partial class SettingsBackupProviderIdentityRestoreFixture
    {
        [OneTimeSetUp]
        public void InitializeSchemaBuilder()
        {
            using var container = new Container();
            container.RegisterInstance<ILocalizationService>(Proxy<ILocalizationService>((_, args) => args[0]));
            SchemaBuilder.Initialize(container);
        }

        [TestCase(false, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(true, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(false, SettingsBackupRestoreMode.Merge)]
        [TestCase(true, SettingsBackupRestoreMode.Merge)]
        public void empty_indexer_backup_previews_every_deletion_before_applying(bool postgres, SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var first = context.AddIndexer();
                var second = context.AddIndexer();
                var package = new SettingsBackupPackage { Categories = new() { SettingsBackupCategory.Indexers }, Indexers = new() };
                var preview = context.Restore(package, mode, preview: true);
                Assert.That(preview.WouldRemove, Is.EquivalentTo(mode == SettingsBackupRestoreMode.Overwrite
                    ? new[] { $"Indexer: {first.Name}", $"Indexer: {second.Name}" } : Array.Empty<string>()));
                Assert.That(context.Indexers.All().Select(indexer => indexer.Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
                var restored = context.Restore(package, mode);
                Assert.That(restored.WouldRemove, Is.EqualTo(preview.WouldRemove));
                Assert.That(context.Indexers.All(), Has.Count.EqualTo(mode == SettingsBackupRestoreMode.Overwrite ? 0 : 2));
            });
        }

        [TestCase(false, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(true, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(false, SettingsBackupRestoreMode.Merge)]
        [TestCase(true, SettingsBackupRestoreMode.Merge)]
        public void client_name_case_change_keeps_identity_and_mapping(bool postgres, SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var client = context.Clients.Insert(Client("Deluge Home"));
                var mapping = context.AddMapping(client.Id);
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients },
                    DownloadClients = new() { ClientBackup("DELUGE HOME") }
                };
                Assert.That(context.Restore(package, mode, preview: true).WouldRemove, Is.Empty);
                var result = context.Restore(package, mode);
                Assert.That(result.Warnings, Is.Empty);
                Assert.That(context.Clients.All().Single().Id, Is.EqualTo(client.Id));
                Assert.That(context.Mappings.Get(mapping.Id).DownloadClientId, Is.EqualTo(client.Id));
                Assert.That(((DelugeSettings)context.Clients.Get(client.Id).Settings).Host,
                    Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? "original.example" : "restored.example"));
            });
        }

        [TestCase(false, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(true, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(false, SettingsBackupRestoreMode.Merge)]
        [TestCase(true, SettingsBackupRestoreMode.Merge)]
        public void client_restore_should_keep_matching_identity_and_unselected_mappings(bool postgres, SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var kept = context.Clients.Insert(Client("Deluge Home"));
                var omitted = context.Clients.Insert(Client("Deluge Seedbox"));
                var keptMapping = context.AddMapping(kept.Id);
                var omittedMapping = context.AddMapping(omitted.Id);
                var indexer = context.AddIndexer(clientId: kept.Id);
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients },
                    DownloadClients = new() { ClientBackup("Deluge Home"), ClientBackup("New client", 901) }
                };

                var preview = context.Restore(package, mode, preview: true);
                Assert.That(preview.WouldRemove, Is.EqualTo(mode == SettingsBackupRestoreMode.Overwrite
                    ? new[] { "Download client: Deluge Seedbox", "Remote path mapping: original.example:/remote → /local" } : Array.Empty<string>()));
                Assert.That(preview.Applied, Is.Empty);
                Assert.That(context.Clients.All().Select(client => client.Id), Is.EquivalentTo(new[] { kept.Id, omitted.Id }));
                Assert.That(((DelugeSettings)context.Clients.Get(kept.Id).Settings).Host, Is.EqualTo("original.example"));
                Assert.That(context.Mappings.Get(omittedMapping.Id).DownloadClientId, Is.EqualTo(omitted.Id));
                var result = context.Restore(package, mode);

                Assert.That(context.Clients.All().Single(c => c.Name == "Deluge Home").Id, Is.EqualTo(kept.Id));
                var settings = (DelugeSettings)context.Clients.Get(kept.Id).Settings;
                Assert.That(settings.Host, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? "original.example" : "restored.example"));
                Assert.That(settings.Port, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? new DelugeSettings().Port : 9123));
                Assert.That(settings.Password, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? "synthetic original password" : "synthetic restored password"));
                Assert.That(context.Mappings.Get(keptMapping.Id).DownloadClientId, Is.EqualTo(kept.Id));
                Assert.That(context.Indexers.Get(indexer.Id).DownloadClientId, Is.EqualTo(kept.Id));
                Assert.That(context.Clients.All().Single(c => c.Name == "New client").Id, Is.Not.EqualTo(901));
                Assert.That(context.Clients.Find(omitted.Id) != null, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge));
                Assert.That(context.Mappings.Find(omittedMapping.Id) != null, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge));
                if (mode == SettingsBackupRestoreMode.Overwrite)
                    Assert.That(result.Warnings.Any(w => w.Contains("Deluge Seedbox") && w.Contains("mapping")), Is.True);
            });
        }

        [TestCase(false, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(true, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(false, SettingsBackupRestoreMode.Merge)]
        [TestCase(true, SettingsBackupRestoreMode.Merge)]
        public void proxy_restore_should_keep_matching_identity_and_unselected_indexer_assignments(bool postgres, SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var kept = context.Proxies.Insert(ProxyDefinition("Private proxy"));
                var omitted = context.Proxies.Insert(ProxyDefinition("Omitted proxy"));
                var keptIndexer = context.AddIndexer(proxyId: kept.Id);
                var omittedIndexer = context.AddIndexer(proxyId: omitted.Id);
                context.Config[nameof(IConfigService.ProxyMode)] = ProxyMode.IndexerOnly;
                context.Config[nameof(IConfigService.GlobalProxyId)] = kept.Id;
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.Proxies },
                    Proxies = new() { ProxyBackup("PRIVATE PROXY"), ProxyBackup("New proxy", 901) }
                };

                var result = context.Restore(package, mode);

                Assert.That(context.Proxies.All().Single(p => p.Name == (mode == SettingsBackupRestoreMode.Merge ? "Private proxy" : "PRIVATE PROXY")).Id, Is.EqualTo(kept.Id));
                Assert.That(context.Proxies.Get(kept.Id).Hostname, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? "original.example" : "restored.example"));
                Assert.That(context.Proxies.Get(kept.Id).Port, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? 8112 : 9123));
                Assert.That(context.Indexers.Get(keptIndexer.Id).ProxyId, Is.EqualTo(kept.Id));
                Assert.That(context.Config[nameof(IConfigService.GlobalProxyId)], Is.EqualTo(kept.Id));
                Assert.That(context.Config[nameof(IConfigService.ProxyMode)], Is.EqualTo(ProxyMode.IndexerOnly));
                Assert.That(context.Proxies.All().Single(p => p.Name == "New proxy").Id, Is.Not.EqualTo(901));
                Assert.That(context.Indexers.Get(omittedIndexer.Id).ProxyId, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? omitted.Id : (int?)null));
                Assert.That(context.Proxies.Find(omitted.Id) != null, Is.EqualTo(mode == SettingsBackupRestoreMode.Merge));
                if (mode == SettingsBackupRestoreMode.Overwrite)
                    Assert.That(result.Warnings.Any(w => w.Contains("Omitted proxy") && w.Contains("assignment")), Is.True);
            });
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void overwrite_should_not_treat_a_renamed_or_different_client_type_as_the_same_client(bool postgres, bool differentType)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var old = Client(differentType ? "Deluge Home" : "Renamed client");
                if (differentType) old.Implementation = "Transmission";
                context.Clients.Insert(old);
                var mapping = context.AddMapping(old.Id);
                context.Restore(new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients },
                    DownloadClients = new() { ClientBackup("Deluge Home", old.Id) }
                });
                Assert.That(context.Clients.All().Single().Id, Is.Not.EqualTo(old.Id));
                Assert.That(context.Mappings.Find(mapping.Id), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void selected_mappings_should_use_retained_and_new_destination_ids(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var kept = context.Clients.Insert(Client("Deluge Home"));
                context.AddMapping(kept.Id);
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients, SettingsBackupCategory.RemotePathMappings },
                    DownloadClients = new() { ClientBackup("Deluge Home"), ClientBackup("New client", 901) },
                    RemotePathMappings = new()
                    {
                        new RemotePathMappingBackup { DownloadClientId = 900, Host = "restored.example", RemotePath = "/home-downloads", LocalPath = Path.GetTempPath() },
                        new RemotePathMappingBackup { DownloadClientId = 901, Host = "restored.example", RemotePath = "/new-downloads", LocalPath = Path.GetTempPath() }
                    }
                };

                context.Restore(package);

                Assert.That(context.Clients.All().Single(c => c.Name == "Deluge Home").Id, Is.EqualTo(kept.Id));
                var newId = context.Clients.All().Single(c => c.Name == "New client").Id;
                Assert.That(context.Mappings.All(), Has.Count.EqualTo(2));
                Assert.That(context.Mappings.All().Single(m => m.RemotePath.TrimEnd('/', '\\') == "/home-downloads").DownloadClientId, Is.EqualTo(kept.Id));
                Assert.That(context.Mappings.All().Single(m => m.RemotePath.TrimEnd('/', '\\') == "/new-downloads").DownloadClientId, Is.EqualTo(newId));
            });
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void ambiguous_names_skip_only_the_affected_category(bool postgres, bool proxies, bool existingDuplicates)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                context.Clients.Insert(Client("Repeated"));
                context.Proxies.Insert(ProxyDefinition("Repeated"));
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients, SettingsBackupCategory.Proxies },
                    DownloadClientSettings = new() { DownloadClientHistoryLimit = 20 },
                    DownloadClients = new() { ClientBackup("Repeated") },
                    Proxies = new() { ProxyBackup("Repeated") }
                };
                if (existingDuplicates)
                {
                    if (proxies) context.Proxies.Insert(ProxyDefinition("REPEATED"));
                    else context.Clients.Insert(Client("Repeated"));
                }
                else if (proxies) package.Proxies.Add(ProxyBackup("REPEATED", 901));
                else package.DownloadClients.Add(ClientBackup("Repeated", 901));

                var beforeClients = JsonSerializer.Serialize(context.Clients.All(), STJson.GetSerializerSettings());
                var beforeProxies = JsonSerializer.Serialize(context.Proxies.All(), STJson.GetSerializerSettings());
                var result = context.Restore(package);
                Assert.That(result.Warnings, Has.Some.Contains("Repeated").IgnoreCase.And.Contains("duplicate").IgnoreCase);
                if (proxies)
                {
                    Assert.That(JsonSerializer.Serialize(context.Proxies.All(), STJson.GetSerializerSettings()), Is.EqualTo(beforeProxies));
                    Assert.That(((DelugeSettings)context.Clients.All().Single().Settings).Host, Is.EqualTo("restored.example"));
                    Assert.That(result.Applied, Does.Contain("Download Clients").And.Not.Contain("Proxy Settings"));
                }
                else
                {
                    Assert.That(JsonSerializer.Serialize(context.Clients.All(), STJson.GetSerializerSettings()), Is.EqualTo(beforeClients));
                    Assert.That(context.Proxies.All().Single().Hostname, Is.EqualTo("restored.example"));
                    Assert.That(result.Applied, Does.Contain("Proxy Settings").And.Not.Contain("Download Clients"));
                }
            });
        }

        [TestCase(false, SettingsBackupRestoreMode.Merge)]
        [TestCase(true, SettingsBackupRestoreMode.Merge)]
        [TestCase(false, SettingsBackupRestoreMode.Overwrite)]
        [TestCase(true, SettingsBackupRestoreMode.Overwrite)]
        public void disabled_client_saved_by_the_editor_should_roundtrip_and_still_require_credentials_when_enabled(bool postgres, SettingsBackupRestoreMode mode)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var controller = new DownloadClientController(context.ClientFactory)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                var draft = new DownloadClientDefinition
                {
                    Name = "Unfinished SABnzbd", Implementation = "Sabnzbd", Enable = false,
                    Settings = new SabnzbdSettings()
                };
                controller.CreateProvider(DownloadClientController.ResourceMapper.ToResource(draft));
                var original = context.Clients.All().Single();
                Assert.That(original.Enable, Is.False);
                Assert.That(original.Settings.Validate().IsValid, Is.False);
                var package = context.BackupClients();
                Assert.That(package.DownloadClients.Single().Enable, Is.False);
                context.Clients.Delete(original.Id);
                context.Config.Clear();

                var result = context.Restore(package, mode);
                Assert.That(result.Applied, Does.Contain("Download Clients"));
                var restored = context.Clients.All().Single();
                Assert.That(restored.Name, Is.EqualTo(original.Name));
                Assert.That(restored.Enable, Is.False);
                var settings = (SabnzbdSettings)restored.Settings;
                Assert.That(settings.ApiKey, Is.Null.Or.Empty);
                Assert.That(settings.Username, Is.Null.Or.Empty);
                Assert.That(settings.Password, Is.Null.Or.Empty);
                var resource = DownloadClientController.ResourceMapper.ToResource(restored);
                resource.Enable = true;
                Assert.Throws<FluentValidation.ValidationException>(() => controller.UpdateProvider(resource));
                Assert.That(context.Clients.Get(restored.Id).Enable, Is.False);

                package.DownloadClients.Single().Enable = true;
                package.DownloadClients.Single().Name = "New enabled SABnzbd";
                context.Config.Clear();
                var error = Assert.Throws<NzbDroneClientException>(() => context.Restore(package, mode));
                Assert.That(error.Message, Does.Contain("ApiKey").And.Contain("No settings were restored"));
                Assert.That(context.Clients.Get(restored.Id).Enable, Is.False);
                Assert.That(context.Config, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void merge_should_fill_credentials_without_overwriting_values_or_creating_duplicates(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var client = Client("Deluge Home");
                ((DelugeSettings)client.Settings).Password = " ";
                client.Enable = false;
                client.RemoveCompletedDownloads = false;
                client.Priority = 0;
                client.AudiobookTags = new() { 41 };
                client = context.Clients.Insert(client);
                var backup = ClientBackup(client.Name);
                backup.Enable = true;
                backup.Priority = 5;
                backup.RemoveCompletedDownloads = true;
                backup.AudiobookTags = new() { 99 };
                var package = new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients },
                    DownloadClients = new() { backup },
                    DownloadClientSettings = new() { DownloadClientHistoryLimit = 50 }
                };
                context.Config[nameof(IConfigService.DownloadClientHistoryLimit)] = 0;
                context.Restore(package, SettingsBackupRestoreMode.Merge);
                context.Restore(package, SettingsBackupRestoreMode.Merge);
                var restored = context.Clients.All().Single();
                var settings = (DelugeSettings)restored.Settings;
                Assert.Multiple(() =>
                {
                    Assert.That(restored.Id, Is.EqualTo(client.Id));
                    Assert.That(settings.Host, Is.EqualTo("original.example"));
                    Assert.That(settings.Password, Is.EqualTo("synthetic restored password"));
                    Assert.That(restored.Enable, Is.False);
                    Assert.That(restored.RemoveCompletedDownloads, Is.False);
                    Assert.That(restored.Priority, Is.Zero);
                    Assert.That(restored.AudiobookTags, Is.EquivalentTo(new[] { 41 }));
                    Assert.That(context.Config[nameof(IConfigService.DownloadClientHistoryLimit)], Is.EqualTo(0));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void merge_should_skip_a_different_client_type_with_the_same_name_without_writing(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                using var context = new RestoreContext(db);
                var original = context.Clients.Insert(Client("Home"));
                var incoming = new DownloadClientDefinitionBackup
                {
                    Name = "Home", Implementation = "Sabnzbd", ConfigContract = nameof(SabnzbdSettings), Enable = false,
                    Settings = JsonSerializer.SerializeToElement(new SabnzbdSettings(), STJson.GetSerializerSettings())
                };
                var result = context.Restore(new SettingsBackupPackage
                {
                    Categories = new() { SettingsBackupCategory.DownloadClients },
                    DownloadClients = new() { incoming },
                    DownloadClientSettings = new() { DownloadClientHistoryLimit = 50 }
                }, SettingsBackupRestoreMode.Merge);
                Assert.That(result.Warnings, Has.Some.Contains("Home").And.Contains("Deluge").And.Contains("Skipped"));
                Assert.That(result.Applied, Is.Empty);
                Assert.That(context.Clients.All().Single().Id, Is.EqualTo(original.Id));
                Assert.That(context.Config, Is.Empty);
            });
        }

        private static DownloadClientDefinition Client(string name) => new()
        {
            Name = name, Implementation = "Deluge", ConfigContract = "DelugeSettings",
            Settings = new DelugeSettings { Host = "original.example", Password = "synthetic original password" }
        };

        private static DownloadClientDefinitionBackup ClientBackup(string name, int id = 900) => new()
        {
            OriginalId = id, Name = name, Implementation = "Deluge", ConfigContract = "DelugeSettings",
            Settings = JsonSerializer.SerializeToElement(new DelugeSettings { Host = "restored.example", Port = 9123, Password = "synthetic restored password" }, STJson.GetSerializerSettings())
        };

        private static ProxyDefinition ProxyDefinition(string name) => new() { Name = name, Hostname = "original.example", Port = 8112, ProxyType = ProxyType.Http };
        private static ProxyDefinitionBackup ProxyBackup(string name, int id = 900) => new() { OriginalId = id, Name = name, Hostname = "restored.example", Port = 9123, ProxyType = ProxyType.Http };

        private sealed class RestoreContext : IDisposable
        {
            private readonly string _folder = Path.Combine(Path.GetTempPath(), "settings-identity-" + Guid.NewGuid().ToString("N"));
            private readonly SettingsBackupService _service;
            public DownloadClientRepository Clients { get; }
            public DownloadClientFactory ClientFactory { get; }
            public RemotePathMappingRepository Mappings { get; }
            public ProxyRepository Proxies { get; }
            public IndexerRepository Indexers { get; }
            public NotificationRepository Notifications { get; }
            public CustomFormatRepository Formats { get; }
            public DelayProfileService Delays { get; }
            public ReleaseProfileService Releases { get; }
            public TagService Tags { get; }
            public string Folder => _folder;
            public Dictionary<string, object> Config { get; } = new();

            public RestoreContext(IMainDatabase database)
            {
                var logger = LogManager.GetCurrentClassLogger();
                RemotePathMappingService mappingService = null;
                ProxyReferenceCleanupService cleanup = null;
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (method.Name == "PublishEvent" && args[0] is ProviderDeletedEvent<IDownloadClient> clientDeleted) mappingService.Handle(clientDeleted);
                    if (method.Name == "PublishEvent" && args[0] is ProxyDeletedEvent proxyDeleted) cleanup.Handle(proxyDeleted);
                    return null;
                });
                Clients = new DownloadClientRepository(database, events);
                Mappings = new RemotePathMappingRepository(database, events);
                Proxies = new ProxyRepository(database, events);
                Indexers = new IndexerRepository(database, events);
                Notifications = new NotificationRepository(database, events);
                var notifications = Proxy<INotificationFactory>((method, args) =>
                {
                    if (method.Name == "All") return Notifications.All().ToList();
                    if (method.Name == "Delete") { Notifications.DeleteMany(((IEnumerable<int>)args[0]).ToList()); return null; }
                    if (method.Name == "Create") return Notifications.Insert((NotificationDefinition)args[0]);
                    if (method.Name == "Update") { Notifications.Update((NotificationDefinition)args[0]); return args[0]; }
                    throw new NotImplementedException(method.Name);
                });
                var config = Proxy<IConfigService>((method, args) =>
                {
                    if (method.Name == "SaveConfigDictionary")
                        foreach (var pair in (Dictionary<string, object>)args[0]) Config[pair.Key] = pair.Value;
                    else if (method.Name.StartsWith("set_")) Config[method.Name.Substring(4)] = args[0];
                    else if (method.Name.StartsWith("get_") && Config.TryGetValue(method.Name.Substring(4), out var value)) return value;
                    return method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
                });
                var indexers = Proxy<IIndexerFactory>((method, args) =>
                {
                    if (method.Name == "All") return Indexers.All().ToList();
                    if (method.Name == "Delete") { Indexers.DeleteMany(((IEnumerable<int>)args[0]).ToList()); return null; }
                    if (method.Name == "Create") return Indexers.Insert((IndexerDefinition)args[0]);
                    if (method.Name == "Update")
                    {
                        if (args[0] is IndexerDefinition item) Indexers.Update(item);
                        else Indexers.UpdateMany(((IEnumerable<IndexerDefinition>)args[0]).ToList());
                        return args[0];
                    }
                    throw new NotImplementedException(method.Name);
                });
                IDiskProvider disk = OsInfo.IsWindows ? new NzbDrone.Windows.Disk.DiskProvider()
                    : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, logger);
                mappingService = new RemotePathMappingService(Clients, Mappings, disk, new CacheManager(), logger);
                var proxies = new ProxyService(Proxies, events, logger);
                cleanup = new ProxyReferenceCleanupService(config, proxies, indexers, logger);
                var deluge = new Deluge(null, null, null, config, disk, mappingService, null, null, null, new NzbDrone.Common.Cache.CacheManager(), logger);
                var sab = new Sabnzbd(null, null, config, disk, mappingService, null, Chaptarr.Core.Test.Download.NoopDownloadClientFileSnapshotService.Instance, logger, Chaptarr.Core.Test.Download.DownloadClientTestRoots.Empty);
                var providers = new IDownloadClient[] { deluge, sab };
                ClientFactory = new DownloadClientFactory(null, Clients, providers,
                    Proxy<IServiceProvider>((_, args) => providers.Single(provider => provider.GetType() == (Type)args[0])), events, logger);
                Formats = new CustomFormatRepository(database, events);
                Delays = new DelayProfileService(new DelayProfileRepository(database, events), new CacheManager());
                Releases = new ReleaseProfileService(new ReleaseProfileRepository(database, events), logger);
                Tags = new TagService(new TagRepository(database, events), events, null, null, null, null, null, null, null, null);
                var formats = Proxy<ICustomFormatService>((method, args) => method.Name switch
                {
                    "All" => Formats.All().ToList(),
                    "Insert" => Formats.Insert((CustomFormat)args[0]),
                    "Update" => Formats.Update((CustomFormat)args[0]),
                    "Delete" => DeleteFormat((int)args[0]),
                    _ => throw new NotImplementedException(method.Name)
                });
                _service = new SettingsBackupService(Proxy<IAppFolderInfo>((_, _) => _folder), disk, config, null,
                    indexers, ClientFactory, notifications,
                    Proxy<IQualityProfileService>((_, _) => new List<QualityProfile>()),
                    Proxy<IMetadataProfileService>((_, _) => new List<MetadataProfile>()),
                    formats, Tags, proxies, mappingService, null, Delays, Releases,
                    Proxy<IQualityDefinitionService>((method, _) => method.Name == "All" ? new List<QualityDefinition>() : null),
                    mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
                Directory.CreateDirectory(_folder);
            }

            private object DeleteFormat(int id)
            {
                Formats.Delete(id);
                return null;
            }

            public RemotePathMapping AddMapping(int clientId) => Mappings.Insert(new RemotePathMapping
            {
                DownloadClientId = clientId, Host = "original.example", RemotePath = "/remote", LocalPath = "/local"
            });

            public IndexerDefinition AddIndexer(int clientId = 0, int? proxyId = null) => Indexers.Insert(new IndexerDefinition
            {
                Name = Guid.NewGuid().ToString("N"), Implementation = "Newznab", ConfigContract = "NewznabSettings",
                Settings = new NewznabSettings { BaseUrl = "https://indexer.example" }, DownloadClientId = clientId, ProxyId = proxyId
            });

            public SettingsBackupPackage BackupClients()
            {
                var backup = _service.CreateBackup(new SettingsBackupCreateRequest
                {
                    RootFolder = _folder, FileName = "export", Passphrase = "synthetic passphrase",
                    Categories = new() { SettingsBackupCategory.DownloadClients }
                });
                var envelope = JsonSerializer.Deserialize<SettingsBackupEnvelope>(File.ReadAllText(backup.Path), STJson.GetSerializerSettings());
                return JsonSerializer.Deserialize<SettingsBackupPackage>(SettingsBackupCrypto.Decrypt(envelope, "synthetic passphrase"), STJson.GetSerializerSettings());
            }

            public SettingsBackupRestoreResult Restore(SettingsBackupPackage package, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Overwrite, bool preview = false)
            {
                var file = Path.Combine(_folder, "identity.chaptarr-settings-backup.json");
                File.WriteAllText(file, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "synthetic passphrase"), STJson.GetSerializerSettings()));
                return _service.RestoreBackup(new SettingsBackupRestoreRequest
                {
                    FilePath = file, Passphrase = "synthetic passphrase", Categories = package.Categories.ToHashSet(), Mode = mode, Preview = preview
                });
            }

            public void Dispose() => Directory.Delete(_folder, true);
        }
    }
}
