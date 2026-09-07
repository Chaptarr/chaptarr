using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class SettingsBackupServiceDirectDownloadRestoreFixture
    {
        [Test]
        public void should_round_trip_direct_download_settings_through_backup_and_restore()
        {
            var definition = new IndexerDefinition
            {
                Id = 7,
                Name = "Direct Download",
                Implementation = "DirectDownloadIndexer",
                ConfigContract = nameof(DirectDownloadSettings),
                EnableAutomaticSearch = true,
                EnableInteractiveSearch = true,
                Protocol = DownloadProtocol.Direct,
                Priority = 25,
                Settings = new DirectDownloadSettings
                {
                    Urls = " https://primary.example/ \nhttps://mirror.example\nhttps://primary.example/ ",
                    ApiKey = "secret-key"
                }
            };

            var backup = InvokeToBackup(definition);
            var backupJson = backup.Settings.GetRawText();
            var factory = CreateIndexerFactory(out var factoryProxy);
            var service = CreateService(factory);
            var result = new SettingsBackupRestoreResult();

            InvokeRestoreIndexers(
                service,
                new SettingsBackupPackage { Indexers = new List<IndexerDefinitionBackup> { backup } },
                SettingsBackupRestoreMode.Overwrite,
                result);

            Assert.That(backupJson, Does.Contain("urls"));
            Assert.That(backupJson, Does.Contain("apiKey"));
            Assert.That(backupJson, Does.Not.Contain("baseUrl"));
            Assert.That(factoryProxy.DeletedIds, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(factoryProxy.Definitions, Has.Count.EqualTo(1));

            var restored = factoryProxy.Definitions.Single();
            Assert.That(restored.ConfigContract, Is.EqualTo(nameof(DirectDownloadSettings)));
            Assert.That(restored.Protocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(restored.Settings, Is.InstanceOf<DirectDownloadSettings>());

            var restoredSettings = (DirectDownloadSettings)restored.Settings;
            Assert.That(restoredSettings.Urls, Is.EqualTo("https://primary.example\nhttps://mirror.example"));
            Assert.That(restoredSettings.ApiKey, Is.EqualTo("secret-key"));
            Assert.That(restoredSettings.BaseUrl, Is.EqualTo("https://primary.example"));
        }

        [Test]
        public void should_ignore_runtime_only_baseurl_when_restoring_polluted_backup_payload()
        {
            var pollutedSettings = JsonDocument.Parse(@"{
  ""urls"": ""https://primary.example\nhttps://mirror.example"",
  ""apiKey"": ""secret-key"",
  ""baseUrl"": ""https://stale.example""
}");

            var backup = new IndexerDefinitionBackup
            {
                Name = "Direct Download",
                Implementation = "DirectDownloadIndexer",
                ConfigContract = nameof(DirectDownloadSettings),
                Protocol = DownloadProtocol.Direct,
                Settings = pollutedSettings.RootElement.Clone()
            };

            var factory = CreateIndexerFactory(out var factoryProxy);
            var service = CreateService(factory);
            var result = new SettingsBackupRestoreResult();

            InvokeRestoreIndexers(
                service,
                new SettingsBackupPackage { Indexers = new List<IndexerDefinitionBackup> { backup } },
                SettingsBackupRestoreMode.Overwrite,
                result);

            var restored = factoryProxy.Definitions.Single();
            var restoredSettings = (DirectDownloadSettings)restored.Settings;

            Assert.That(result.Warnings, Is.Empty);
            Assert.That(restoredSettings.Urls, Is.EqualTo("https://primary.example\nhttps://mirror.example"));
            Assert.That(restoredSettings.BaseUrl, Is.EqualTo("https://primary.example"));
            Assert.That(restoredSettings.BaseUrl, Is.Not.EqualTo("https://stale.example"));
        }

        private static SettingsBackupService CreateService(IIndexerFactory indexerFactory)
        {
            return new SettingsBackupService(
                null,
                null,
                null,
                null,
                indexerFactory,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        private static IndexerDefinitionBackup InvokeToBackup(IndexerDefinition definition)
        {
            var method = typeof(SettingsBackupService).GetMethod("ToBackup", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(IndexerDefinition) }, null);

            Assert.That(method, Is.Not.Null);

            return (IndexerDefinitionBackup)method.Invoke(null, new object[] { definition });
        }

        private static void InvokeRestoreIndexers(
            SettingsBackupService service,
            SettingsBackupPackage package,
            SettingsBackupRestoreMode mode,
            SettingsBackupRestoreResult result)
        {
            var method = typeof(SettingsBackupService).GetMethod("RestoreIndexers", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);

            method.Invoke(service, new object[]
            {
                package,
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                mode,
                result
            });
        }

        private static IIndexerFactory CreateIndexerFactory(out IndexerFactoryProxy proxy)
        {
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            proxy = (IndexerFactoryProxy)(object)factory;
            return factory;
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            private int _nextId = 1;

            public List<IndexerDefinition> Definitions { get; } = new();
            public List<int> DeletedIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IIndexerFactory.All):
                        return Definitions.ToList();

                    case nameof(IIndexerFactory.Create):
                        var created = (IndexerDefinition)args[0];
                        if (created.Id <= 0)
                        {
                            created.Id = _nextId++;
                        }

                        Definitions.Add(created);
                        return created;

                    case nameof(IIndexerFactory.Update):
                        var updated = (IndexerDefinition)args[0];
                        var index = Definitions.FindIndex(existing => existing.Id == updated.Id);
                        if (index >= 0)
                        {
                            Definitions[index] = updated;
                        }
                        else
                        {
                            Definitions.Add(updated);
                        }

                        return null;

                    case nameof(IIndexerFactory.Delete) when args[0] is IEnumerable<int> ids:
                        var deleted = ids.ToList();
                        DeletedIds.AddRange(deleted);
                        Definitions.RemoveAll(definition => deleted.Contains(definition.Id));
                        return null;

                    case nameof(IIndexerFactory.Delete) when args[0] is int id:
                        DeletedIds.Add(id);
                        Definitions.RemoveAll(definition => definition.Id == id);
                        return null;

                    default:
                        throw new NotImplementedException($"Test proxy does not implement {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
                }
            }
        }
    }
}
