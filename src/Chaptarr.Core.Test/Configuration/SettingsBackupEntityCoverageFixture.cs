using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Tags;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    [NonParallelizable]
    public class SettingsBackupEntityCoverageFixture
    {
        private static readonly Type[] EntityTypes =
        {
            typeof(QualityProfile), typeof(MetadataProfile), typeof(CustomFormat),
            typeof(ProxyDefinition), typeof(Tag), typeof(RemotePathMapping), typeof(DelayProfile), typeof(ReleaseProfile), typeof(QualityDefinition)
        };

        // Local row identity is assigned by the destination services; references use the restore maps.
        private static readonly Dictionary<(Type Type, string Property), string> Exclusions = new()
        {
            [(typeof(ModelBase), nameof(ModelBase.Id))] = "Local identity is exported separately and assigned by the destination service"
        };

        private static IEnumerable<TestCaseData> EntityProperties()
        {
            EnsureMappings();
            foreach (var type in EntityTypes)
            {
                var ignored = TableMapping.Mapper.ExcludeProperties(type).Select(property => property.Name).ToHashSet();
                foreach (var property in WritableProperties(type).Where(property => property.IsMappableProperty() && !ignored.Contains(property.Name)))
                {
                    yield return new TestCaseData(type, property.Name)
                        .SetName($"backup_should_preserve_{type.Name}_{property.Name}");
                }
            }
        }

        private static IEnumerable<TestCaseData> SpecificationProperties()
        {
            var types = typeof(ICustomFormatSpecification).Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(ICustomFormatSpecification).IsAssignableFrom(type))
                .OrderBy(type => type.FullName);
            foreach (var type in types)
            {
                foreach (var property in WritableProperties(type))
                {
                    yield return new TestCaseData(type, property.Name)
                        .SetName($"backup_should_preserve_{type.Name}_{property.Name}");
                }
            }
        }

        private static IEnumerable<TestCaseData> EmbeddedProfileProperties()
        {
            foreach (var type in new[] { typeof(ProfileFormatItem), typeof(QualityProfileQualityItem) })
            {
                foreach (var property in WritableProperties(type))
                {
                    yield return new TestCaseData(type, property.Name)
                        .SetName($"backup_should_preserve_{type.Name}_{property.Name}");
                }
            }
        }

        [TestCaseSource(nameof(EntityProperties))]
        public void backup_should_preserve_every_discovered_entity_setting(Type type, string propertyName)
        {
            var source = CreateEntity(type);
            var property = type.GetProperty(propertyName);
            var original = property.GetValue(source);
            property.SetValue(source, SettingsBackupTestValues.DifferentValue(property.PropertyType, original));
            var expected = property.GetValue(source);
            Assert.That(Equivalent(expected, original, property.PropertyType), Is.False, "The case must change the source setting");

            var package = Export(source);
            var restored = Restore(package, type);

            Assert.That(Equivalent(property.GetValue(restored), expected, property.PropertyType), Is.True,
                $"{type.Name}.{propertyName} was omitted or changed by settings export/JSON/restore");
        }

        [TestCaseSource(nameof(SpecificationProperties))]
        public void backup_should_preserve_every_discovered_custom_format_specification_setting(Type type, string propertyName)
        {
            var specification = (ICustomFormatSpecification)Activator.CreateInstance(type);
            specification.Name = "Specification coverage";
            var property = type.GetProperty(propertyName);
            var original = property.GetValue(specification);
            property.SetValue(specification, SettingsBackupTestValues.DifferentValue(property.PropertyType, original));
            var expected = property.GetValue(specification);
            Assert.That(Equivalent(expected, original, property.PropertyType), Is.False, "The case must change the source setting");
            var source = (CustomFormat)CreateEntity(typeof(CustomFormat));
            source.Specifications = new List<ICustomFormatSpecification> { specification };

            var restored = (CustomFormat)Restore(Export(source), typeof(CustomFormat));

            Assert.That(restored.Specifications, Has.Count.EqualTo(1));
            Assert.That(restored.Specifications[0], Is.TypeOf(type));
            Assert.That(Equivalent(property.GetValue(restored.Specifications[0]), expected, property.PropertyType), Is.True,
                $"{type.Name}.{propertyName} was omitted or changed by custom format export/JSON/restore");
        }

        [TestCaseSource(nameof(EmbeddedProfileProperties))]
        public void backup_should_preserve_every_discovered_embedded_profile_setting(Type type, string propertyName)
        {
            var item = Activator.CreateInstance(type);
            var property = type.GetProperty(propertyName);
            var original = property.GetValue(item);
            property.SetValue(item, SettingsBackupTestValues.DifferentValue(property.PropertyType, original));
            var expected = property.GetValue(item);
            Assert.That(Equivalent(expected, original, property.PropertyType), Is.False, "The case must change the source setting");
            var source = (QualityProfile)CreateEntity(typeof(QualityProfile));
            if (item is ProfileFormatItem formatItem)
            {
                formatItem.Format ??= new CustomFormat { Id = 41, Name = "Coverage format" };
                source.FormatItems.Add(formatItem);
            }
            else
            {
                source.Items.Add((QualityProfileQualityItem)item);
            }

            var restored = (QualityProfile)Restore(Export(source), typeof(QualityProfile));
            var restoredItem = type == typeof(ProfileFormatItem) ? (object)restored.FormatItems.Single() : restored.Items.Single();
            Assert.That(Equivalent(property.GetValue(restoredItem), expected, property.PropertyType), Is.True,
                $"{type.Name}.{propertyName} was omitted or changed by profile export/JSON/restore");
        }

        [Test]
        public void entity_exclusions_should_reference_current_properties_and_explain_their_scope()
        {
            Assert.Multiple(() =>
            {
                foreach (var exclusion in Exclusions)
                {
                    Assert.That(exclusion.Key.Type.GetProperty(exclusion.Key.Property), Is.Not.Null);
                    Assert.That(exclusion.Value, Is.Not.Empty);
                }
            });
        }

        private static IEnumerable<PropertyInfo> WritableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
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

        private static ModelBase CreateEntity(Type type)
        {
            if (type == typeof(QualityProfile))
            {
                return new QualityProfile { Name = "Coverage profile", ProfileType = ProfileType.Audiobook, Items = new List<QualityProfileQualityItem>() };
            }

            if (type == typeof(MetadataProfile))
            {
                return new MetadataProfile { Name = "Coverage metadata", ProfileType = MetadataProfileType.Audiobook };
            }

            if (type == typeof(CustomFormat))
            {
                return new CustomFormat { Name = "Coverage format", Specifications = new List<ICustomFormatSpecification>() };
            }

            if (type == typeof(DelayProfile))
            {
                return new DelayProfile { Id = 1, EnableTorrent = true };
            }
            if (type == typeof(ReleaseProfile))
            {
                return new ReleaseProfile();
            }
            if (type == typeof(QualityDefinition))
            {
                return new QualityDefinition(Quality.MP3);
            }
            if (type == typeof(ProxyDefinition))
            {
                return new ProxyDefinition { Name = "Coverage proxy" };
            }

            return type == typeof(Tag)
                ? new Tag { Label = "coverage-tag" }
                : new RemotePathMapping { Host = "coverage-host", RemotePath = @"C:\Downloads".AsOsAgnostic(), LocalPath = @"C:\Books".AsOsAgnostic() };
        }

        private static SettingsBackupService CreateService(ModelBase source, Action<ModelBase> capture)
        {
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            return new SettingsBackupService(null, null, config, null,
                Service<IIndexerFactory, IndexerDefinition>(source, capture),
                Service<IDownloadClientFactory, DownloadClientDefinition>(source, capture),
                Service<INotificationFactory, NotificationDefinition>(source, capture),
                Service<IQualityProfileService, QualityProfile>(source, capture),
                Service<IMetadataProfileService, MetadataProfile>(source, capture),
                Service<ICustomFormatService, CustomFormat>(source, capture),
                Service<ITagService, Tag>(source, capture),
                Service<IProxyService, ProxyDefinition>(source, capture),
                Service<IRemotePathMappingService, RemotePathMapping>(source, capture), null,
                Service<IDelayProfileService, DelayProfile>(source, capture),
                Service<IReleaseProfileService, ReleaseProfile>(source, capture),
                Service<IQualityDefinitionService, QualityDefinition>(source, capture), mediaPaths: null, filenameValidation: SettingsBackupCoverageFixture.CreateFilenameValidation());
        }

        private static TService Service<TService, TModel>(ModelBase source, Action<ModelBase> capture)
            where TService : class
            where TModel : ModelBase
        {
            return InterfaceProxy.Proxy<TService>((method, args) =>
            {
                if (method.Name == "All")
                {
                    return source is TModel model ? new List<TModel> { model } : new List<TModel>();
                }

                if (method.Name == "UpdateMany")
                {
                    foreach (var item in (List<TModel>)args[0])
                    {
                        capture(item);
                    }
                    return null;
                }
                if (method.Name is "Add" or "Insert" or "Update")
                {
                    var model = (TModel)args[0];
                    capture(model);
                    return method.ReturnType == typeof(void) ? null : model;
                }

                throw new NotImplementedException($"Unexpected backup service call {typeof(TService).Name}.{method.Name}");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void profiles_must_not_export_unselected_connection_credentials(bool connectionsSelected)
        {
            var connection = new NotificationDefinition
            {
                Name = "Synthetic connection",
                Settings = new NzbDrone.Core.Notifications.Discord.DiscordSettings { WebHookUrl = "https://example.invalid/synthetic-secret" }
            };
            var categories = new HashSet<SettingsBackupCategory> { SettingsBackupCategory.Profiles };
            if (connectionsSelected) categories.Add(SettingsBackupCategory.Connections);
            var package = (SettingsBackupPackage)Invoke("BuildPackage", CreateService(connection, _ => { }), categories);
            Assert.That(package.Connections.Count, Is.EqualTo(connectionsSelected ? 1 : 0));
            Assert.That(JsonSerializer.Serialize(package, STJson.GetSerializerSettings()).Contains("synthetic-secret"), Is.EqualTo(connectionsSelected));
        }

        private static SettingsBackupPackage Export(ModelBase source)
        {
            var category = source is ProxyDefinition ? SettingsBackupCategory.Proxies
                : source is RemotePathMapping ? SettingsBackupCategory.RemotePathMappings
                : source is Tag ? SettingsBackupCategory.Indexers : SettingsBackupCategory.Profiles;
            var package = (SettingsBackupPackage)Invoke("BuildPackage", CreateService(source, _ => { }),
                new HashSet<SettingsBackupCategory> { category });
            var json = JsonSerializer.Serialize(package, STJson.GetSerializerSettings());
            return JsonSerializer.Deserialize<SettingsBackupPackage>(json, STJson.GetSerializerSettings());
        }

        private static ModelBase Restore(SettingsBackupPackage package, Type type)
        {
            var result = new SettingsBackupRestoreResult();
            ModelBase restored = null;
            var service = CreateService(null, entity => restored = entity);
            if (type == typeof(QualityProfile))
            {
                restored = (ModelBase)Invoke("CloneQualityProfile", null, package.QualityProfiles.Single(), null);
            }
            else if (type == typeof(DelayProfile) || type == typeof(ReleaseProfile) || type == typeof(QualityDefinition))
            {
                var tags = package.DelayProfiles?.SelectMany(profile => profile.Tags).Concat(package.ReleaseProfiles?.SelectMany(profile => profile.Tags) ?? Enumerable.Empty<int>()).Distinct().ToDictionary(id => id, id => id) ?? new Dictionary<int, int>();
                if (type == typeof(QualityDefinition))
                {
                    service = CreateService(new QualityDefinition(package.QualityDefinitions.Single().Quality), entity => restored = entity);
                }
                if (type == typeof(ReleaseProfile) && package.ReleaseProfiles.Single().IndexerId != 0)
                {
                    var id = package.ReleaseProfiles.Single().IndexerId;
                    package.IndexerNames[id] = "Coverage indexer";
                    service = CreateService(new IndexerDefinition { Id = id, Name = "Coverage indexer" }, entity => restored = entity);
                }
                if (type == typeof(QualityDefinition)) Invoke("RestoreQualitySizes", service, package, result, SettingsBackupRestoreMode.Overwrite);
                else Invoke(type == typeof(DelayProfile) ? "RestoreDelayProfiles" : "RestoreReleaseProfiles", service, package, tags, SettingsBackupRestoreMode.Merge, result);
            }
            else if (type == typeof(CustomFormat))
            {
                restored = (ModelBase)Invoke("FromBackup", null, package.CustomFormats.Single(), result);
            }
            else if (type == typeof(MetadataProfile))
            {
                Invoke("RestoreMetadataProfiles", service, package, result, SettingsBackupRestoreMode.Overwrite);
            }
            else if (type == typeof(ProxyDefinition))
            {
                Invoke("RestoreProxies", service, package, SettingsBackupRestoreMode.Merge, result);
            }
            else if (type == typeof(Tag))
            {
                Invoke("RestoreTags", service, package, new HashSet<SettingsBackupCategory> { SettingsBackupCategory.Indexers }, SettingsBackupRestoreMode.Merge, result);
            }
            else
            {
                var clientId = package.RemotePathMappings.Single().DownloadClientId;
                Invoke("RestoreRemotePathMappings", service, package, new Dictionary<int, int> { [clientId] = clientId }, SettingsBackupRestoreMode.Merge, result);
            }

            Assert.That(result.Warnings, Is.Empty);
            Assert.That(restored, Is.Not.Null, type.Name);
            return restored;
        }

        private static object Invoke(string name, object instance, params object[] args)
        {
            var flags = BindingFlags.NonPublic | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            var method = typeof(SettingsBackupService).GetMethods(flags)
                .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length &&
                    candidate.GetParameters().Select((parameter, index) => args[index] == null || parameter.ParameterType.IsInstanceOfType(args[index])).All(matches => matches));
            return method.Invoke(instance, args);
        }

        private static bool Equivalent(object actual, object expected, Type type)
        {
            return JsonSerializer.Serialize(actual, type, STJson.GetSerializerSettings()) ==
                   JsonSerializer.Serialize(expected, type, STJson.GetSerializerSettings());
        }

    }
}
