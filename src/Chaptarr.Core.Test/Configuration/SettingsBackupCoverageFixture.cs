using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaptarr.Api.V1.Config;
using Chaptarr.Core.Test.Common;
using Microsoft.Extensions.Options;
using Dapper;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.Options;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RemotePathMappings;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    [NonParallelizable]
    public class SettingsBackupCoverageFixture
    {
        // Every new writable config setting is tested unless deliberately outside portable backup scope.
        private static readonly Dictionary<string, string> Exclusions = new()
        {
            [nameof(IConfigService.DefaultAudiobookRootFolderPath)] = "Root folders are installation-specific and not exported",
            [nameof(IConfigService.DefaultEbookRootFolderPath)] = "Root folders are installation-specific and not exported",
            [nameof(IConfigService.GlobalProxyId)] = "Proxy references are remapped by RestoreProxySettings, not copied as config values",
            [nameof(IConfigService.CleanupMetadataImages)] = "Internal maintenance state is not a portable setting",
            [nameof(IConfigService.AudioProductionCustomFormatsSeeded)] = "Internal seed state is not a portable setting",
            [nameof(IConfigService.SeededBuiltInCustomFormatKeys)] = "Internal seed state is not a portable setting",
            [nameof(IConfigService.MatchingLogsUploadToken)] = "Diagnostic-upload credentials have no backup category",
            [nameof(IConfigService.PlexClientIdentifier)] = "Generated installation identity; restored by full backup, not portable settings",
            [nameof(IConfigService.RijndaelPassphrase)] = "Generated installation encryption state; full backup only",
            [nameof(IConfigService.HmacPassphrase)] = "Generated installation encryption state; full backup only",
            [nameof(IConfigService.RijndaelSalt)] = "Generated installation encryption state; full backup only",
            [nameof(IConfigService.HmacSalt)] = "Generated installation encryption state; full backup only",
            [nameof(IConfigService.ProxyEnabled)] = "Derived from ProxyMode, carried by ProxySettingsBackup",
            [nameof(IConfigService.ProxyType)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyMode)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyHostname)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyPort)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyUsername)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyPassword)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyBypassFilter)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.ProxyBypassLocalAddresses)] = "Carried by ProxySettingsBackup rather than the scalar settings sections",
            [nameof(IConfigService.BackupFolder)] = "Installation-specific backup destination; full backup only",
            [nameof(IConfigService.BackupInterval)] = "Installation-specific backup schedule; full backup only",
            [nameof(IConfigService.BackupRetention)] = "Installation-specific backup retention; full backup only",
            [nameof(IConfigService.BackupIncludeLogsAndCache)] = "Installation-specific full-backup contents; full backup only",
            [nameof(IConfigService.CertificateValidation)] = "Host networking policy; full backup only",
            [nameof(IConfigService.ApplicationUrl)] = "Installation-specific external address; full backup only"
        };

        private static IEnumerable<PropertyInfo> Settings => typeof(IConfigService).GetProperties()
            .Where(p => p.CanRead && p.CanWrite && !Exclusions.ContainsKey(p.Name))
            .OrderBy(p => p.Name);

        [TestCase(false)]
        [TestCase(true)]
        public void retired_update_source_keys_do_not_prevent_settings_restore(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database => WithBackupFolder(folder =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var config = new ConfigService(new ConfigRepository(database, events), events, null, LogManager.GetCurrentClassLogger());
                var host = CreateHostConfig(folder);
                host.EnsureDefaultConfigFile();
                var package = Export(config, host: host);
                package.Categories = new() { SettingsBackupCategory.UiSettings };
                package.UiSettings.Theme = "dark";
                package.ExtensionData["useGitHubUpdates"] = JsonSerializer.SerializeToElement(true);
                package.ExtensionData["gitHubOwner"] = JsonSerializer.SerializeToElement("old-owner");
                package.ExtensionData["gitHubRepo"] = JsonSerializer.SerializeToElement("old-repo");
                var path = Path.Combine(folder, "retired-update-source.chaptarr-settings-backup.json");
                File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(
                    JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));

                FileBackupService(config, folder, host).RestoreBackup(new SettingsBackupRestoreRequest
                {
                    FilePath = path, Passphrase = "test passphrase", Categories = package.Categories.ToHashSet()
                });

                Assert.That(CreateHostConfig(folder).Theme, Is.EqualTo("dark"));
            }));
        }

        private static IEnumerable<TestCaseData> ConfigSettings()
        {
            foreach (var property in Settings)
            {
                foreach (var useDefault in new[] { false, true })
                {
                    yield return new TestCaseData(property.Name, useDefault)
                        .SetName($"backup_should_restore_{property.Name}_{(useDefault ? "default" : "changed")}");
                }
            }
        }

        private static IEnumerable<string> MergeSettings() => Settings.Select(property => property.Name);

        [TestCaseSource(nameof(MergeSettings))]
        public void merge_should_fill_only_blank_config_values(string settingName)
        {
            var property = typeof(IConfigService).GetProperty(settingName);
            foreach (var keepDefault in new[] { true, false })
            {
                var source = CreateConfigService();
                var target = CreateConfigService();
                var current = property.GetValue(target);
                if (!keepDefault)
                {
                    current = SettingsBackupTestValues.DifferentValue(property.PropertyType, current);
                    property.SetValue(target, current);
                    current = property.GetValue(target);
                }
                property.SetValue(source, SettingsBackupTestValues.DifferentValue(property.PropertyType, current));
                var incoming = property.GetValue(source);
                var blank = current == null || current is string text && string.IsNullOrWhiteSpace(text) ||
                    current is System.Collections.IEnumerable values && current is not string && !values.Cast<object>().Any();
                var package = Export(source);
                target = CreateConfigService();
                if (!keepDefault)
                {
                    property.SetValue(target, current);
                }
                Restore(target, package, mode: SettingsBackupRestoreMode.Merge);
                Assert.That(property.GetValue(target), Is.EqualTo(blank ? incoming : current), settingName);
            }
        }

        private static IEnumerable<string> UiSettings() => typeof(UiConfigResource).GetProperties()
            .Where(property => property.DeclaringType == typeof(UiConfigResource) && property.CanRead && property.CanWrite)
            .Select(property => property.Name);

        [TestCaseSource(nameof(UiSettings))]
        public void backup_should_preserve_every_setting_exposed_on_the_ui_page(string settingName)
        {
            var source = CreateConfigService();
            var target = CreateConfigService();
            var sourceHost = new Dictionary<string, object> { [nameof(IConfigFileProvider.Theme)] = "dark" };
            var targetHost = new Dictionary<string, object> { [nameof(IConfigFileProvider.Theme)] = "light" };
            IConfigFileProvider Host(Dictionary<string, object> values) => InterfaceProxy.Proxy<IConfigFileProvider>((method, args) =>
            {
                if (method.Name == nameof(IConfigFileProvider.SaveConfigDictionary))
                {
                    foreach (var entry in (Dictionary<string, object>)args[0])
                    {
                        values[entry.Key] = entry.Value;
                    }

                    return null;
                }

                return values.GetValueOrDefault(method.Name.Substring(4));
            });
            var property = typeof(UiConfigResource).GetProperty(settingName);
            var sourceFile = Host(sourceHost);
            var targetFile = Host(targetHost);
            var original = property.GetValue(UiConfigResourceMapper.ToResource(targetFile, target));
            var changed = SettingsBackupTestValues.DifferentValue(property.PropertyType, original);
            var databaseProperty = typeof(IConfigService).GetProperty(settingName);
            if (databaseProperty != null)
            {
                databaseProperty.SetValue(source, changed);
            }
            else
            {
                sourceHost[settingName] = changed;
            }

            var expected = property.GetValue(UiConfigResourceMapper.ToResource(sourceFile, source));
            Assert.That(expected, Is.Not.EqualTo(original), $"{settingName} needs non-default test data");
            Restore(target, Export(source, host: sourceFile), host: targetFile);
            Assert.That(property.GetValue(UiConfigResourceMapper.ToResource(targetFile, target)), Is.EqualTo(expected),
                $"Settings > UI field {settingName} was lost during export/JSON/restore. Include it in Interface preferences.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void older_interface_backups_should_leave_the_current_theme_unchanged(bool missingSection)
        {
            var json = JsonSerializer.SerializeToNode(Export(CreateConfigService()), STJson.GetSerializerSettings());
            if (missingSection)
            {
                json.AsObject().Remove("uiSettings");
            }
            else
            {
                json["uiSettings"].AsObject().Remove("theme");
            }

            var untouchedHost = InterfaceProxy.Proxy<IConfigFileProvider>((_, _) => throw new AssertionException("An absent theme must not change config.xml"));
            Restore(CreateConfigService(), json.Deserialize<SettingsBackupPackage>(STJson.GetSerializerSettings()), host: untouchedHost);
        }

        private static IEnumerable<string> NamingSettings() => typeof(NamingConfig).GetProperties()
            .Where(property => property.CanRead && property.CanWrite && property.DeclaringType == typeof(NamingConfig))
            .Select(property => property.Name);

        [TestCaseSource(nameof(NamingSettings))]
        public void backup_should_preserve_every_discovered_naming_setting(string settingName)
        {
            var property = typeof(NamingConfig).GetProperty(settingName);
            var source = NamingConfig.Default;
            source.Id = 11;
            property.SetValue(source, ChangedValue(property, property.GetValue(source)));
            var package = Export(CreateConfigService(), InterfaceProxy.Proxy<INamingConfigService>((_, _) => source));
            NamingConfig restored = null;
            var naming = InterfaceProxy.Proxy<INamingConfigService>((method, args) =>
            {
                if (method.Name == nameof(INamingConfigService.GetConfig))
                {
                    return new NamingConfig { Id = 42 };
                }

                restored = (NamingConfig)args[0];
                return restored;
            });

            Restore(CreateConfigService(), package, naming);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Not.Null, "Naming configuration was not restored");
                Assert.That(property.GetValue(restored), Is.EqualTo(property.GetValue(source)), settingName);
                Assert.That(restored.Id, Is.EqualTo(42), "Naming configuration must retain the destination's local ID");
            });
        }

        [TestCaseSource(nameof(ConfigSettings))]
        public void backup_should_restore_every_discovered_config_setting(string settingName, bool useDefault)
        {
            var property = typeof(IConfigService).GetProperty(settingName);
            var source = CreateConfigService();
            var defaultValue = property.GetValue(source);
            var changedValue = ChangedValue(property, defaultValue);
            property.SetValue(source, useDefault ? defaultValue : changedValue);
            var expected = property.GetValue(source);
            Assert.That(expected, Is.Not.EqualTo(useDefault ? changedValue : defaultValue), "The test must exercise a different value");

            var package = Export(source);
            var target = CreateConfigService();
            property.SetValue(target, useDefault ? changedValue : defaultValue);

            Restore(target, package);

            Assert.That(property.GetValue(target), Is.EqualTo(expected),
                $"{settingName} was not preserved by settings export/JSON/restore. Wire it into backup, or document why it is outside portable backup scope.");
        }

        [Test]
        public void backup_exclusions_should_reference_current_settings_and_explain_their_scope()
        {
            var properties = typeof(IConfigService).GetProperties().Where(p => p.CanRead).Select(p => p.Name).ToHashSet();
            Assert.Multiple(() =>
            {
                Assert.That(Exclusions.Keys.Except(properties), Is.Empty);
                Assert.That(Exclusions.Values, Has.None.Empty);
                Assert.That(typeof(IConfigService).GetProperties().Where(property => property.CanRead && !property.CanWrite)
                    .Select(property => property.Name).Except(Exclusions.Keys), Is.Empty,
                    "New getter-only settings need round-trip coverage or an explicit scope reason; they must not disappear from discovery.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void encrypted_settings_request_should_round_trip_every_discovered_setting_in_a_clean_database(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var repository = new ConfigRepository(database, events);
                var source = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                var expected = new Dictionary<string, object>();
                foreach (var property in Settings)
                {
                    property.SetValue(source, ChangedValue(property, property.GetValue(source)));
                    expected[property.Name] = property.GetValue(source);
                }

                WithBackupFolder(folder =>
                {
                    source.RecycleBin = folder;
                    source.ChmodFolder = "775";
                    source.MetadataServerUrl = "https://example.invalid";
                    expected[nameof(IConfigService.RecycleBin)] = source.RecycleBin;
                    expected[nameof(IConfigService.ChmodFolder)] = source.ChmodFolder;
                    expected[nameof(IConfigService.MetadataServerUrl)] = source.MetadataServerUrl;
                    var sourceHost = CreateHostConfig(folder);
                    sourceHost.SaveConfigDictionary(new Dictionary<string, object> { [nameof(IConfigFileProvider.Theme)] = "dark" });
                    var package = FileBackupService(source, folder, sourceHost).CreateBackup(new SettingsBackupCreateRequest
                    {
                        RootFolder = folder,
                        Passphrase = "synthetic backup passphrase",
                        Categories = new HashSet<SettingsBackupCategory>
                        {
                            SettingsBackupCategory.MediaManagement, SettingsBackupCategory.Hardcover, SettingsBackupCategory.MetadataServer, SettingsBackupCategory.Indexers, SettingsBackupCategory.DownloadClients, SettingsBackupCategory.UiSettings
                        }
                    });
                    using (var connection = database.OpenConnection())
                    {
                        connection.Execute("DELETE FROM \"Config\"");
                    }

                    sourceHost.SaveConfigDictionary(new Dictionary<string, object>
                    {
                        [nameof(IConfigFileProvider.Theme)] = "light",
                        [nameof(IConfigFileProvider.Port)] = 9797
                    });
                    var configBefore = File.ReadAllText(Path.Combine(folder, "config.xml"));
                    FileBackupService(source, folder, CreateHostConfig(folder)).RestoreBackup(new SettingsBackupRestoreRequest
                    {
                        FilePath = package.Path,
                        Passphrase = "synthetic backup passphrase",
                        Categories = new HashSet<SettingsBackupCategory> { SettingsBackupCategory.MediaManagement }
                    });
                    Assert.That(File.ReadAllText(Path.Combine(folder, "config.xml")), Is.EqualTo(configBefore),
                        "Deselecting Interface preferences must leave config.xml untouched");
                    var target = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                    FileBackupService(target, folder, CreateHostConfig(folder)).RestoreBackup(new SettingsBackupRestoreRequest
                    {
                        FilePath = package.Path,
                        Passphrase = "synthetic backup passphrase",
                        Categories = new HashSet<SettingsBackupCategory>
                        {
                            SettingsBackupCategory.MediaManagement, SettingsBackupCategory.Hardcover, SettingsBackupCategory.MetadataServer, SettingsBackupCategory.Indexers, SettingsBackupCategory.DownloadClients, SettingsBackupCategory.UiSettings
                        }
                    });
                    Assert.That(CreateHostConfig(folder).Theme, Is.EqualTo("dark"), "Theme must survive a fresh config.xml read");
                    Assert.That(CreateHostConfig(folder).Port, Is.EqualTo(9797), "Theme restore must not replace host configuration");
                    var reloaded = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                    Assert.Multiple(() =>
                    {
                        foreach (var property in Settings)
                        {
                            Assert.That(property.GetValue(reloaded), Is.EqualTo(expected[property.Name]), property.Name);
                        }
                    });
                });
            });
        }

        [TestCase(false, "password")]
        [TestCase(true, "password")]
        [TestCase(false, "ciphertext")]
        [TestCase(true, "ciphertext")]
        [TestCase(false, "tag")]
        [TestCase(true, "tag")]
        public void rejected_encrypted_settings_request_must_not_change_the_destination(bool postgres, string defect)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var repository = new ConfigRepository(database, events);
                var config = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                config.HardcoverApiToken = "synthetic-private-credential";
                WithBackupFolder(folder =>
                {
                    var service = FileBackupService(config, folder);
                    var backup = service.CreateBackup(new SettingsBackupCreateRequest
                    {
                        RootFolder = folder,
                        Passphrase = "synthetic backup passphrase",
                        Categories = new HashSet<SettingsBackupCategory> { SettingsBackupCategory.Hardcover }
                    });
                    var json = File.ReadAllText(backup.Path);
                    Assert.That(json, Does.Not.Contain(config.HardcoverApiToken).And.Not.Contain("synthetic backup passphrase"));
                    if (defect != "password")
                    {
                        var envelope = JsonNode.Parse(json);
                        var key = defect == "tag" ? "tagB64" : "ciphertextB64";
                        var bytes = Convert.FromBase64String(envelope[key].GetValue<string>());
                        bytes[0] ^= 1;
                        envelope[key] = Convert.ToBase64String(bytes);
                        File.WriteAllText(backup.Path, envelope.ToJsonString());
                    }

                    config.HardcoverApiToken = "destination-credential";
                    var before = repository.All().ToDictionary(item => item.Key, item => item.Value);
                    Assert.Throws<NzbDroneClientException>(() => service.RestoreBackup(new SettingsBackupRestoreRequest
                    {
                        FilePath = backup.Path,
                        Passphrase = defect == "password" ? "wrong passphrase" : "synthetic backup passphrase",
                        Categories = new HashSet<SettingsBackupCategory> { SettingsBackupCategory.Hardcover }
                    }));
                    Assert.That(repository.All().ToDictionary(item => item.Key, item => item.Value), Is.EquivalentTo(before));
                });
            });
        }

        [TestCase(false, true, false, true)]
        [TestCase(true, true, false, true)]
        [TestCase(false, false, true, true)]
        [TestCase(true, false, true, true)]
        [TestCase(false, true, true, true)]
        [TestCase(true, true, true, true)]
        [TestCase(false, true, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(true, false, true, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, true, true, false)]
        public void proxy_routing_restore_should_reject_invalid_enums_before_writes_and_ignore_unselected_settings(
            bool postgres, bool invalidMode, bool invalidType, bool selectProxies)
        {
            DualDatabaseFixture.Run(postgres, database => WithBackupFolder(folder =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var repository = new ConfigRepository(database, events);
                var config = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                var host = CreateHostConfig(folder);
                host.EnsureDefaultConfigFile();
                config.SaveConfigDictionary(new Dictionary<string, object>());
                var package = Export(config, host: host);
                package.Categories = new() { SettingsBackupCategory.Proxies, SettingsBackupCategory.UiSettings };
                package.ProxySettings = new ProxySettingsBackup
                {
                    ProxyMode = invalidMode ? (ProxyMode)999 : ProxyMode.Disabled,
                    ProxyType = invalidType ? (ProxyType)999 : ProxyType.Http,
                    ProxyPort = 8080
                };
                package.UiSettings.Theme = "dark";
                var path = Path.Combine(folder, "proxy-routing.chaptarr-settings-backup.json");
                File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(
                    JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));
                var before = repository.All().ToDictionary(item => item.Key, item => item.Value);
                var xml = File.ReadAllBytes(Path.Combine(folder, "config.xml"));
                var selected = new HashSet<SettingsBackupCategory> { SettingsBackupCategory.UiSettings };
                if (selectProxies) selected.Add(SettingsBackupCategory.Proxies);
                var request = new SettingsBackupRestoreRequest { FilePath = path, Passphrase = "test passphrase", Categories = selected };
                if (selectProxies)
                {
                    var error = Assert.Throws<NzbDroneClientException>(() => FileBackupService(config, folder, host).RestoreBackup(request));
                    Assert.That(error.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
                    Assert.That(error.Message, Does.Contain("No settings were restored"));
                    if (invalidMode) Assert.That(error.Message, Does.Contain(nameof(IConfigService.ProxyMode)));
                    if (invalidType) Assert.That(error.Message, Does.Contain(nameof(IConfigService.ProxyType)));
                    Assert.That(File.ReadAllBytes(Path.Combine(folder, "config.xml")), Is.EqualTo(xml));
                }
                else
                {
                    Assert.DoesNotThrow(() => FileBackupService(config, folder, host).RestoreBackup(request));
                    Assert.That(CreateHostConfig(folder).Theme, Is.EqualTo("dark"));
                    Assert.That(config.ProxyMode, Is.EqualTo(ProxyMode.Disabled));
                    Assert.That(config.ProxyType, Is.EqualTo(ProxyType.Http));
                }

                Assert.That(repository.All().ToDictionary(item => item.Key, item => item.Value), Is.EquivalentTo(before));
            }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void proxy_routing_restore_should_preserve_valid_and_legacy_packages(bool postgres, bool legacy)
        {
            DualDatabaseFixture.Run(postgres, database => WithBackupFolder(folder =>
            {
                var config = new ConfigService(new ConfigRepository(database, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null)),
                    InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), null, LogManager.GetCurrentClassLogger());
                var host = CreateHostConfig(folder);
                host.EnsureDefaultConfigFile();
                var package = Export(config, host: host);
                package.Categories = new() { SettingsBackupCategory.Proxies, SettingsBackupCategory.UiSettings };
                package.ProxySettings = legacy ? null : new ProxySettingsBackup
                {
                    ProxyMode = ProxyMode.IndexerOnly,
                    ProxyType = ProxyType.Socks5,
                    ProxyPort = 8080
                };
                package.UiSettings.Theme = "dark";
                var path = Path.Combine(folder, "valid-proxy-routing.chaptarr-settings-backup.json");
                File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(
                    JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));
                Assert.DoesNotThrow(() => FileBackupService(config, folder, host).RestoreBackup(new SettingsBackupRestoreRequest
                {
                    FilePath = path, Passphrase = "test passphrase", Categories = package.Categories.ToHashSet()
                }));
                Assert.That(CreateHostConfig(folder).Theme, Is.EqualTo("dark"));
                Assert.That(config.ProxyMode, Is.EqualTo(legacy ? ProxyMode.Disabled : ProxyMode.IndexerOnly));
                Assert.That(config.ProxyType, Is.EqualTo(legacy ? ProxyType.Http : ProxyType.Socks5));
            }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void invalid_selected_settings_must_report_all_bad_fields_before_any_database_or_config_write(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var repository = new ConfigRepository(database, events);
                var config = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                config.Retention = 45;
                WithBackupFolder(folder =>
                {
                    var host = CreateHostConfig(folder);
                    host.EnsureDefaultConfigFile();
                    var package = Export(config, host: host);
                    package.Categories = new() { SettingsBackupCategory.Indexers, SettingsBackupCategory.UiSettings };
                    package.IndexerSettings.Retention = -1;
                    package.IndexerSettings.RssSyncInterval = 1;
                    package.UiSettings.UILanguage = int.MaxValue;
                    package.UiSettings.AddNewDefaultMediaType = "unsupported-media";
                    var path = Path.Combine(folder, "invalid.chaptarr-settings-backup.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));
                    var before = repository.All().ToDictionary(item => item.Key, item => item.Value);
                    var xml = File.ReadAllBytes(Path.Combine(folder, "config.xml"));
                    var error = Assert.Throws<NzbDroneClientException>(() => FileBackupService(config, folder, host).RestoreBackup(new SettingsBackupRestoreRequest
                    {
                        FilePath = path, Passphrase = "test passphrase", Categories = package.Categories.ToHashSet()
                    }));
                    foreach (var field in new[] { "Retention", "RssSyncInterval", "UILanguage", "AddNewDefaultMediaType" })
                    {
                        Assert.That(error.Message, Does.Contain(field));
                    }
                    Assert.That(repository.All().ToDictionary(item => item.Key, item => item.Value), Is.EquivalentTo(before));
                    Assert.That(File.ReadAllBytes(Path.Combine(folder, "config.xml")), Is.EqualTo(xml));
                });
            });
        }

        [TestCase("proxy", "Port")]
        [TestCase("metadata", "MinPages")]
        [TestCase("delay", "TorrentDelay")]
        [TestCase("release", "Must contain")]
        [TestCase("quality", "Name")]
        [TestCase("naming", "AuthorFolderFormat")]
        [TestCase("url", "Metadata server URL")]
        [TestCase("custom-format", "Regex")]
        public void selected_tiles_must_reuse_their_settings_page_validation(string kind, string field)
        {
            var package = new SettingsBackupPackage();
            var category = SettingsBackupCategory.Profiles;
            switch (kind)
            {
                case "url":
                    category = SettingsBackupCategory.MetadataServer;
                    package.MetadataServerUrl = "ftp://example.invalid";
                    break;
                case "custom-format":
                    package.CustomFormats = new() { new CustomFormatBackup { Name = "Invalid pattern", Specifications = new() { new CustomFormatSpecificationBackup
                    {
                        Implementation = "ReleaseTitleSpecification", Settings = JsonSerializer.SerializeToElement(new { value = "" })
                    } } } };
                    break;
                case "proxy":
                    category = SettingsBackupCategory.Proxies;
                    package.Proxies.Add(new ProxyDefinitionBackup { Name = "Test proxy", Hostname = "localhost", Port = -1 });
                    break;
                case "metadata":
                    package.MetadataProfiles.Add(new NzbDrone.Core.Profiles.Metadata.MetadataProfile { Name = "Test profile", MinPages = -1 });
                    break;
                case "delay":
                    package.DelayProfiles = new() { new NzbDrone.Core.Profiles.Delay.DelayProfile { Id = 1, EnableTorrent = true, TorrentDelay = -1 } };
                    break;
                case "release":
                    package.ReleaseProfiles = new() { new NzbDrone.Core.Profiles.Releases.ReleaseProfile() };
                    break;
                case "quality":
                    package.QualityProfiles.Add(new QualityProfileBackup());
                    break;
                case "naming":
                    category = SettingsBackupCategory.MediaManagement;
                    package.MediaManagement = new MediaManagementBackup { MinimumFreeSpaceWhenImporting = 100, NamingConfig = NamingConfig.Default };
                    package.MediaManagement.NamingConfig.AuthorFolderFormat = "no author token";
                    break;
            }
            var error = Assert.Throws<NzbDroneClientException>(() => SettingsBackupService.ValidateRestore(package, new() { category }, filenameValidation: CreateFilenameValidation()));
            Assert.That(error.Message, Does.Contain(field).And.Contain("No settings were restored"));
            Assert.DoesNotThrow(() => SettingsBackupService.ValidateRestore(package, new(), CreateFilenameValidation()));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void restore_previews_and_skips_unusable_paths_but_applies_valid_settings(bool postgres, bool readOnly)
        {
            DualDatabaseFixture.Run(postgres, database => WithBackupFolder(folder =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var config = new ConfigService(new ConfigRepository(database, events), events, null, LogManager.GetCurrentClassLogger());
                IDiskProvider realDisk = OsInfo.IsWindows ? new NzbDrone.Windows.Disk.DiskProvider() : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, LogManager.GetCurrentClassLogger());
                var bad = Path.Combine(folder, "unavailable");
                if (readOnly) Directory.CreateDirectory(bad);
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name == "FolderWritable" && (string)args[0] == bad ? false : method.Invoke(realDisk, args));
                var repository = new RemotePathMappingRepository(database, events);
                var mappings = new RemotePathMappingService(null, repository, disk, new CacheManager(), LogManager.GetCurrentClassLogger());
                var original = mappings.Add(new RemotePathMapping { Host = "existing-client", RemotePath = "/downloads/", LocalPath = folder });
                config.RecycleBin = folder;
                var host = CreateHostConfig(folder);
                host.EnsureDefaultConfigFile();
                var package = Export(config, host: host);
                package.Categories = new() { SettingsBackupCategory.UiSettings, SettingsBackupCategory.RemotePathMappings, SettingsBackupCategory.MediaManagement };
                package.UiSettings.Theme = "dark";
                package.MediaManagement.RecycleBin = bad;
                package.RemotePathMappings = new()
                {
                    new RemotePathMappingBackup { Host = "existing-client", RemotePath = "/downloads/", LocalPath = bad },
                    new RemotePathMappingBackup { Host = "valid-client", RemotePath = "/downloads/", LocalPath = folder }
                };
                var path = Path.Combine(folder, "paths.chaptarr-settings-backup.json");
                File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));
                var xml = File.ReadAllBytes(Path.Combine(folder, "config.xml"));
                var folders = InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => folder);
                var service = CreateBackupService(config, folders: folders, disk: disk, host: host, mappings: mappings);
                var request = new SettingsBackupRestoreRequest { FilePath = path, Passphrase = "test passphrase", Categories = package.Categories.ToHashSet(), Preview = true };
                var preview = service.RestoreBackup(request);
                Assert.That(preview.Warnings, Has.Some.Contains("existing-client"));
                Assert.That(preview.Warnings, Has.Some.Contains("Recycling Bin"));
                Assert.That(preview.Applied, Is.Empty);
                Assert.That(File.ReadAllBytes(Path.Combine(folder, "config.xml")), Is.EqualTo(xml));
                Assert.That(repository.All().Select(mapping => mapping.Id), Is.EqualTo(new[] { original.Id }));
                request.Preview = false;
                var result = service.RestoreBackup(request);
                Assert.That(result.Warnings, Is.EquivalentTo(preview.Warnings));
                Assert.That(config.RecycleBin, Is.EqualTo(folder));
                Assert.That(host.Theme, Is.EqualTo("dark"));
                Assert.That(repository.All().Select(mapping => mapping.Host), Is.EquivalentTo(new[] { "existing-client", "valid-client" }));
            }));
        }

        [Test]
        public void media_paths_must_use_the_same_preflight_rules_as_the_settings_page()
        {
            WithBackupFolder(folder =>
            {
                IDiskProvider disk = OsInfo.IsWindows ? new NzbDrone.Windows.Disk.DiskProvider()
                    : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, LogManager.GetCurrentClassLogger());
                var folders = InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => AppContext.BaseDirectory);
                var validator = CreateMediaPathValidator(disk, folders);
                var package = Export(CreateConfigService());
                package.MediaManagement.RecycleBin = Path.Combine(folder, "missing");
                package.MediaManagement.ChmodFolder = "not-octal";
                var validation = validator.Validate(new MediaManagementPathSettings { RecycleBin = package.MediaManagement.RecycleBin, ChmodFolder = package.MediaManagement.ChmodFolder });
                Assert.That(validation.Errors.Select(error => error.PropertyName), Does.Contain("RecycleBin"));
                if (!OsInfo.IsWindows) Assert.That(validation.Errors.Select(error => error.PropertyName), Does.Contain("ChmodFolder"));
                Assert.That(validator.Validate(new MediaManagementPathSettings { RecycleBin = folder, ChmodFolder = "775" }).IsValid, Is.True);
            });
        }

        [Test]
        public void a_mid_profiles_failure_must_report_completed_partial_and_unattempted_work_and_log_the_same_result()
        {
            WithBackupFolder(folder =>
            {
                var originalLogging = LogManager.Configuration;
                var messages = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRule(LogLevel.Info, LogLevel.Fatal, messages);
                LogManager.Configuration = logging;
                try
                {
                    var config = CreateConfigService();
                    var host = CreateHostConfig(folder);
                    var calls = new List<string>();
                    var formats = new List<NzbDrone.Core.CustomFormats.CustomFormat>();
                    var qualities = new List<NzbDrone.Core.Profiles.Qualities.QualityProfile>();
                    var package = Export(config);
                    package.Categories = new() { SettingsBackupCategory.UiSettings, SettingsBackupCategory.Profiles, SettingsBackupCategory.Connections };
                    package.UiSettings = new UiSettingsBackup
                    {
                        Theme = "dark", UILanguage = config.UILanguage, AddNewDefaultMediaType = config.AddNewDefaultMediaType,
                        FirstDayOfWeek = config.FirstDayOfWeek, CalendarWeekColumnHeader = config.CalendarWeekColumnHeader,
                        ShortDateFormat = config.ShortDateFormat, LongDateFormat = config.LongDateFormat, TimeFormat = config.TimeFormat
                    };
                    package.CustomFormats = new() { new CustomFormatBackup { OriginalId = 4, Name = "Requested format" } };
                    package.QualityProfiles = new()
                    {
                        new QualityProfileBackup
                        {
                            Name = "Requested quality", ProfileType = NzbDrone.Core.Profiles.Qualities.ProfileType.Audiobook, Cutoff = NzbDrone.Core.Qualities.Quality.MP3.Id,
                            Items = new() { new NzbDrone.Core.Profiles.Qualities.QualityProfileQualityItem { Quality = NzbDrone.Core.Qualities.Quality.MP3, Allowed = true } }
                        }
                    };
                    package.DelayProfiles = new() { new NzbDrone.Core.Profiles.Delay.DelayProfile { Id = 1, EnableTorrent = true } };
                    package.ReleaseProfiles = new();
                    package.QualityDefinitions = new();
                    var file = Path.Combine(folder, "failure.chaptarr-settings-backup.json");
                    File.WriteAllText(file, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(package, STJson.GetSerializerSettings())), "synthetic passphrase"), STJson.GetSerializerSettings()));
                    IDiskProvider disk = OsInfo.IsWindows ? new NzbDrone.Windows.Disk.DiskProvider()
                        : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, LogManager.GetCurrentClassLogger());
                    var service = new SettingsBackupService(InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => folder), disk, config, null,
                        InterfaceProxy.Proxy<NzbDrone.Core.Indexers.IIndexerFactory>((_, _) => new List<NzbDrone.Core.Indexers.IndexerDefinition>()), null,
                        InterfaceProxy.Proxy<NzbDrone.Core.Notifications.INotificationFactory>((method, _) => method.Name == "All" ? new List<NzbDrone.Core.Notifications.NotificationDefinition>() : throw new AssertionException("Connection writes must not be attempted")),
                        InterfaceProxy.Proxy<NzbDrone.Core.Profiles.Qualities.IQualityProfileService>((method, args) =>
                        {
                            if (method.Name == "All") return qualities;
                            var value = (NzbDrone.Core.Profiles.Qualities.QualityProfile)args[0];
                            value.Id = 1; qualities.Add(value); calls.Add("quality profiles"); return value;
                        }),
                        InterfaceProxy.Proxy<NzbDrone.Core.Profiles.Metadata.IMetadataProfileService>((_, _) => new List<NzbDrone.Core.Profiles.Metadata.MetadataProfile>()),
                        InterfaceProxy.Proxy<NzbDrone.Core.CustomFormats.ICustomFormatService>((method, args) =>
                        {
                            if (method.Name == "All") return formats;
                            Assert.That(messages.Logs.Any(line => line.StartsWith("Info|Settings restore started:")), Is.True, "Start must be logged before the first profile write");
                            var value = (NzbDrone.Core.CustomFormats.CustomFormat)args[0]; value.Id = 1; formats.Add(value); calls.Add("custom formats"); return value;
                        }), InterfaceProxy.Proxy<NzbDrone.Core.Tags.ITagService>((_, _) => new List<NzbDrone.Core.Tags.Tag>()), null, null, host,
                        InterfaceProxy.Proxy<NzbDrone.Core.Profiles.Delay.IDelayProfileService>((method, _) =>
                        {
                            if (method.Name == "All") return new List<NzbDrone.Core.Profiles.Delay.DelayProfile> { new() { Id = 1, EnableTorrent = true } };
                            calls.Add("delay profiles"); throw new IOException("synthetic secret must not be exposed");
                        }), releaseProfileService: InterfaceProxy.Proxy<NzbDrone.Core.Profiles.Releases.IReleaseProfileService>((_, _) => new List<NzbDrone.Core.Profiles.Releases.ReleaseProfile>()), qualityDefinitionService: null, mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
                    var controller = new Chaptarr.Api.V1.System.SettingsBackup.SettingsBackupController(service);
                    var error = Assert.Throws<NzbDroneClientException>(() => controller.Restore(new SettingsBackupRestoreRequest
                    {
                        FilePath = file, Passphrase = "synthetic passphrase", Categories = package.Categories.ToHashSet(), Mode = SettingsBackupRestoreMode.Overwrite
                    }));
                    Assert.That(error.Message, Does.StartWith("Settings restore stopped"));
                    Assert.That(calls, Is.EqualTo(new[] { "custom formats", "quality profiles", "delay profiles" }));
                    Assert.That(host.Theme, Is.EqualTo("dark"));
                    Assert.That(error.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.InternalServerError));
                    Assert.That(error.Message, Does.Contain("Completed categories: Interface Preferences."));
                    Assert.That(error.Message, Does.Contain("Partly applied categories: Profiles: custom formats, metadata profiles, quality profiles completed; failed at delay profiles (may be partially changed); release profiles, quality sizes not attempted."));
                    Assert.That(error.Message, Does.Contain("Not attempted categories: Connections."));
                    Assert.That(error.Message, Does.Contain("IOException").And.Contain("Changes were not rolled back").And.Not.Contain("synthetic secret"));
                    Assert.That(messages.Logs, Does.Contain("Error|" + error.Message));
                    var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                    context.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>(new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature { Error = error });
                    using var response = new MemoryStream();
                    context.Response.Body = response;
                    new Chaptarr.Http.ErrorManagement.ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context).GetAwaiter().GetResult();
                    Assert.That(context.Response.StatusCode, Is.EqualTo(500));
                    using var json = JsonDocument.Parse(response.ToArray());
                    Assert.That(json.RootElement.GetProperty("message").GetString(), Is.EqualTo(error.Message));
                }
                finally
                {
                    LogManager.Configuration = originalLogging;
                }
            });
        }

        private static void WithBackupFolder(Action<string> check)
        {
            var folder = Path.Combine(Path.GetTempPath(), "settings_backup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                check(folder);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static ConfigFileProvider CreateHostConfig(string folder)
        {
            IDiskProvider disk = OsInfo.IsWindows
                ? new NzbDrone.Windows.Disk.DiskProvider()
                : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, LogManager.GetCurrentClassLogger());
            return new ConfigFileProvider(InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => folder),
                new CacheManager(), InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), disk,
                Options.Create(new PostgresOptions()), Options.Create(new AuthOptions()), Options.Create(new AppOptions()),
                Options.Create(new ServerOptions()), Options.Create(new UpdateOptions()), Options.Create(new LogOptions()));
        }

        private static SettingsBackupService FileBackupService(IConfigService config, string folder, IConfigFileProvider host = null, IRemotePathMappingService mappings = null)
        {
            var folders = InterfaceProxy.Proxy<IAppFolderInfo>((method, _) => method.Name == "get_StartUpFolder" ? AppContext.BaseDirectory : folder);
            IDiskProvider disk = OsInfo.IsWindows
                ? new NzbDrone.Windows.Disk.DiskProvider()
                : new NzbDrone.Mono.Disk.DiskProvider(null, null, null, LogManager.GetCurrentClassLogger());
            return CreateBackupService(config, folders: folders, disk: disk, host: host, mappings: mappings);
        }

        [Test]
        public void older_backups_should_preserve_settings_whose_optional_fields_are_absent()
        {
            var properties = typeof(MediaManagementBackup).GetProperties()
                .Where(p => Nullable.GetUnderlyingType(p.PropertyType) != null)
                .Select(p => typeof(IConfigService).GetProperty(p.Name))
                .Where(p => p != null)
                .ToList();
            Assert.That(properties, Is.Not.Empty);

            var source = CreateConfigService();
            var json = JsonSerializer.SerializeToNode(Export(source), STJson.GetSerializerSettings());
            var mediaManagement = json["mediaManagement"].AsObject();
            foreach (var property in properties)
            {
                mediaManagement.Remove(JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            }

            var package = json.Deserialize<SettingsBackupPackage>(STJson.GetSerializerSettings());
            var target = CreateConfigService();
            var expected = new Dictionary<string, object>();
            foreach (var property in properties)
            {
                property.SetValue(target, ChangedValue(property, property.GetValue(target)));
                expected[property.Name] = property.GetValue(target);
            }

            Restore(target, package);

            Assert.Multiple(() =>
            {
                foreach (var property in properties)
                {
                    Assert.That(property.GetValue(target), Is.EqualTo(expected[property.Name]), property.Name);
                }
            });
        }

        private static object ChangedValue(PropertyInfo property, object defaultValue)
        {
            if (property.Name == nameof(IConfigService.AudiobookConversionTagMode))
            {
                return ConversionTagModes.Clean;
            }

            if (property.Name == nameof(IConfigService.AddNewDefaultMediaType)) return "ebook";
            if (property.Name == nameof(IConfigService.AudiobookConversionAudioChannels)) return "mono";
            if (property.Name == nameof(IConfigService.EbookConversionTargetFormat)) return "azw3";
            return SettingsBackupTestValues.DifferentValue(property.PropertyType, defaultValue);
        }

        [TestCase(CoverDownloadScope.AllBooks, CoverDownloadScope.LibraryBooks, true)]
        [TestCase(CoverDownloadScope.LibraryBooks, CoverDownloadScope.LibraryBooks, false)]
        [TestCase(CoverDownloadScope.LibraryBooks, CoverDownloadScope.AllBooks, false)]
        [TestCase(CoverDownloadScope.AllBooks, CoverDownloadScope.AllBooks, false)]
        [TestCase(CoverDownloadScope.LibraryBooks, null, false)]
        [TestCase(CoverDownloadScope.AllBooks, null, false)]
        public void save_and_encrypted_restore_should_share_cover_cleanup_after_persistence(
            CoverDownloadScope initialScope, CoverDownloadScope? requestedScope, bool expectCleanup)
        {
            foreach (var restore in new[] { false, true })
            {
                var queued = new List<Command>();
                ConfigService config = null;
                var queue = InterfaceProxy.Proxy<IManageCommandQueue>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IManageCommandQueue.Push)));
                    Assert.That(config.CoverDownloadScope, Is.EqualTo(CoverDownloadScope.LibraryBooks));
                    queued.Add((Command)args[0]);
                    return null;
                });
                var covers = new DeferredCoverDownloadService(null, null, queue, null, LogManager.GetCurrentClassLogger());
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is ConfigSavedEvent saved)
                    {
                        covers.Handle(saved);
                    }

                    return null;
                });
                config = CreateConfigService(events);
                config.CoverDownloadScope = initialScope;

                if (restore)
                {
                    WithBackupFolder(folder =>
                    {
                        var package = Export(config);
                        package.Categories = new() { SettingsBackupCategory.MediaManagement };
                        package.MediaManagement.CoverDownloadScope = requestedScope;
                        var path = Path.Combine(folder, "cover-scope.chaptarr-settings-backup.json");
                        File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(
                            JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));
                        FileBackupService(config, folder).RestoreBackup(new SettingsBackupRestoreRequest
                        {
                            FilePath = path, Passphrase = "test passphrase", Categories = package.Categories.ToHashSet()
                        });
                    });
                }
                else
                {
                    var values = new Dictionary<string, object>();
                    if (requestedScope.HasValue)
                    {
                        values[nameof(IConfigService.CoverDownloadScope)] = requestedScope.Value;
                    }

                    config.SaveConfigDictionary(values);
                }

                Assert.That(config.CoverDownloadScope, Is.EqualTo(requestedScope ?? initialScope));
                Assert.That(queued.Count, Is.EqualTo(expectCleanup ? 1 : 0), $"restore={restore}");
                Assert.That(queued, Has.All.TypeOf<CleanupBookCoversCommand>());
                config.SaveConfigDictionary(new Dictionary<string, object> { [nameof(IConfigService.DeleteEmptyFolders)] = true });
                Assert.That(queued.Count, Is.EqualTo(expectCleanup ? 1 : 0), "Unrelated settings must not repeat cleanup");
            }
        }

        [Test]
        public void encrypted_media_management_restore_updates_live_watchers_without_optional_cover_settings(
            [Values(false, true)] bool postgres, [Values(false, true)] bool initialWatch,
            [Values(false, true)] bool includeCoverScope)
        {
            WithBackupFolder(folder => DualDatabaseFixture.Run(postgres, database =>
            {
                var library = Path.Combine(folder, "library");
                Directory.CreateDirectory(library);
                RootFolderWatchingService watcher = null;
                ConfigService config = null;
                var savedEvents = new List<ConfigSavedEvent>();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is ConfigSavedEvent saved)
                    {
                        Assert.That(config.WatchLibraryForChanges, Is.EqualTo(!initialWatch));
                        Assert.That(config.GranularFileSystemScanning, Is.True);
                        savedEvents.Add(saved);
                        watcher.Handle(saved);
                    }

                    return null;
                });
                var repository = new ConfigRepository(database, events);
                config = new ConfigService(repository, events, null, LogManager.GetCurrentClassLogger());
                config.WatchLibraryForChanges = initialWatch;
                config.GranularFileSystemScanning = false;
                config.CoverDownloadScope = CoverDownloadScope.AllBooks;
                var roots = InterfaceProxy.Proxy<NzbDrone.Core.RootFolders.IRootFolderService>((method, _) =>
                    method.Name == "All" ? new List<NzbDrone.Core.RootFolders.RootFolder> { new() { Path = library } }
                    : throw new NotSupportedException(method.Name));
                using (watcher = new RootFolderWatchingService(roots, null, config, null, LogManager.GetCurrentClassLogger()))
                {
                    var watchers = (System.Collections.Concurrent.ConcurrentDictionary<string, FileSystemWatcher>)
                        typeof(RootFolderWatchingService).GetField("_fileSystemWatchers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(watcher);
                    watcher.Handle(new NzbDrone.Core.Lifecycle.ApplicationStartedEvent());
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => watchers.Count == (initialWatch ? 1 : 0), 5000), Is.True);
                    var package = Export(config);
                    package.Categories = new() { SettingsBackupCategory.MediaManagement };
                    package.MediaManagement.WatchLibraryForChanges = !initialWatch;
                    package.MediaManagement.GranularFileSystemScanning = true;
                    package.MediaManagement.CoverDownloadScope = includeCoverScope ? CoverDownloadScope.LibraryBooks : null;
                    var path = Path.Combine(folder, "watchers.chaptarr-settings-backup.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(SettingsBackupCrypto.Encrypt(
                        JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings()), "test passphrase"), STJson.GetSerializerSettings()));

                    FileBackupService(config, folder).RestoreBackup(new SettingsBackupRestoreRequest
                    {
                        FilePath = path, Passphrase = "test passphrase", Categories = package.Categories.ToHashSet()
                    });

                    Assert.That(savedEvents, Has.Count.EqualTo(1));
                    Assert.That(savedEvents.Single().BookCoverCleanupRequired, Is.EqualTo(includeCoverScope));
                    Assert.That(repository.Get("watchlibraryforchanges").Value, Is.EqualTo((!initialWatch).ToString()));
                    Assert.That(config.CoverDownloadScope, Is.EqualTo(includeCoverScope ? CoverDownloadScope.LibraryBooks : CoverDownloadScope.AllBooks));
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => watchers.Count == (initialWatch ? 0 : 1), 5000), Is.True);
                }
            }));
        }

        [Test]
        public void unchanged_invalid_settings_allow_unrelated_save_and_remain_visible_in_health()
        {
            var config = CreateConfigService();
            config.RssSyncInterval = 999;
            var controller = new IndexerConfigController(config);
            var resource = controller.GetConfig();
            resource.MinimumAge = 2;
            var validator = (FluentValidation.IValidator<IndexerConfigResource>)typeof(Chaptarr.Http.REST.RestController<IndexerConfigResource>)
                .GetProperty("SharedValidator", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            Assert.That(validator.Validate(resource).IsValid, Is.True);
            controller.SaveConfig(resource);
            Assert.That(config.MinimumAge, Is.EqualTo(2));
            Assert.That(config.RssSyncInterval, Is.EqualTo(999));
            var check = new NzbDrone.Core.HealthCheck.Checks.ConfigValueCheck(config,
                InterfaceProxy.Proxy<NzbDrone.Core.Localization.ILocalizationService>((_, _) => "Invalid: {0}"));
            Assert.That(check.Check().Message, Does.Contain("RssSyncInterval"));
            resource.RssSyncInterval = 998;
            Assert.That(validator.Validate(resource).Errors.Single().PropertyName, Is.EqualTo("RssSyncInterval"));
            resource.RssSyncInterval = 15;
            Assert.That(validator.Validate(resource).IsValid, Is.True);
            controller.SaveConfig(resource);
            Assert.That(check.Check().Type, Is.EqualTo(NzbDrone.Core.HealthCheck.HealthCheckResult.Ok));
        }

        [Test]
        public void changing_concurrency_still_checks_the_unchanged_cpu_thread_budget()
        {
            var config = CreateConfigService();
            var controller = new ConversionConfigController(config);
            var current = controller.GetConfig();
            var requested = controller.GetConfig();
            requested.AudiobookConversionConcurrentConversions = current.AudiobookConversionMaxCpuThreads + 1;
            Assert.That(ConfigValueValidation.Validate(requested, current).Select(error => error.PropertyName), Does.Contain(nameof(IConfigService.AudiobookConversionMaxCpuThreads)));
        }

        private static ConfigService CreateConfigService(IEventAggregator events = null)
        {
            var values = new Dictionary<string, Config>();
            var repository = InterfaceProxy.Proxy<IConfigRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IConfigRepository.All):
                        return values.Values.ToList();
                    case nameof(IConfigRepository.Get):
                        return values.GetValueOrDefault((string)args[0]);
                    case nameof(IConfigRepository.Upsert):
                        values[(string)args[0]] = new Config { Key = (string)args[0], Value = (string)args[1] };
                        return null;
                    default:
                        throw new NotImplementedException(method.Name);
                }
            });
            return new ConfigService(repository, events ?? InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), null, LogManager.GetCurrentClassLogger());
        }

        [TestCase("/data", true)]
        [TestCase("/data/settings-backups", true)]
        [TestCase("/database", false)]
        [TestCase("/data/../outside", false)]
        public void settings_backup_accepts_shared_data_mount_without_escaping_it(string path, bool allowed)
        {
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, _) => method.Name == "FolderExists" ? false : throw new AssertionException(method.Name));
            var folders = InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => "/config");
            var service = CreateBackupService(null, folders: folders, disk: disk);
            if (allowed) Assert.That(service.GetFiles(path), Is.Empty);
            else Assert.Throws<NzbDroneClientException>(() => service.GetFiles(path));
        }

        [Test]
        public void settings_backup_locations_hide_absent_mounts_but_keep_read_only_mounts_visible()
        {
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                "FolderExists" => (string)args[0] == "/data",
                "FolderWritable" => false,
                _ => throw new AssertionException(method.Name)
            });
            var service = CreateBackupService(null, folders: InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => "/config"), disk: disk);
            var locations = service.GetLocations();
            Assert.That(locations.Select(location => location.Path), Is.EqualTo(new[] { "/data" }));
            Assert.That(locations.Single().Writable, Is.False);
        }

        private static SettingsBackupService CreateBackupService(IConfigService config, INamingConfigService naming = null, IAppFolderInfo folders = null, IDiskProvider disk = null, IConfigFileProvider host = null, IRemotePathMappingService mappings = null)
        {
            host ??= InterfaceProxy.Proxy<IConfigFileProvider>((_, _) => "auto");
            naming ??= InterfaceProxy.Proxy<INamingConfigService>((method, _) => method.Name == nameof(INamingConfigService.GetConfig) ? NamingConfig.Default : null);
            return new SettingsBackupService(folders, disk, config, naming,
                InterfaceProxy.Proxy<NzbDrone.Core.Indexers.IIndexerFactory>((_, _) => new List<NzbDrone.Core.Indexers.IndexerDefinition>()),
                InterfaceProxy.Proxy<NzbDrone.Core.Download.IDownloadClientFactory>((_, _) => new List<NzbDrone.Core.Download.DownloadClientDefinition>()),
                null, null, null, null, InterfaceProxy.Proxy<NzbDrone.Core.Tags.ITagService>((_, _) => new List<NzbDrone.Core.Tags.Tag>()),
                InterfaceProxy.Proxy<NzbDrone.Core.Configuration.IProxyService>((_, _) => new List<NzbDrone.Core.Configuration.ProxyDefinition>()), mappings, host, mediaPaths: disk == null ? null : CreateMediaPathValidator(disk, folders), filenameValidation: CreateFilenameValidation(), delayProfileService: null, releaseProfileService: null, qualityDefinitionService: null);
        }

        internal static IFilenameValidationService CreateFilenameValidation() =>
            new FileNameValidationService(new FileNameSampleService(Organizer.FileNameBuilderSeriesTokensFixture.CreateBuilder()));

        [TestCase(false)]
        [TestCase(true)]
        public void restore_must_reject_a_pattern_that_only_breaks_the_multipart_sample(bool ebook)
        {
            var naming = NamingConfig.Default;
            const string invalid = "{Book Title}{ (PartNumber:Q)}";
            if (ebook) naming.EbookStandardBookFormat = invalid;
            else naming.StandardBookFormat = invalid;
            Assert.That(ConfigValueValidation.ValidateNaming(naming), Is.Empty, "The syntax-only check missed this valid token with an unusable part format");
            var package = new SettingsBackupPackage
            {
                MediaManagement = new MediaManagementBackup { MinimumFreeSpaceWhenImporting = 100, NamingConfig = naming }
            };
            var error = Assert.Throws<NzbDroneClientException>(() => SettingsBackupService.ValidateRestore(package,
                new() { SettingsBackupCategory.MediaManagement }, filenameValidation: CreateFilenameValidation()));
            Assert.That(error.Message, Does.Contain(ebook ? "Naming.EbookStandardBookFormat" : "Naming.StandardBookFormat")
                .And.Contain("Produces invalid file names").And.Contain("No settings were restored"));
            naming.StandardBookFormat = naming.EbookStandardBookFormat = "{Book Title}{ (PartNumber:smart)}";
            Assert.DoesNotThrow(() => SettingsBackupService.ValidateRestore(package,
                new() { SettingsBackupCategory.MediaManagement }, filenameValidation: CreateFilenameValidation()));
        }

        private static MediaManagementPathValidator CreateMediaPathValidator(IDiskProvider disk, IAppFolderInfo folders)
        {
            var authors = InterfaceProxy.Proxy<NzbDrone.Core.Books.IAuthorService>((_, _) => new List<KeyValuePair<int, string>>());
            var roots = InterfaceProxy.Proxy<NzbDrone.Core.RootFolders.IRootFolderService>((_, _) => new List<NzbDrone.Core.RootFolders.RootFolder>());
            return new MediaManagementPathValidator(new NzbDrone.Core.Validation.Paths.PathExistsValidator(disk),
                new NzbDrone.Core.Validation.FolderChmodValidator(disk), new NzbDrone.Core.Validation.Paths.FolderWritableValidator(disk),
                new NzbDrone.Core.Validation.Paths.AuthorPathValidator(authors), new NzbDrone.Core.Validation.Paths.StartupFolderValidator(folders),
                new NzbDrone.Core.Validation.Paths.SystemFolderValidator(), new NzbDrone.Core.Validation.Paths.RootFolderAncestorValidator(roots),
                new NzbDrone.Core.Validation.Paths.RootFolderValidator(roots));
        }

        private static SettingsBackupPackage Export(IConfigService config, INamingConfigService naming = null, IConfigFileProvider host = null)
        {
            var method = typeof(SettingsBackupService).GetMethod("BuildPackage", BindingFlags.Instance | BindingFlags.NonPublic);
            var package = (SettingsBackupPackage)method.Invoke(CreateBackupService(config, naming, host: host), new object[]
            {
                new HashSet<SettingsBackupCategory> { SettingsBackupCategory.MediaManagement, SettingsBackupCategory.MetadataServer, SettingsBackupCategory.Hardcover, SettingsBackupCategory.Indexers, SettingsBackupCategory.DownloadClients, SettingsBackupCategory.UiSettings }
            });
            return JsonSerializer.Deserialize<SettingsBackupPackage>(JsonSerializer.Serialize(package, STJson.GetSerializerSettings()), STJson.GetSerializerSettings());
        }

        private static void Restore(IConfigService config, SettingsBackupPackage package, INamingConfigService naming = null, IConfigFileProvider host = null, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Overwrite)
        {
            var service = CreateBackupService(config, naming, host: host);
            if (mode == SettingsBackupRestoreMode.Merge)
            {
                typeof(SettingsBackupService).GetMethod("MergeGeneralSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(service, new object[] { package, package.Categories.ToHashSet() });
            }
            foreach (var section in new[] { "MediaManagement", "MetadataServer", "Hardcover", "IndexerSettings", "DownloadClientSettings", "UiSettings" })
            {
                var method = typeof(SettingsBackupService).GetMethod($"Restore{section}", BindingFlags.Instance | BindingFlags.NonPublic);
                method.Invoke(service, new object[] { package, new SettingsBackupRestoreResult() });
            }
        }
    }
}
