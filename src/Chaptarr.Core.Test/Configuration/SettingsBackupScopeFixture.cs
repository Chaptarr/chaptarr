using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.CustomFilters;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.History;
using NzbDrone.Core.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Update.History;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    [NonParallelizable]
    public class SettingsBackupScopeFixture
    {
        // Portable categories deliberately omit library and installation state. New persistence
        // must receive an explicit scope decision rather than silently falling out of coverage.
        private static readonly Dictionary<Type, (string Table, SettingsBackupCategory? Category, string Reason)> Scope = new()
        {
            [typeof(Config)] = ("Config", null, "Partially carried by settings categories; every writable IConfigService setting is checked by SettingsBackupCoverageFixture"),
            [typeof(RootFolder)] = ("RootFolders", null, "Excluded: installation-specific root paths and defaults have no portable root-folder category"),
            [typeof(ScheduledTask)] = ("ScheduledTasks", null, "Excluded: runtime scheduling state is recreated by the task manager"),
            [typeof(IndexerDefinition)] = ("Indexers", SettingsBackupCategory.Indexers, "Carried by IndexerDefinitionBackup with provider settings and remapped references"),
            [typeof(ImportListDefinition)] = ("ImportLists", null, "Excluded: import-list definitions have no portable backup category"),
            [typeof(PendingNotificationItem)] = ("PendingNotificationItems", null, "Excluded: pending notification deliveries reference installation-specific paths and connection IDs; portable settings carry connection configuration only"),
            [typeof(NotificationDefinition)] = ("Notifications", SettingsBackupCategory.Connections, "Carried by NotificationDefinitionBackup with provider settings and remapped tags"),
            [typeof(MetadataDefinition)] = ("Metadata", null, "Excluded: metadata-provider definitions have no portable backup category"),
            [typeof(DownloadClientDefinition)] = ("DownloadClients", SettingsBackupCategory.DownloadClients, "Carried by DownloadClientDefinitionBackup with provider settings and remapped tags"),
            [typeof(EntityHistory)] = ("History", null, "Excluded: library operation history is retained by database backup, not portable settings"),
            [typeof(Author)] = ("Authors", null, "Excluded: author catalog, local paths and monitoring state belong to the library database"),
            [typeof(AuthorSyncMetadata)] = ("AuthorSyncMetadata", null, "Excluded: per-author synchronization metadata belongs to the library database"),
            [typeof(AuthorSyncQueue)] = ("AuthorSyncQueue", null, "Excluded: durable author synchronization work is runtime state"),
            [typeof(ProviderAlias)] = ("ProviderAliasIndex", null, "Excluded: aliases reference local library entities and are retained with their database"),
            [typeof(PendingAuthorImport)] = ("PendingAuthorImport", null, "Excluded: pending author requests and retries are durable runtime work"),
            [typeof(ImportListBookIdentityCache)] = ("ImportListBookIdentityCache", null, "Excluded: cached import-list identity evidence belongs to the source installation"),
            [typeof(NzbDrone.Core.Books.PendingImport)] = ("PendingImport", null, "Excluded: pending file imports are runtime work tied to local paths"),
            [typeof(Series)] = ("Series", null, "Excluded: authoritative series catalog belongs to the library database"),
            [typeof(SeriesBookLink)] = ("SeriesBookLink", null, "Excluded: series links reference local library rows"),
            [typeof(NarratorMetadata)] = ("NarratorMetadata", null, "Excluded: narrator metadata belongs to the library catalog"),
            [typeof(Narrator)] = ("Narrators", null, "Excluded: narrator catalog belongs to the library database"),
            [typeof(BookNarratorOption)] = ("BookNarratorOptions", null, "Excluded: narrator selections reference local book and narrator rows"),
            [typeof(BookNarratorLink)] = ("BookNarratorLink", null, "Excluded: narrator links reference local library rows"),
            [typeof(EditionNarratorLink)] = ("EditionNarratorLink", null, "Excluded: narrator links reference local edition rows"),
            [typeof(Book)] = ("Books", null, "Excluded: book catalog, monitoring and physical-copy identity belong to the library database"),
            [typeof(Edition)] = ("Editions", null, "Excluded: editions and narration pins belong to the library database"),
            [typeof(BookFile)] = ("BookFiles", null, "Excluded: tracked media files refer to local editions and filesystem paths"),
            [typeof(QualityDefinition)] = ("QualityDefinitions", SettingsBackupCategory.Profiles, "Carried by Profiles with destination reference remapping"),
            [typeof(CustomFormat)] = ("CustomFormats", SettingsBackupCategory.Profiles, "Carried by CustomFormatBackup with serialized specifications"),
            [typeof(QualityProfile)] = ("QualityProfiles", SettingsBackupCategory.Profiles, "Carried by QualityProfileBackup with remapped custom-format references"),
            [typeof(MetadataProfile)] = ("MetadataProfiles", SettingsBackupCategory.Profiles, "Carried directly by the existing MetadataProfiles package section"),
            [typeof(Log)] = ("Logs", null, "Excluded from portable settings; carried by full database backups"),
            [typeof(NamingConfig)] = ("NamingConfig", SettingsBackupCategory.MediaManagement, "Carried directly as MediaManagementBackup.NamingConfig using the existing model serializer"),
            [typeof(Blocklist)] = ("Blocklist", null, "Excluded: acquisition rejection history belongs to the library database"),
            [typeof(MetadataFile)] = ("MetadataFiles", null, "Excluded: tracked metadata sidecars refer to local library files"),
            [typeof(OtherExtraFile)] = ("ExtraFiles", null, "Excluded: tracked extra files refer to local library paths"),
            [typeof(PendingRelease)] = ("PendingReleases", null, "Excluded: pending download decisions are runtime acquisition state"),
            [typeof(RemotePathMapping)] = ("RemotePathMappings", SettingsBackupCategory.RemotePathMappings, "Carried by RemotePathMappingBackup with remapped download-client references"),
            [typeof(Tag)] = ("Tags", SettingsBackupCategory.Indexers, "Carried as shared TagBackup dependencies when Indexers, DownloadClients or Connections is selected"),
            [typeof(ReleaseProfile)] = ("ReleaseProfiles", SettingsBackupCategory.Profiles, "Carried by Profiles with destination reference remapping"),
            [typeof(DelayProfile)] = ("DelayProfiles", SettingsBackupCategory.Profiles, "Carried by Profiles with destination reference remapping"),
            [typeof(User)] = ("Users", null, "Excluded: installation authentication users are outside portable provider/settings categories"),
            [typeof(CommandModel)] = ("Commands", null, "Excluded: queued commands and their progress are runtime state"),
            [typeof(IndexerStatus)] = ("IndexerStatus", null, "Excluded: provider failure/backoff status is runtime state"),
            [typeof(DownloadClientStatus)] = ("DownloadClientStatus", null, "Excluded: provider failure/backoff status is runtime state"),
            [typeof(ImportListStatus)] = ("ImportListStatus", null, "Excluded: provider failure/backoff status is runtime state"),
            [typeof(NotificationStatus)] = ("NotificationStatus", null, "Excluded: provider failure/backoff status is runtime state"),
            [typeof(CustomFilter)] = ("CustomFilters", null, "Excluded: saved UI filters have no portable backup category"),
            [typeof(ImportListExclusion)] = ("ImportListExclusions", null, "Excluded: excluded author/book identities have no portable backup category"),
            [typeof(CachedHttpResponse)] = ("HttpResponse", null, "Excluded from portable settings; carried by full database backups"),
            [typeof(DownloadHistory)] = ("DownloadHistory", null, "Excluded: download lifecycle history belongs to the library database"),
            [typeof(DownloadClientFileSnapshot)] = ("DownloadClientFileSnapshots", null, "Excluded: remote download file snapshots are runtime tracking state"),
            [typeof(MamUnsatisfiedSlotReservation)] = ("MamUnsatisfiedSlotReservations", null, "Excluded: provider slot reservations are runtime acquisition state"),
            [typeof(ConversionJob)] = ("ConversionJobs", null, "Excluded: queued media conversion jobs are runtime work tied to library paths"),
            [typeof(HardcoverLibraryImportListState)] = ("HardcoverLibraryImportListState", null, "Excluded: per-list provider synchronization state belongs to the source installation"),
            [typeof(UpdateHistory)] = ("UpdateHistory", null, "Excluded: deployment history belongs to the source installation"),
            [typeof(ProxyDefinition)] = ("Proxies", SettingsBackupCategory.Proxies, "Carried by ProxyDefinitionBackup with remapped routing references"),
            [typeof(IConfigFileProvider)] = (null, null, "Host/network/authentication/database/deployment configuration is carried as complete config.xml by traditional backup; Theme is also portable and covered through the Settings > UI discovery gate")
        };

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Test fixtures may register their own models before production mapping initializes.
            if (!TableMapping.Mapper.TableMap.ContainsKey(typeof(Config)))
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void every_production_mapped_entity_should_have_an_explicit_backup_scope()
        {
            var mapped = TableMapping.Mapper.TableMap
                .Where(entry => entry.Key.Assembly == typeof(TableMapping).Assembly)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            var discovered = mapped.Keys.Append(typeof(IConfigFileProvider)).ToHashSet();
            var unclassified = mapped
                .Where(entry => !Scope.ContainsKey(entry.Key))
                .Select(entry => $"{entry.Key.FullName} -> {entry.Value}")
                .OrderBy(entry => entry)
                .ToList();
            var stale = Scope.Keys.Except(discovered).Select(type => type.FullName).OrderBy(name => name).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(mapped, Is.Not.Empty, "Production mappings must be initialized before checking backup scope");
                Assert.That(unclassified, Is.Empty,
                    "New persisted entities need a tested portable category or an explicit exclusion reason in SettingsBackupScopeFixture.Scope");
                Assert.That(stale, Is.Empty, "Remove scope entries whose production entity is no longer mapped");

                foreach (var entry in mapped.Where(entry => Scope.ContainsKey(entry.Key)))
                {
                    Assert.That(entry.Value, Is.EqualTo(Scope[entry.Key].Table),
                        $"The mapped table for {entry.Key.FullName} changed; re-check and update its backup scope");
                }

                foreach (var entry in Scope)
                {
                    Assert.That(string.IsNullOrWhiteSpace(entry.Value.Reason), Is.False,
                        $"{entry.Key.FullName} needs a reason for its portable category or exclusion");
                    if (entry.Value.Category.HasValue)
                    {
                        Assert.That(Enum.IsDefined(entry.Value.Category.Value), Is.True,
                            $"{entry.Key.FullName} refers to a removed backup category");
                    }
                }
            });
        }

        [Test]
        public void host_config_should_be_classified_as_a_whole_traditional_backup_scope()
        {
            var hostProperties = typeof(IConfigFileProvider).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(hostProperties, Is.Not.Empty, "Discover read-only host properties as well as writable database settings");
                Assert.That(Scope[typeof(IConfigFileProvider)].Table, Is.Null);
                Assert.That(Scope[typeof(IConfigFileProvider)].Category, Is.Null,
                    Scope[typeof(IConfigFileProvider)].Reason);
            });
        }
    }
}
