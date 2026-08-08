using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class SettingsBackupServiceCustomFormatRestoreFixture
    {
        [Test]
        public void restore_custom_formats_should_accept_legacy_interface_specification_shape()
        {
            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(@"
{
  ""format"": ""chaptarr.settings-backup"",
  ""categories"": [""profiles""],
  ""customFormats"": [
    {
      ""id"": 7,
      ""name"": ""Title Match"",
      ""includeCustomFormatWhenRenaming"": true,
      ""specifications"": [
        {
          ""implementationName"": ""Release Title"",
          ""name"": ""Title Spec"",
          ""negate"": false,
          ""required"": true,
          ""value"": ""dragon""
        }
      ]
    }
  ]
}", STJson.GetSerializerSettings());

            var service = CreateService(out var customFormats);
            var result = new SettingsBackupRestoreResult();

            var map = InvokeRestoreCustomFormats(service, package, SettingsBackupRestoreMode.Merge, result);

            Assert.That(map[7], Is.EqualTo(customFormats.Formats.Single().Id));
            Assert.That(customFormats.Formats.Single().Name, Is.EqualTo("Title Match"));
            Assert.That(customFormats.Formats.Single().IncludeCustomFormatWhenRenaming, Is.True);
            Assert.That(customFormats.Formats.Single().AppliesTo, Is.EqualTo(CustomFormatMediaType.Both));

            var specification = customFormats.Formats.Single().Specifications.Single();
            Assert.That(specification, Is.InstanceOf<ReleaseTitleSpecification>());
            Assert.That(specification.Name, Is.EqualTo("Title Spec"));
            Assert.That(specification.Required, Is.True);
            Assert.That(((ReleaseTitleSpecification)specification).Value, Is.EqualTo("dragon"));
        }

        [Test]
        public void restore_custom_formats_should_accept_current_backup_specification_shape()
        {
            var backup = InvokeToBackup(new CustomFormat
            {
                Id = 12,
                Name = "Size Match",
                BuiltInKey = "size-match",
                IncludeCustomFormatWhenRenaming = true,
                AppliesTo = CustomFormatMediaType.Ebook,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new SizeSpecification
                    {
                        Name = "Large File",
                        Negate = true,
                        Min = 1.5,
                        Max = 2.5
                    }
                }
            });

            var json = JsonSerializer.Serialize(new SettingsBackupPackage
            {
                Categories = new List<SettingsBackupCategory> { SettingsBackupCategory.Profiles },
                CustomFormats = new List<CustomFormatBackup> { backup }
            }, STJson.GetSerializerSettings());

            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(json, STJson.GetSerializerSettings());
            var service = CreateService(out var customFormats);
            var result = new SettingsBackupRestoreResult();

            var map = InvokeRestoreCustomFormats(service, package, SettingsBackupRestoreMode.Merge, result);

            Assert.That(map[12], Is.EqualTo(customFormats.Formats.Single().Id));
            Assert.That(customFormats.Formats.Single().BuiltInKey, Is.EqualTo("size-match"));
            Assert.That(customFormats.Formats.Single().AppliesTo, Is.EqualTo(CustomFormatMediaType.Ebook));

            var specification = customFormats.Formats.Single().Specifications.Single();
            Assert.That(specification, Is.InstanceOf<SizeSpecification>());
            Assert.That(specification.Name, Is.EqualTo("Large File"));
            Assert.That(specification.Negate, Is.True);
            Assert.That(((SizeSpecification)specification).Min, Is.EqualTo(1.5));
            Assert.That(((SizeSpecification)specification).Max, Is.EqualTo(2.5));
        }

        [Test]
        public void restore_quality_profiles_should_accept_legacy_embedded_custom_format_shape()
        {
            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(@"
{
  ""format"": ""chaptarr.settings-backup"",
  ""categories"": [""profiles""],
  ""qualityProfiles"": [
    {
      ""id"": 4,
      ""name"": ""Audiobook Default"",
      ""profileType"": ""audiobook"",
      ""upgradeAllowed"": true,
      ""cutoff"": 1,
      ""minFormatScore"": 0,
      ""cutoffFormatScore"": 0,
      ""searchCriteriaProfileId"": 3,
      ""items"": [],
      ""formatItems"": [
        {
          ""score"": 50,
          ""format"": {
            ""id"": 7,
            ""name"": ""Title Match"",
            ""includeCustomFormatWhenRenaming"": true,
            ""specifications"": [
              {
                ""implementationName"": ""Release Title"",
                ""name"": ""Title Spec"",
                ""negate"": false,
                ""required"": true,
                ""value"": ""dragon""
              }
            ]
          }
        }
      ]
    }
  ]
}", STJson.GetSerializerSettings());

            var restored = InvokeCloneQualityProfile(
                package.QualityProfiles.Single(),
                new Dictionary<int, int> { { 7, 19 } });

            Assert.That(package.QualityProfiles.Single().EffectiveOriginalId, Is.EqualTo(4));
            Assert.That(restored.Name, Is.EqualTo("Audiobook Default"));
            Assert.That(restored.ProfileType, Is.EqualTo(ProfileType.Audiobook));
            Assert.That(restored.FormatItems.Single().Score, Is.EqualTo(50));
            Assert.That(restored.FormatItems.Single().Format.Id, Is.EqualTo(19));
            Assert.That(restored.FormatItems.Single().Format.Name, Is.EqualTo("Title Match"));
        }

        [Test]
        public void quality_profile_backup_should_round_trip_preference_priority()
        {
            var backup = InvokeToBackup(new QualityProfile
            {
                Id = 4,
                Name = "Narrator First",
                ProfileType = ProfileType.Audiobook,
                PreferCustomFormatsOverQuality = true,
                Items = new List<QualityProfileQualityItem>(),
                FormatItems = new List<NzbDrone.Core.Profiles.ProfileFormatItem>()
            });

            var json = JsonSerializer.Serialize(new SettingsBackupPackage
            {
                Categories = new List<SettingsBackupCategory> { SettingsBackupCategory.Profiles },
                QualityProfiles = new List<QualityProfileBackup> { backup }
            }, STJson.GetSerializerSettings());
            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(json, STJson.GetSerializerSettings());
            var restored = InvokeCloneQualityProfile(
                package.QualityProfiles.Single(),
                new Dictionary<int, int>());

            Assert.Multiple(() =>
            {
                Assert.That(package.QualityProfiles.Single().PreferCustomFormatsOverQuality, Is.True);
                Assert.That(restored.PreferCustomFormatsOverQuality, Is.True);
            });
        }

        [Test]
        public void settings_backup_package_should_ignore_legacy_search_criteria_profiles_section()
        {
            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(@"
{
  ""format"": ""chaptarr.settings-backup"",
  ""categories"": [""profiles""],
  ""searchCriteriaProfiles"": [
    {
      ""id"": 3,
      ""name"": ""Default Search"",
      ""isDefault"": true,
      ""items"": [
        {
          ""type"": ""qualityProfile"",
          ""enabled"": true,
          ""priority"": 1,
          ""settings"": {
            ""qualityProfileId"": 4
          }
        }
      ]
    }
  ]
}", STJson.GetSerializerSettings());

            var packageProperties = typeof(SettingsBackupPackage)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(property => property.Name);

            Assert.That(package, Is.Not.Null);
            Assert.That(packageProperties, Does.Not.ContainKey("SearchCriteriaProfiles"));
            Assert.That(package.ExtensionData.Keys, Does.Contain("searchCriteriaProfiles"));

            var result = new SettingsBackupRestoreResult();
            SettingsBackupService.AddLegacySectionWarnings(
                package,
                new HashSet<SettingsBackupCategory> { SettingsBackupCategory.Profiles },
                result);

            Assert.That(result.Warnings, Has.One.Contains("legacy Search Criteria"));
        }

        [Test]
        public void settings_backup_package_should_deserialize_all_categories_with_legacy_profile_shapes()
        {
            var package = JsonSerializer.Deserialize<SettingsBackupPackage>(@"
{
  ""format"": ""chaptarr.settings-backup"",
  ""version"": 1,
  ""appVersion"": ""0.9.578"",
  ""createdAtUtc"": ""2026-05-20T19:00:00Z"",
  ""categories"": [""indexers"", ""downloadClients"", ""remotePathMappings"", ""connections"", ""proxies"", ""hardcover"", ""profiles"", ""mediaManagement"", ""metadataServer""],
  ""tags"": [{ ""id"": 1, ""label"": ""mam"" }],
  ""indexers"": [{
    ""originalId"": 2,
    ""name"": ""Indexer"",
    ""implementation"": ""Newznab"",
    ""configContract"": ""NewznabSettings"",
    ""tags"": [1],
    ""enableRss"": true,
    ""enableAutomaticSearch"": true,
    ""enableInteractiveSearch"": true,
    ""downloadClientId"": 5,
    ""protocol"": ""usenet"",
    ""priority"": 25,
    ""proxyId"": 8,
    ""settings"": { ""baseUrl"": ""https://indexer.example"", ""apiKey"": ""token"" }
  }],
  ""downloadClients"": [{
    ""originalId"": 5,
    ""name"": ""Client"",
    ""implementation"": ""QBittorrent"",
    ""configContract"": ""QBittorrentSettings"",
    ""enable"": true,
    ""tags"": [1],
    ""audiobookTags"": [1],
    ""ebookTags"": [],
    ""protocol"": ""torrent"",
    ""priority"": 1,
    ""removeCompletedDownloads"": true,
    ""removeFailedDownloads"": true,
    ""copyUnmanagedDownloads"": false,
    ""settings"": { ""host"": ""localhost"", ""port"": 8080 }
  }],
  ""remotePathMappings"": [{ ""originalId"": 6, ""downloadClientId"": 5, ""downloadClientName"": ""Client"", ""host"": ""host"", ""remotePath"": ""/remote"", ""localPath"": ""/local"" }],
  ""connections"": [{
    ""originalId"": 7,
    ""name"": ""Webhook"",
    ""implementation"": ""Webhook"",
    ""configContract"": ""WebhookSettings"",
    ""enable"": true,
    ""tags"": [1],
    ""onGrab"": true,
    ""onReleaseImport"": true,
    ""settings"": { ""url"": ""https://example.invalid/hook"" }
  }],
  ""proxies"": [{ ""originalId"": 8, ""name"": ""Proxy"", ""proxyType"": ""http"", ""hostname"": ""proxy.example"", ""port"": 8080, ""username"": ""u"", ""password"": ""p"", ""bypassLocalAddresses"": true, ""bypassFilter"": ""localhost"" }],
  ""proxySettings"": { ""proxyMode"": ""indexerOnly"", ""globalProxyId"": 8, ""proxyType"": ""http"", ""proxyHostname"": ""proxy.example"", ""proxyPort"": 8080, ""proxyUsername"": ""u"", ""proxyPassword"": ""p"", ""proxyBypassLocalAddresses"": true, ""proxyBypassFilter"": ""localhost"" },
  ""hardcover"": { ""enabled"": true, ""apiToken"": ""hc"", ""username"": ""user"", ""userImageUrl"": ""https://example.invalid/avatar.png"" },
  ""customFormats"": [{
    ""id"": 7,
    ""name"": ""Title Match"",
    ""includeCustomFormatWhenRenaming"": true,
    ""specifications"": [{ ""implementationName"": ""Release Title"", ""name"": ""Title Spec"", ""required"": true, ""value"": ""dragon"" }]
  }],
  ""qualityProfiles"": [{
    ""id"": 4,
    ""name"": ""Audiobook Default"",
    ""profileType"": ""audiobook"",
    ""upgradeAllowed"": true,
    ""convertMp3ToM4b"": true,
    ""convertToQualityId"": 12,
    ""cutoff"": 12,
    ""items"": [],
    ""formatItems"": [{ ""score"": 50, ""format"": { ""id"": 7, ""name"": ""Title Match"", ""specifications"": [{ ""implementationName"": ""Release Title"", ""value"": ""dragon"" }] } }],
    ""searchCriteriaProfileId"": 3
  }],
  ""metadataProfiles"": [{ ""id"": 9, ""name"": ""Metadata"", ""profileType"": ""audiobook"", ""allowedLanguages"": ""eng"", ""ignored"": [] }],
  ""searchCriteriaProfiles"": [{ ""id"": 3, ""name"": ""Default Search"", ""isDefault"": true, ""items"": [{ ""type"": ""qualityProfile"", ""enabled"": true, ""priority"": 1, ""settings"": { ""qualityProfileId"": 4 } }] }],
  ""mediaManagement"": {
    ""autoUnmonitorPreviouslyDownloadedBooks"": true,
    ""recycleBin"": ""/trash"",
    ""downloadPropersAndRepacks"": ""preferAndUpgrade"",
    ""fileDate"": ""none"",
    ""rescanAfterRefresh"": ""always"",
    ""allowFingerprinting"": ""never"",
    ""audiobookConversionConcurrentConversions"": 1,
    ""audiobookConversionMaxBitrate"": 64,
    ""audiobookConversionMaxCpuThreads"": 4,
    ""audiobookConversionAudioChannels"": ""source"",
    ""audiobookConversionTagMode"": ""preserve"",
    ""ebookConversionTargetFormat"": ""epub"",
    ""namingConfig"": { ""renameBooks"": true, ""replaceIllegalCharacters"": true, ""colonReplacementFormat"": ""smart"", ""standardBookFormat"": ""{Book Title}"", ""authorFolderFormat"": ""{Author NameFirstLast}"" }
  },
  ""metadataServerUrl"": ""https://api2.chaptarr.com"",
  ""metadataSource"": ""chaptarr"",
  ""counts"": { ""qualityProfiles"": 1 },
  ""warnings"": []
}", STJson.GetSerializerSettings());

            Assert.That(package, Is.Not.Null);
            Assert.That(package.Indexers.Single().Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(package.DownloadClients.Single().Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(package.Connections.Single().Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(package.CustomFormats.Single().Specifications.Single().ImplementationName, Is.EqualTo("Release Title"));
            Assert.That(package.QualityProfiles.Single().FormatItems.Single().Format.Specifications.Single().ImplementationName, Is.EqualTo("Release Title"));
            Assert.That(package.MediaManagement.NamingConfig.StandardBookFormat, Is.EqualTo("{Book Title}"));
        }

        [Test]
        public void settings_backup_contract_should_not_embed_runtime_profile_models_that_contain_interfaces()
        {
            var packageProperties = typeof(SettingsBackupPackage)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(property => property.Name);

            Assert.That(packageProperties[nameof(SettingsBackupPackage.CustomFormats)].PropertyType, Is.EqualTo(typeof(List<CustomFormatBackup>)));
            Assert.That(packageProperties[nameof(SettingsBackupPackage.QualityProfiles)].PropertyType, Is.EqualTo(typeof(List<QualityProfileBackup>)));
            Assert.That(packageProperties, Does.Not.ContainKey("SearchCriteriaProfiles"));
            Assert.That(typeof(ProfileFormatItemBackup).GetProperty(nameof(ProfileFormatItemBackup.Format))?.PropertyType, Is.EqualTo(typeof(CustomFormatBackup)));
        }

        [Test]
        public void legacy_backup_should_not_reenable_mam_wedge_spending()
        {
            var package = new SettingsBackupPackage { Version = 1 };
            var definition = new IndexerDefinition
            {
                Settings = new MyAnonaMouseSettings
                {
                    UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred
                }
            };

            var changed = SettingsBackupService.ResetLegacyMamWedgePreference(package, definition);

            Assert.That(changed, Is.True);
            Assert.That(((MyAnonaMouseSettings)definition.Settings).UseFreeleechWedge, Is.EqualTo((int)MyAnonaMouseFreeleechWedgeAction.Never));
        }

        [Test]
        public void current_backup_should_preserve_explicit_mam_wedge_preference()
        {
            var package = new SettingsBackupPackage();
            var definition = new IndexerDefinition
            {
                Settings = new MyAnonaMouseSettings
                {
                    UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred
                }
            };

            var changed = SettingsBackupService.ResetLegacyMamWedgePreference(package, definition);

            Assert.That(changed, Is.False);
            Assert.That(((MyAnonaMouseSettings)definition.Settings).UseFreeleechWedge, Is.EqualTo((int)MyAnonaMouseFreeleechWedgeAction.Preferred));
        }

        private static SettingsBackupService CreateService(out StubCustomFormatService customFormatService)
        {
            customFormatService = new StubCustomFormatService();

            return new SettingsBackupService(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                customFormatService,
                null,
                null,
                null);
        }

        private static Dictionary<int, int> InvokeRestoreCustomFormats(
            SettingsBackupService service,
            SettingsBackupPackage package,
            SettingsBackupRestoreMode mode,
            SettingsBackupRestoreResult result)
        {
            var method = typeof(SettingsBackupService).GetMethod("RestoreCustomFormats", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);

            return (Dictionary<int, int>)method.Invoke(service, new object[]
            {
                package,
                new HashSet<SettingsBackupCategory> { SettingsBackupCategory.Profiles },
                mode,
                result
            });
        }

        private static CustomFormatBackup InvokeToBackup(CustomFormat customFormat)
        {
            var method = typeof(SettingsBackupService).GetMethod("ToBackup", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(CustomFormat) }, null);

            Assert.That(method, Is.Not.Null);

            return (CustomFormatBackup)method.Invoke(null, new object[] { customFormat });
        }

        private static QualityProfileBackup InvokeToBackup(QualityProfile qualityProfile)
        {
            var method = typeof(SettingsBackupService).GetMethod("ToBackup", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(QualityProfile) }, null);

            Assert.That(method, Is.Not.Null);

            return (QualityProfileBackup)method.Invoke(null, new object[] { qualityProfile });
        }

        private static QualityProfile InvokeCloneQualityProfile(
            QualityProfileBackup qualityProfile,
            Dictionary<int, int> customFormatIdMap)
        {
            var method = typeof(SettingsBackupService).GetMethod("CloneQualityProfile", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);

            return (QualityProfile)method.Invoke(null, new object[] { qualityProfile, customFormatIdMap });
        }

        private sealed class StubCustomFormatService : ICustomFormatService
        {
            private int _nextId = 1;

            public List<CustomFormat> Formats { get; } = new();

            public void Update(CustomFormat customFormat)
            {
                var index = Formats.FindIndex(format => format.Id == customFormat.Id);
                if (index >= 0)
                {
                    Formats[index] = customFormat;
                }
            }

            public CustomFormat Insert(CustomFormat customFormat)
            {
                customFormat.Id = _nextId++;
                Formats.Add(customFormat);
                return customFormat;
            }

            public List<CustomFormat> All()
            {
                return Formats.ToList();
            }

            public CustomFormat GetById(int id)
            {
                return Formats.Single(format => format.Id == id);
            }

            public void Delete(int id)
            {
                Formats.RemoveAll(format => format.Id == id);
            }
        }
    }
}
