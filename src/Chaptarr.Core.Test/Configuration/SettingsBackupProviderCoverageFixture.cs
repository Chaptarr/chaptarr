using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.ThingiProvider;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    [NonParallelizable]
    public class SettingsBackupProviderCoverageFixture
    {
        // Discover source properties so a setting omitted from the backup DTO cannot escape coverage.
        private static readonly Dictionary<(Type Type, string Property), string> Exclusions = new()
        {
            [(typeof(ModelBase), nameof(ModelBase.Id))] = "Local IDs are exported as OriginalId and assigned by the destination factory",
            [(typeof(ProviderDefinition), nameof(ProviderDefinition.ConfigContract))] = "Derived from the settings type and checked on every materialized definition",
            [(typeof(IndexerDefinition), nameof(IndexerDefinition.Enable))] = "Derived from the three search/RSS switches",
            [(typeof(IndexerDefinition), nameof(IndexerDefinition.SupportsRss))] = "Runtime provider capability, reconstructed by IndexerFactory",
            [(typeof(IndexerDefinition), nameof(IndexerDefinition.SupportsSearch))] = "Runtime provider capability, reconstructed by IndexerFactory",
            [(typeof(NotificationDefinition), nameof(NotificationDefinition.Enable))] = "Derived from notification triggers and implementation",
            [(typeof(NotificationDefinition), nameof(NotificationDefinition.SupportsOnBookAdded))] = "Runtime provider capability, reconstructed by NotificationFactory",
            [(typeof(NotificationDefinition), nameof(NotificationDefinition.SupportsOnHealthRestored))] = "Runtime capability rather than a user-configured notification trigger",
            [(typeof(NewznabSettings), nameof(NewznabSettings.EnableEnhancedSearching))] = "Write-only legacy alias for EnableNarratorMetadata",
            [(typeof(NewznabSettings), nameof(NewznabSettings.EnhancedSearchingBaseUrl))] = "Write-only legacy alias for NarratorMetadataBaseUrl",
            [(typeof(NewznabSettings), nameof(NewznabSettings.EnhancedSearchingApiKey))] = "Write-only legacy alias for NarratorMetadataApiKey",
            [(typeof(MyAnonaMouseSettings), nameof(MyAnonaMouseSettings.SeedCriteria))] = "Derived from SeedTimeHours and NeverMoveOnImport; setter intentionally does nothing",
            [(typeof(AudioBookShelfSettings), nameof(AudioBookShelfSettings.ServerUrl))] = "Legacy input normalized to Host/Port/UseSsl/UrlBase during deserialization",
            [(typeof(AudioBookShelfSettings), nameof(AudioBookShelfSettings.LibraryMappingsJson))] = "Contains local root IDs deliberately cleared by RestoreConnections; covered by the connection restore fixture",
            [(typeof(AudioBookShelfSettings), nameof(AudioBookShelfSettings.AudiobookLibraryId))] = "Nonportable legacy library selection cleared by RestoreConnections",
            [(typeof(AudioBookShelfSettings), nameof(AudioBookShelfSettings.EbookLibraryId))] = "Nonportable legacy library selection cleared by RestoreConnections",
            [(typeof(AudioBookShelfSettings), nameof(AudioBookShelfSettings.LibraryId))] = "Nonportable legacy library selection cleared by RestoreConnections"
        };

        private static readonly Type[] DefinitionTypes =
        {
            typeof(IndexerDefinition), typeof(DownloadClientDefinition), typeof(NotificationDefinition)
        };

        private static IEnumerable<TestCaseData> ProviderSettings()
        {
            return typeof(IProviderConfig).Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(IProviderConfig).IsAssignableFrom(type))
                .Where(type => DefinitionTypeForSettings(type) != null)
                .OrderBy(type => type.FullName)
                .Select(type => new TestCaseData(type).SetName($"backup_should_preserve_{type.Name}_settings"));
        }

        [TestCaseSource(nameof(DefinitionTypes))]
        public void backup_should_preserve_every_persisted_provider_definition_setting(Type definitionType)
        {
            EnsureMappings();
            var ignored = TableMapping.Mapper.ExcludeProperties(definitionType).Select(property => property.Name).ToHashSet();
            var properties = WritableProperties(definitionType)
                .Where(property => property.IsMappableProperty() && !ignored.Contains(property.Name))
                .Where(property => property.Name != nameof(ProviderDefinition.Settings))
                .ToList();
            Assert.That(properties, Is.Not.Empty);

            Assert.Multiple(() =>
            {
                foreach (var property in properties)
                {
                    var source = CreateDefinition(definitionType, CreateSettings(definitionType));
                    var original = property.GetValue(source);
                    property.SetValue(source, SettingsBackupTestValues.DifferentValue(property.PropertyType, original));
                    var expected = property.GetValue(source);
                    Assert.That(Equivalent(expected, original, property.PropertyType), Is.False,
                        $"{definitionType.Name}.{property.Name} needs a value that differs from its default");

                    var restored = RoundTrip(source);
                    Assert.That(Equivalent(property.GetValue(restored), expected, property.PropertyType), Is.True,
                        $"{definitionType.Name}.{property.Name} is missing or changed in ToBackup/JSON/FromBackup");
                }
            });
        }

        [TestCaseSource(nameof(ProviderSettings))]
        public void backup_should_preserve_every_discovered_provider_settings_property(Type settingsType)
        {
            var properties = WritableProperties(settingsType).ToList();
            Assert.That(properties, Is.Not.Empty, $"Classify an intentionally empty settings contract: {settingsType.Name}");

            Assert.Multiple(() =>
            {
                foreach (var property in properties)
                {
                    var settings = (IProviderConfig)Activator.CreateInstance(settingsType);
                    var original = property.GetValue(settings);
                    property.SetValue(settings, SettingsBackupTestValues.DifferentValue(property.PropertyType, original));
                    var expected = property.GetValue(settings);
                    Assert.That(Equivalent(expected, original, property.PropertyType), Is.False,
                        $"{settingsType.Name}.{property.Name} needs a value that differs from its default");

                    var source = CreateDefinition(DefinitionTypeForSettings(settingsType), settings);
                    var restored = RoundTrip(source);
                    Assert.That(restored.Settings, Is.TypeOf(settingsType));
                    Assert.That(Equivalent(property.GetValue(restored.Settings), expected, property.PropertyType), Is.True,
                        $"{settingsType.Name}.{property.Name} is missing or changed in provider settings export/JSON/restore");
                }
            });
        }

        [TestCase("client", false, false)]
        [TestCase("client", true, false)]
        [TestCase("rss", false, false)]
        [TestCase("rss", true, false)]
        [TestCase("automatic", true, false)]
        [TestCase("interactive", true, false)]
        [TestCase("notification", false, false)]
        [TestCase("notification", true, false)]
        [TestCase("audiobookshelf", false, false)]
        [TestCase("client", false, true)]
        [TestCase("rss", false, true)]
        [TestCase("notification", false, true)]
        public void restore_preflight_should_follow_editor_enablement_without_accepting_malformed_settings(string kind, bool enabled, bool malformed)
        {
            ProviderDefinition source = kind switch
            {
                "client" => new DownloadClientDefinition { Enable = enabled, Settings = new SabnzbdSettings() },
                "notification" => new NotificationDefinition { OnImportFailure = enabled, Settings = new WebhookSettings() },
                "audiobookshelf" => new NotificationDefinition { Settings = new AudioBookShelfSettings() },
                _ => new IndexerDefinition
                {
                    EnableRss = kind == "rss" && enabled,
                    EnableAutomaticSearch = kind == "automatic" && enabled,
                    EnableInteractiveSearch = kind == "interactive" && enabled,
                    Settings = new NewznabSettings()
                }
            };
            source.Name = "Unfinished connection";
            source.Implementation = source.Settings.GetType().Name.Replace("Settings", string.Empty);
            Assert.That(source.Settings.Validate().IsValid, Is.False);
            Assert.That(source.Enable, Is.EqualTo(enabled || kind == "audiobookshelf"));
            var backup = ExportDefinition(source);
            if (malformed)
            {
                backup.GetType().GetProperty("Settings").SetValue(backup, JsonSerializer.SerializeToElement("not an object"));
            }
            var package = new SettingsBackupPackage();
            var category = SettingsBackupCategory.DownloadClients;
            switch (backup)
            {
                case DownloadClientDefinitionBackup client:
                    package.DownloadClients.Add(client);
                    break;
                case IndexerDefinitionBackup indexer:
                    category = SettingsBackupCategory.Indexers;
                    package.Indexers.Add(indexer);
                    break;
                case NotificationDefinitionBackup notification:
                    category = SettingsBackupCategory.Connections;
                    // Enable is derived from triggers/implementation; a serialized value must not override it.
                    notification.Enable = !source.Enable;
                    package.Connections.Add(notification);
                    break;
            }
            package = JsonSerializer.Deserialize<SettingsBackupPackage>(JsonSerializer.Serialize(package, STJson.GetSerializerSettings()), STJson.GetSerializerSettings());
            if (source.Enable || malformed)
            {
                var error = Assert.Throws<NzbDroneClientException>(() => SettingsBackupService.ValidateRestore(package, new() { category }, SettingsBackupCoverageFixture.CreateFilenameValidation()));
                Assert.That(error.Message, Does.Contain(source.Name).And.Contain("No settings were restored"));
            }
            else
            {
                Assert.That(SettingsBackupService.ValidateRestore(package, new() { category }, SettingsBackupCoverageFixture.CreateFilenameValidation()), Is.Empty);
                Assert.That(RoundTrip(source).Enable, Is.False);
            }
        }

        [Test]
        public void exclusions_should_reference_existing_properties_and_explain_their_scope()
        {
            Assert.Multiple(() =>
            {
                foreach (var exclusion in Exclusions)
                {
                    Assert.That(exclusion.Key.Type.GetProperty(exclusion.Key.Property), Is.Not.Null,
                        $"Remove stale exclusion {exclusion.Key.Type.Name}.{exclusion.Key.Property}");
                    Assert.That(exclusion.Value, Is.Not.Empty);
                }
            });
        }

        [Test]
        public void provider_references_should_follow_the_destination_id_maps()
        {
            var indexer = (IndexerDefinition)CreateDefinition(typeof(IndexerDefinition), new NewznabSettings());
            indexer.Id = 11;
            indexer.Tags = new HashSet<int> { 12 };
            indexer.DownloadClientId = 13;
            indexer.ProxyId = 14;
            var restoredIndexer = (IndexerDefinition)RoundTrip(indexer,
                new Dictionary<int, int> { [12] = 112 },
                new Dictionary<int, int> { [13] = 113 },
                new Dictionary<int, int> { [14] = 114 });
            Assert.Multiple(() =>
            {
                Assert.That(restoredIndexer.Id, Is.Zero);
                Assert.That(restoredIndexer.Tags, Is.EquivalentTo(new[] { 112 }));
                Assert.That(restoredIndexer.DownloadClientId, Is.EqualTo(113));
                Assert.That(restoredIndexer.ProxyId, Is.EqualTo(114));
            });

            var client = (DownloadClientDefinition)CreateDefinition(typeof(DownloadClientDefinition), new QBittorrentSettings());
            client.AudiobookTags = new HashSet<int> { 12 };
            client.EbookTags = new HashSet<int> { 15 };
            var restoredClient = (DownloadClientDefinition)RoundTrip(client,
                new Dictionary<int, int> { [12] = 112, [15] = 115 });
            Assert.Multiple(() =>
            {
                Assert.That(restoredClient.AudiobookTags, Is.EquivalentTo(new[] { 112 }));
                Assert.That(restoredClient.EbookTags, Is.EquivalentTo(new[] { 115 }));
            });
        }

        private static IEnumerable<PropertyInfo> WritableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
                .Where(property => !Exclusions.ContainsKey((property.DeclaringType, property.Name)))
                .OrderBy(property => property.Name);
        }

        private static void EnsureMappings()
        {
            if (!TableMapping.Mapper.TableMap.ContainsKey(typeof(NzbDrone.Core.Configuration.Config)))
            {
                TableMapping.Map();
            }
        }

        private static Type DefinitionTypeForSettings(Type settingsType)
        {
            var ns = settingsType.Namespace ?? string.Empty;
            if (ns.StartsWith("NzbDrone.Core.Indexers.", StringComparison.Ordinal))
            {
                return typeof(IndexerDefinition);
            }

            if (ns.StartsWith("NzbDrone.Core.Download.Clients.", StringComparison.Ordinal))
            {
                return typeof(DownloadClientDefinition);
            }

            return ns.StartsWith("NzbDrone.Core.Notifications.", StringComparison.Ordinal) ? typeof(NotificationDefinition) : null;
        }

        private static IProviderConfig CreateSettings(Type definitionType)
        {
            if (definitionType == typeof(IndexerDefinition))
            {
                return new NewznabSettings();
            }

            return definitionType == typeof(DownloadClientDefinition) ? new QBittorrentSettings() : new WebhookSettings();
        }

        private static ProviderDefinition CreateDefinition(Type definitionType, IProviderConfig settings)
        {
            var definition = (ProviderDefinition)Activator.CreateInstance(definitionType);
            definition.Name = "Backup provider coverage";
            definition.Implementation = settings.GetType().Name.Replace("Settings", string.Empty);
            definition.Settings = settings;
            return definition;
        }

        private static ProviderDefinition RoundTrip(ProviderDefinition source,
            Dictionary<int, int> tags = null,
            Dictionary<int, int> clients = null,
            Dictionary<int, int> proxies = null)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var backup = ExportDefinition(source);
            Assert.That((int)backup.GetType().GetProperty("OriginalId").GetValue(backup), Is.EqualTo(source.Id));
            var json = JsonSerializer.Serialize(backup, backup.GetType(), STJson.GetSerializerSettings());
            backup = JsonSerializer.Deserialize(json, backup.GetType(), STJson.GetSerializerSettings());
            var parameters = source is IndexerDefinition
                ? new object[] { backup, tags, clients, proxies }
                : new object[] { backup, tags };
            var restore = typeof(SettingsBackupService).GetMethods(flags)
                .Single(method => method.Name == "FromBackup" && method.GetParameters()[0].ParameterType == backup.GetType());
            var restored = (ProviderDefinition)restore.Invoke(null, parameters);
            Assert.That(restored, Is.Not.Null, $"Failed to materialize {source.GetType().Name} with {source.ConfigContract}");
            Assert.That(restored.ConfigContract, Is.EqualTo(source.ConfigContract));
            return restored;
        }

        private static object ExportDefinition(ProviderDefinition source)
        {
            var export = typeof(SettingsBackupService).GetMethod("ToBackup", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { source.GetType() }, null);
            Assert.That(export, Is.Not.Null, source.GetType().Name);
            return export.Invoke(null, new object[] { source });
        }

        private static bool Equivalent(object actual, object expected, Type type)
        {
            return JsonSerializer.Serialize(actual, type, STJson.GetSerializerSettings()) ==
                   JsonSerializer.Serialize(expected, type, STJson.GetSerializerSettings());
        }

    }
}
