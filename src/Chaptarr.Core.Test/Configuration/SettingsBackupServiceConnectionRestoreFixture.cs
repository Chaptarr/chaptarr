using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class SettingsBackupServiceConnectionRestoreFixture
    {
        [TestCase(SettingsBackupRestoreMode.Merge)]
        [TestCase(SettingsBackupRestoreMode.Overwrite)]
        public void case_only_connection_name_change_keeps_its_identity(SettingsBackupRestoreMode mode)
        {
            var factory = CreateNotificationFactory(out var proxy);
            proxy.Definitions.Add(new NotificationDefinition
            {
                Id = 42, Name = "AUDIOBOOKSHELF", Implementation = "AudioBookShelf",
                Settings = new AudioBookShelfSettings { Host = "destination" }
            });
            InvokeRestoreConnections(CreateService(factory), BuildPackageWithAudioBookShelfConnection(), mode, new SettingsBackupRestoreResult());
            var restored = proxy.Definitions.Single();
            Assert.That(restored.Id, Is.EqualTo(42));
            Assert.That(((AudioBookShelfSettings)restored.Settings).Host,
                Is.EqualTo(mode == SettingsBackupRestoreMode.Merge ? "destination" : "audiobookshelf"));
        }

        [Test]
        public void restore_connections_should_clear_audiobookshelf_library_mappings_and_warn()
        {
            var factory = CreateNotificationFactory(out var factoryProxy);
            var service = CreateService(factory);
            var result = new SettingsBackupRestoreResult();

            InvokeRestoreConnections(
                service,
                BuildPackageWithAudioBookShelfConnection(),
                SettingsBackupRestoreMode.Overwrite,
                result);

            Assert.That(factoryProxy.Definitions, Has.Count.EqualTo(1));
            AssertAudioBookShelfMappingsCleared(factoryProxy.Definitions[0]);
            Assert.That(result.Warnings, Has.Some.Contains("Discarded backup AudioBookShelf library mappings"));
            Assert.That(result.Warnings, Has.Some.Contains("Root folders are not included in settings backups"));
        }

        [Test]
        public void merge_connections_should_clear_audiobookshelf_library_mappings_on_existing_connection()
        {
            var factory = CreateNotificationFactory(out var factoryProxy);
            factoryProxy.Definitions.Add(new NotificationDefinition
            {
                Id = 42,
                Name = "AudioBookShelf",
                Implementation = "AudioBookShelf",
                Settings = new AudioBookShelfSettings()
            });

            var service = CreateService(factory);
            var result = new SettingsBackupRestoreResult();

            InvokeRestoreConnections(
                service,
                BuildPackageWithAudioBookShelfConnection(),
                SettingsBackupRestoreMode.Merge,
                result);

            Assert.That(factoryProxy.Definitions, Has.Count.EqualTo(1));
            Assert.That(factoryProxy.Definitions[0].Id, Is.EqualTo(42));
            AssertAudioBookShelfMappingsCleared(factoryProxy.Definitions[0]);
            Assert.That(result.Warnings, Has.Some.Contains("Discarded backup AudioBookShelf library mappings"));
        }

        [Test]
        public void merge_connections_should_keep_destination_settings_and_library_mappings()
        {
            var factory = CreateNotificationFactory(out var proxy);
            var settings = new AudioBookShelfSettings { Host = "destination", Port = 0, ApiKey = "" };
            settings.SetLibraryMappings(new List<AudioBookShelfLibraryMapping>
            {
                new() { RootFolderId = 42, MediaType = "audiobook", LibraryId = "destination-library" }
            });
            proxy.Definitions.Add(new NotificationDefinition
            {
                Id = 42, Name = "AudioBookShelf", Implementation = "AudioBookShelf",
                Settings = settings, IncludeHealthWarnings = false
            });
            var package = BuildPackageWithAudioBookShelfConnection();
            package.Connections.Single().IncludeHealthWarnings = true;
            InvokeRestoreConnections(CreateService(factory), package, SettingsBackupRestoreMode.Merge, new SettingsBackupRestoreResult());
            var restored = (AudioBookShelfSettings)proxy.Definitions.Single().Settings;
            Assert.That(restored.Host, Is.EqualTo("destination"));
            Assert.That(restored.Port, Is.Zero);
            Assert.That(restored.ApiKey, Is.EqualTo("apikey"));
            Assert.That(restored.GetLibraryMappings().Single().RootFolderId, Is.EqualTo(42));
            Assert.That(restored.GetLibraryMappings().Single().LibraryId, Is.EqualTo("destination-library"));
            Assert.That(proxy.Definitions.Single().IncludeHealthWarnings, Is.False);
        }

        [Test]
        public void an_older_backup_without_health_warnings_should_preserve_the_existing_connection_choice()
        {
            var factory = CreateNotificationFactory(out var proxy);
            proxy.Definitions.Add(new NotificationDefinition
            {
                Id = 42,
                Name = "AudioBookShelf",
                Implementation = "AudioBookShelf",
                IncludeHealthWarnings = true,
                Settings = new AudioBookShelfSettings()
            });
            var package = BuildPackageWithAudioBookShelfConnection();
            Assert.That(package.Connections.Single().IncludeHealthWarnings, Is.Null);
            InvokeRestoreConnections(CreateService(factory), package, SettingsBackupRestoreMode.Merge, new SettingsBackupRestoreResult());
            Assert.That(proxy.Definitions.Single().IncludeHealthWarnings, Is.True);
        }

        private static SettingsBackupService CreateService(INotificationFactory notificationFactory)
        {
            return new SettingsBackupService(
                null,
                null,
                null,
                null,
                null,
                null,
                notificationFactory,
                null,
                null,
                null,
                null,
                null,
                null,
                null, delayProfileService: null, releaseProfileService: null, qualityDefinitionService: null, mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
        }

        private static SettingsBackupPackage BuildPackageWithAudioBookShelfConnection()
        {
            var settings = new AudioBookShelfSettings
            {
                Host = "audiobookshelf",
                Port = 13378,
                ApiKey = "apikey",
                AudiobookLibraryId = "legacy-audio",
                EbookLibraryId = "legacy-ebook",
                LibraryId = "legacy-single"
            };

            settings.SetLibraryMappings(new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 123,
                    MediaType = "audiobook",
                    LibraryId = "lib-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                }
            });

            return new SettingsBackupPackage
            {
                Connections = new List<NotificationDefinitionBackup>
                {
                    new NotificationDefinitionBackup
                    {
                        Name = "AudioBookShelf",
                        Implementation = "AudioBookShelf",
                        ConfigContract = nameof(AudioBookShelfSettings),
                        Enable = true,
                        Settings = JsonSerializer.SerializeToElement(settings, typeof(AudioBookShelfSettings), STJson.GetSerializerSettings())
                    }
                }
            };
        }

        private static void InvokeRestoreConnections(
            SettingsBackupService service,
            SettingsBackupPackage package,
            SettingsBackupRestoreMode mode,
            SettingsBackupRestoreResult result)
        {
            var method = typeof(SettingsBackupService).GetMethod("RestoreConnections", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);

            method.Invoke(service, new object[]
            {
                package,
                new Dictionary<int, int>(),
                mode,
                result
            });
        }

        private static void AssertAudioBookShelfMappingsCleared(NotificationDefinition definition)
        {
            Assert.That(definition.Settings, Is.InstanceOf<AudioBookShelfSettings>());

            var settings = (AudioBookShelfSettings)definition.Settings;
            Assert.That(settings.GetLibraryMappings(), Is.Empty);
            Assert.That(settings.LibraryMappingsJson, Is.Null);
            Assert.That(settings.AudiobookLibraryId, Is.Null);
            Assert.That(settings.EbookLibraryId, Is.Null);
            Assert.That(settings.LibraryId, Is.Null);
        }

        private static INotificationFactory CreateNotificationFactory(out NotificationFactoryProxy proxy)
        {
            var factory = DispatchProxy.Create<INotificationFactory, NotificationFactoryProxy>();
            proxy = (NotificationFactoryProxy)(object)factory;
            return factory;
        }

        private class NotificationFactoryProxy : DispatchProxy
        {
            private int _nextId = 1;

            public List<NotificationDefinition> Definitions { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(INotificationFactory.All))
                {
                    return Definitions.ToList();
                }

                if (targetMethod?.Name == nameof(INotificationFactory.Create))
                {
                    var definition = (NotificationDefinition)args[0];
                    if (definition.Id <= 0)
                    {
                        definition.Id = _nextId++;
                    }

                    Definitions.Add(definition);
                    return definition;
                }

                if (targetMethod?.Name == nameof(INotificationFactory.Update))
                {
                    var definition = (NotificationDefinition)args[0];
                    var index = Definitions.FindIndex(existing => existing.Id == definition.Id);
                    if (index >= 0)
                    {
                        Definitions[index] = definition;
                    }
                    else
                    {
                        Definitions.Add(definition);
                    }

                    return null;
                }

                if (targetMethod?.Name == nameof(INotificationFactory.Delete))
                {
                    if (args[0] is IEnumerable<int> ids)
                    {
                        var idSet = ids.ToHashSet();
                        Definitions.RemoveAll(definition => idSet.Contains(definition.Id));
                        return null;
                    }

                    if (args[0] is int id)
                    {
                        Definitions.RemoveAll(definition => definition.Id == id);
                        return null;
                    }
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
            }
        }
    }
}
