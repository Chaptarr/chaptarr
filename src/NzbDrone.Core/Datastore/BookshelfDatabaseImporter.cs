using System;
using System.Collections.Generic;
using System.Globalization;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;

namespace NzbDrone.Core.Datastore
{
    /// <summary>
    /// Imports the latest BookshelfNG SQLite or PostgreSQL schema into a freshly migrated Chaptarr database.
    /// Unknown schema versions, tables, providers, and ambiguous relationships fail closed.
    /// </summary>
    public static class BookshelfDatabaseImporter
    {
        internal const string BookshelfNGMigrationMarkerKey = "BookshelfNGDatabaseMigrated";

        private const int SupportedSourceMigration = BookshelfNGCompatibilityProfile.LatestSupportedSourceSchemaVersion;
        private static readonly IReadOnlyDictionary<string, string> CompatibleIndexerContracts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DirectDownloadIndexer"] = "DirectDownloadSettings",
                ["FileList"] = "FileListSettings",
                ["Gazelle"] = "GazelleSettings",
                ["IPTorrents"] = "IPTorrentsSettings",
                ["MyAnonaMouse"] = "MyAnonaMouseSettings",
                ["Newznab"] = "NewznabSettings",
                ["Nyaa"] = "NyaaSettings",
                ["TorrentRssIndexer"] = "TorrentRssIndexerSettings",
                ["Torrentleech"] = "TorrentleechSettings",
                ["Torznab"] = "TorznabSettings"
            };

        private static readonly IReadOnlyDictionary<string, string> DownloadClientContracts =
            BuildProviderContractMap(typeof(IDownloadClient), typeof(DownloadClientBase<>));
        private static readonly IReadOnlyDictionary<string, string> ImportListContracts =
            BuildProviderContractMap(typeof(IImportList), typeof(ImportListBase<>));
        private static readonly IReadOnlyDictionary<string, string> ImportListImplementationAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReadarrImport"] = "ChaptarrImport"
            };
        private static readonly IReadOnlyDictionary<string, string> ImportListContractAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReadarrSettings"] = "ChaptarrSettings"
            };
        private static readonly ISet<string> ImportListMappedSourceColumns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ProfileId" };
        private static readonly ISet<string> RootFolderMappedSourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DefaultQualityProfileId", "DefaultMetadataProfileId", "DefaultMonitorOption", "DefaultNewItemMonitorOption", "DefaultTags"
        };
        private static readonly ISet<string> AuthorMappedSourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AuthorMetadataId", "LastInfoSync"
        };
        private static readonly ISet<string> AuthorMetadataMappedSourceColumns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ForeignAuthorId" };
        private static readonly ISet<string> BookMappedSourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AuthorMetadataId", "ForeignBookId", "Monitored"
        };
        private static readonly ISet<string> EditionMappedSourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IsEbook", "Narrator"
        };
        private static readonly ISet<string> SeriesMappedSourceColumns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ForeignSeriesId" };
        private static readonly ISet<string> SeriesBookLinkMappedSourceColumns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Position" };
        private static readonly ISet<string> BlocklistMappedSourceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AuthorId", "BookIds"
        };
        private static readonly ISet<string> NamingConfigMappedSourceColumns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MultiEpisodeStyle" };
        private static readonly IReadOnlyDictionary<string, string> MetadataContracts =
            BuildProviderContractMap(typeof(IMetadata), typeof(MetadataBase<>));
        private static readonly IReadOnlyDictionary<string, string> MetadataImplementationAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AudiobookshelfMetadata"] = "LegacyAudiobookshelfMetadata"
            };
        private static readonly IReadOnlyDictionary<string, string> MetadataFileConsumerAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AudioBookShelf"] = "LegacyAudiobookshelfMetadata",
                ["AudiobookshelfMetadata"] = "LegacyAudiobookshelfMetadata",
                ["Readarr"] = "LegacyAudiobookshelfMetadata"
            };
        private static readonly IReadOnlyDictionary<string, string> NotificationContracts =
            BuildProviderContractMap(typeof(INotification), typeof(NotificationBase<>));
        private static readonly ISet<string> NotificationMappedSourceColumns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OnTrackRetag" };

        private static readonly HashSet<string> SupportedSourceTables = new(StringComparer.Ordinal)
        {
            "AuthorMetadata", "Authors", "Blocklist", "BookFiles", "Books", "Commands", "Config",
            "CustomFilters", "CustomFormats", "DelayProfiles", "DownloadClientStatus", "DownloadClients",
            "DownloadHistory", "Editions", "ExtraFiles", "History", "ImportListExclusions",
            "ImportListStatus", "ImportLists", "IndexerStatus", "Indexers", "Metadata", "MetadataFiles",
            "MetadataProfiles", "NamingConfig", "NotificationStatus", "Notifications", "PendingReleases",
            "QualityDefinitions", "QualityProfiles", "ReleaseProfiles", "RemotePathMappings", "RootFolders",
            "ScheduledTasks", "Series", "SeriesBookLink", "Tags", "Users", "VersionInfo"
        };

        private static readonly HashSet<string> GoodreadsShelfRuntimeSettings = new(StringComparer.OrdinalIgnoreCase)
        {
            "signIn", "signingUrl", "oauthUrl", "oauthRequestTokenUrl", "oauthAccessTokenUrl"
        };

        private static readonly HashSet<string> HistoricalSourceTableAliases = new(StringComparer.Ordinal)
        {
            // Bookshelf migration 14 renames this table to Blocklist.
            "Blacklist"
        };

        private static readonly string[] TargetTablesToClear =
        {
            "BookshelfLegacyIdentityMap", "ProviderAliasIndex", "AuthorSeries", "SeriesBookLink", "EditionNarratorLink", "BookNarratorLink",
            "ExtraFiles", "OtherFiles", "MetadataFiles", "BookFiles", "Editions", "Books", "Authors", "Series",
            "Narrators", "NarratorMetadata", "ImportListExclusions", "ImportListStatus", "ImportLists", "Indexers",
            "IndexerStatus", "Notifications", "NotificationStatus", "DownloadClients", "DownloadClientStatus", "Metadata",
            "MetadataProfiles", "QualityProfiles", "QualityDefinitions", "CustomFormats", "CustomFilters",
            "DelayProfiles", "ReleaseProfiles", "RemotePathMappings", "RootFolders", "Tags", "Users", "History",
            "DownloadHistory", "Blocklist", "PendingReleases", "ScheduledTasks", "Commands", "Config"
        };

        public static BookshelfDatabaseImportReport Import(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            string sourceDatabasePath,
            int sourceSchemaVersion,
            string sourceApiBasePath = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (sourceSchemaVersion < 1 || sourceSchemaVersion > SupportedSourceMigration)
            {
                throw new InvalidDataException($"BookshelfNG source schema version {sourceSchemaVersion} is unsupported; conversion stopped before promotion.");
            }

            ValidateSourceSchema(source);
            var mediaInference = BookshelfMediaTypeInference.Read(source);
            var mediaChoices = BookshelfBookMediaMapping.Load(source, sourceDatabasePath, mediaInference);
            mediaInference.ApplyMediaChoices(mediaChoices);
            DeferForeignKeys(target, transaction);
            ClearTarget(target, transaction);

            var report = new BookshelfDatabaseImportReport();
            var qualityProfiles = ImportQualityProfiles(source, target, transaction, report);
            var metadataProfiles = ImportMetadataProfiles(source, target, transaction, report);
            var rootFolders = ImportRootFolders(source, target, transaction, qualityProfiles, metadataProfiles, report);
            var authors = ImportAuthors(source, target, transaction, qualityProfiles, metadataProfiles, rootFolders, report);
            var books = ImportBooks(source, target, transaction, authors, mediaInference.Books, mediaChoices, report);
            var editions = ImportEditions(source, target, transaction, books, mediaInference.Editions, report);
            var files = ImportBookFiles(source, target, transaction, editions, report);

            ImportExtraFiles(source, target, transaction, books, files, report);
            ImportMetadataFiles(source, target, transaction, books, files, report);
            ReserveOrphanBookFileIds(source, target, transaction);
            var series = ImportSeries(source, target, transaction, report);
            ImportSeriesLinks(source, target, transaction, books, series, report);
            RebuildAuthorSeries(target, transaction);
            ImportBlocklist(source, target, transaction, authors, books, report);
            ImportDownloadHistory(source, target, transaction, authors, report);

            ImportConfig(source, target, transaction, report, sourceSchemaVersion, sourceApiBasePath);
            CopyTable(source, target, transaction, report, "CustomFilters");
            CopyTable(source, target, transaction, report, "CustomFormats");
            CopyTable(source, target, transaction, report, "DelayProfiles");
            ImportProviderTable(source, target, transaction, report, "DownloadClients", DownloadClientContracts, "download-client");
            ImportHistory(source, target, transaction, authors, books, files, report);
            CopyTable(source, target, transaction, report, "ImportListExclusions");
            ImportLists(source, target, transaction, qualityProfiles, metadataProfiles, report);
            ImportIndexers(source, target, transaction, report);
            ImportProviderTable(source, target, transaction, report, "Metadata", MetadataContracts, "metadata-consumer", MetadataImplementationAliases);
            CopyTable(source, target, transaction, report, "QualityDefinitions");
            CopyTable(source, target, transaction, report, "ReleaseProfiles");
            CopyTable(source, target, transaction, report, "RemotePathMappings");
            CopyTable(source, target, transaction, report, "Tags");
            CopyTable(source, target, transaction, report, "Users");
            ImportNotifications(source, target, transaction, report);
            ImportNamingConfig(source, target, transaction, report);

            ResetTable(source, report, "Commands", "Command queue and execution state is transient and will be recreated by Chaptarr.");
            ResetTable(source, report, "DownloadClientStatus", "Client health backoff is transient and will be probed again.");
            ResetTable(source, report, "ImportListStatus", "Import-list health backoff is transient and will be probed again.");
            ResetTable(source, report, "IndexerStatus", "Indexer health and RSS cursors are rebuilt by Chaptarr.");
            ResetTable(source, report, "NotificationStatus", "Notification health backoff is transient and will be probed again.");
            ResetTable(source, report, "PendingReleases", "Pending search results are transient and are not portable across implementations.");
            ResetTable(source, report, "ScheduledTasks", "Task schedule state is recreated by Chaptarr.");
            ResetTable(source, report, "VersionInfo", "Chaptarr migration history was created before importing source rows.");

            RebuildProviderAliases(target, transaction, authors, books, editions, series);
            if (target is NpgsqlConnection)
            {
                ResetPostgresSequences(target, transaction);
                ReserveOrphanBookFileIds(source, target, transaction);
            }

            return report;
        }

        internal static void ValidateSourceSchema(DbConnection source)
        {
            var tables = GetTables(source);
            var missing = SupportedSourceTables.Except(tables, StringComparer.Ordinal).OrderBy(x => x).ToArray();
            var unknown = tables.Except(SupportedSourceTables, StringComparer.Ordinal).OrderBy(x => x).ToArray();
            if (missing.Length > 0 || unknown.Length > 0)
            {
                throw new InvalidDataException(
                    "BookshelfNG database table set is not supported (missing: " + string.Join(", ", missing) +
                    "; unknown: " + string.Join(", ", unknown) + "). The source database was not changed.");
            }

            var version = ValidateSourceBackup(source);
            if (version != SupportedSourceMigration)
            {
                throw new InvalidDataException(
                    $"BookshelfNG schema version {version} is not supported; this converter supports version {SupportedSourceMigration}. Update BookshelfNG first. The source database was not changed.");
            }
        }

        internal static int ValidateSourceBackup(DbConnection source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var tables = GetTables(source);
            if (!tables.Contains("VersionInfo"))
            {
                throw new InvalidDataException("BookshelfNG backup has no migration history and cannot be upgraded by the bundled migration chain. The source database was not changed.");
            }

            var version = Convert.ToInt32(Scalar(source, "SELECT COALESCE(MAX(\"Version\"), 0) FROM \"VersionInfo\";"), CultureInfo.InvariantCulture);
            if (version < 1 || version > SupportedSourceMigration)
            {
                throw new InvalidDataException(
                    $"BookshelfNG schema version {version} is outside the bundled migration chain (1-{SupportedSourceMigration}). The source database was not changed.");
            }

            // Accept a historical alias only when the bundled migration chain will transform it.
            var historicalTables = version < 14
                ? SupportedSourceTables.Concat(HistoricalSourceTableAliases)
                : SupportedSourceTables;
            var unknown = tables.Except(historicalTables, StringComparer.Ordinal).OrderBy(x => x).ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidDataException(
                    "BookshelfNG backup table set cannot be upgraded by the bundled migration chain (unknown: " +
                    string.Join(", ", unknown) + "). The source database was not changed.");
            }

            return version;
        }

        private static void DeferForeignKeys(DbConnection target, DbTransaction transaction)
        {
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = target is NpgsqlConnection
                ? "SET CONSTRAINTS ALL DEFERRED;"
                : "PRAGMA defer_foreign_keys = ON;";
            command.ExecuteNonQuery();
        }

        private static void ClearTarget(DbConnection target, DbTransaction transaction)
        {
            foreach (var table in TargetTablesToClear)
            {
                if (!TableExists(target, table))
                {
                    continue;
                }

                using var command = target.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {Quote(table)};";
                command.ExecuteNonQuery();
            }
        }

        private static Dictionary<(int SourceId, int MediaType), int> ImportQualityProfiles(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "QualityProfiles");
            var targetColumns = GetColumns(target, "QualityProfiles");
            var map = new Dictionary<(int SourceId, int MediaType), int>();
            var nextId = Convert.ToInt32(Scalar(source, "SELECT COALESCE(MAX(\"Id\"), 0) + 1 FROM \"QualityProfiles\";"), CultureInfo.InvariantCulture);

            foreach (var row in ReadRows(source, "QualityProfiles"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "QualityProfiles");
                var sourceId = GetInt(row, "Id");
                var audiobookId = sourceId;
                var ebookId = nextId++;

                foreach (var (mediaType, targetId) in new[] { (1, audiobookId), (2, ebookId) })
                {
                    var values = Intersect(row, targetColumns);
                    values["Id"] = targetId;
                    values["ProfileType"] = mediaType;
                    InsertRow(target, transaction, "QualityProfiles", targetColumns, values);
                }

                map[(sourceId, 0)] = audiobookId;
                map[(sourceId, 1)] = ebookId;
                RecordBookshelfLegacyIdentity(target, transaction, "QualityProfile", sourceId, audiobookId, 0);
                RecordBookshelfLegacyIdentity(target, transaction, "QualityProfile", sourceId, ebookId, 1);
            }

            report.RecordTable("QualityProfiles", sourceCount, 0, sourceCount, 0, 0,
                "each generic BookshelfNG profile is cloned as Chaptarr audiobook and ebook profiles");
            return map;
        }

        private static Dictionary<int, int> ImportMetadataProfiles(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "MetadataProfiles");
            var targetColumns = GetColumns(target, "MetadataProfiles");
            var map = new Dictionary<int, int>();

            foreach (var row in ReadRows(source, "MetadataProfiles"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "MetadataProfiles");
                var values = Intersect(row, targetColumns);
                values["ProfileType"] = 0;
                SetIfPresent(values, "Ignored", "[]");
                SetIfPresent(values, "IgnoredGenres", "[]");
                InsertRow(target, transaction, "MetadataProfiles", targetColumns, values);
                map[GetInt(row, "Id")] = GetInt(row, "Id");
            }

            report.RecordTable("MetadataProfiles", sourceCount, 0, sourceCount, 0, 0,
                "BookshelfNG general metadata profiles remain general Chaptarr profiles");
            return map;
        }

        private static List<RootFolderMap> ImportRootFolders(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<(int SourceId, int MediaType), int> qualityProfiles,
            IReadOnlyDictionary<int, int> metadataProfiles,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "RootFolders");
            var targetColumns = GetColumns(target, "RootFolders");
            var result = new List<RootFolderMap>();

            foreach (var row in ReadRows(source, "RootFolders"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "RootFolders", RootFolderMappedSourceColumns);
                var values = Intersect(row, targetColumns);
                var qualityId = GetNullableInt(row, "DefaultQualityProfileId");
                var metadataId = GetNullableInt(row, "DefaultMetadataProfileId");
                var monitorExisting = GetNullableInt(row, "DefaultMonitorOption");
                var monitorNew = GetNullableInt(row, "DefaultNewItemMonitorOption");
                var tags = GetString(row, "DefaultTags");

                values["FolderType"] = 0;
                values["AudiobookSettings"] = BuildRootSettings(qualityId, metadataId, monitorExisting, monitorNew, tags, 0, qualityProfiles, metadataProfiles);
                values["EbookSettings"] = BuildRootSettings(qualityId, metadataId, monitorExisting, monitorNew, tags, 1, qualityProfiles, metadataProfiles);
                SetIfPresent(values, "PlaceEbooksWithAudiobooks", false);
                SetIfPresent(values, "DefaultSyncMonitoredAcrossFormats", true);

                InsertRow(target, transaction, "RootFolders", targetColumns, values);
                result.Add(new RootFolderMap(GetString(row, "Path")));
            }

            report.RecordTable("RootFolders", sourceCount, 0, sourceCount, 0, 0,
                "root paths and Calibre settings are preserved; legacy defaults are moved into per-media Chaptarr settings");
            return result;
        }

        private static string BuildRootSettings(
            int? sourceQualityId,
            int? sourceMetadataId,
            int? sourceMonitorExisting,
            int? sourceMonitorNew,
            string tags,
            int mediaType,
            IReadOnlyDictionary<(int SourceId, int MediaType), int> qualityProfiles,
            IReadOnlyDictionary<int, int> metadataProfiles)
        {
            var settings = new JObject();
            if (sourceQualityId.GetValueOrDefault() > 0)
            {
                settings["QualityProfileId"] = MapQualityProfile(qualityProfiles, sourceQualityId.Value, mediaType, "root-folder quality");
            }

            if (sourceMetadataId.GetValueOrDefault() > 0)
            {
                settings["MetadataProfileId"] = MapMetadataProfile(metadataProfiles, sourceMetadataId.Value, "root-folder metadata");
            }

            if (sourceMonitorExisting.HasValue)
            {
                settings["MonitorExistingMode"] = RemapMonitorType(sourceMonitorExisting.Value);
            }

            if (sourceMonitorNew.HasValue)
            {
                settings["MonitorNewItems"] = sourceMonitorNew.Value;
            }

            if (!string.IsNullOrWhiteSpace(tags))
            {
                settings["Tags"] = JToken.Parse(tags);
            }

            return settings.HasValues ? settings.ToString(Formatting.None) : null;
        }

        private static Dictionary<int, AuthorMap> ImportAuthors(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<(int SourceId, int MediaType), int> qualityProfiles,
            IReadOnlyDictionary<int, int> metadataProfiles,
            IReadOnlyList<RootFolderMap> rootFolders,
            BookshelfDatabaseImportReport report)
        {
            var metadataRows = ReadRows(source, "AuthorMetadata").ToDictionary(x => GetInt(x, "Id"));
            var referencedMetadataIds = new HashSet<int>();
            var targetColumns = GetColumns(target, "Authors");
            var authors = new Dictionary<int, AuthorMap>();
            var metadataToAuthor = new Dictionary<int, int>();
            var sourceCount = CountRows(source, "Authors");

            foreach (var row in ReadRows(source, "Authors"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "Authors", AuthorMappedSourceColumns);
                var metadataId = GetInt(row, "AuthorMetadataId");
                if (!metadataRows.TryGetValue(metadataId, out var metadata))
                {
                    throw new InvalidDataException($"BookshelfNG author {GetInt(row, "Id")} references missing author metadata {metadataId}.");
                }

                if (!metadataToAuthor.TryAdd(metadataId, GetInt(row, "Id")))
                {
                    throw new InvalidDataException($"BookshelfNG author metadata {metadataId} is shared by multiple managed authors; Chaptarr cannot preserve that relationship unambiguously.");
                }

                referencedMetadataIds.Add(metadataId);
                EnsureAllSourceValuesMapped(metadata, targetColumns, "AuthorMetadata", AuthorMetadataMappedSourceColumns);
                var sourceId = GetInt(row, "Id");
                var canonicalId = NormalizeSourceId(GetString(metadata, "ForeignAuthorId"), "author");
                var values = Intersect(row, targetColumns);
                AddMetadataColumns(values, metadata);
                values["Id"] = sourceId;
                values["Name"] = GetString(metadata, "Name") ?? GetString(row, "CleanName") ?? "Unknown author";
                values["SortName"] = GetString(metadata, "SortName") ?? GetString(metadata, "Name") ?? "Unknown author";
                values["CleanName"] = GetString(row, "CleanName") ?? GetString(metadata, "Name") ?? "Unknown author";
                values["Path"] = GetString(row, "Path") ?? throw new InvalidDataException($"BookshelfNG author {sourceId} has no managed path.");
                values["Added"] = GetValue(row, "Added") ?? DateTime.UnixEpoch;
                values["Monitored"] = GetValue(row, "Monitored") ?? false;
                values["AudiobookMonitored"] = GetValue(row, "Monitored") ?? false;
                values["EbookMonitored"] = GetValue(row, "Monitored") ?? false;
                values["MonitorNewItems"] = GetValue(row, "MonitorNewItems") ?? 0;
                values["AudiobookMonitorNewItems"] = GetValue(row, "MonitorNewItems") ?? 0;
                values["EbookMonitorNewItems"] = GetValue(row, "MonitorNewItems") ?? 0;
                SetIfPresent(values, "AudiobookTags", GetValue(row, "Tags") ?? "[]");
                SetIfPresent(values, "EbookTags", GetValue(row, "Tags") ?? "[]");

                var qualityId = GetNullableInt(row, "QualityProfileId");
                var audioQualityId = GetNullableInt(row, "AudiobookQualityProfileId") ?? qualityId;
                var ebookQualityId = GetNullableInt(row, "EbookQualityProfileId") ?? qualityId;
                SetIfPresent(values, "BookshelfNGQualityProfileId", qualityId);
                SetIfPresent(values, "BookshelfNGAudiobookQualityProfileId", GetNullableInt(row, "AudiobookQualityProfileId"));
                SetIfPresent(values, "BookshelfNGEbookQualityProfileId", GetNullableInt(row, "EbookQualityProfileId"));
                values["QualityProfileId"] = audioQualityId.HasValue ? MapQualityProfile(qualityProfiles, audioQualityId.Value, 0, "author quality") : 1;
                values["AudiobookQualityProfileId"] = audioQualityId.HasValue ? MapQualityProfile(qualityProfiles, audioQualityId.Value, 0, "author audiobook quality") : null;
                values["EbookQualityProfileId"] = ebookQualityId.HasValue ? MapQualityProfile(qualityProfiles, ebookQualityId.Value, 1, "author ebook quality") : null;

                var metadataIdLegacy = GetNullableInt(row, "MetadataProfileId");
                var audioMetadataId = GetNullableInt(row, "AudiobookMetadataProfileId") ?? metadataIdLegacy;
                var ebookMetadataId = GetNullableInt(row, "EbookMetadataProfileId") ?? metadataIdLegacy;
                values["MetadataProfileId"] = metadataIdLegacy.HasValue ? MapMetadataProfile(metadataProfiles, metadataIdLegacy.Value, "author metadata") : null;
                values["AudiobookMetadataProfileId"] = audioMetadataId.HasValue ? MapMetadataProfile(metadataProfiles, audioMetadataId.Value, "author audiobook metadata") : null;
                values["EbookMetadataProfileId"] = ebookMetadataId.HasValue ? MapMetadataProfile(metadataProfiles, ebookMetadataId.Value, "author ebook metadata") : null;

                var audioPath = GetString(row, "AudiobookPath");
                var ebookPath = GetString(row, "EbookPath");
                values["AudiobookPath"] = audioPath;
                values["EbookPath"] = ebookPath;
                values["AudiobookRootFolderPath"] = FindRootFolder(audioPath ?? GetString(row, "Path"), rootFolders);
                values["EbookRootFolderPath"] = FindRootFolder(ebookPath ?? GetString(row, "Path"), rootFolders);
                SetProviderColumns(values, "Author", canonicalId);
                SetIfPresent(values, "LastUpdated", GetValue(row, "LastInfoSync"));
                SetIfPresent(values, "ProviderUrls", "{}");

                InsertRow(target, transaction, "Authors", targetColumns, values);
                RecordBookshelfLegacyIdentity(target, transaction, "Author", sourceId, sourceId, null);
                RecordBookshelfLegacyIdentity(target, transaction, "AuthorMetadata", metadataId, sourceId, null);
                var providerIds = canonicalId == null ? new List<string>() : new List<string> { canonicalId };
                authors[sourceId] = new AuthorMap(sourceId, metadataId, providerIds);
            }

            var metadataCount = CountRows(source, "AuthorMetadata");
            var metadataTransformed = referencedMetadataIds.Count;
            report.RecordTable("Authors", sourceCount, 0, sourceCount, 0, 0,
                "managed author rows are merged with their AuthorMetadata row while preserving Authors.Id; " + NumericProviderDisposition());
            report.RecordTable("AuthorMetadata", metadataCount, 0, metadataTransformed, 0, metadataCount - metadataTransformed,
                "managed metadata is merged into Authors; unreferenced metadata-only cache rows are deliberately reset");
            return authors;
        }

        private static void AddMetadataColumns(IDictionary<string, object> values, IReadOnlyDictionary<string, object> metadata)
        {
            foreach (var field in new[]
            {
                "TitleSlug", "Name", "SortName", "NameLastFirst", "SortNameLastFirst", "Aliases", "Overview", "Disambiguation",
                "Gender", "Hometown", "Born", "Died", "Status", "Images", "Links", "Genres", "Ratings"
            })
            {
                if (metadata.TryGetValue(field, out var value)) values[field] = value;
            }

            SetIfPresent(values, "Aliases", "[]");
            SetIfPresent(values, "Images", "[]");
            SetIfPresent(values, "Links", "[]");
            SetIfPresent(values, "Genres", "[]");
        }

        private static Dictionary<int, BookMap> ImportBooks(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, AuthorMap> authors,
            IReadOnlyDictionary<int, BookshelfBookMediaState> bookMediaTypes,
            IReadOnlyDictionary<int, BookshelfMediaChoice> mediaChoices,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "Books");
            var targetColumns = GetColumns(target, "Books");
            var result = new Dictionary<int, BookMap>();
            var nextId = Convert.ToInt64(Scalar(source, "SELECT COALESCE(MAX(\"Id\"), 0) + 1 FROM \"Books\";"), CultureInfo.InvariantCulture);

            foreach (var row in ReadRows(source, "Books"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "Books", BookMappedSourceColumns);
                var sourceId = GetInt(row, "Id");
                var metadataId = GetInt(row, "AuthorMetadataId");
                var author = authors.Values.SingleOrDefault(x => x.MetadataId == metadataId);
                if (author == null)
                {
                    throw new InvalidDataException($"BookshelfNG book {sourceId} references author metadata {metadataId} with no unambiguous managed author.");
                }

                bookMediaTypes.TryGetValue(sourceId, out var mediaState);
                mediaState ??= new BookshelfBookMediaState();
                var hasAudio = mediaState.MediaTypes.Contains(0);
                var hasEbook = mediaState.MediaTypes.Contains(1);
                if (!hasAudio && !hasEbook)
                {
                    if (!mediaChoices.TryGetValue(sourceId, out var noEditionChoice))
                    {
                        throw new InvalidDataException($"BookshelfNG book {sourceId} has no edition and no explicit media mapping. The source database was not changed.");
                    }

                    hasAudio = noEditionChoice is BookshelfMediaChoice.Audiobook or BookshelfMediaChoice.Both or BookshelfMediaChoice.BothUnmonitored;
                    hasEbook = noEditionChoice is BookshelfMediaChoice.Ebook or BookshelfMediaChoice.Both or BookshelfMediaChoice.BothUnmonitored;
                }

                var sourceMonitored = GetBoolean(row, "Monitored");
                var monitoredMediaTypes = new HashSet<int>();
                if (mediaState.MonitoredEditionIdsByMediaType.Count > 0)
                {
                    monitoredMediaTypes.UnionWith(mediaState.MonitoredEditionIdsByMediaType.Keys);
                }
                else if (sourceMonitored)
                {
                    if (mediaState.MediaTypes.Count == 0)
                    {
                        if (!mediaChoices.TryGetValue(sourceId, out var noEditionChoice))
                        {
                            throw new InvalidDataException($"BookshelfNG book {sourceId} has no edition and no explicit media mapping. The source database was not changed.");
                        }

                        if (noEditionChoice is BookshelfMediaChoice.Audiobook or BookshelfMediaChoice.Both)
                        {
                            monitoredMediaTypes.Add(0);
                        }

                        if (noEditionChoice is BookshelfMediaChoice.Ebook or BookshelfMediaChoice.Both)
                        {
                            monitoredMediaTypes.Add(1);
                        }
                    }
                    else if (mediaState.MediaTypes.Count == 1)
                    {
                        monitoredMediaTypes.Add(mediaState.MediaTypes.Single());
                    }
                    else if (mediaState.MediaTypes.Count > 1)
                    {
                        if (!mediaChoices.TryGetValue(sourceId, out var mixedFormatChoice))
                        {
                            throw new InvalidDataException($"BookshelfNG book {sourceId} is monitored but has audiobook and ebook editions without a selected edition. The source database was not changed.");
                        }

                        if (mixedFormatChoice is BookshelfMediaChoice.Audiobook or BookshelfMediaChoice.Both)
                        {
                            monitoredMediaTypes.Add(0);
                        }

                        if (mixedFormatChoice is BookshelfMediaChoice.Ebook or BookshelfMediaChoice.Both)
                        {
                            monitoredMediaTypes.Add(1);
                        }
                    }
                }

                var mapping = new Dictionary<int, int>();
                if (hasAudio) mapping[0] = sourceId;
                if (hasEbook)
                {
                    if (hasAudio)
                    {
                        if (nextId > int.MaxValue)
                        {
                            throw new InvalidDataException("BookshelfNG book IDs leave no room for a synthetic book row; conversion stopped before promotion.");
                        }

                        mapping[1] = checked((int)nextId++);
                    }
                    else
                    {
                        mapping[1] = sourceId;
                    }
                }

                var foreignBookId = GetString(row, "ForeignBookId");
                var canonicalBookId = NormalizeSourceId(foreignBookId, "book");
                foreach (var mediaType in mapping.Keys.OrderBy(x => x))
                {
                    var targetId = mapping[mediaType];
                    var values = Intersect(row, targetColumns);
                    values["Id"] = targetId;
                    values["AuthorId"] = author.Id;
                    values["Title"] = GetString(row, "Title") ?? "Unknown title";
                    values["CleanTitle"] = GetString(row, "CleanTitle") ?? GetString(row, "Title") ?? "Unknown title";
                    values["Added"] = GetValue(row, "Added") ?? DateTime.UnixEpoch;
                    values["MediaType"] = mediaType;
                    var mediaMonitored = sourceMonitored && monitoredMediaTypes.Contains(mediaType);
                    values["Monitored"] = mediaMonitored;
                    values["AudiobookMonitored"] = mediaType == 0 && mediaMonitored;
                    values["EbookMonitored"] = mediaType == 1 && mediaMonitored;
                    values["ForeignEditionId"] = mediaState.MonitoredEditionIdsByMediaType.TryGetValue(mediaType, out var monitoredEditionId)
                        ? NormalizeSourceId(monitoredEditionId, "edition")
                        : null;
                    values["BaseBookId"] = canonicalBookId;
                    values["AnyEditionOk"] = GetBoolean(row, "AnyEditionOk");
                    SetProviderColumns(values, "Book", canonicalBookId);
                    SetIfPresent(values, "Images", "[]");
                    SetIfPresent(values, "ProviderUrls", "{}");
                    InsertRow(target, transaction, "Books", targetColumns, values);
                    RecordBookshelfLegacyIdentity(target, transaction, "Book", sourceId, targetId, mediaType);
                }

                result[sourceId] = new BookMap(sourceId, mapping, canonicalBookId, author.Id);
            }

            report.RecordTable("Books", sourceCount, 0, sourceCount, 0, 0,
                $"BookshelfNG books are split by inferred source edition formats and recognized file types, then linked to the preserved managed author ID; " +
                $"{mediaChoices.Count(x => x.Value == BookshelfMediaChoice.Both)} ambiguous books retain both catalog records with monitoring preserved from the source and " +
                $"{mediaChoices.Count(x => x.Value == BookshelfMediaChoice.BothUnmonitored)} are kept in both catalogs without monitoring per the fallback or explicit media map; " +
                $"{NumericProviderDisposition()}");
            return result;
        }

        private static Dictionary<int, EditionMap> ImportEditions(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, BookMap> books,
            IReadOnlyDictionary<int, BookshelfEditionMediaState> inferredEditions,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "Editions");
            var targetColumns = GetColumns(target, "Editions");
            var result = new Dictionary<int, EditionMap>();
            var additionalMediaRows = 0;
            var nextSyntheticEditionId = Convert.ToInt64(
                Scalar(source, "SELECT COALESCE(MAX(\"Id\"), 0) + 1 FROM \"Editions\";"),
                CultureInfo.InvariantCulture);

            foreach (var row in ReadRows(source, "Editions"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "Editions", EditionMappedSourceColumns);
                var sourceBookId = GetInt(row, "BookId");
                if (!books.TryGetValue(sourceBookId, out var book))
                {
                    throw new InvalidDataException($"BookshelfNG edition {GetInt(row, "Id")} references missing book {sourceBookId}.");
                }

                var sourceEditionId = GetInt(row, "Id");
                if (!inferredEditions.TryGetValue(sourceEditionId, out var mediaState) || mediaState.MediaTypes.Count == 0)
                {
                    throw new InvalidDataException($"BookshelfNG edition {sourceEditionId} has no resolved media type.");
                }

                var primaryMediaType = mediaState.PreferredMediaType ?? mediaState.MediaTypes.Min();
                var foreignEditionId = NormalizeSourceId(GetString(row, "ForeignEditionId"), "edition");
                var narrator = GetString(row, "Narrator")?.Trim();
                var targetByMediaType = new Dictionary<int, EditionTargetMap>();

                foreach (var mediaType in mediaState.MediaTypes.OrderBy(x => x))
                {
                    if (!book.TargetIds.TryGetValue(mediaType, out var targetBookId))
                    {
                        throw new InvalidDataException($"BookshelfNG edition {sourceEditionId} has a media type with no target book row.");
                    }

                    var isPrimary = mediaType == primaryMediaType;
                    var targetEditionId = sourceEditionId;
                    if (!isPrimary)
                    {
                        if (nextSyntheticEditionId > int.MaxValue)
                        {
                            throw new InvalidDataException("BookshelfNG edition IDs leave no room for a synthetic edition row; conversion stopped before promotion.");
                        }

                        targetEditionId = checked((int)nextSyntheticEditionId++);
                        additionalMediaRows++;
                    }

                    var readingFormatId = mediaState.ReadingFormatIdsByMediaType.TryGetValue(mediaType, out var inferredReadingFormatId)
                        ? inferredReadingFormatId
                        : mediaType == 1 ? 3 : 2;
                    var targetForeignEditionId = isPrimary ? foreignEditionId : null;
                    var values = Intersect(row, targetColumns);
                    values["Id"] = targetEditionId;
                    values["BookId"] = targetBookId;
                    values["IsEbook"] = readingFormatId == 3;
                    values["MediaType"] = mediaType;
                    values["ForeignEditionId"] = targetForeignEditionId;
                    SetIfPresent(values, "ReadingFormatId", readingFormatId);
                    SetProviderColumns(values, "Edition", targetForeignEditionId);
                    SetIfPresent(values, "Narrator", mediaType == 0 ? narrator : null);
                    if (!isPrimary)
                    {
                        SetIfPresent(values, "Format", mediaType == 1 ? "Ebook" : "Audiobook");
                        SetIfPresent(values, "Isbn13", null);
                        SetIfPresent(values, "Asin", null);
                        SetIfPresent(values, "GoodreadsEditionId", null);
                        SetIfPresent(values, "HardcoverEditionId", null);
                        SetIfPresent(values, "OpenLibraryEditionId", null);
                        SetIfPresent(values, "AudibleASIN", null);
                        SetIfPresent(values, "GoogleBooksEditionId", null);
                        SetIfPresent(values, "LibraryThingEditionId", null);
                    }
                    SetIfPresent(values, "Images", PreserveJsonArrayOrEmpty(GetValue(row, "Images")));
                    SetIfPresent(values, "Links", isPrimary ? PreserveJsonArrayOrEmpty(GetValue(row, "Links")) : "[]");
                    SetIfPresent(values, "NarratorNames", JsonConvert.SerializeObject(
                        mediaType == 0 && !string.IsNullOrWhiteSpace(narrator) ? new[] { narrator } : Array.Empty<string>()));
                    SetIfPresent(values, "ProviderUrls", "{}");
                    InsertRow(target, transaction, "Editions", targetColumns, values);
                    RecordBookshelfLegacyIdentity(target, transaction, "Edition", sourceEditionId, targetEditionId, mediaType);
                    targetByMediaType.Add(mediaType, new EditionTargetMap(targetEditionId, targetBookId));
                }

                result[sourceEditionId] = new EditionMap(
                    sourceEditionId,
                    sourceBookId,
                    primaryMediaType,
                    foreignEditionId,
                    targetByMediaType);
            }

            report.RecordTable("Editions", sourceCount, 0, sourceCount, 0, 0,
                $"source edition rows retain their IDs and provider identity; {additionalMediaRows} additional media-specific rows are created when linked files span formats or ambiguous editions are preserved in both catalogs, and formats are stored in ReadingFormatId; " + NumericProviderDisposition());
            return result;
        }

        private static Dictionary<int, BookFileMap> ImportBookFiles(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, EditionMap> editions,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "BookFiles");
            var targetColumns = GetColumns(target, "BookFiles");
            var result = new Dictionary<int, BookFileMap>();
            var filesWithoutEditionId = 0;
            var filesWithMissingEdition = 0;
            var filesRoutedByExtension = 0;
            var unmappedFilesTypedByExtension = 0;

            foreach (var row in ReadRows(source, "BookFiles"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "BookFiles");
                var sourceFileId = GetInt(row, "Id");
                var sourceEditionId = GetInt(row, "EditionId");
                var path = GetString(row, "Path");
                var hasPathMediaType = BookshelfMediaTypeInference.TryGetMediaTypeFromPath(path, out var pathMediaType);
                if (sourceEditionId == 0 || !editions.TryGetValue(sourceEditionId, out var edition))
                {
                    if (sourceEditionId == 0)
                    {
                        filesWithoutEditionId++;
                    }
                    else
                    {
                        filesWithMissingEdition++;
                    }

                    var orphanModified = GetValue(row, "Modified") ?? GetValue(row, "DateAdded") ?? DateTime.UnixEpoch;
                    var orphanValues = Intersect(row, targetColumns);
                    orphanValues["Id"] = sourceFileId;
                    orphanValues["EditionId"] = 0;
                    orphanValues["Modified"] = orphanModified;
                    orphanValues["DateAdded"] = GetValue(row, "DateAdded") ?? orphanModified;
                    orphanValues["MediaInfo"] = GetValue(row, "MediaInfo") ?? "{}";
                    SetIfPresent(orphanValues, "MediaType", hasPathMediaType ? pathMediaType == 1 ? "ebook" : "audiobook" : string.Empty);
                    SetIfPresent(orphanValues, "UnmappedReason", sourceEditionId == 0
                        ? hasPathMediaType
                            ? "Source file had no linked edition; its media type was inferred from its recognized path extension, but its book and edition relationships remain unassigned."
                            : "Source file had no linked edition; its media type and book relationship were not inferred."
                        : hasPathMediaType
                            ? "Source file referenced a missing edition; its media type was inferred from its recognized path extension, but its book and edition relationships remain unassigned."
                            : "Source file referenced a missing edition; its media type and book relationship were not inferred.");
                    SetIfPresent(orphanValues, "Part", GetValue(row, "Part") ?? 0);
                    SetIfPresent(orphanValues, "IndexerFlags", GetValue(row, "IndexerFlags") ?? 0);
                    InsertRow(target, transaction, "BookFiles", targetColumns, orphanValues);
                    RecordBookshelfLegacyIdentity(target, transaction, "BookFile", sourceFileId, sourceFileId, hasPathMediaType ? pathMediaType : null);
                    if (hasPathMediaType)
                    {
                        unmappedFilesTypedByExtension++;
                    }

                    result[sourceFileId] = new BookFileMap(
                        sourceFileId,
                        0,
                        hasPathMediaType ? pathMediaType : -1,
                        0,
                        0,
                        path);
                    continue;
                }

                var modified = GetValue(row, "Modified") ?? GetValue(row, "DateAdded") ?? DateTime.UnixEpoch;
                var dateAdded = GetValue(row, "DateAdded") ?? modified;
                var mediaType = hasPathMediaType
                    ? pathMediaType
                    : edition.PrimaryMediaType;
                if (!edition.TargetsByMediaType.TryGetValue(mediaType, out var editionTarget))
                {
                    throw new InvalidDataException($"BookshelfNG book file {sourceFileId} has a media type with no matching target edition.");
                }

                if (hasPathMediaType)
                {
                    filesRoutedByExtension++;
                }

                var values = Intersect(row, targetColumns);
                values["Id"] = sourceFileId;
                values["EditionId"] = editionTarget.Id;
                values["Modified"] = modified;
                values["DateAdded"] = dateAdded;
                values["MediaInfo"] = GetValue(row, "MediaInfo") ?? "{}";
                values["MediaType"] = mediaType == 1 ? "ebook" : "audiobook";
                SetIfPresent(values, "Part", GetValue(row, "Part") ?? 0);
                SetIfPresent(values, "IndexerFlags", GetValue(row, "IndexerFlags") ?? 0);
                InsertRow(target, transaction, "BookFiles", targetColumns, values);
                RecordBookshelfLegacyIdentity(target, transaction, "BookFile", sourceFileId, sourceFileId, mediaType);
                result[sourceFileId] = new BookFileMap(
                    sourceFileId,
                    edition.SourceBookId,
                    mediaType,
                    editionTarget.TargetBookId,
                    editionTarget.Id,
                    path);
            }

            report.RecordTable("BookFiles", sourceCount, 0, sourceCount, 0, 0,
                $"book-file IDs and paths plus LastRemoteSearchTime are preserved when present; {filesRoutedByExtension} linked files and {unmappedFilesTypedByExtension} unlinked files are classified by recognized path extension while no book or edition relationship is inferred for unmapped rows; unmapped file counts by cause: EditionId=0 {filesWithoutEditionId}, missing edition {filesWithMissingEdition}");
            return result;
        }

        private static void ImportExtraFiles(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, BookMap> books,
            IReadOnlyDictionary<int, BookFileMap> files,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "ExtraFiles");
            var targetColumns = GetColumns(target, "ExtraFiles");
            foreach (var row in ReadRows(source, "ExtraFiles"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "ExtraFiles");
                var sourceBookId = GetInt(row, "BookId");
                var sourceFileId = GetInt(row, "BookFileId");
                if (!books.TryGetValue(sourceBookId, out var book))
                {
                    throw new InvalidDataException($"BookshelfNG extra file {GetInt(row, "Id")} references missing book {sourceBookId}.");
                }

                var targetBookId = book.TargetIds.Values.OrderBy(x => x).First();
                if (files.TryGetValue(sourceFileId, out var file))
                {
                    if (file.SourceBookId != 0 && file.SourceBookId != sourceBookId)
                    {
                        throw new InvalidDataException($"BookshelfNG extra file {GetInt(row, "Id")} points to book file {sourceFileId} from a different book; conversion stopped to preserve the relationship.");
                    }

                    if (file.TargetBookId > 0)
                    {
                        targetBookId = file.TargetBookId;
                    }
                    else if (file.MediaType >= 0 && book.TargetIds.TryGetValue(file.MediaType, out var mediaSpecificBookId))
                    {
                        targetBookId = mediaSpecificBookId;
                    }
                }

                var values = Intersect(row, targetColumns);
                values["BookId"] = targetBookId;
                // Keep an orphaned source BookFileId verbatim. Chaptarr must not attach it to an unrelated file.
                values["BookFileId"] = sourceFileId;
                InsertRow(target, transaction, "ExtraFiles", targetColumns, values);
            }

            report.RecordTable("ExtraFiles", sourceCount, 0, sourceCount, 0, 0,
                "extra-file IDs and file references are retained; orphaned BookFileId values remain orphaned and are reserved against future ID reuse");
        }

        private static void ImportMetadataFiles(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, BookMap> books,
            IReadOnlyDictionary<int, BookFileMap> files,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "MetadataFiles");
            var targetColumns = GetColumns(target, "MetadataFiles");
            var unsupportedConsumers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ReadRows(source, "MetadataFiles"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "MetadataFiles");
                var values = Intersect(row, targetColumns);
                var sourceConsumer = GetString(row, "Consumer") ?? "(missing consumer)";
                var mappedConsumer = MetadataFileConsumerAliases.TryGetValue(sourceConsumer, out var alias)
                    ? alias
                    : MetadataContracts.Keys.FirstOrDefault(consumer =>
                        consumer.Equals(sourceConsumer, StringComparison.OrdinalIgnoreCase));

                if (mappedConsumer == null)
                {
                    unsupportedConsumers.Add(sourceConsumer);
                }
                else
                {
                    values["Consumer"] = mappedConsumer;
                }

                var sourceBookId = GetNullableInt(row, "BookId");
                var sourceFileId = GetNullableInt(row, "BookFileId");
                if (sourceFileId.HasValue && files.TryGetValue(sourceFileId.Value, out var file))
                {
                    if (sourceBookId.HasValue && file.SourceBookId != 0 && sourceBookId.Value != file.SourceBookId)
                    {
                        throw new InvalidDataException($"BookshelfNG metadata file {GetInt(row, "Id")} has mismatched book and book-file references.");
                    }

                    if (file.SourceBookId == 0 && sourceBookId.HasValue)
                    {
                        if (!books.TryGetValue(sourceBookId.Value, out var linkedBook))
                        {
                            throw new InvalidDataException($"BookshelfNG metadata file {GetInt(row, "Id")} references missing book {sourceBookId.Value}.");
                        }

                        values["BookId"] = file.MediaType >= 0 && linkedBook.TargetIds.TryGetValue(file.MediaType, out var mediaSpecificBookId)
                            ? mediaSpecificBookId
                            : linkedBook.TargetIds.Values.OrderBy(x => x).First();
                    }
                    else if (file.TargetBookId > 0)
                    {
                        values["BookId"] = file.TargetBookId;
                    }

                    values["BookFileId"] = sourceFileId.Value;
                }
                else if (sourceBookId.HasValue)
                {
                    if (!books.TryGetValue(sourceBookId.Value, out var book))
                    {
                        throw new InvalidDataException($"BookshelfNG metadata file {GetInt(row, "Id")} references missing book {sourceBookId.Value}.");
                    }

                    values["BookId"] = book.TargetIds.Values.OrderBy(x => x).First();
                    // Keep a missing source file ID verbatim for audit and later recovery.
                    values["BookFileId"] = sourceFileId;
                }

                InsertRow(target, transaction, "MetadataFiles", targetColumns, values);
            }

            foreach (var consumer in unsupportedConsumers)
            {
                report.AddBlockingIssue($"MetadataFiles contains sidecars for metadata consumer {consumer}, which has no Chaptarr mapping");
            }

            report.RecordTable("MetadataFiles", sourceCount, 0, sourceCount, 0, 0,
                "sidecar records are retained and book/file references are remapped without inventing relationships; known provider IDs are canonicalized; unsupported consumers block apply: " +
                (unsupportedConsumers.Count == 0 ? "none" : string.Join(", ", unsupportedConsumers)));
        }

        private static void ReserveOrphanBookFileIds(DbConnection source, DbConnection target, DbTransaction transaction)
        {
            var maxOrphanValue = Scalar(source, @"
                SELECT MAX(""BookFileId"")
                  FROM (
                        SELECT ""ExtraFiles"".""BookFileId""
                          FROM ""ExtraFiles""
                         WHERE NOT EXISTS (SELECT 1 FROM ""BookFiles"" WHERE ""BookFiles"".""Id"" = ""ExtraFiles"".""BookFileId"")
                        UNION ALL
                        SELECT ""MetadataFiles"".""BookFileId""
                          FROM ""MetadataFiles""
                         WHERE ""MetadataFiles"".""BookFileId"" IS NOT NULL
                           AND NOT EXISTS (SELECT 1 FROM ""BookFiles"" WHERE ""BookFiles"".""Id"" = ""MetadataFiles"".""BookFileId"")
                  ) AS OrphanedBookFileIds;");
            var maxOrphanId = maxOrphanValue == null || maxOrphanValue == DBNull.Value
                ? 0
                : Convert.ToInt64(maxOrphanValue, CultureInfo.InvariantCulture);
            if (maxOrphanId >= int.MaxValue)
            {
                throw new InvalidDataException("BookshelfNG contains an orphaned book-file ID at the SQLite integer limit; conversion stopped before promotion to prevent future ID collisions.");
            }

            if (target is NpgsqlConnection)
            {
                using var sequence = target.CreateCommand();
                sequence.Transaction = transaction;
                sequence.CommandText = "SELECT pg_get_serial_sequence('\"BookFiles\"', 'Id');";
                var sequenceName = sequence.ExecuteScalar()?.ToString();
                if (!string.IsNullOrWhiteSpace(sequenceName))
                {
                    using var setSequence = target.CreateCommand();
                    setSequence.Transaction = transaction;
                    setSequence.CommandText = @"
                        SELECT setval(
                            @sequence::regclass,
                            GREATEST(@orphanId, COALESCE((SELECT MAX(""Id"") FROM ""BookFiles""), 1)),
                            GREATEST(@orphanId, COALESCE((SELECT MAX(""Id"") FROM ""BookFiles""), 0)) > 0);";
                    AddParameter(setSequence, "@sequence", sequenceName);
                    AddParameter(setSequence, "@orphanId", maxOrphanId);
                    setSequence.ExecuteScalar();
                }

                return;
            }

            if (maxOrphanId == 0) return;

            using var update = target.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE sqlite_sequence SET seq = MAX(seq, @sequence) WHERE name = 'BookFiles';";
            AddParameter(update, "@sequence", maxOrphanId);
            if (update.ExecuteNonQuery() == 0)
            {
                using var insert = target.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO sqlite_sequence (name, seq) VALUES ('BookFiles', @sequence);";
                AddParameter(insert, "@sequence", maxOrphanId);
                insert.ExecuteNonQuery();
            }
        }

        private static void ResetPostgresSequences(DbConnection target, DbTransaction transaction)
        {
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT sequenceNamespace.nspname,
                       sequenceClass.relname,
                       tableClass.relname,
                       tableColumn.attname
                  FROM pg_class sequenceClass
                  JOIN pg_namespace sequenceNamespace ON sequenceNamespace.oid = sequenceClass.relnamespace
                  JOIN pg_depend dependency ON dependency.objid = sequenceClass.oid
                  JOIN pg_class tableClass ON tableClass.oid = dependency.refobjid
                  JOIN pg_namespace tableNamespace ON tableNamespace.oid = tableClass.relnamespace
                  JOIN pg_attribute tableColumn
                    ON tableColumn.attrelid = tableClass.oid
                   AND tableColumn.attnum = dependency.refobjsubid
                 WHERE sequenceClass.relkind = 'S'
                   AND dependency.deptype IN ('a', 'i')
                   AND tableNamespace.nspname = current_schema()
                 ORDER BY sequenceClass.relname;";

            var sequences = new List<(string Schema, string Sequence, string Table, string Column)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                }
            }

            foreach (var sequence in sequences)
            {
                using var setSequence = target.CreateCommand();
                setSequence.Transaction = transaction;
                setSequence.CommandText = $@"
                    SELECT setval(
                        @sequence::regclass,
                        GREATEST(COALESCE((SELECT MAX({Quote(sequence.Column)}) FROM {Quote(sequence.Table)}), 1), 1),
                        COALESCE((SELECT MAX({Quote(sequence.Column)}) FROM {Quote(sequence.Table)}), 0) > 0);";
                AddParameter(setSequence, "@sequence", Quote(sequence.Schema) + "." + Quote(sequence.Sequence));
                setSequence.ExecuteScalar();
            }
        }

        private static Dictionary<int, SeriesMap> ImportSeries(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "Series");
            var targetColumns = GetColumns(target, "Series");
            var result = new Dictionary<int, SeriesMap>();

            foreach (var row in ReadRows(source, "Series"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "Series", SeriesMappedSourceColumns);
                var canonicalId = NormalizeSourceId(GetString(row, "ForeignSeriesId"), "series");
                var values = Intersect(row, targetColumns);
                values["Title"] = GetString(row, "Title") ?? "Unknown series";
                values["BaseSeriesId"] = canonicalId;
                SetProviderColumns(values, "Series", canonicalId);
                SetIfPresent(values, "Links", "{}");
                SetIfPresent(values, "ProviderUrls", "{}");
                InsertRow(target, transaction, "Series", targetColumns, values);
                result[GetInt(row, "Id")] = new SeriesMap(GetInt(row, "Id"), canonicalId);
            }

            report.RecordTable("Series", sourceCount, 0, sourceCount, 0, 0,
                "series IDs and descriptions are preserved; provider identity is routed into the matching Chaptarr provider field; " + NumericProviderDisposition());
            return result;
        }

        private static void ImportSeriesLinks(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, BookMap> books,
            IReadOnlyDictionary<int, SeriesMap> series,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "SeriesBookLink");
            var targetColumns = GetColumns(target, "SeriesBookLink");
            var nextId = Convert.ToInt32(Scalar(source, "SELECT COALESCE(MAX(\"Id\"), 0) + 1 FROM \"SeriesBookLink\";"), CultureInfo.InvariantCulture);
            long skippedMissingRows = 0;
            long missingBookReferenceRows = 0;
            long missingSeriesReferenceRows = 0;

            foreach (var row in ReadRows(source, "SeriesBookLink"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "SeriesBookLink", SeriesBookLinkMappedSourceColumns);
                var sourceBookId = GetInt(row, "BookId");
                var sourceSeriesId = GetInt(row, "SeriesId");
                if (!series.ContainsKey(sourceSeriesId))
                {
                    missingSeriesReferenceRows++;
                    skippedMissingRows++;
                    if (!books.ContainsKey(sourceBookId))
                    {
                        missingBookReferenceRows++;
                    }

                    continue;
                }

                if (!books.TryGetValue(sourceBookId, out var book))
                {
                    // BookshelfNG can leave a stale series link after deleting its
                    // book or series. The relationship has no target endpoint, so
                    // account for it as reset while retaining the source backup.
                    missingBookReferenceRows++;
                    skippedMissingRows++;
                    continue;
                }

                var targets = book.TargetIds.OrderBy(x => x.Key).ToArray();
                for (var i = 0; i < targets.Length; i++)
                {
                    var values = Intersect(row, targetColumns);
                    values["Id"] = i == 0 ? GetInt(row, "Id") : nextId++;
                    values["BookId"] = targets[i].Value;
                    values["SeriesId"] = sourceSeriesId;
                    if (!values.ContainsKey("SeriesPosition"))
                    {
                        values["SeriesPosition"] = ParsePosition(GetString(row, "Position"));
                    }

                    InsertRow(target, transaction, "SeriesBookLink", targetColumns, values);
                }
            }

            report.RecordTable("SeriesBookLink", sourceCount, 0, sourceCount - skippedMissingRows, 0, skippedMissingRows,
                $"valid links are rebuilt for each corresponding audiobook and ebook row; stale link rows reset: {skippedMissingRows} (missing Books references: {missingBookReferenceRows}; missing Series references: {missingSeriesReferenceRows}; a row missing both endpoints is counted once); the retained BookshelfNG backup preserves the original rows");
        }

        private static void RebuildAuthorSeries(DbConnection target, DbTransaction transaction)
        {
            if (!TableExists(target, "AuthorSeries")) return;
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO ""AuthorSeries"" (""SeriesId"", ""AuthorId"")
                SELECT DISTINCT links.""SeriesId"", books.""AuthorId""
                  FROM ""SeriesBookLink"" links
                  JOIN ""Books"" books ON books.""Id"" = links.""BookId"";";
            command.ExecuteNonQuery();
        }

        private static void ImportBlocklist(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, AuthorMap> authors,
            IReadOnlyDictionary<int, BookMap> books,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "Blocklist");
            var targetColumns = GetColumns(target, "Blocklist");
            foreach (var row in ReadRows(source, "Blocklist"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "Blocklist", BlocklistMappedSourceColumns);
                var sourceAuthorId = GetInt(row, "AuthorId");
                if (!authors.TryGetValue(sourceAuthorId, out var author))
                {
                    throw new InvalidDataException($"BookshelfNG blocklist row {GetInt(row, "Id")} references missing author {sourceAuthorId}.");
                }

                var authorIds = author.ProviderIds;
                if (authorIds.Count == 0)
                {
                    throw new InvalidDataException($"BookshelfNG blocklist row {GetInt(row, "Id")} cannot be mapped because its author has no supported provider ID.");
                }

                var bookIds = ParseIntegerArray(GetString(row, "BookIds"));
                var bookProviderIds = new List<string>();
                foreach (var sourceBookId in bookIds)
                {
                    if (!books.TryGetValue(sourceBookId, out var book) || string.IsNullOrWhiteSpace(book.ProviderId))
                    {
                        throw new InvalidDataException($"BookshelfNG blocklist row {GetInt(row, "Id")} cannot map book {sourceBookId} to a supported provider ID.");
                    }

                    bookProviderIds.Add(book.ProviderId);
                }

                var values = Intersect(row, targetColumns);
                values["AuthorProviderIds"] = JsonConvert.SerializeObject(authorIds.Distinct(StringComparer.Ordinal).ToArray());
                values["BookProviderIds"] = JsonConvert.SerializeObject(bookProviderIds.Distinct(StringComparer.Ordinal).ToArray());
                SetIfPresent(values, "IndexerFlags", GetValue(row, "IndexerFlags") ?? 0);
                InsertRow(target, transaction, "Blocklist", targetColumns, values);
            }

            report.RecordTable("Blocklist", sourceCount, 0, sourceCount, 0, 0,
                "numeric author/book links are converted to the provider-qualified IDs Chaptarr blocklists use");
        }

        private static void ImportDownloadHistory(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, AuthorMap> authors,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "DownloadHistory");
            var targetColumns = GetColumns(target, "DownloadHistory");
            long unlinkedAuthorRows = 0;
            long noSourceAuthorRows = 0;
            long unmappedPositiveAuthorRows = 0;
            foreach (var row in ReadRows(source, "DownloadHistory"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "DownloadHistory");
                var values = Intersect(row, targetColumns);
                values["BookId"] = 0; // BookshelfNG does not store a book ID on these events.
                var sourceAuthorId = GetNullableInt(row, "AuthorId");
                if (sourceAuthorId.GetValueOrDefault() > 0 && authors.TryGetValue(sourceAuthorId.Value, out var author))
                {
                    values["AuthorId"] = author.Id;
                }
                else
                {
                    values["AuthorId"] = 0;
                    unlinkedAuthorRows++;
                    if (sourceAuthorId.GetValueOrDefault() > 0)
                    {
                        // Retain history without letting an unmapped source ID
                        // alias a Chaptarr author added later.
                        unmappedPositiveAuthorRows++;
                    }
                    else
                    {
                        // DownloadHistory.AuthorId is required in Chaptarr's schema;
                        // zero represents events that never had a mapped author.
                        noSourceAuthorRows++;
                    }
                }

                SetIfPresent(values, "Data", GetValue(row, "Data"));
                InsertRow(target, transaction, "DownloadHistory", targetColumns, values);
            }

            report.RecordTable("DownloadHistory", sourceCount, 0, sourceCount, 0, 0,
                $"download events are preserved; source events without a book use the target's non-null BookId=0 sentinel; events without a mapped author use required AuthorId=0: {unlinkedAuthorRows} (source rows without a positive author ID: {noSourceAuthorRows}; positive source author IDs without a mapped author: {unmappedPositiveAuthorRows})");
        }

        private static void ImportHistory(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, AuthorMap> authors,
            IReadOnlyDictionary<int, BookMap> books,
            IReadOnlyDictionary<int, BookFileMap> files,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "History");
            var targetColumns = GetColumns(target, "History");
            var conflictingFileReferences = 0;
            var ambiguousFileReferences = 0;
            var unlinkedUnmappedAuthorRows = 0;
            var unlinkedUnmappedBookRows = 0;
            foreach (var row in ReadRows(source, "History"))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "History");
                var sourceAuthorId = GetNullableInt(row, "AuthorId");
                int? targetAuthorId = null;
                if (sourceAuthorId.GetValueOrDefault() > 0 && authors.TryGetValue(sourceAuthorId.Value, out var author))
                {
                    targetAuthorId = author.Id;
                }
                else if (sourceAuthorId.GetValueOrDefault() > 0)
                {
                    // Keep the historical event, but do not retain a dangling
                    // numeric ID that could later identify an unrelated author.
                    unlinkedUnmappedAuthorRows++;
                }

                var sourceBookId = GetNullableInt(row, "BookId");
                // History.BookId is required in the native schema. Zero is the
                // no-book sentinel and cannot alias an imported or new book ID.
                var targetBookId = 0;
                var historyFile = FindHistoryFile(row, files, out var ambiguousFileMatch);
                int? historyMediaType = historyFile is { MediaType: >= 0 }
                    ? historyFile.MediaType
                    : FindHistoryMediaType(row);
                if (ambiguousFileMatch)
                {
                    ambiguousFileReferences++;
                }

                if (sourceBookId.GetValueOrDefault() != 0)
                {
                    if (!books.TryGetValue(sourceBookId.Value, out var book))
                    {
                        // BookshelfNG retains history after a work has been
                        // deleted. Preserve the event without linking it to a
                        // future Chaptarr work that reuses the source ID.
                        unlinkedUnmappedBookRows++;
                        historyFile = null;
                        historyMediaType = null;
                    }
                    else
                    {
                        // Bookshelf history is book-scoped, so retain its source ID as the
                        // default target identity. When the event names a specific file,
                        // follow that file to the correct media-specific target book.
                        targetBookId = book.TargetIds.TryGetValue(0, out var audiobookId)
                            ? audiobookId
                            : book.TargetIds.Values.OrderBy(x => x).First();

                        if (historyFile != null && historyFile.SourceBookId != 0 && historyFile.SourceBookId != sourceBookId.Value)
                        {
                            // The source event's book identity and file identity disagree.
                            // Keep the event attached to its source book and retain its
                            // original payload, but do not invent a file or edition link.
                            conflictingFileReferences++;
                            historyFile = null;
                            historyMediaType = null;
                        }

                        if (historyFile?.TargetBookId > 0)
                        {
                            targetBookId = historyFile.TargetBookId;
                        }
                        else if (historyMediaType.HasValue && book.TargetIds.TryGetValue(historyMediaType.Value, out var formatSpecificBookId))
                        {
                            targetBookId = formatSpecificBookId;
                        }
                    }
                }
                else if (historyFile?.TargetBookId > 0)
                {
                    // Legacy history may omit BookId while still naming a source BookFile.
                    // Follow that file so the event remains visible in its format-scoped history.
                    targetBookId = historyFile.TargetBookId;
                }

                var values = Intersect(row, targetColumns);
                values["AuthorId"] = targetAuthorId;
                values["BookId"] = targetBookId;
                SetIfPresent(values, "EditionId", historyFile?.TargetEditionId > 0 ? historyFile.TargetEditionId : null);
                InsertRow(target, transaction, "History", targetColumns, values);
            }

            report.RecordTable("History", sourceCount, 0, sourceCount, 0, 0,
                $"history rows and IDs are retained; events with an unmapped positive author ID left unlinked: {unlinkedUnmappedAuthorRows}; events with an unmapped positive book ID left unlinked: {unlinkedUnmappedBookRows}; cross-book file references: {conflictingFileReferences}; ambiguous file matches: {ambiguousFileReferences}; inconsistent references remain unlinked, while consistent file-specific events follow their source file to the correct media row and edition");
        }

        private static BookFileMap FindHistoryFile(
            IReadOnlyDictionary<string, object> row,
            IReadOnlyDictionary<int, BookFileMap> files,
            out bool ambiguousFileMatch)
        {
            ambiguousFileMatch = false;
            var dataText = GetString(row, "Data");
            JObject data = null;
            if (!string.IsNullOrWhiteSpace(dataText))
            {
                try
                {
                    data = JObject.Parse(dataText);
                }
                catch (JsonException)
                {
                    // Preserve an opaque legacy payload; fall back to the book identity.
                }
            }

            var fileIdText = data?.GetValue("FileId", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            if (int.TryParse(fileIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId) &&
                files.TryGetValue(fileId, out var fileById))
            {
                return fileById;
            }

            var sourceTitle = GetString(row, "SourceTitle");
            var candidatePaths = new[]
            {
                sourceTitle,
                data?.GetValue("Path", StringComparison.OrdinalIgnoreCase)?.Value<string>(),
                data?.GetValue("ImportedPath", StringComparison.OrdinalIgnoreCase)?.Value<string>(),
                data?.GetValue("SourcePath", StringComparison.OrdinalIgnoreCase)?.Value<string>()
            }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matches = files.Values.Where(x => candidatePaths.Contains(NormalizePath(x.Path))).ToArray();
            if (matches.Length > 1)
            {
                ambiguousFileMatch = true;
                return null;
            }

            return matches.SingleOrDefault();
        }

        private static int? FindHistoryMediaType(IReadOnlyDictionary<string, object> row)
        {
            var candidatePaths = new List<string> { GetString(row, "SourceTitle") };
            var dataText = GetString(row, "Data");
            if (!string.IsNullOrWhiteSpace(dataText))
            {
                try
                {
                    var data = JObject.Parse(dataText);
                    candidatePaths.AddRange(new[]
                    {
                        data.GetValue("Path", StringComparison.OrdinalIgnoreCase)?.Value<string>(),
                        data.GetValue("ImportedPath", StringComparison.OrdinalIgnoreCase)?.Value<string>(),
                        data.GetValue("SourcePath", StringComparison.OrdinalIgnoreCase)?.Value<string>()
                    });
                }
                catch (JsonException)
                {
                    // Preserve an opaque legacy payload and use the source title if it identifies a format.
                }
            }

            foreach (var path in candidatePaths.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (BookshelfMediaTypeInference.TryGetMediaTypeFromPath(path, out var mediaType))
                {
                    return mediaType;
                }
            }

            return null;
        }

        private static void ImportLists(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<(int SourceId, int MediaType), int> qualityProfiles,
            IReadOnlyDictionary<int, int> metadataProfiles,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "ImportLists");
            var targetColumns = GetColumns(target, "ImportLists");
            var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ReadRows(source, "ImportLists"))
            {
                EnsureAllSourceValuesMapped(
                    row,
                    targetColumns,
                    "ImportLists",
                    ImportListMappedSourceColumns);
                var values = Intersect(row, targetColumns);
                var sourceProfileId = GetNullableInt(row, "ProfileId");
                var sourceMetadataProfileId = GetNullableInt(row, "MetadataProfileId");
                values["QualityProfileId"] = sourceProfileId.HasValue
                    ? MapQualityProfile(qualityProfiles, sourceProfileId.Value, 0, "import-list quality")
                    : 1;
                values["MetadataProfileId"] = sourceMetadataProfileId.HasValue
                    ? MapMetadataProfile(metadataProfiles, sourceMetadataProfileId.Value, "import-list metadata")
                    : 1;
                values["ShouldMonitor"] = RemapMonitorType(GetInt(row, "ShouldMonitor"));
                SetIfPresent(values, "EnableAutomaticAdd", GetBoolean(row, "EnableAutomaticAdd"));
                SetIfPresent(values, "ConfigContract", GetString(row, "ConfigContract") ?? "");
                CheckProviderSettingsCompatibility(
                    row,
                    values,
                    ImportListContracts,
                    unsupported,
                    ImportListImplementationAliases,
                    ImportListContractAliases);
                SetIfPresent(values, "MonitorNewItems", GetValue(row, "MonitorNewItems") ?? 0);
                InsertRow(target, transaction, "ImportLists", targetColumns, values);
            }

            report.RecordTable("ImportLists", sourceCount, 0, sourceCount, 0, 0,
                AddUnsupportedProviderDisposition(
                    "list settings, credentials, monitoring, profile IDs, and root paths are retained with the target quality-profile mapping",
                    "ImportLists",
                    unsupported,
                    report));
        }

        private static void ImportIndexers(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report)
        {
            var rows = ReadRows(source, "Indexers").ToList();
            var targetColumns = GetColumns(target, "Indexers");
            var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                EnsureAllSourceValuesMapped(row, targetColumns, "Indexers");
                var values = Intersect(row, targetColumns);
                CheckProviderSettingsCompatibility(row, values, CompatibleIndexerContracts, unsupported);

                // These are implementation capabilities, not user preferences. The runtime
                // provider factory recalculates them from the installed Chaptarr provider.
                SetIfPresent(values, "SupportsRss", false);
                SetIfPresent(values, "SupportsSearch", false);
                InsertRow(target, transaction, "Indexers", targetColumns, values);
            }

            var mapping = "matching BookshelfNG indexer implementations, contracts, and settings fields are retained; provider capability flags are recalculated by Chaptarr";
            if (unsupported.Count > 0)
            {
                mapping += "; unsupported implementation/configuration/settings are disabled in the staged copy and block apply: " +
                           string.Join(", ", unsupported);
                foreach (var provider in unsupported)
                {
                    report.AddBlockingIssue("Indexer settings need an explicit Chaptarr mapping for " + provider);
                }
            }

            report.RecordTable("Indexers", rows.Count, 0, rows.Count, 0, 0, mapping);
        }

        private static void ImportProviderTable(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report,
            string table,
            IReadOnlyDictionary<string, string> compatibleContracts,
            string providerKind,
            IReadOnlyDictionary<string, string> implementationAliases = null)
        {
            var rows = ReadRows(source, table).ToList();
            var targetColumns = GetColumns(target, table);
            var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                EnsureAllSourceValuesMapped(row, targetColumns, table);

                var values = Intersect(row, targetColumns);
                CheckProviderSettingsCompatibility(row, values, compatibleContracts, unsupported, implementationAliases);
                ApplyBookshelfProviderDefaults(table, row, values);
                InsertRow(target, transaction, table, targetColumns, values);
            }

            report.RecordTable(table, rows.Count, 0, rows.Count, 0, 0,
                AddUnsupportedProviderDisposition(
                    $"{providerKind} settings are retained when their implementation and configuration contract match a Chaptarr provider",
                    table,
                    unsupported,
                    report));
        }

        private static void ApplyBookshelfProviderDefaults(
            string table,
            IReadOnlyDictionary<string, object> sourceRow,
            IDictionary<string, object> targetRow)
        {
            if (!string.Equals(table, "DownloadClients", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(sourceRow, "Implementation"), "QBittorrent", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(sourceRow, "ConfigContract"), nameof(QBittorrentSettings), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var settingsJson = GetString(sourceRow, "Settings");
            JObject settings;
            try
            {
                settings = JObject.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
            }
            catch (JsonException)
            {
                return;
            }

            // BookshelfNG's QBittorrentSettings constructor defaults its legacy category to "readarr".
            // The ChaptarrNG-native default is "chaptarr", so persist the source default when the
            // legacy database omitted the property instead of changing category routing on migration.
            if (!settings.Properties().Any(property =>
                    property.Name.Equals(nameof(QBittorrentSettings.MusicCategory), StringComparison.OrdinalIgnoreCase)))
            {
                settings["musicCategory"] = "readarr";
                SetIfPresent(targetRow, "Settings", settings.ToString(Formatting.None));
            }
        }

        private static void CheckProviderSettingsCompatibility(
            IReadOnlyDictionary<string, object> sourceRow,
            IDictionary<string, object> targetRow,
            IReadOnlyDictionary<string, string> compatibleContracts,
            ISet<string> unsupported,
            IReadOnlyDictionary<string, string> implementationAliases = null,
            IReadOnlyDictionary<string, string> contractAliases = null)
        {
            var sourceImplementation = GetString(sourceRow, "Implementation") ?? "(missing implementation)";
            var implementation = implementationAliases != null && implementationAliases.TryGetValue(sourceImplementation, out var alias)
                ? alias
                : sourceImplementation;
            if (!implementation.Equals(sourceImplementation, StringComparison.OrdinalIgnoreCase))
            {
                SetIfPresent(targetRow, "Implementation", implementation);
            }

            var sourceConfigContract = GetString(sourceRow, "ConfigContract") ?? "";
            var configContract = contractAliases != null && contractAliases.TryGetValue(sourceConfigContract, out var contractAlias)
                ? contractAlias
                : sourceConfigContract;
            if (!configContract.Equals(sourceConfigContract, StringComparison.OrdinalIgnoreCase))
            {
                SetIfPresent(targetRow, "ConfigContract", configContract);
            }

            var hasCompatibleImplementation = compatibleContracts.TryGetValue(implementation, out var expectedContract) &&
                string.Equals(configContract, expectedContract, StringComparison.OrdinalIgnoreCase);
            var settingsIssues = hasCompatibleImplementation
                ? FindUnsupportedSettingsFields(GetString(sourceRow, "Settings"), expectedContract, implementation)
                : Array.Empty<string>();
            var missingOAuthSettings = hasCompatibleImplementation
                ? FindMissingGoodreadsOAuthSettings(GetString(sourceRow, "Settings"), implementation, configContract)
                : Array.Empty<string>();

            if (hasCompatibleImplementation && settingsIssues.Length == 0 && missingOAuthSettings.Length == 0)
            {
                return;
            }

            SetIfPresent(targetRow, "ConfigContract", "");
            SetIfPresent(targetRow, "Enable", false);
            var settingsDisposition = settingsIssues.Length == 0
                ? ""
                : " (unsupported or non-equivalent settings: " + string.Join(", ", settingsIssues) + ")";
            if (missingOAuthSettings.Length > 0)
            {
                settingsDisposition += " (missing Goodreads OAuth settings: " + string.Join(", ", missingOAuthSettings) + ")";
            }

            unsupported.Add($"row {GetInt(sourceRow, "Id")}: {sourceImplementation}/{(sourceConfigContract.Length == 0 ? "(missing contract)" : sourceConfigContract)}{settingsDisposition}");
        }

        private static void EnsureAllSourceValuesMapped(
            IReadOnlyDictionary<string, object> sourceRow,
            IReadOnlyDictionary<string, ColumnInfo> targetColumns,
            string table,
            ISet<string> explicitlyMappedSourceColumns = null)
        {
            var unmapped = sourceRow.Where(x => x.Value != null &&
                    !targetColumns.ContainsKey(x.Key) &&
                    explicitlyMappedSourceColumns?.Contains(x.Key) != true)
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (unmapped.Length > 0)
            {
                throw new InvalidDataException(
                    $"BookshelfNG {table} row {GetInt(sourceRow, "Id")} has non-empty fields with no Chaptarr mapping: " +
                    string.Join(", ", unmapped.Select(x => table + "." + x)) + ". Conversion stopped before promotion.");
            }
        }

        private static string[] FindMissingGoodreadsOAuthSettings(string settingsJson, string implementation, string configContract)
        {
            var isGoodreadsBookshelfImportList =
                implementation.Equals("GoodreadsBookshelf", StringComparison.OrdinalIgnoreCase) &&
                configContract.Equals("GoodreadsBookshelfImportListSettings", StringComparison.OrdinalIgnoreCase);
            var requiresOAuth =
                implementation.Equals("GoodreadsOwnedBooks", StringComparison.OrdinalIgnoreCase) ||
                ((implementation.Equals("GoodreadsBookshelf", StringComparison.OrdinalIgnoreCase) ||
                  implementation.Equals("GoodreadsOwnedBooks", StringComparison.OrdinalIgnoreCase)) &&
                 configContract.EndsWith("NotificationSettings", StringComparison.OrdinalIgnoreCase));

            if (!isGoodreadsBookshelfImportList && !requiresOAuth)
            {
                return Array.Empty<string>();
            }

            JObject settings;
            try
            {
                settings = JObject.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
            }
            catch (JsonException)
            {
                // The invalid JSON issue is already reported by the settings-contract check.
                return Array.Empty<string>();
            }

            var accessToken = settings.GetValue("accessToken", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            var accessTokenSecret = settings.GetValue("accessTokenSecret", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            var hasAccessToken = !string.IsNullOrWhiteSpace(accessToken);
            var hasAccessTokenSecret = !string.IsNullOrWhiteSpace(accessTokenSecret);

            if (!requiresOAuth && !hasAccessToken && !hasAccessTokenSecret)
            {
                var userId = settings.GetValue("userId", StringComparison.OrdinalIgnoreCase)?.Value<string>();
                return GoodreadsUserIdParser.TryParse(userId, out _) ? Array.Empty<string>() : new[] { "userId or OAuth credentials" };
            }

            var missing = new List<string>();
            if (!hasAccessToken)
            {
                missing.Add("accessToken");
            }

            if (!hasAccessTokenSecret)
            {
                missing.Add("accessTokenSecret");
            }

            return missing.ToArray();
        }

        private static string[] FindUnsupportedSettingsFields(string settingsJson, string configContract, string implementation)
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return Array.Empty<string>();
            }

            var settingsTypes = typeof(BookshelfDatabaseImporter).Assembly.GetTypes()
                .Where(type => type.Name.Equals(configContract, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (settingsTypes.Length != 1)
            {
                return new[] { "(unresolved settings contract)" };
            }

            JObject settings;
            try
            {
                settings = JObject.Parse(settingsJson);
            }
            catch (JsonException)
            {
                return new[] { "(invalid settings JSON)" };
            }

            var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            FindUnsupportedSettingsFields(settings, settingsTypes[0], implementation, "", unsupported);
            return unsupported.ToArray();
        }

        private static void FindUnsupportedSettingsFields(
            JObject settings,
            Type settingsType,
            string implementation,
            string path,
            ISet<string> unsupported)
        {
            var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            var acceptsExtensionData = false;
            foreach (var property in settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null ||
                    property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() != null)
                {
                    continue;
                }

                if (property.GetCustomAttribute<JsonExtensionDataAttribute>() != null ||
                    property.GetCustomAttribute<System.Text.Json.Serialization.JsonExtensionDataAttribute>() != null)
                {
                    acceptsExtensionData = true;
                    continue;
                }

                properties[property.Name] = property;
                var jsonName = property.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ??
                    property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
                if (!string.IsNullOrWhiteSpace(jsonName))
                {
                    properties[jsonName] = property;
                }
            }

            foreach (var setting in settings.Properties())
            {
                var settingPath = string.IsNullOrWhiteSpace(path) ? setting.Name : path + "." + setting.Name;
                if (string.IsNullOrWhiteSpace(path) &&
                    implementation.Equals("GoodreadsBookshelf", StringComparison.OrdinalIgnoreCase) &&
                    GoodreadsShelfRuntimeSettings.Contains(setting.Name))
                {
                    // BookshelfNG serializes these getter-only OAuth endpoints and the sign-in
                    // command as part of its legacy settings shape. They are fixed runtime
                    // metadata, not user configuration.
                    continue;
                }

                if (!properties.TryGetValue(setting.Name, out var property))
                {
                    if (!acceptsExtensionData && IsMeaningfulSettingValue(setting.Value))
                    {
                        unsupported.Add(settingPath);
                    }

                    continue;
                }

                var nestedType = GetNestedSettingsType(property.PropertyType);
                if (nestedType == null)
                {
                    continue;
                }

                if (setting.Value is JObject nestedSettings)
                {
                    FindUnsupportedSettingsFields(nestedSettings, nestedType, implementation, settingPath, unsupported);
                }
                else if (setting.Value is JArray nestedArray)
                {
                    for (var index = 0; index < nestedArray.Count; index++)
                    {
                        if (nestedArray[index] is JObject nestedItem)
                        {
                            FindUnsupportedSettingsFields(nestedItem, nestedType, implementation, settingPath + "[" + index + "]", unsupported);
                        }
                    }
                }
            }
        }

        private static bool IsMeaningfulSettingValue(JToken value)
        {
            return value.Type != JTokenType.Null &&
                !(value.Type == JTokenType.String && string.IsNullOrWhiteSpace(value.Value<string>()));
        }

        private static Type GetNestedSettingsType(Type propertyType)
        {
            if (propertyType == null)
            {
                return null;
            }

            var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (type == typeof(string) || type == typeof(object) || typeof(JToken).IsAssignableFrom(type) ||
                typeof(System.Collections.IDictionary).IsAssignableFrom(type) || type.IsPrimitive || type.IsEnum ||
                type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid))
            {
                return null;
            }

            if (type.IsArray)
            {
                return GetNestedSettingsType(type.GetElementType());
            }

            if (type.IsGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();
                if (genericType == typeof(IDictionary<,>) || genericType == typeof(IReadOnlyDictionary<,>))
                {
                    return null;
                }

                var elementType = type.GetGenericArguments().LastOrDefault();
                if (elementType != null && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                {
                    return GetNestedSettingsType(elementType);
                }
            }

            return type.IsClass ? type : null;
        }

        private static string AddUnsupportedProviderDisposition(
            string mapping,
            string table,
            IEnumerable<string> unsupported,
            BookshelfDatabaseImportReport report)
        {
            var providers = unsupported.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
            if (providers.Length == 0)
            {
                return mapping;
            }

            foreach (var provider in providers)
            {
                report.AddBlockingIssue($"{table} provider settings need an explicit Chaptarr mapping for {provider}");
            }

            return mapping + "; unsupported implementation/configuration pairs are disabled in the staged copy and block apply: " +
                   string.Join(", ", providers);
        }

        private static IReadOnlyDictionary<string, string> BuildProviderContractMap(Type providerInterface, Type genericProviderBase)
        {
            var contracts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var providerType in providerInterface.Assembly.GetTypes())
            {
                if (providerType.IsAbstract || providerType.IsInterface || !providerInterface.IsAssignableFrom(providerType))
                {
                    continue;
                }

                var settingsContract = FindSettingsContract(providerType, genericProviderBase);
                if (settingsContract == null)
                {
                    continue;
                }

                if (!contracts.TryAdd(providerType.Name, settingsContract.Name))
                {
                    ambiguousNames.Add(providerType.Name);
                }
            }

            foreach (var ambiguousName in ambiguousNames)
            {
                contracts.Remove(ambiguousName);
            }

            return contracts;
        }

        private static Type FindSettingsContract(Type providerType, Type genericProviderBase)
        {
            for (var type = providerType; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == genericProviderBase)
                {
                    return type.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static void ImportNamingConfig(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report)
        {
            var sourceRows = ReadRows(source, "NamingConfig").ToList();
            var sourceCount = sourceRows.Count;
            var targetColumns = GetColumns(target, "NamingConfig");

            // The migrated Chaptarr database already has a valid default naming row.
            // BookshelfNG can have an empty NamingConfig table when the operator has
            // never changed its defaults, so keep the target default in that case.
            if (sourceCount > 0)
            {
                using var clear = target.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = $"DELETE FROM {Quote("NamingConfig")};";
                clear.ExecuteNonQuery();

                foreach (var row in sourceRows)
                {
                    EnsureAllSourceValuesMapped(row, targetColumns, "NamingConfig", NamingConfigMappedSourceColumns);
                    var values = Intersect(row, targetColumns);
                    SetIfPresent(values, "MultiEpisodeStyle", 0);
                    SetIfPresent(values, "AuthorFolderFormat", GetString(row, "AuthorFolderFormat") ?? "{Author Name}");
                    SetIfPresent(values, "StandardBookFormat", GetString(row, "StandardBookFormat") ?? "{Book Title}");
                    InsertRow(target, transaction, "NamingConfig", targetColumns, values);
                }
            }

            report.RecordTable("NamingConfig", sourceCount, 0, sourceCount, 0, 0,
                "filename and folder patterns are preserved; target-only naming settings use Chaptarr defaults");
        }

        private static void ImportConfig(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report,
            int sourceSchemaVersion,
            string sourceApiBasePath)
        {
            const string legacyMatchKey = "bookimportminimummatchpercent";
            const string chaptarrStrictnessKey = "bookmatchingstrictness";
            const string chaptarrMatchCompatibilityKey = "bookshelfmatchcompatibility";
            const string chaptarrExperienceModeKey = "experiencemode";
            const string bookshelfMigrationMarkerKey = "bookshelfngdatabasemigrated";
            const string legacyHardcoverTokenKey = "hardcoverauth";
            const string chaptarrHardcoverTokenKey = "hardcoverapitoken";
            const string chaptarrHardcoverEnabledKey = "hardcoverenabled";
            const string bookshelfProviderDialectKey = "bookshelfngproviderdialect";
            const string bookshelfCompatibilityProfileVersionKey = "bookshelfngcompatibilityprofileversion";
            const string bookshelfSourceProductKey = "bookshelfngsourceproduct";
            const string bookshelfSourceSchemaVersionKey = "bookshelfngsourceschemaversion";
            const string bookshelfApiBasePathKey = "bookshelfngapibasepath";
            const string bookshelfFormatSemanticsKey = "bookshelfngformatsemantics";
            const string bookshelfIdentityMapVersionKey = "bookshelfngidentitymapversion";
            const string bookshelfMigrationOutcomeKey = "bookshelfngmigrationoutcome";
            const string legacyMetadataSourceKey = "metadatasource";
            const string chaptarrMetadataServerUrlKey = "metadataserverurl";
            const string chaptarrGitHubUpdatesKey = "usegithubupdates";

            var configRows = ReadRows(source, "Config").ToList();
            var sourceCount = configRows.Count;
            var targetColumns = GetColumns(target, "Config");
            var nextGeneratedConfigId = configRows
                .Select(row => GetNullableInt(row, "Id") ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            var legacyRows = configRows.Where(row => string.Equals(GetString(row, "Key"), legacyMatchKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var strictnessRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrStrictnessKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var matchCompatibilityRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrMatchCompatibilityKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var experienceModeRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrExperienceModeKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfMigrationMarkerRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfMigrationMarkerKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var legacyHardcoverTokenRows = configRows.Where(row => string.Equals(GetString(row, "Key"), legacyHardcoverTokenKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var chaptarrHardcoverTokenRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrHardcoverTokenKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var chaptarrHardcoverEnabledRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrHardcoverEnabledKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfProviderDialectRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfProviderDialectKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfCompatibilityProfileVersionRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfCompatibilityProfileVersionKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfSourceProductRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfSourceProductKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfSourceSchemaVersionRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfSourceSchemaVersionKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfApiBasePathRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfApiBasePathKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfFormatSemanticsRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfFormatSemanticsKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfIdentityMapVersionRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfIdentityMapVersionKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var bookshelfMigrationOutcomeRows = configRows.Where(row => string.Equals(GetString(row, "Key"), bookshelfMigrationOutcomeKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var legacyMetadataSourceRows = configRows.Where(row => string.Equals(GetString(row, "Key"), legacyMetadataSourceKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var chaptarrMetadataServerUrlRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrMetadataServerUrlKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var chaptarrGitHubUpdatesRows = configRows.Where(row => string.Equals(GetString(row, "Key"), chaptarrGitHubUpdatesKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var autoInitializeAuthorPathsRows = configRows
                .Where(row => string.Equals(GetString(row, "Key"), "AutoInitializeFormatSpecificAuthorPaths", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (legacyRows.Count > 1 || strictnessRows.Count > 1 || matchCompatibilityRows.Count > 1 || experienceModeRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate match/experience settings; conversion stopped before promotion.");
            }

            if (bookshelfMigrationMarkerRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate migration markers; conversion stopped before promotion.");
            }

            if (legacyHardcoverTokenRows.Count > 1 || chaptarrHardcoverTokenRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate Hardcover token settings; conversion stopped before promotion.");
            }

            if (chaptarrHardcoverEnabledRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate Hardcover mode settings; conversion stopped before promotion.");
            }

            if (bookshelfProviderDialectRows.Count > 1 || bookshelfCompatibilityProfileVersionRows.Count > 1 ||
                bookshelfSourceProductRows.Count > 1 || bookshelfSourceSchemaVersionRows.Count > 1 ||
                bookshelfApiBasePathRows.Count > 1 || bookshelfFormatSemanticsRows.Count > 1 ||
                bookshelfIdentityMapVersionRows.Count > 1 || bookshelfMigrationOutcomeRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate compatibility profile settings; conversion stopped before promotion.");
            }

            if (legacyMetadataSourceRows.Count > 1 || chaptarrMetadataServerUrlRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate metadata-server settings; conversion stopped before promotion.");
            }

            if (chaptarrGitHubUpdatesRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate ChaptarrNG update-source settings; conversion stopped before promotion.");
            }

            if (autoInitializeAuthorPathsRows.Count > 1)
            {
                throw new InvalidDataException("BookshelfNG config contains duplicate format-specific author path settings; conversion stopped before promotion.");
            }

            var matchSettingDisposition = "no BookshelfNG numeric match threshold was saved; the BookshelfNG default 70% remains active for fuzzy RSS-title fallback and candidate acceptance";
            var existingStrictnessValue = strictnessRows.SingleOrDefault() is { } strictnessRow
                ? GetString(strictnessRow, "Value")
                : null;
            int? legacyPercent = null;
            BookMatchingStrictness? legacyMappedStrictness = null;

            if (legacyRows.Count == 1)
            {
                var rawPercent = GetString(legacyRows[0], "Value");
                if (!int.TryParse(rawPercent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) ||
                    !BookshelfMatchSettingMigration.TryMapMinimumMatchPercent(percent, out var mappedStrictness))
                {
                    throw new InvalidDataException("BookshelfNG BookImportMinimumMatchPercent must be an integer from 50 through 99; conversion stopped before promotion.");
                }

                legacyPercent = percent;
                legacyMappedStrictness = mappedStrictness;
            }

            if (!string.IsNullOrWhiteSpace(existingStrictnessValue))
            {
                if (!Enum.TryParse(existingStrictnessValue, ignoreCase: true, out BookMatchingStrictness parsedStrictness) ||
                    !Enum.IsDefined(typeof(BookMatchingStrictness), parsedStrictness))
                {
                    throw new InvalidDataException("BookshelfNG config contains an invalid BookMatchingStrictness value; conversion stopped before promotion.");
                }

                matchSettingDisposition = legacyRows.Count == 0
                    ? $"explicit ChaptarrNG matching mode '{existingStrictnessValue}' was preserved; the BookshelfNG default numeric threshold of 70% remains active for fuzzy RSS-title fallback and candidate acceptance"
                    : $"explicit ChaptarrNG matching mode '{existingStrictnessValue}' was preserved; the BookshelfNG numeric threshold remains active for fuzzy RSS-title fallback and candidate acceptance";
            }
            else if (legacyPercent.HasValue && legacyMappedStrictness.HasValue)
            {
                var mappedValue = legacyMappedStrictness.Value.ToString().ToLowerInvariant();
                if (strictnessRows.Count == 1)
                {
                    strictnessRows[0]["Value"] = mappedValue;
                }
                else
                {
                    var mappedStrictness = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Key"] = chaptarrStrictnessKey,
                        ["Value"] = mappedValue
                    };
                    if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                    {
                        mappedStrictness["Id"] = checked(++nextGeneratedConfigId);
                    }
                    configRows.Add(mappedStrictness);
                }

                matchSettingDisposition = $"BookshelfNG BookImportMinimumMatchPercent={legacyPercent.Value.ToString(CultureInfo.InvariantCulture)} remains active for fuzzy RSS-title fallback and candidate acceptance, and was also mapped to ChaptarrNG BookMatchingStrictness='{mappedValue}' for its additional safety rules.";
            }

            if (matchCompatibilityRows.Count == 0)
            {
                var compatibilitySetting = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Key"] = "BookshelfMatchCompatibility",
                    ["Value"] = "true"
                };
                if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                {
                    compatibilitySetting["Id"] = checked(++nextGeneratedConfigId);
                }
                configRows.Add(compatibilitySetting);
            }
            else if (matchCompatibilityRows.Count == 1 &&
                     !bool.TryParse(GetString(matchCompatibilityRows[0], "Value"), out _))
            {
                throw new InvalidDataException("BookshelfNG BookshelfMatchCompatibility must be true or false; conversion stopped before promotion.");
            }

            if (experienceModeRows.Count == 0)
            {
                var experienceMode = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Key"] = "ExperienceMode",
                    ["Value"] = "simple"
                };
                if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                {
                    experienceMode["Id"] = checked(++nextGeneratedConfigId);
                }
                configRows.Add(experienceMode);
            }
            else
            {
                var experienceModeValue = GetString(experienceModeRows[0], "Value");
                if (!string.Equals(experienceModeValue, "simple", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(experienceModeValue, "advanced", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("BookshelfNG config contains an invalid ChaptarrNG experience mode; conversion stopped before promotion.");
                }
                experienceModeRows[0]["Value"] = experienceModeValue.ToLowerInvariant();
            }

            if (bookshelfMigrationMarkerRows.Count == 0)
            {
                var migrationMarker = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Key"] = BookshelfNGMigrationMarkerKey,
                    ["Value"] = "true"
                };
                if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                {
                    migrationMarker["Id"] = checked(++nextGeneratedConfigId);
                }
                configRows.Add(migrationMarker);
            }
            else
            {
                bookshelfMigrationMarkerRows[0]["Key"] = BookshelfNGMigrationMarkerKey;
                bookshelfMigrationMarkerRows[0]["Value"] = "true";
            }

            var updateRouteDisposition = "the updater was routed to checksum-verified ChaptarrNG GitHub releases instead of the BookshelfNG/Readarr update service";
            if (chaptarrGitHubUpdatesRows.Count == 0)
            {
                var githubUpdates = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Key"] = "UseGitHubUpdates",
                    ["Value"] = "true"
                };
                if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                {
                    githubUpdates["Id"] = checked(++nextGeneratedConfigId);
                }
                configRows.Add(githubUpdates);
            }
            else if (!bool.TryParse(GetString(chaptarrGitHubUpdatesRows[0], "Value"), out var useGitHubUpdates))
            {
                throw new InvalidDataException("BookshelfNG config contains an invalid ChaptarrNG update-source setting; conversion stopped before promotion.");
            }
            else if (!useGitHubUpdates)
            {
                chaptarrGitHubUpdatesRows[0]["Value"] = "true";
                updateRouteDisposition = "the existing legacy update-source setting was switched to checksum-verified ChaptarrNG GitHub releases";
            }

            var nativeHardcoverPrimaryMode = ConfiguredNativeHardcoverPrimaryMode();
            var sourceProviderDialect = nativeHardcoverPrimaryMode ? "hc" : "gr";
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfProviderDialectRows,
                "BookshelfNGProviderDialect",
                sourceProviderDialect,
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfCompatibilityProfileVersionRows,
                "BookshelfNGCompatibilityProfileVersion",
                BookshelfNGCompatibilityProfile.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfSourceProductRows,
                "BookshelfNGSourceProduct",
                "BookshelfNG",
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfSourceSchemaVersionRows,
                "BookshelfNGSourceSchemaVersion",
                sourceSchemaVersion.ToString(CultureInfo.InvariantCulture),
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfApiBasePathRows,
                "BookshelfNGApiBasePath",
                BookshelfNGCompatibilityProfile.NormalizeApiBasePath(sourceApiBasePath),
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfFormatSemanticsRows,
                "BookshelfNGFormatSemantics",
                BookshelfNGCompatibilityProfile.CurrentFormatSemantics,
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfIdentityMapVersionRows,
                "BookshelfNGIdentityMapVersion",
                BookshelfNGCompatibilityProfile.CurrentIdentityMapVersion.ToString(CultureInfo.InvariantCulture),
                ref nextGeneratedConfigId,
                target);
            UpsertCompatibilityProfileValue(
                configRows,
                targetColumns,
                bookshelfMigrationOutcomeRows,
                "BookshelfNGMigrationOutcome",
                "converted",
                ref nextGeneratedConfigId,
                target);

            var hardcoverTokenDisposition = "no saved Hardcover token was present";
            var legacyHardcoverToken = legacyHardcoverTokenRows.SingleOrDefault();
            if (legacyHardcoverToken != null && !string.IsNullOrWhiteSpace(GetString(legacyHardcoverToken, "Value")))
            {
                var targetHardcoverToken = chaptarrHardcoverTokenRows.SingleOrDefault();
                if (targetHardcoverToken != null)
                {
                    hardcoverTokenDisposition = "an explicit ChaptarrNG Hardcover token took precedence; the original BookshelfNG token row was retained";
                }
                else
                {
                    var token = GetString(legacyHardcoverToken, "Value").Trim();
                    const string bearerPrefix = "Bearer ";
                    if (token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        token = token.Substring(bearerPrefix.Length).Trim();
                    }

                    if (targetHardcoverToken == null)
                    {
                        var mappedToken = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["Key"] = "HardcoverApiToken",
                            ["Value"] = token
                        };
                        if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                        {
                            mappedToken["Id"] = checked(++nextGeneratedConfigId);
                        }
                        configRows.Add(mappedToken);
                    }
                    else
                    {
                        targetHardcoverToken["Value"] = token;
                    }

                    hardcoverTokenDisposition = "the saved BookshelfNG Hardcover token was copied to ChaptarrNG HardcoverApiToken; the original token row was retained";
                }
            }

            var hardcoverModeDisposition = "BookshelfNG's effective source mode was recorded as " + sourceProviderDialect + "; an explicit ChaptarrNG mode setting was preserved when present";
            if (chaptarrHardcoverEnabledRows.Count == 1)
            {
                hardcoverModeDisposition = "a saved ChaptarrNG HardcoverEnabled setting was preserved";
            }
            else
            {
                var mappedHardcoverMode = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Key"] = "HardcoverEnabled",
                    ["Value"] = nativeHardcoverPrimaryMode ? "true" : "false"
                };
                if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                {
                    mappedHardcoverMode["Id"] = checked(++nextGeneratedConfigId);
                }
                configRows.Add(mappedHardcoverMode);
                hardcoverModeDisposition = $"BookshelfNG's effective {sourceProviderDialect} runtime mode was copied to ChaptarrNG HardcoverEnabled";
            }

            var metadataServerUrlDisposition = "no saved metadata-server URL was present; ChaptarrNG's configured environment or default URL applies";
            var legacyMetadataSource = legacyMetadataSourceRows.SingleOrDefault();
            var chaptarrMetadataServerUrl = chaptarrMetadataServerUrlRows.SingleOrDefault();
            if (chaptarrMetadataServerUrl != null && !string.IsNullOrWhiteSpace(GetString(chaptarrMetadataServerUrl, "Value")))
            {
                metadataServerUrlDisposition = "an explicit ChaptarrNG MetadataServerUrl was preserved; the legacy MetadataSource row was retained";
            }
            else if (legacyMetadataSource != null && !string.IsNullOrWhiteSpace(GetString(legacyMetadataSource, "Value")))
            {
                var mappedMetadataServerUrl = GetString(legacyMetadataSource, "Value").Trim();
                if (chaptarrMetadataServerUrl != null)
                {
                    chaptarrMetadataServerUrl["Value"] = mappedMetadataServerUrl;
                }
                else
                {
                    var mappedMetadataServerSetting = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Key"] = "MetadataServerUrl",
                        ["Value"] = mappedMetadataServerUrl
                    };
                    if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
                    {
                        mappedMetadataServerSetting["Id"] = checked(++nextGeneratedConfigId);
                    }
                    configRows.Add(mappedMetadataServerSetting);
                }

                metadataServerUrlDisposition = "BookshelfNG MetadataSource was copied to ChaptarrNG MetadataServerUrl; the original legacy row was retained";
            }

            var authorPathSettingDisposition = "no BookshelfNG format-specific author path setting was saved; ChaptarrNG's disabled default applies";
            if (autoInitializeAuthorPathsRows.SingleOrDefault() is { } autoInitializeAuthorPathsRow)
            {
                if (!bool.TryParse(GetString(autoInitializeAuthorPathsRow, "Value"), out var autoInitializeAuthorPaths))
                {
                    throw new InvalidDataException("BookshelfNG AutoInitializeFormatSpecificAuthorPaths must be true or false; conversion stopped before promotion.");
                }

                if (autoInitializeAuthorPaths)
                {
                    MapBookshelfNewAuthorRoot(
                        configRows,
                        "NewAuthorEbookRootFolderPath",
                        "DefaultEbookRootFolderPath",
                        target,
                        targetColumns,
                        ref nextGeneratedConfigId);
                    MapBookshelfNewAuthorRoot(
                        configRows,
                        "NewAuthorAudiobookRootFolderPath",
                        "DefaultAudiobookRootFolderPath",
                        target,
                        targetColumns,
                        ref nextGeneratedConfigId);
                    authorPathSettingDisposition = "BookshelfNG's opt-in author path initialization was preserved; configured new-author roots map to ChaptarrNG's per-format default roots, with explicit Chaptarr values taking precedence";
                }
                else
                {
                    authorPathSettingDisposition = "BookshelfNG's opt-in author path initialization was disabled; its new-author root overrides were not applied";
                }
            }

            foreach (var row in configRows)
            {
                var unmapped = row.Where(x => x.Value != null && !targetColumns.ContainsKey(x.Key))
                    .Select(x => x.Key)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                if (unmapped.Length > 0)
                {
                    throw new InvalidDataException(
                        $"BookshelfNG Config row '{GetString(row, "Key")}' has non-empty fields with no Chaptarr mapping: " +
                        string.Join(", ", unmapped.Select(x => "Config." + x)) + ". Conversion stopped before promotion.");
                }

                InsertRow(target, transaction, "Config", targetColumns, Intersect(row, targetColumns));
            }

            report.RecordTable("Config", sourceCount, 0, sourceCount, 0, 0,
                $"saved configuration is copied with compatible fields; BookshelfNG compatibility profile version {BookshelfNGCompatibilityProfile.CurrentVersion} records source schema {sourceSchemaVersion}, provider dialect '{sourceProviderDialect}', API base path, audiobook/ebook format semantics, identity-map version, and conversion outcome; a dedicated BookshelfNG migration marker is retained on the ChaptarrNG database; {matchSettingDisposition}; {hardcoverTokenDisposition}; {hardcoverModeDisposition}; {metadataServerUrlDisposition}; {authorPathSettingDisposition}; {updateRouteDisposition}");
        }

        private static void UpsertCompatibilityProfileValue(
            List<Dictionary<string, object>> rows,
            IReadOnlyDictionary<string, ColumnInfo> targetColumns,
            List<Dictionary<string, object>> matchingRows,
            string key,
            string value,
            ref int nextGeneratedConfigId,
            DbConnection target)
        {
            if (matchingRows.Count == 1)
            {
                matchingRows[0]["Key"] = key;
                matchingRows[0]["Value"] = value;
                return;
            }

            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Key"] = key,
                ["Value"] = value
            };
            if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
            {
                row["Id"] = checked(++nextGeneratedConfigId);
            }

            rows.Add(row);
        }

        private static void MapBookshelfNewAuthorRoot(
            List<Dictionary<string, object>> configRows,
            string bookshelfKey,
            string chaptarrKey,
            DbConnection target,
            IReadOnlyDictionary<string, ColumnInfo> targetColumns,
            ref int nextGeneratedConfigId)
        {
            var bookshelfRows = configRows
                .Where(row => string.Equals(GetString(row, "Key"), bookshelfKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bookshelfRows.Count > 1)
            {
                throw new InvalidDataException($"BookshelfNG config contains duplicate {bookshelfKey} settings; conversion stopped before promotion.");
            }

            var bookshelfRow = bookshelfRows.SingleOrDefault();
            if (bookshelfRow == null)
            {
                return;
            }

            var bookshelfValue = GetString(bookshelfRow, "Value");
            if (string.IsNullOrWhiteSpace(bookshelfValue))
            {
                return;
            }

            var chaptarrRows = configRows
                .Where(row => string.Equals(GetString(row, "Key"), chaptarrKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (chaptarrRows.Count > 1)
            {
                throw new InvalidDataException($"BookshelfNG config contains duplicate {chaptarrKey} settings; conversion stopped before promotion.");
            }

            if (chaptarrRows.SingleOrDefault() is { } chaptarrRow)
            {
                if (string.IsNullOrWhiteSpace(GetString(chaptarrRow, "Value")))
                {
                    chaptarrRow["Value"] = bookshelfValue;
                }

                return;
            }

            var mappedRoot = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Key"] = chaptarrKey,
                ["Value"] = bookshelfValue
            };
            if (target is NpgsqlConnection && targetColumns.ContainsKey("Id"))
            {
                mappedRoot["Id"] = checked(++nextGeneratedConfigId);
            }

            configRows.Add(mappedRoot);
        }

        private static void ImportNotifications(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report)
        {
            var sourceCount = CountRows(source, "Notifications");
            var targetColumns = GetColumns(target, "Notifications");
            var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ReadRows(source, "Notifications"))
            {
                EnsureAllSourceValuesMapped(
                    row,
                    targetColumns,
                    "Notifications",
                    NotificationMappedSourceColumns);
                var values = Intersect(row, targetColumns);
                CheckProviderSettingsCompatibility(row, values, NotificationContracts, unsupported);
                if (!row.ContainsKey("OnBookRetag"))
                {
                    SetIfPresent(values, "OnBookRetag", GetValue(row, "OnTrackRetag") ?? false);
                }
                SetIfPresent(values, "OnBookAdded", false);
                SetIfPresent(values, "OnHealthRestored", false);
                InsertRow(target, transaction, "Notifications", targetColumns, values);
            }

            report.RecordTable("Notifications", sourceCount, 0, sourceCount, 0, 0,
                AddUnsupportedProviderDisposition(
                    "notification settings and triggers are preserved; the legacy track-retag trigger maps to book retag",
                    "Notifications",
                    unsupported,
                    report));
        }

        private static void CopyTable(
            DbConnection source,
            DbConnection target,
            DbTransaction transaction,
            BookshelfDatabaseImportReport report,
            string table,
            Action<Dictionary<string, object>> transform = null)
        {
            var sourceCount = CountRows(source, table);
            var targetColumns = GetColumns(target, table);
            foreach (var row in ReadRows(source, table))
            {
                EnsureAllSourceValuesMapped(row, targetColumns, table);

                var values = Intersect(row, targetColumns);
                transform?.Invoke(values);
                InsertRow(target, transaction, table, targetColumns, values);
            }

            report.RecordTable(table, sourceCount, 0, sourceCount, 0, 0,
                $"source {table} rows are mapped by their compatible fields into Chaptarr {table}");
        }

        private static void ResetTable(DbConnection source, BookshelfDatabaseImportReport report, string table, string reason)
        {
            var sourceCount = CountRows(source, table);
            report.RecordTable(table, sourceCount, 0, 0, 0, sourceCount, reason);
        }

        private static void RebuildProviderAliases(
            DbConnection target,
            DbTransaction transaction,
            IReadOnlyDictionary<int, AuthorMap> authors,
            IReadOnlyDictionary<int, BookMap> books,
            IReadOnlyDictionary<int, EditionMap> editions,
            IReadOnlyDictionary<int, SeriesMap> series)
        {
            foreach (var author in authors.Values)
            {
                foreach (var id in author.ProviderIds) InsertAlias(target, transaction, "Author", author.Id, "author", id);
            }

            foreach (var book in books.Values)
            {
                if (book.ProviderId != null)
                {
                    foreach (var id in book.TargetIds.Values)
                    {
                        InsertAlias(target, transaction, "Book", id, "work", book.ProviderId);
                    }
                }
            }

            foreach (var edition in editions.Values)
            {
                if (edition.ProviderId != null) InsertAlias(target, transaction, "Edition", edition.Id, "edition", edition.ProviderId);
            }

            foreach (var item in series.Values)
            {
                if (item.ProviderId != null) InsertAlias(target, transaction, "Series", item.Id, "series", item.ProviderId);
            }
        }

        private static void InsertAlias(DbConnection target, DbTransaction transaction, string entityType, int entityId, string scope, string canonicalId)
        {
            var separator = canonicalId.IndexOf(':');
            if (separator <= 0 || separator == canonicalId.Length - 1) return;

            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO ""ProviderAliasIndex""
                    (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
                VALUES (@entityType, @entityId, @scope, @provider, @providerId, @createdAt, @createdAt)
                ON CONFLICT DO NOTHING;";
            AddParameter(command, "@entityType", entityType);
            AddParameter(command, "@entityId", entityId);
            AddParameter(command, "@scope", scope);
            AddParameter(command, "@provider", canonicalId.Substring(0, separator));
            AddParameter(command, "@providerId", canonicalId.Substring(separator + 1));
            AddParameter(command, "@createdAt", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }

        private static int MapQualityProfile(
            IReadOnlyDictionary<(int SourceId, int MediaType), int> profiles,
            int sourceId,
            int mediaType,
            string context)
        {
            if (profiles.TryGetValue((sourceId, mediaType), out var targetId)) return targetId;
            throw new InvalidDataException($"BookshelfNG {context} profile {sourceId} has no matching Chaptarr profile.");
        }

        private static int MapMetadataProfile(IReadOnlyDictionary<int, int> profiles, int sourceId, string context)
        {
            if (profiles.TryGetValue(sourceId, out var targetId)) return targetId;
            throw new InvalidDataException($"BookshelfNG {context} profile {sourceId} has no matching Chaptarr profile.");
        }

        private static string NormalizeSourceId(string value, string entityType)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var normalized = value.Trim();
            var separator = normalized.IndexOf(':');
            if (separator < 0)
            {
                if (normalized.All(char.IsDigit))
                {
                    // BookshelfNG stores numeric IDs without a namespace when its
                    // selected primary catalog owns the record. Preserve that
                    // dialect only when the source provider mode is explicit. Guessing
                    // here can silently attach every record to the wrong catalog.
                    return (RequireNativeHardcoverPrimaryMode() ? "hc:" : "gr:") + normalized;
                }

                throw new InvalidDataException($"BookshelfNG {entityType} provider ID '{normalized}' has no recognized provider prefix.");
            }

            var prefix = normalized.Substring(0, separator).Trim().ToLowerInvariant();
            var id = normalized.Substring(separator + 1).Trim();

            // BookshelfNG namespaces its primary metadata API IDs to distinguish
            // those records from IDs returned by direct catalogs. Strip that
            // wrapper before applying Chaptarr's canonical provider rules.
            if (prefix is "metadata-api" or "metadata-api-author")
            {
                return NormalizeSourceId(id, entityType);
            }

            var additionalSource = prefix.EndsWith("-author", StringComparison.Ordinal)
                ? prefix.Substring(0, prefix.Length - "-author".Length)
                : prefix;
            if (AdditionalMetadataSources.IsSupportedSource(additionalSource))
            {
                var providerId = AdditionalMetadataSources.CreateProviderId(prefix, id);
                if (!string.IsNullOrWhiteSpace(providerId))
                {
                    return providerId;
                }
            }

            if (prefix == "meta" && AdditionalMetadataSources.TryParseProviderId(normalized, out _, out _))
            {
                return normalized;
            }

            if (id.Length == 0 || id.Contains(':'))
            {
                throw new InvalidDataException($"BookshelfNG {entityType} provider ID '{normalized}' is malformed.");
            }

            prefix = prefix switch
            {
                "hardcover" or "hardcover-author" or "hardcover-book" or "hardcover-edition" or "hardcover-series" => "hc",
                "goodreads" or "goodreads-author" or "goodreads-book" or "goodreads-work" or "goodreads-edition" or "goodreads-series" => "gr",
                "openlibrary" or "openlibrary-author" or "openlibrary-work" or "openlibrary-edition" or "openlibrary-series" => "ol",
                "googlebooks" or "googlebooks-author" or "googlebooks-book" or "googlebooks-edition" => "gb",
                "audnexus" or "audnexus-author" or "amazon" or "amazon-author" or "amazon-book" or "amazon-edition" or "amazon-series" => "az",
                "ranobedb" or "ranobedb-author" or "ranobedb-book" => "rn",
                "ranobedb-release" => "rnr",
                _ => prefix
            };

            if (prefix is not ("hc" or "gr" or "ol" or "gb" or "az" or "rn" or "rnr"))
            {
                throw new InvalidDataException($"BookshelfNG {entityType} provider ID '{normalized}' uses unsupported provider '{prefix}'. Conversion stopped with the source database intact.");
            }

            if (prefix == "az") id = id.ToUpperInvariant();
            return prefix + ":" + id;
        }

        private static bool ConfiguredNativeHardcoverPrimaryMode()
        {
            // The old image flavor supplied this value implicitly. A single
            // standard ChaptarrNG image cannot encode the old tag's provider
            // mode, so require the caller to carry that setting forward instead
            // of silently interpreting bare numeric IDs as Goodreads.
            var nativeMode = Environment.GetEnvironmentVariable("HARDCOVER_NATIVE");
            if (!string.IsNullOrWhiteSpace(nativeMode) && !bool.TryParse(nativeMode, out _))
            {
                throw new InvalidDataException("BookshelfNG migration requires HARDCOVER_NATIVE to be true or false when it is set. No database was changed.");
            }

            if (string.Equals(nativeMode, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var hardcoverMode = Environment.GetEnvironmentVariable("HARDCOVER");
            if (string.IsNullOrWhiteSpace(hardcoverMode) || !bool.TryParse(hardcoverMode, out var enabled))
            {
                throw new InvalidDataException("BookshelfNG migration requires an explicit HARDCOVER=true or HARDCOVER=false provider mode. No database was changed.");
            }

            return enabled;
        }

        private static bool RequireNativeHardcoverPrimaryMode()
        {
            return ConfiguredNativeHardcoverPrimaryMode();
        }

        private static string NumericProviderDisposition()
        {
            return ConfiguredNativeHardcoverPrimaryMode()
                ? "bare numeric provider IDs are interpreted as Hardcover because BookshelfNG's runtime predicate enables native mode"
                : "bare numeric provider IDs are interpreted as Goodreads because BookshelfNG uses Goodreads when HARDCOVER is unset or not true";
        }

        private static void SetProviderColumns(IDictionary<string, object> values, string entityType, string canonicalId)
        {
            if (string.IsNullOrWhiteSpace(canonicalId)) return;
            var separator = canonicalId.IndexOf(':');
            if (separator <= 0) return;
            var prefix = canonicalId.Substring(0, separator);
            var id = canonicalId.Substring(separator + 1);

            switch (entityType)
            {
                case "Author":
                    SetIfPresent(values, "AdditionalMetadataProviderId", prefix == "meta" ? canonicalId : null);
                    SetIfPresent(values, prefix switch
                    {
                        "gr" => "GoodreadsAuthorId",
                        "hc" => "HardcoverAuthorId",
                        "ol" => "OpenLibraryAuthorId",
                        "gb" => "GoogleBooksAuthorId",
                        "az" => "AudnexusAuthorId",
                        "rn" => "RanobeDbAuthorId",
                        _ => null
                    }, canonicalId);
                    break;
                case "Book":
                    SetIfPresent(values, "AdditionalMetadataProviderId", prefix == "meta" ? canonicalId : null);
                    SetIfPresent(values, prefix switch
                    {
                        "gr" => "GoodreadsWorkId",
                        "hc" => "HardcoverBookId",
                        "ol" => "OpenLibraryWorkId",
                        "gb" => "GoogleBooksId",
                        "az" => "ASIN",
                        "rn" => "RanobeDbBookId",
                        _ => null
                    }, canonicalId);
                    break;
                case "Edition":
                    switch (prefix)
                    {
                        case "gr":
                            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var goodreadsId))
                            {
                                SetIfPresent(values, "GoodreadsEditionId", goodreadsId);
                            }
                            break;
                        // Chaptarr's HardcoverEditionId field stores the raw
                        // catalog ID; ForeignEditionId and ProviderAliasIndex
                        // carry the canonical provider-qualified form.
                        case "hc": SetIfPresent(values, "HardcoverEditionId", id); break;
                        case "ol": SetIfPresent(values, "OpenLibraryEditionId", canonicalId); break;
                        case "gb": SetIfPresent(values, "GoogleBooksEditionId", canonicalId); break;
                        case "az":
                            SetIfPresent(values, "Asin", id);
                            SetIfPresent(values, "AudibleASIN", canonicalId);
                            break;
                    }
                    break;
                case "Series":
                    SetIfPresent(values, prefix switch
                    {
                        "gr" => "GoodreadsSeriesId",
                        "hc" => "HardcoverSeriesId",
                        "ol" => "OpenLibrarySeriesId",
                        "az" => "AmazonSeriesAsin",
                        _ => null
                    }, canonicalId);
                    break;
            }
        }

        private static string FindRootFolder(string mediaPath, IReadOnlyList<RootFolderMap> rootFolders)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return null;
            var normalizedMediaPath = NormalizePath(mediaPath);
            var matches = rootFolders
                .Where(x => IsPathWithin(normalizedMediaPath, NormalizePath(x.Path)))
                .OrderByDescending(x => NormalizePath(x.Path).Length)
                .ToArray();
            return matches.FirstOrDefault()?.Path;
        }

        private static bool IsPathWithin(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static int RemapMonitorType(int sourceValue)
        {
            // Chaptarr inserted SpecificBook before Unknown; BookshelfNG's value 7 meant Unknown.
            return sourceValue >= 7 ? sourceValue + 1 : sourceValue;
        }

        private static int ParsePosition(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ? position : 0;
        }

        private static List<int> ParseIntegerArray(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<int>();
            try
            {
                return JArray.Parse(value).Select(x => x.Value<int>()).ToList();
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException)
            {
                throw new InvalidDataException("A BookshelfNG blocklist contains an unreadable book ID list.", exception);
            }
        }

        internal static HashSet<string> GetTables(DbConnection connection)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = connection is SqliteConnection
                ? "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"
                : "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE';";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }

        private static bool TableExists(DbConnection connection, string table)
        {
            if (connection is NpgsqlConnection)
            {
                return GetTables(connection).Contains(table);
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
            AddParameter(command, "@name", table);
            return command.ExecuteScalar() != null;
        }

        private static Dictionary<string, ColumnInfo> GetColumns(DbConnection connection, string table)
        {
            var result = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            if (connection is NpgsqlConnection)
            {
                command.CommandText = @"
                    SELECT c.column_name, c.is_nullable, c.column_default, c.udt_name,
                           EXISTS (
                               SELECT 1
                                 FROM information_schema.table_constraints tc
                                 JOIN information_schema.key_column_usage kcu
                                   ON kcu.constraint_schema = tc.constraint_schema
                                  AND kcu.constraint_name = tc.constraint_name
                                  AND kcu.table_name = tc.table_name
                                WHERE tc.table_schema = current_schema()
                                  AND tc.table_name = c.table_name
                                  AND tc.constraint_type = 'PRIMARY KEY'
                                  AND kcu.column_name = c.column_name
                           )
                      FROM information_schema.columns c
                     WHERE c.table_schema = current_schema()
                       AND c.table_name = @table
                     ORDER BY c.ordinal_position;";
                AddParameter(command, "@table", table);
                using var postgresReader = command.ExecuteReader();
                while (postgresReader.Read())
                {
                    var name = postgresReader.GetString(0);
                    var notNull = string.Equals(postgresReader.GetString(1), "NO", StringComparison.OrdinalIgnoreCase);
                    var defaultValue = postgresReader.IsDBNull(2) ? null : postgresReader.GetValue(2)?.ToString();
                    var dataType = postgresReader.GetString(3);
                    var primaryKey = postgresReader.GetBoolean(4);
                    result[name] = new ColumnInfo(notNull, defaultValue, primaryKey, dataType);
                }

                if (result.Count == 0) throw new InvalidDataException($"Target table '{table}' does not exist in the staged Chaptarr schema.");
                return result;
            }

            command.CommandText = $"PRAGMA table_info({Quote(table)});"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table is an identifier escaped by Quote; row data is never interpolated.
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(reader.GetOrdinal("name"));
                var notNull = reader.GetInt32(reader.GetOrdinal("notnull")) != 0;
                var defaultValueOrdinal = reader.GetOrdinal("dflt_value");
                var defaultValue = reader.IsDBNull(defaultValueOrdinal) ? null : reader.GetValue(defaultValueOrdinal)?.ToString();
                var primaryKey = reader.GetInt32(reader.GetOrdinal("pk")) != 0;
                var dataType = reader.GetString(reader.GetOrdinal("type"));
                result[name] = new ColumnInfo(notNull, defaultValue, primaryKey, dataType);
            }

            if (result.Count == 0) throw new InvalidDataException($"Target table '{table}' does not exist in the staged Chaptarr schema.");
            return result;
        }

        private static long CountRows(DbConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table is an identifier escaped by Quote.
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static object Scalar(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- callers pass constant migration queries; values use parameters.
            return command.ExecuteScalar();
        }

        private static IEnumerable<Dictionary<string, object>> ReadRows(DbConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {Quote(table)} ORDER BY {Quote("Id")};"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table and column are escaped identifiers.
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                yield return row;
            }
        }

        private static Dictionary<string, object> Intersect(IReadOnlyDictionary<string, object> source, IReadOnlyDictionary<string, ColumnInfo> target)
        {
            return source.Where(x => target.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        }

        private static void InsertRow(
            DbConnection target,
            DbTransaction transaction,
            string table,
            IReadOnlyDictionary<string, ColumnInfo> targetColumns,
            IReadOnlyDictionary<string, object> values)
        {
            foreach (var targetColumn in targetColumns)
            {
                if (!targetColumn.Value.NotNull || targetColumn.Value.HasDefault || targetColumn.Value.PrimaryKey) continue;
                if (!values.TryGetValue(targetColumn.Key, out var value) || value == null)
                {
                    throw new InvalidDataException($"BookshelfNG conversion did not provide required Chaptarr column '{table}.{targetColumn.Key}'.");
                }
            }

            var insertValues = values.Where(x => targetColumns.ContainsKey(x.Key) && !(targetColumns[x.Key].PrimaryKey && x.Value == null)).ToArray();
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            if (insertValues.Length == 0)
            {
                command.CommandText = $"INSERT INTO {Quote(table)} DEFAULT VALUES;"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table is an identifier escaped by Quote.
            }
            else
            {
                var names = insertValues.Select(x => Quote(x.Key)).ToArray();
                var parameters = insertValues.Select((_, index) => "@p" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
                var identityOverride = target is NpgsqlConnection ? " OVERRIDING SYSTEM VALUE" : string.Empty;
                command.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(", ", names)}){identityOverride} VALUES ({string.Join(", ", parameters)});"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- identifiers are escaped by Quote and row values are bound below.
                for (var i = 0; i < insertValues.Length; i++)
                {
                    var column = targetColumns[insertValues[i].Key];
                    AddParameter(command, parameters[i], NormalizeTargetValue(target, column, insertValues[i].Value), column);
                }
            }

            try
            {
                command.ExecuteNonQuery();
            }
            catch (DbException exception)
            {
                throw new InvalidDataException($"BookshelfNG row could not be represented in Chaptarr table '{table}'; conversion was stopped before promotion.", exception);
            }
        }

        private static void AddParameter(DbCommand command, string name, object value, ColumnInfo targetColumn = null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            if (parameter is NpgsqlParameter postgresParameter && targetColumn?.DataType == "jsonb")
            {
                postgresParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
            }
            else if (parameter is NpgsqlParameter jsonParameter && targetColumn?.DataType == "json")
            {
                jsonParameter.NpgsqlDbType = NpgsqlDbType.Json;
            }

            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static void RecordBookshelfLegacyIdentity(
            DbConnection target,
            DbTransaction transaction,
            string entityType,
            int sourceId,
            int targetId,
            int? mediaType)
        {
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"INSERT INTO {Quote("BookshelfLegacyIdentityMap")}
                ({Quote("Source")}, {Quote("EntityType")}, {Quote("SourceId")}, {Quote("TargetId")}, {Quote("MediaType")})
                VALUES (@source, @entityType, @sourceId, @targetId, @mediaType);"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- fixed table and column identifiers are escaped; all row values are parameters.
            AddParameter(command, "@source", "BookshelfNG");
            AddParameter(command, "@entityType", entityType);
            AddParameter(command, "@sourceId", sourceId);
            AddParameter(command, "@targetId", targetId);
            AddParameter(command, "@mediaType", mediaType);
            command.ExecuteNonQuery();
        }

        private static object NormalizeTargetValue(DbConnection target, ColumnInfo targetColumn, object value)
        {
            if (value == null || value == DBNull.Value || target is not NpgsqlConnection)
            {
                return value ?? DBNull.Value;
            }

            switch (targetColumn.DataType)
            {
                case "json":
                case "jsonb":
                    return value is JToken token ? token.ToString(Formatting.None) : value.ToString();
                case "bool":
                    if (value is bool boolean) return boolean;
                    if (value is string text && bool.TryParse(text, out var parsedBoolean)) return parsedBoolean;
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
                case "timestamptz":
                    if (value is DateTimeOffset offset) return offset.ToUniversalTime();
                    if (value is DateTime dateTime)
                    {
                        return dateTime.Kind == DateTimeKind.Utc
                            ? dateTime
                            : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
                    }

                    if (DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedOffset))
                    {
                        return parsedOffset;
                    }

                    break;
                case "timestamp":
                    if (value is DateTime timestamp) return DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
                    if (value is DateTimeOffset timestampOffset) return timestampOffset.UtcDateTime;
                    if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp))
                    {
                        return DateTime.SpecifyKind(parsedTimestamp, DateTimeKind.Unspecified);
                    }

                    break;
            }

            return value;
        }

        private static void SetIfPresent(IDictionary<string, object> values, string column, object value)
        {
            if (!string.IsNullOrWhiteSpace(column)) values[column] = value;
        }

        private static string PreserveJsonArrayOrEmpty(object value)
        {
            var json = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(json) || json.Equals("null", StringComparison.OrdinalIgnoreCase) || json == "{}") return "[]";
            return json;
        }

        private static object GetValue(IReadOnlyDictionary<string, object> row, string name)
        {
            return row.TryGetValue(name, out var value) ? value : null;
        }

        private static string GetString(IReadOnlyDictionary<string, object> row, string name)
        {
            return GetValue(row, name)?.ToString();
        }

        private static int GetInt(IReadOnlyDictionary<string, object> row, string name)
        {
            var value = GetValue(row, name);
            if (value == null) throw new InvalidDataException($"BookshelfNG row is missing required value '{name}'.");
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static int? GetNullableInt(IReadOnlyDictionary<string, object> row, string name)
        {
            var value = GetValue(row, name);
            return value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBoolean(IReadOnlyDictionary<string, object> row, string name)
        {
            var value = GetValue(row, name);
            return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static bool? GetNullableBool(IReadOnlyDictionary<string, object> row, string name)
        {
            var value = GetValue(row, name);
            return value == null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static string Quote(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private sealed record ColumnInfo(bool NotNull, string DefaultValue, bool PrimaryKey, string DataType = null)
        {
            public bool HasDefault => DefaultValue != null;
        }

        private sealed record RootFolderMap(string Path);

        private sealed record AuthorMap(int Id, int MetadataId, List<string> ProviderIds);

        private sealed record BookMap(int SourceId, Dictionary<int, int> TargetIds, string ProviderId, int AuthorId);

        private sealed record EditionMap(
            int Id,
            int SourceBookId,
            int PrimaryMediaType,
            string ProviderId,
            Dictionary<int, EditionTargetMap> TargetsByMediaType);

        private sealed record EditionTargetMap(int Id, int TargetBookId);

        private sealed record BookFileMap(int Id, int SourceBookId, int MediaType, int TargetBookId, int TargetEditionId, string Path);

        private sealed record SeriesMap(int Id, string ProviderId);
    }
}
