using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Moq;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.Indexers.DirectDownload;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Notifications.BookLore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class BookshelfDatabaseImporterFixture
    {
        private string _directory;
        private string _sourcePath;
        private string _sourceConnectionString;
        private string _previousBookshelfUrlBase;
        private string _previousHardcoverMode;
        private string _previousHardcoverNativeMode;
        private string _previousMetadataUrl;

        [SetUp]
        public void SetUp()
        {
            _previousHardcoverMode = Environment.GetEnvironmentVariable("HARDCOVER");
            _previousHardcoverNativeMode = Environment.GetEnvironmentVariable("HARDCOVER_NATIVE");
            _previousBookshelfUrlBase = Environment.GetEnvironmentVariable("BOOKSHELF_URLBASE");
            _previousMetadataUrl = Environment.GetEnvironmentVariable("METADATA_URL");
            Environment.SetEnvironmentVariable("METADATA_URL", null);
            // Most migration fixtures use the legacy Goodreads default
            // explicitly. Tests for ambiguous provider mode clear this value.
            Environment.SetEnvironmentVariable("HARDCOVER", "false");
            Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_URLBASE", null);

            _directory = Path.Combine(Path.GetTempPath(), $"bookshelf-importer-{Guid.NewGuid().ToString("N").Substring(0, 8)}");
            Directory.CreateDirectory(_directory);
            _sourcePath = Path.Combine(_directory, "readarr.db");
            _sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _sourcePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString;

            CreateSourceDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            Environment.SetEnvironmentVariable("HARDCOVER", _previousHardcoverMode);
            Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", _previousHardcoverNativeMode);
            Environment.SetEnvironmentVariable("BOOKSHELF_URLBASE", _previousBookshelfUrlBase);
            Environment.SetEnvironmentVariable("METADATA_URL", _previousMetadataUrl);
        }

        [Test]
        public void should_reserve_quality_profile_ids_deleted_before_source_migration()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO QualityProfiles (Id, Name, Cutoff, Items, UpgradeAllowed)
                    SELECT 127, 'Deleted synthetic profile', Cutoff, Items, UpgradeAllowed
                    FROM QualityProfiles ORDER BY Id LIMIT 1;
                    DELETE FROM QualityProfiles WHERE Id=127;";
                command.ExecuteNonQuery();
            }

            Assert.That(QueryScalar<long>(_sourcePath, "SELECT seq FROM sqlite_sequence WHERE name='QualityProfiles';"), Is.EqualTo(127));
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            Assert.That(QueryScalar<string>(_sourcePath,
                "SELECT Value FROM Config WHERE LOWER(Key)='bookshelfnglegacyqualityprofilesequence';"), Is.EqualTo("127"));
        }

        [Test]
        public void should_reject_out_of_range_source_profile_sequence_without_changing_source()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE sqlite_sequence SET seq=2147483648 WHERE name='QualityProfiles';";
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(_sourcePath);
            Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite));
            Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
        }

        [TestCase(40, "")]
        [TestCase(40, "  Reader One / Reader Two  ")]
        [TestCase(41, "")]
        [TestCase(41, "  Reader One / Reader Two  ")]
        public void should_preserve_raw_narrator_metadata_on_the_original_edition(int editionId, string narrator)
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE Editions SET Narrator=$narrator WHERE Id=$id";
                command.Parameters.AddWithValue("$id", editionId);
                command.Parameters.AddWithValue("$narrator", narrator);
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            Assert.That(QueryScalar<int>(_sourcePath, $"SELECT Narrator IS NULL FROM Editions WHERE Id = {editionId};"), Is.Zero);
            Assert.That(QueryScalar<string>(_sourcePath, $"SELECT Narrator FROM Editions WHERE Id = {editionId};"), Is.EqualTo(narrator));
        }

        [TestCase("Aliases", "[\"Another author name\"]")]
        [TestCase("Images", "[{\"CoverType\":\"Poster\",\"Url\":\"https://catalog.example/author.jpg\"}]")]
        [TestCase("Links", "[{\"Name\":\"Hardcover\",\"Url\":\"https://hardcover.app/authors/fixture\"}]")]
        [TestCase("Genres", "[\"Fantasy\"]")]
        [TestCase("Aliases", "[]")]
        [TestCase("Images", "[]")]
        [TestCase("Links", null)]
        [TestCase("Genres", null)]
        public void should_preserve_author_metadata_arrays_and_default_only_missing_values(string field, string json)
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = field switch
                {
                    "Aliases" => "UPDATE AuthorMetadata SET Aliases = $value WHERE Id = 10;",
                    "Images" => "UPDATE AuthorMetadata SET Images = $value WHERE Id = 10;",
                    "Links" => "UPDATE AuthorMetadata SET Links = $value WHERE Id = 10;",
                    "Genres" => "UPDATE AuthorMetadata SET Genres = $value WHERE Id = 10;",
                    _ => throw new ArgumentOutOfRangeException(nameof(field))
                };
                command.Parameters.AddWithValue("$value", (object)json ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.That(QueryScalar<string>(_sourcePath, $"SELECT {field} FROM Authors WHERE Id = 20;"), Is.EqualTo(json ?? "[]"));
        }

        [Test]
        public void should_preserve_bare_numeric_provider_ids_as_hardcover_ids_when_native_mode_is_enabled()
        {
            SetBareNumericProviderIds();
            Environment.SetEnvironmentVariable("HARDCOVER", "true");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT HardcoverAuthorId FROM Authors WHERE Id = 20;"), Is.EqualTo("hc:10"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT HardcoverBookId FROM Books WHERE Id = 30;"), Is.EqualTo("hc:30"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT ForeignEditionId FROM Editions WHERE Id = 40;"), Is.EqualTo("hc:40"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT HardcoverEditionId FROM Editions WHERE Id = 40;"), Is.EqualTo("40"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT HardcoverSeriesId FROM Series WHERE Id = 70;"), Is.EqualTo("hc:70"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverEnabled';"), Is.EqualTo("true"),
                    "native BookshelfNG Hardcover mode must remain enabled after conversion");
            });
        }

        [Test]
        public void should_keep_bare_numeric_provider_ids_as_goodreads_ids_when_hardcover_mode_is_disabled()
        {
            SetBareNumericProviderIds();
            Environment.SetEnvironmentVariable("HARDCOVER", "false");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT GoodreadsAuthorId FROM Authors WHERE Id = 20;"), Is.EqualTo("gr:10"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT GoodreadsWorkId FROM Books WHERE Id = 30;"), Is.EqualTo("gr:30"));
                Assert.That(QueryScalar<long>(_sourcePath, "SELECT GoodreadsEditionId FROM Editions WHERE Id = 40;"), Is.EqualTo(40));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT GoodreadsSeriesId FROM Series WHERE Id = 70;"), Is.EqualTo("gr:70"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Config WHERE Key = 'HardcoverEnabled' AND Value = 'true';"), Is.Zero,
                    "Goodreads mode must not be changed to native Hardcover during conversion");
            });
        }

        [Test]
        public void should_map_saved_bookshelf_metadata_source_to_chaptarr_metadata_server_url()
        {
            const string metadataUrl = "https://legacy-metadata.example/catalog";
            AddConfigValue("MetadataSource", metadataUrl);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataServerUrl';"), Is.EqualTo(metadataUrl));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataSource';"), Is.EqualTo(metadataUrl),
                    "the legacy setting should remain available for migration audit and rollback");
                Assert.That(QueryScalar<string>(backupPath, "SELECT Value FROM Config WHERE Key = 'MetadataSource';"), Is.EqualTo(metadataUrl),
                    "the immutable source backup should retain the original legacy setting");
            });
        }

        [TestCase(null, "https://api.bookinfo.pro")]
        [TestCase("", "https://api.bookinfo.pro")]
        [TestCase("https://inherited-metadata.example/catalog", "https://inherited-metadata.example/catalog")]
        public void should_retain_effective_source_metadata_fallback_after_environment_is_removed(string environmentValue, string expected)
        {
            Environment.SetEnvironmentVariable("METADATA_URL", environmentValue);
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGMetadataServerUrlFallback';"), Is.EqualTo(expected));
            Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'BookshelfNGMetadataServerUrlFallback';"), Is.Zero);

            var values = new List<Config>();
            using (var connection = new SqliteConnection(_sourceConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Key,Value FROM Config";
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    values.Add(new Config { Key = rows.GetString(0), Value = rows.IsDBNull(1) ? null : rows.GetString(1) });
                }
            }

            Environment.SetEnvironmentVariable("METADATA_URL", null);
            var repository = new Mock<IConfigRepository>();
            repository.Setup(repo => repo.All()).Returns(values);
            var service = new ConfigService(repository.Object, null, null, LogManager.GetCurrentClassLogger());
            Assert.That(service.MetadataServerUrl, Is.EqualTo(expected));
        }

        [Test]
        public void should_preserve_explicit_chaptarr_metadata_server_url_over_bookshelf_metadata_source()
        {
            AddConfigValue("MetadataSource", "https://legacy-metadata.example/catalog");
            AddConfigValue("MetadataServerUrl", "https://configured-chaptarr.example/catalog");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataServerUrl';"), Is.EqualTo("https://configured-chaptarr.example/catalog"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataSource';"), Is.EqualTo("https://legacy-metadata.example/catalog"));
            });
        }

        [Test]
        public void should_report_unmapped_provider_settings_in_preview_and_refuse_to_apply_them()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                    VALUES (123, 'Plugin fixture', 'FixturePluginIndexer', '{}', 'FixturePluginSettings', 1, 1, 1, 25, '[]', 0);
                    INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                    VALUES (130, 'Legacy Torznab fixture', 'Torznab', '{""baseUrl"":""https://indexer.example"",""apiKey"":""fixture-indexer-key"",""legacyIndexerSecret"":""fixture-indexer-secret""}', 'TorznabSettings', 1, 1, 1, 25, '[]', 0);
                    INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                    VALUES (136, 'Legacy Required MAM fixture', 'MyAnonaMouse', '{""baseUrl"":""https://www.myanonamouse.net"",""mamId"":""fixture-required-mam-secret"",""useFreeleechWedge"":2}', 'MyAnonaMouseSettings', 1, 1, 1, 25, '[]', 0);
                    INSERT INTO DownloadClients (Id, Enable, Name, Implementation, Settings, ConfigContract)
                    VALUES (124, 1, 'Plugin download fixture', 'FixturePluginDownload', '{}', 'FixturePluginDownloadSettings');
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (125, 'Legacy Hardcover fixture', 'HardcoverImport', '{}', 'HardcoverImportSettings', 0, '/library', 0, 1, 1, '[]');
                    INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                    VALUES (126, 'Legacy Goodreads owned notification', 1, '{}', 'GoodreadsOwnedBooks', 'GoodreadsOwnedBooksNotificationSettings', 1, '[]', 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0);
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (127, 'Legacy Goodreads owned list', 'GoodreadsOwnedBooks', '{}', 'GoodreadsOwnedBooksImportListSettings', 0, '/library', 0, 1, 1, '[]');
                    INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                    VALUES (128, 'Legacy Goodreads bookshelf notification', 1, '{}', 'GoodreadsBookshelf', 'GoodreadsBookshelfNotificationSettings', 1, '[]', 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0);
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (129, 'Legacy Goodreads shelf list', 'GoodreadsBookshelf', '{""userId"":""12345678"",""bookshelfIds"":[""to-read""],""accessToken"":""fixture-goodreads-access-secret"",""accessTokenSecret"":""fixture-goodreads-secret"",""requestTokenSecret"":""fixture-request-secret"",""userName"":""fixture-user"",""signIn"":""startOAuth"",""signingUrl"":""https://auth.servarr.com/v1/goodreads/sign"",""oauthUrl"":""https://www.goodreads.com/oauth/authorize"",""oauthRequestTokenUrl"":""https://www.goodreads.com/oauth/request_token"",""oauthAccessTokenUrl"":""https://www.goodreads.com/oauth/access_token""}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 1, 1, 1, '[]');
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (131, 'Legacy public Goodreads shelf list', 'GoodreadsBookshelf', '{""userId"":""12345678"",""bookshelfIds"":[""to-read""],""accessToken"":"""",""accessTokenSecret"":"""",""requestTokenSecret"":"""",""userName"":"""",""signIn"":""startOAuth"",""signingUrl"":""https://auth.servarr.com/v1/goodreads/sign"",""oauthUrl"":""https://www.goodreads.com/oauth/authorize"",""oauthRequestTokenUrl"":""https://www.goodreads.com/oauth/request_token"",""oauthAccessTokenUrl"":""https://www.goodreads.com/oauth/access_token""}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 1, 1, 1, '[]');
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (132, 'Legacy Goodreads partial OAuth shelf list', 'GoodreadsBookshelf', '{""userId"":""12345678"",""bookshelfIds"":[""read""],""accessToken"":""fixture-partial-goodreads-token""}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 1, 1, 1, '[]');
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (133, 'Legacy Goodreads shelf without an account', 'GoodreadsBookshelf', '{""bookshelfIds"":[""read""]}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 1, 1, 1, '[]');
                    INSERT INTO Metadata (Id, Enable, Name, Implementation, Settings, ConfigContract)
                    VALUES (134, 1, 'Legacy OPF sidecar', 'AudiobookshelfMetadata', '{}', 'NullConfig');
                    INSERT INTO Metadata (Id, Enable, Name, Implementation, Settings, ConfigContract)
                    VALUES (135, 1, 'Unmapped metadata plugin', 'FixturePluginMetadata', '{""apiKey"":""fixture-metadata-secret""}', 'FixturePluginMetadataSettings');
                    INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Extension)
                    VALUES (66, 20, 'FixturePluginMetadata', 0, 'plugin.sidecar', '2026-01-05', 30, NULL, 'sidecar');";
                command.ExecuteNonQuery();
            }

            var sourceBefore = File.ReadAllBytes(_sourcePath);
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var preview = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(preview.ImportReport.BlockingIssues, Has.Count.EqualTo(10));
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("FixturePluginIndexer/FixturePluginSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue =>
                    issue.Contains("Torznab/TorznabSettings", StringComparison.Ordinal) &&
                    issue.Contains("legacyIndexerSecret", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue =>
                    issue.Contains("row 136: MyAnonaMouse/MyAnonaMouseSettings", StringComparison.Ordinal)), Is.False,
                    "the legacy Required wedge option has equivalent behavior in ChaptarrNG");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("fixture-indexer-secret", StringComparison.Ordinal)), Is.False,
                    "provider settings and credentials must not be included in the report");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("fixture-required-mam-secret", StringComparison.Ordinal)), Is.False,
                    "provider settings and credentials must not be included in the report");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("FixturePluginDownload/FixturePluginDownloadSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue =>
                    issue.Contains("LegacyAudiobookshelfMetadata/NullConfig", StringComparison.Ordinal)), Is.False,
                    "the legacy OPF metadata provider is mapped by its exact implementation and contract");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue =>
                    issue.Contains("FixturePluginMetadata/FixturePluginMetadataSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue =>
                    issue.Contains("FixturePluginMetadata", StringComparison.Ordinal) &&
                    issue.Contains("metadata consumer", StringComparison.OrdinalIgnoreCase)), Is.True,
                    "unmapped sidecar consumers must block apply");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("fixture-metadata-secret", StringComparison.Ordinal)), Is.False,
                    "metadata provider settings and credentials must not be included in the report");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("HardcoverImport/HardcoverImportSettings", StringComparison.Ordinal)), Is.False,
                    "the migrated Hardcover public-list provider is available in Chaptarr");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("GoodreadsOwnedBooks/GoodreadsOwnedBooksNotificationSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("GoodreadsBookshelf/GoodreadsBookshelfNotificationSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("GoodreadsOwnedBooks/GoodreadsOwnedBooksImportListSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue =>
                    issue.Contains("GoodreadsBookshelf/GoodreadsBookshelfImportListSettings", StringComparison.Ordinal)), Is.True);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("row 129:", StringComparison.Ordinal)), Is.False,
                    "complete legacy OAuth settings are supported by the migration candidate");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("row 132:", StringComparison.Ordinal) &&
                    issue.Contains("accessTokenSecret", StringComparison.Ordinal)), Is.True,
                    "partial OAuth credentials must block migration without exposing their values");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("row 133:", StringComparison.Ordinal) &&
                    issue.Contains("userId or OAuth credentials", StringComparison.Ordinal)), Is.True,
                    "public shelves without an account ID or complete OAuth credentials must block migration");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("row 131:", StringComparison.Ordinal)), Is.False,
                    "empty OAuth fields and fixed Goodreads runtime settings do not block public-shelf migration");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("fixture-goodreads-access-secret", StringComparison.Ordinal)), Is.False,
                    "provider settings and credentials must not be included in the report");
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("fixture-partial-goodreads-token", StringComparison.Ordinal)), Is.False,
                    "provider settings and credentials must not be included in the report");
                Assert.That(preview.ImportReport.Tables["DownloadClients"].TargetMapping, Does.Contain("block apply"));
                Assert.That(preview.ImportReport.Tables["ImportLists"].TargetMapping, Does.Contain("block apply"));
                Assert.That(preview.ImportReport.Tables["Notifications"].TargetMapping, Does.Contain("block apply"));
                Assert.That(preview.ImportReport.Tables["Indexers"].TargetMapping, Does.Contain("block apply"));
                Assert.That(preview.ImportReport.Tables["Metadata"].TargetMapping, Does.Contain("block apply"));
                Assert.That(preview.ImportReport.Tables["MetadataFiles"].TargetMapping, Does.Contain("block apply"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBefore), "preview must leave source bytes unchanged");
            });

            var exception = Assert.Throws<InvalidDataException>(() => migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("cannot be applied"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBefore), "a blocked apply must leave source bytes unchanged");
            });
        }

        [Test]
        public void should_preserve_goodreads_oauth_provider_settings_during_bookshelf_conversion()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = """
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (201, 'Legacy Goodreads OAuth shelf', 'GoodreadsBookshelf', '{"userId":"12345678","bookshelfIds":["to-read"],"accessToken":"fixture-access-token","accessTokenSecret":"fixture-access-secret","requestTokenSecret":"fixture-request-secret","userName":"fixture-user","signIn":"startOAuth","signingUrl":"https://auth.servarr.com/v1/goodreads/sign","oauthUrl":"https://www.goodreads.com/oauth/authorize","oauthRequestTokenUrl":"https://www.goodreads.com/oauth/request_token","oauthAccessTokenUrl":"https://www.goodreads.com/oauth/access_token"}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 1, 1, 1, '[]');
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (202, 'Legacy Goodreads public shelf', 'GoodreadsBookshelf', '{"userId":"12345678","bookshelfIds":["to-read"],"accessToken":"","accessTokenSecret":"","requestTokenSecret":"","userName":"","signIn":"startOAuth","signingUrl":"https://auth.servarr.com/v1/goodreads/sign","oauthUrl":"https://www.goodreads.com/oauth/authorize","oauthRequestTokenUrl":"https://www.goodreads.com/oauth/request_token","oauthAccessTokenUrl":"https://www.goodreads.com/oauth/access_token"}', 'GoodreadsBookshelfImportListSettings', 1, '/library', 1, 1, 1, '[]');
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (203, 'Legacy Goodreads owned list', 'GoodreadsOwnedBooks', '{"userId":"12345678","accessToken":"fixture-owned-access-token","accessTokenSecret":"fixture-owned-access-secret","requestTokenSecret":"fixture-owned-request-secret","userName":"fixture-owned-user","signIn":"startOAuth"}', 'GoodreadsOwnedBooksImportListSettings', 0, '/library', 0, 1, 1, '[]');
                    INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                    VALUES (204, 'Legacy Goodreads bookshelf notification', 0, '{"accessToken":"fixture-notify-access-token","accessTokenSecret":"fixture-notify-access-secret","requestTokenSecret":"fixture-notify-request-secret","userId":"12345678","userName":"fixture-notify-user","addIds":["read"],"removeIds":["to-read"],"signIn":"startOAuth"}', 'GoodreadsBookshelf', 'GoodreadsBookshelfNotificationSettings', 0, '[]', 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                    INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                    VALUES (205, 'Legacy Goodreads owned notification', 0, '{"accessToken":"fixture-owned-notify-token","accessTokenSecret":"fixture-owned-notify-secret","requestTokenSecret":"fixture-owned-notify-request","userId":"12345678","userName":"fixture-owned-notify-user","condition":10,"description":"","location":"","signIn":"startOAuth"}', 'GoodreadsOwnedBooks', 'GoodreadsOwnedBooksNotificationSettings', 0, '[]', 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                    """;
                command.ExecuteNonQuery();
            }

            var sourceBeforePreview = File.ReadAllBytes(_sourcePath);
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var preview = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(preview.ImportReport.BlockingIssues, Is.Empty);
                Assert.That(preview.ImportReport.BlockingIssues.Any(issue => issue.Contains("fixture-access-secret", StringComparison.Ordinal)), Is.False);
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBeforePreview));
            });

            migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT json_extract(Settings, '$.accessToken') FROM ImportLists WHERE Id = 201;"), Is.EqualTo("fixture-access-token"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT json_extract(Settings, '$.accessTokenSecret') FROM ImportLists WHERE Id = 201;"), Is.EqualTo("fixture-access-secret"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT json_extract(Settings, '$.accessTokenSecret') FROM ImportLists WHERE Id = 203;"), Is.EqualTo("fixture-owned-access-secret"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT json_extract(Settings, '$.accessTokenSecret') FROM Notifications WHERE Id = 204;"), Is.EqualTo("fixture-notify-access-secret"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT json_extract(Settings, '$.accessTokenSecret') FROM Notifications WHERE Id = 205;"), Is.EqualTo("fixture-owned-notify-secret"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT json_extract(Settings, '$.accessTokenSecret') FROM ImportLists WHERE Id = 202;"), Is.EqualTo(string.Empty));
            });
        }

        [Test]
        public void should_preserve_hardcover_public_list_provider_settings()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (111, 'Hardcover public list', 'HardcoverImport', '{""apiKey"":""fixture-token"",""user"":""fixture-user"",""listIds"":[""fixture-list""]}', 'HardcoverImportSettings', 1, '/library', 1, 1, 1, '[]');";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Implementation FROM ImportLists WHERE Id = 111;"), Is.EqualTo("HardcoverImport"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM ImportLists WHERE Id = 111;"), Is.EqualTo("HardcoverImportSettings"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Settings FROM ImportLists WHERE Id = 111;"), Does.Contain("fixture-token"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Settings FROM ImportLists WHERE Id = 111;"), Does.Contain("fixture-list"));
            });
        }

        [Test]
        public void should_map_bookshelf_readarr_import_list_to_chaptarr_with_its_saved_settings()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = """
                    INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags)
                    VALUES (137, 'Legacy Readarr library', 'ReadarrImport', '{"baseUrl":"https://bookshelf.example","apiKey":"fixture-readarr-api-key","profileIds":[1],"tagIds":[3],"rootFolderPaths":["/legacy-library"]}', 'ReadarrSettings', 1, '/legacy-library', 1, 1, 1, '[3]');
                    """;
                command.ExecuteNonQuery();
            }

            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var preview = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);

            Assert.That(preview.ImportReport.BlockingIssues, Is.Empty,
                "the legacy provider name and settings contract have equivalent Chaptarr implementations");

            migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Implementation FROM ImportLists WHERE Id = 137;"), Is.EqualTo("ChaptarrImport"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM ImportLists WHERE Id = 137;"), Is.EqualTo("ChaptarrSettings"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT EnableAutomaticAdd FROM ImportLists WHERE Id = 137;"), Is.EqualTo(1));
                var settings = QueryScalar<string>(_sourcePath, "SELECT Settings FROM ImportLists WHERE Id = 137;");
                var migratedSettings = NzbDrone.Common.Serializer.STJson.Deserialize<ChaptarrSettings>(settings);
                Assert.That(migratedSettings.BaseUrl, Is.EqualTo("https://bookshelf.example"));
                Assert.That(migratedSettings.ApiKey, Is.EqualTo("fixture-readarr-api-key"));
                Assert.That(migratedSettings.ProfileIds, Is.EqualTo(new[] { 1 }));
                Assert.That(migratedSettings.TagIds, Is.EqualTo(new[] { 3 }));
                Assert.That(migratedSettings.RootFolderPaths, Is.EqualTo(new[] { "/legacy-library" }));
            });
        }

        [Test]
        public void should_preserve_every_builtin_bookshelf_indexer_with_its_matching_contract()
        {
            (string Implementation, string Contract, string Settings)[] providers =
            {
                ("DirectDownloadIndexer", "DirectDownloadSettings", "{\"urls\":\"https://direct.example\",\"apiKey\":\"fixture-direct-key\",\"enableSlowFallback\":true}"),
                ("FileList", "FileListSettings", "{\"username\":\"fixture-filelist-user\",\"passkey\":\"fixture-filelist-key\",\"categories\":[7,12]}"),
                ("Gazelle", "GazelleSettings", "{\"baseUrl\":\"https://gazelle.example\",\"username\":\"fixture-gazelle-user\",\"password\":\"fixture-gazelle-password\",\"useFreeleechToken\":true}"),
                ("IPTorrents", "IPTorrentsSettings", "{\"baseUrl\":\"https://iptorrents.example\",\"minimumSeeders\":3}"),
                ("MyAnonaMouse", "MyAnonaMouseSettings", "{\"baseUrl\":\"https://www.myanonamouse.net\",\"mamId\":\"fixture-mam-token\",\"searchType\":2,\"searchInDescription\":true}"),
                ("Newznab", "NewznabSettings", "{\"baseUrl\":\"https://newznab.example\",\"apiPath\":\"/api\",\"apiKey\":\"fixture-newznab-key\",\"categories\":[7020,8000]}"),
                ("Nyaa", "NyaaSettings", "{\"baseUrl\":\"https://nyaa.example\",\"additionalParameters\":\"trusted=1\"}"),
                ("TorrentRssIndexer", "TorrentRssIndexerSettings", "{\"baseUrl\":\"https://rss.example/feed\",\"cookie\":\"fixture-rss-cookie\",\"allowZeroSize\":true}"),
                ("Torrentleech", "TorrentleechSettings", "{\"baseUrl\":\"https://torrentleech.example\",\"apiKey\":\"fixture-torrentleech-key\"}"),
                ("Torznab", "TorznabSettings", "{\"baseUrl\":\"https://torznab.example\",\"apiPath\":\"/api\",\"apiKey\":\"fixture-torznab-key\",\"minimumSeeders\":2}")
            };

            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();

                for (var index = 0; index < providers.Length; index++)
                {
                    using var command = source.CreateCommand();
                    command.CommandText = @"
                        INSERT INTO Indexers (Id, Name, Implementation, Settings, ConfigContract, EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Priority, Tags, DownloadClientId)
                        VALUES ($id, $name, $implementation, $settings, $contract, 1, 1, 1, 25, '[]', 0);";
                    command.Parameters.AddWithValue("$id", 900 + index);
                    command.Parameters.AddWithValue("$name", $"Built-in provider {providers[index].Implementation}");
                    command.Parameters.AddWithValue("$implementation", providers[index].Implementation);
                    command.Parameters.AddWithValue("$settings", providers[index].Settings);
                    command.Parameters.AddWithValue("$contract", providers[index].Contract);
                    command.ExecuteNonQuery();
                }
            }

            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var preview = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);

            Assert.That(preview.ImportReport.BlockingIssues, Is.Empty,
                "all built-in BookshelfNG indexer names and contracts have an equivalent Chaptarr provider");

            migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                for (var index = 0; index < providers.Length; index++)
                {
                    var (implementation, contract, settings) = providers[index];
                    Assert.That(QueryScalar<int>(
                        _sourcePath,
                        $"SELECT COUNT(*) FROM Indexers WHERE Id = {900 + index} AND Implementation = '{implementation}' AND ConfigContract = '{contract}';"),
                        Is.EqualTo(1),
                        $"{implementation} implementation and contract should survive migration");
                    var migrated = JObject.Parse(QueryScalar<string>(_sourcePath, $"SELECT Settings FROM Indexers WHERE Id = {900 + index};"));
                    foreach (var property in JObject.Parse(settings).Properties())
                    {
                        Assert.That(JToken.DeepEquals(migrated.GetValue(property.Name, StringComparison.OrdinalIgnoreCase), property.Value),
                            Is.True, $"{implementation} must retain explicit {property.Name}");
                    }
                }
            });
        }

        [Test]
        public void should_preserve_every_builtin_bookshelf_download_client_import_list_and_notification()
        {
            (string Implementation, string Contract)[] downloadClientTypes =
            {
                ("Aria2", "Aria2Settings"),
                ("TorrentBlackhole", "TorrentBlackholeSettings"),
                ("UsenetBlackhole", "UsenetBlackholeSettings"),
                ("Deluge", "DelugeSettings"),
                ("TorrentDownloadStation", "DownloadStationSettings"),
                ("UsenetDownloadStation", "DownloadStationSettings"),
                ("Flood", "FloodSettings"),
                ("Hadouken", "HadoukenSettings"),
                ("NzbVortex", "NzbVortexSettings"),
                ("Nzbget", "NzbgetSettings"),
                ("Pneumatic", "PneumaticSettings"),
                ("QBittorrent", "QBittorrentSettings"),
                ("Sabnzbd", "SabnzbdSettings"),
                ("RTorrent", "RTorrentSettings"),
                ("UTorrent", "UTorrentSettings")
            };
            (string Implementation, string Contract)[] importListTypes =
            {
                ("GoodreadsBookshelf", "GoodreadsBookshelfImportListSettings"),
                ("GoodreadsListImportList", "GoodreadsListImportListSettings"),
                ("GoodreadsOwnedBooks", "GoodreadsOwnedBooksImportListSettings"),
                ("GoodreadsSeriesImportList", "GoodreadsSeriesImportListSettings"),
                ("HardcoverImport", "HardcoverImportSettings"),
                ("LazyLibrarianImport", "LazyLibrarianImportSettings")
            };
            (string Implementation, string Contract)[] notificationTypes =
            {
                ("Apprise", "AppriseSettings"),
                ("BookLore", "BookLoreSettings"),
                ("CustomScript", "CustomScriptSettings"),
                ("Discord", "DiscordSettings"),
                ("Email", "EmailSettings"),
                ("GoodreadsBookshelf", "GoodreadsBookshelfNotificationSettings"),
                ("GoodreadsOwnedBooks", "GoodreadsOwnedBooksNotificationSettings"),
                ("Gotify", "GotifySettings"),
                ("Join", "JoinSettings"),
                ("Kavita", "KavitaSettings"),
                ("MailGun", "MailgunSettings"),
                ("Ntfy", "NtfySettings"),
                ("PlexServer", "PlexServerSettings"),
                ("Prowl", "ProwlSettings"),
                ("PushBullet", "PushBulletSettings"),
                ("Pushover", "PushoverSettings"),
                ("SendGrid", "SendGridSettings"),
                ("Signal", "SignalSettings"),
                ("Simplepush", "SimplepushSettings"),
                ("Slack", "SlackSettings"),
                ("Subsonic", "SubsonicSettings"),
                ("SynologyIndexer", "SynologyIndexerSettings"),
                ("Telegram", "TelegramSettings"),
                ("Twitter", "TwitterSettings")
            };

            var downloadClients = downloadClientTypes
                .Select((provider, index) => (Id: 900 + index, provider.Implementation, provider.Contract,
                    Settings: CreateBuiltInProviderSettings(provider.Implementation, provider.Contract)))
                .ToArray();
            var importLists = importListTypes
                .Select((provider, index) => (Id: 1000 + index, provider.Implementation, provider.Contract,
                    Settings: CreateBuiltInProviderSettings(provider.Implementation, provider.Contract)))
                .ToArray();
            var notifications = notificationTypes
                .Select((provider, index) => (Id: 1100 + index, provider.Implementation, provider.Contract,
                    Settings: CreateBuiltInProviderSettings(provider.Implementation, provider.Contract)))
                .ToArray();

            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();

                foreach (var provider in downloadClients)
                {
                    using var command = source.CreateCommand();
                    command.CommandText = "INSERT INTO DownloadClients (Id, Enable, Name, Implementation, Settings, ConfigContract) VALUES ($id, 1, $name, $implementation, $settings, $contract);";
                    command.Parameters.AddWithValue("$id", provider.Id);
                    command.Parameters.AddWithValue("$name", $"Built-in download client {provider.Implementation}");
                    command.Parameters.AddWithValue("$implementation", provider.Implementation);
                    command.Parameters.AddWithValue("$settings", provider.Settings);
                    command.Parameters.AddWithValue("$contract", provider.Contract);
                    command.ExecuteNonQuery();
                }

                foreach (var provider in importLists)
                {
                    using var command = source.CreateCommand();
                    command.CommandText = "INSERT INTO ImportLists (Id, Name, Implementation, Settings, ConfigContract, EnableAutomaticAdd, RootFolderPath, ShouldMonitor, ProfileId, MetadataProfileId, Tags) VALUES ($id, $name, $implementation, $settings, $contract, 1, '/library', 1, 1, 1, '[]');";
                    command.Parameters.AddWithValue("$id", provider.Id);
                    command.Parameters.AddWithValue("$name", $"Built-in import list {provider.Implementation}");
                    command.Parameters.AddWithValue("$implementation", provider.Implementation);
                    command.Parameters.AddWithValue("$settings", provider.Settings);
                    command.Parameters.AddWithValue("$contract", provider.Contract);
                    command.ExecuteNonQuery();
                }

                foreach (var provider in notifications)
                {
                    using var command = source.CreateCommand();
                    command.CommandText = """
                        INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                        VALUES ($id, $name, 1, $settings, $implementation, $contract, 1, '[]', 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0);
                        """;
                    command.Parameters.AddWithValue("$id", provider.Id);
                    command.Parameters.AddWithValue("$name", $"Built-in notification {provider.Implementation}");
                    command.Parameters.AddWithValue("$implementation", provider.Implementation);
                    command.Parameters.AddWithValue("$settings", provider.Settings);
                    command.Parameters.AddWithValue("$contract", provider.Contract);
                    command.ExecuteNonQuery();
                }
            }

            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var preview = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);

            Assert.That(preview.ImportReport.BlockingIssues, Is.Empty,
                "all built-in BookshelfNG provider implementation and settings contracts have an equivalent Chaptarr provider");

            migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                foreach (var provider in downloadClients)
                {
                    AssertPreservedProviderSettings("DownloadClients", provider.Id, provider.Implementation, provider.Contract, provider.Settings);
                }

                foreach (var provider in importLists)
                {
                    Assert.That(ReadMigratedProvider(_sourcePath, "ImportLists", provider.Id),
                        Is.EqualTo((provider.Implementation, provider.Contract, provider.Settings)),
                        $"{provider.Implementation} import-list settings should survive migration");
                }

                foreach (var provider in notifications)
                {
                    AssertPreservedProviderSettings("Notifications", provider.Id, provider.Implementation, provider.Contract, provider.Settings);
                }
            });
        }

        private void AssertPreservedProviderSettings(string table, int id, string implementation, string contract, string sourceSettings)
        {
            var migrated = ReadMigratedProvider(_sourcePath, table, id);
            Assert.That((migrated.Implementation, migrated.Contract), Is.EqualTo((implementation, contract)));
            var settings = JObject.Parse(migrated.Settings);
            foreach (var property in JObject.Parse(sourceSettings).Properties())
            {
                Assert.That(JToken.DeepEquals(settings.GetValue(property.Name, StringComparison.OrdinalIgnoreCase), property.Value),
                    Is.True, $"{implementation} must preserve explicit {property.Name}, including null and empty values");
            }
        }

        [Test]
        public void should_migrate_effective_omitted_provider_defaults_without_overwriting_explicit_values()
        {
            var clients = new[]
            {
                ("Deluge", "readarr"), ("QBittorrent", "readarr"), ("RTorrent", "readarr"), ("UTorrent", "readarr"),
                ("Nzbget", "Readarr"), ("NzbVortex", "Readarr"), ("Sabnzbd", "Readarr"),
                ("Hadouken", "readarr-music"), ("Flood", "readarr")
            };
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                for (var index = 0; index < clients.Length; index++)
                {
                    using var command = source.CreateCommand();
                    command.CommandText = "INSERT INTO DownloadClients (Id, Enable, Name, Implementation, Settings, ConfigContract) VALUES ($id, 1, $name, $name, $settings, $contract);";
                    command.Parameters.AddWithValue("$id", 900 + index);
                    command.Parameters.AddWithValue("$name", clients[index].Item1);
                    command.Parameters.AddWithValue("$contract", clients[index].Item1 + "Settings");
                    command.Parameters.AddWithValue("$settings", "{}");
                    command.ExecuteNonQuery();
                }

                using var update = source.CreateCommand();
                update.CommandText = """
                    UPDATE Indexers SET Settings = '{"mamId":"fixture-session"}' WHERE Id = 120;
                    UPDATE Notifications SET Implementation = 'PlexServer', ConfigContract = 'PlexServerSettings',
                        Settings = '{"host":"plex.example","authToken":"fixture-token","updateLibrary":false}' WHERE Id = 111;
                    UPDATE DownloadClients SET Settings = '{"MusicCategory":"","password":null,"audiobookCategory":"operator-audio"}' WHERE Id = 109;
                    """;
                update.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null)
                .MigrateBookshelf(_sourceConnectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite, previewOnly: false);

            for (var index = 0; index < clients.Length; index++)
            {
                var migrated = ReadMigratedProvider(_sourcePath, "DownloadClients", 900 + index);
                var type = typeof(BookshelfDatabaseImporter).Assembly.GetTypes().Single(t => t.Name == migrated.Contract);
                var settings = NzbDrone.Common.Serializer.STJson.Deserialize(migrated.Settings, type);
                if (settings is NzbDrone.Core.Download.Clients.IBookDownloadCategorySettings categories)
                {
                    Assert.That(categories.AudiobookCategory, Is.EqualTo(clients[index].Item2));
                    Assert.That(categories.EbookCategory, Is.EqualTo(clients[index].Item2));
                }
                else if (settings is NzbDrone.Core.Download.Clients.Hadouken.HadoukenSettings hadouken)
                {
                    Assert.That(hadouken.Category, Is.EqualTo("readarr-music"));
                }
                else if (settings is NzbDrone.Core.Download.Clients.Flood.FloodSettings flood)
                {
                    Assert.That(flood.AudiobookTags, Is.EqualTo(new[] { "readarr" }));
                    Assert.That(flood.EbookTags, Is.EqualTo(new[] { "readarr" }));
                }
            }

            var explicitSettings = NzbDrone.Common.Serializer.STJson.Deserialize<QBittorrentSettings>(
                ReadMigratedProvider(_sourcePath, "DownloadClients", 109).Settings);
            Assert.That(explicitSettings.MusicCategory, Is.Empty);
            Assert.That(explicitSettings.EbookCategory, Is.Empty);
            Assert.That(explicitSettings.AudiobookCategory, Is.EqualTo("operator-audio"));
            Assert.That(explicitSettings.Password, Is.Null);
            var mam = NzbDrone.Common.Serializer.STJson.Deserialize<MyAnonaMouseSettings>(
                QueryScalar<string>(_sourcePath, "SELECT Settings FROM Indexers WHERE Id = 120;"));
            Assert.That(mam.SearchType, Is.EqualTo(0));
            Assert.That(mam.SearchInSeries, Is.False);
            Assert.That(mam.MamId, Is.EqualTo("fixture-session"));
            var plex = NzbDrone.Common.Serializer.STJson.Deserialize<NzbDrone.Core.Notifications.Plex.Server.PlexServerSettings>(
                ReadMigratedProvider(_sourcePath, "Notifications", 111).Settings);
            Assert.That(plex.UseSsl, Is.False);
            Assert.That(plex.UpdateLibrary, Is.False);
        }

        private static string CreateBuiltInProviderSettings(string implementation, string contract)
        {
            if (implementation == "GoodreadsBookshelf" && contract == "GoodreadsBookshelfImportListSettings")
            {
                return "{\"userId\":\"12345678\",\"bookshelfIds\":[\"to-read\"],\"accessToken\":\"fixture-access-token\",\"accessTokenSecret\":\"fixture-access-secret\",\"requestTokenSecret\":\"fixture-request-secret\",\"userName\":\"fixture-user\",\"signIn\":\"startOAuth\",\"signingUrl\":\"https://auth.servarr.com/v1/goodreads/sign\",\"oauthUrl\":\"https://www.goodreads.com/oauth/authorize\",\"oauthRequestTokenUrl\":\"https://www.goodreads.com/oauth/request_token\",\"oauthAccessTokenUrl\":\"https://www.goodreads.com/oauth/access_token\"}";
            }

            if (implementation == "GoodreadsOwnedBooks" && contract == "GoodreadsOwnedBooksImportListSettings")
            {
                return "{\"userId\":\"12345678\",\"accessToken\":\"fixture-owned-access-token\",\"accessTokenSecret\":\"fixture-owned-access-secret\",\"requestTokenSecret\":\"fixture-owned-request-secret\",\"userName\":\"fixture-owned-user\",\"signIn\":\"startOAuth\"}";
            }

            if (implementation == "GoodreadsBookshelf" && contract == "GoodreadsBookshelfNotificationSettings")
            {
                return "{\"accessToken\":\"fixture-notify-access-token\",\"accessTokenSecret\":\"fixture-notify-access-secret\",\"requestTokenSecret\":\"fixture-notify-request\",\"userId\":\"12345678\",\"userName\":\"fixture-notify-user\",\"addIds\":[\"read\"],\"removeIds\":[\"to-read\"],\"signIn\":\"startOAuth\"}";
            }

            if (implementation == "GoodreadsOwnedBooks" && contract == "GoodreadsOwnedBooksNotificationSettings")
            {
                return "{\"accessToken\":\"fixture-owned-notify-token\",\"accessTokenSecret\":\"fixture-owned-notify-secret\",\"requestTokenSecret\":\"fixture-owned-notify-request\",\"userId\":\"12345678\",\"userName\":\"fixture-owned-notify-user\",\"condition\":10,\"description\":\"\",\"location\":\"\",\"signIn\":\"startOAuth\"}";
            }

            var settingsType = typeof(BookshelfDatabaseImporter).Assembly.GetTypes()
                .Single(type => type.Name.Equals(contract, StringComparison.OrdinalIgnoreCase));
            var setting = settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(property =>
                    property.PropertyType == typeof(string) &&
                    property.CanWrite &&
                    property.GetCustomAttribute<JsonIgnoreAttribute>() == null &&
                    property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null);
            if (setting == null)
            {
                return "{}";
            }

            var jsonName = setting.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ??
                setting.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name ??
                setting.Name;
            return new JObject { [jsonName] = "fixture-provider-value" }.ToString(Formatting.None);
        }

        private static (string Implementation, string Contract, string Settings) ReadMigratedProvider(
            string sourcePath,
            string table,
            int id)
        {
            using var source = new SqliteConnection($"Data Source={sourcePath}");
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = table switch
            {
                "DownloadClients" => "SELECT Implementation, ConfigContract, Settings FROM DownloadClients WHERE Id = $id;",
                "ImportLists" => "SELECT Implementation, ConfigContract, Settings FROM ImportLists WHERE Id = $id;",
                "Notifications" => "SELECT Implementation, ConfigContract, Settings FROM Notifications WHERE Id = $id;",
                _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unexpected built-in provider table fixture")
            };
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException($"Migrated provider row {table}/{id} is missing.");
            }

            return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }

        [Test]
        public void should_reject_bare_numeric_provider_ids_when_provider_mode_is_missing()
        {
            SetBareNumericProviderIds();
            Environment.SetEnvironmentVariable("HARDCOVER", null);
            var sourceBytes = File.ReadAllBytes(_sourcePath);

            var error = Assert.Throws<InvalidDataException>(() =>
                new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(error!.Message, Does.Contain("explicit HARDCOVER=true or HARDCOVER=false"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBytes), "ambiguous source bytes must remain unchanged");
            });
        }

        [Test]
        public void should_replace_target_naming_defaults_when_bookshelf_has_saved_settings()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO NamingConfig (Id, ReplaceIllegalCharacters, AuthorFolderFormat, RenameBooks, StandardBookFormat, ColonReplacementFormat)
                    VALUES (5, 0, '{Author NameLastFirst}', 1, '{Book Title} custom', 4);";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM NamingConfig;"), Is.EqualTo(1),
                    "saved BookshelfNG settings must replace the seeded Chaptarr defaults");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT MultiEpisodeStyle FROM NamingConfig WHERE Id = 5;"), Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT ReplaceIllegalCharacters FROM NamingConfig WHERE Id = 5;"), Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT RenameBooks FROM NamingConfig WHERE Id = 5;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT AuthorFolderFormat FROM NamingConfig WHERE Id = 5;"), Is.EqualTo("{Author NameLastFirst}"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT StandardBookFormat FROM NamingConfig WHERE Id = 5;"), Is.EqualTo("{Book Title} custom"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT EbookRenameBooks FROM NamingConfig WHERE Id = 5;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT EbookReplaceIllegalCharacters FROM NamingConfig WHERE Id = 5;"), Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT EbookColonReplacementFormat FROM NamingConfig WHERE Id = 5;"), Is.EqualTo(4));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT EbookAuthorFolderFormat FROM NamingConfig WHERE Id = 5;"), Is.EqualTo("{Author NameLastFirst}"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT EbookStandardBookFormat FROM NamingConfig WHERE Id = 5;"), Is.EqualTo("{Book Title} custom"));
            });
        }

        [Test]
        public void should_preserve_bookshelf_naming_defaults_when_no_settings_are_saved()
        {
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            var expectedFormat = "{Book Title}" + Path.DirectorySeparatorChar + "{Author Name} - {Book Title}{ (PartNumber)}";

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM NamingConfig;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT Id FROM NamingConfig;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT RenameBooks FROM NamingConfig;"), Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT ReplaceIllegalCharacters FROM NamingConfig;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT ColonReplacementFormat FROM NamingConfig;"), Is.EqualTo(4));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT AuthorFolderFormat FROM NamingConfig;"), Is.EqualTo("{Author Name}"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT StandardBookFormat FROM NamingConfig;"), Is.EqualTo(expectedFormat));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT EbookAuthorFolderFormat FROM NamingConfig;"), Is.EqualTo("{Author Name}"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT EbookStandardBookFormat FROM NamingConfig;"), Is.EqualTo(expectedFormat));
            });
        }

        [Test]
        public void should_convert_a_populated_v158_bookshelf_database_and_preserve_media_relationships()
        {
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(backupPath), Is.True);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(DatabaseLineageGuard.Detect(ConnectionStringFor(backupPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGDatabaseMigrated';"), Is.EqualTo("true"));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'BookshelfNGDatabaseMigrated';"), Is.Zero);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;").Length, Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM NamingConfig WHERE MultiEpisodeStyle = 0 AND LENGTH(StandardBookFormat) > 0 AND LENGTH(AuthorFolderFormat) > 0;"), Is.EqualTo(1),
                    "an empty BookshelfNG naming table must retain Chaptarr's migrated defaults");
                Assert.That(QueryScalar<string>(backupPath, "SELECT ForeignBookId FROM Books WHERE Id = 30;"), Is.EqualTo("hardcover:book-30"));
            });

            Assert.That(QueryScalar<string>(_sourcePath, "SELECT HardcoverAuthorId FROM Authors WHERE Id = 20;"), Is.EqualTo("hc:author-10"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookshelfNGQualityProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookshelfNGAudiobookQualityProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookshelfNGEbookQualityProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(2));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT AudiobookQualityProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT EbookQualityProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(4));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'QualityProfile' AND SourceId = 2 AND TargetId = 4 AND MediaType = 1;"), Is.EqualTo(1));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT AudiobookPath FROM Authors WHERE Id = 20;"), Is.EqualTo("/library/Fixture Author/Audiobooks"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT EbookPath FROM Authors WHERE Id = 20;"), Is.EqualTo("/library/Fixture Author/Ebooks"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT AudiobookMetadataProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT EbookMetadataProfileId FROM Authors WHERE Id = 20;"), Is.EqualTo(2));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT ProfileType FROM QualityProfiles WHERE Id = 1;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT ProfileType FROM QualityProfiles WHERE Id = 3;"), Is.EqualTo(2));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Name FROM QualityProfiles WHERE Id = 4;"), Is.EqualTo("Ebook override"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT Cutoff FROM QualityProfiles WHERE Id = 4;"), Is.EqualTo(2));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT AudiobookSettings FROM RootFolders WHERE Id = 1;"), Does.Contain("\"QualityProfileId\":1"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT EbookSettings FROM RootFolders WHERE Id = 1;"), Does.Contain("\"QualityProfileId\":3"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT PreferredEditionTerms FROM MetadataProfiles WHERE Id = 1;"), Is.EqualTo("3|audible"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT PreferredSeries FROM MetadataProfiles WHERE Id = 1;"), Is.EqualTo("The Fixture Series"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT PreferredEditionTerms FROM MetadataProfiles WHERE Id = 2;"), Is.EqualTo("2|epub"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT PreferredSeries FROM MetadataProfiles WHERE Id = 2;"), Is.EqualTo("Fixture Ebooks"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'FixtureSetting';"), Is.EqualTo("preserved"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT QualityProfileId FROM ImportLists WHERE Id = 110;"), Is.EqualTo(1));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM ImportLists WHERE Id = 110;"), Is.EqualTo("GoodreadsBookshelfImportListSettings"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Settings FROM ImportLists WHERE Id = 110;"), Does.Contain("12345678"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM Notifications WHERE Id = 111;"), Is.EqualTo(nameof(BookLoreSettings)));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM DownloadClients WHERE Id = 109;"), Is.EqualTo(nameof(QBittorrentSettings)));
            var qBittorrentSettings = NzbDrone.Common.Serializer.STJson.Deserialize<QBittorrentSettings>(
                QueryScalar<string>(_sourcePath, "SELECT Settings FROM DownloadClients WHERE Id = 109;"));
            Assert.That(qBittorrentSettings.AudiobookCategory, Is.EqualTo("legacy-audio"));
            Assert.That(qBittorrentSettings.EbookCategory, Is.EqualTo("legacy-ebooks"));
            Assert.That(qBittorrentSettings.MusicCategory, Is.EqualTo("readarr"),
                "a missing BookshelfNG legacy category uses its source default rather than ChaptarrNG's native default");
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Indexers WHERE Id = 120 AND EnableRss = 1 AND SupportsRss = 0 AND SupportsSearch = 0;"), Is.EqualTo(1));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM Indexers WHERE Id = 120;"), Is.EqualTo(nameof(MyAnonaMouseSettings)));
            var mamSettings = NzbDrone.Common.Serializer.STJson.Deserialize<MyAnonaMouseSettings>(
                QueryScalar<string>(_sourcePath, "SELECT Settings FROM Indexers WHERE Id = 120;"));
            Assert.Multiple(() =>
            {
                Assert.That(mamSettings.MamId, Is.EqualTo("fixture-token"));
                Assert.That(mamSettings.SearchType, Is.EqualTo((int)MyAnonaMouseSearchType.Freeleech));
                Assert.That(mamSettings.SearchInDescription, Is.True);
                Assert.That(mamSettings.SearchInSeries, Is.False);
                Assert.That(mamSettings.SearchInFilenames, Is.True);
                Assert.That(mamSettings.SearchLanguages, Is.EqualTo(new[] { 4, 1 }));
                Assert.That(mamSettings.SeedCriteria.SeedRatio, Is.EqualTo(1.5));
                Assert.That(mamSettings.SeedCriteria.SeedTime, Is.EqualTo(4320));
                Assert.That(mamSettings.SeedCriteria.DiscographySeedTime, Is.EqualTo(10080));
                Assert.That(mamSettings.NeverMoveOnImport, Is.True);
                Assert.That(mamSettings.RejectBlocklistedTorrentHashesWhileGrabbing, Is.True);
                Assert.That(mamSettings.UseFreeleechWedge, Is.EqualTo((int)MyAnonaMouseFreeleechWedgeAction.Preferred),
                    "the supported legacy Prefer value must keep using MAM's freeleech wedge");
            });
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM Indexers WHERE Id = 121;"), Is.EqualTo(nameof(DirectDownloadSettings)));
            var directDownloadSettings = NzbDrone.Common.Serializer.STJson.Deserialize<DirectDownloadSettings>(
                QueryScalar<string>(_sourcePath, "SELECT Settings FROM Indexers WHERE Id = 121;"));
            Assert.That(directDownloadSettings.Urls, Is.EqualTo("https://primary.example\nhttps://secondary.example"));
            Assert.That(directDownloadSettings.ApiKey, Is.EqualTo("fixture-key"));
            Assert.That(directDownloadSettings.EnableSlowFallback, Is.True);
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT ConfigContract FROM Indexers WHERE Id = 122;"), Is.EqualTo("NewznabSettings"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT OnBookRetag FROM Notifications WHERE Id = 111;"), Is.EqualTo(1));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Password FROM Users WHERE Id = 112;"), Is.EqualTo("legacy-sha256-hash"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Users WHERE Id = 112 AND Salt IS NULL AND Iterations IS NULL;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE AuthorId = 20;"), Is.EqualTo(2));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND MediaType = 0 AND AudiobookMonitored = 1 AND ForeignEditionId = 'hc:audio-40';"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 31 AND MediaType = 1 AND EbookMonitored = 0 AND ForeignEditionId IS NULL;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND HardcoverEditionId = 'audio-40';"), Is.EqualTo(1));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT NarratorNames FROM Editions WHERE Id = 40;"), Is.EqualTo("[\"A Narrator\"]"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Images FROM Editions WHERE Id = 40;"), Is.EqualTo("[{\"Url\":\"https://covers.example/audio.jpg\",\"CoverType\":6}]"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Links FROM Editions WHERE Id = 40;"), Is.EqualTo("[{\"Url\":\"https://catalog.example/audio-40\",\"Name\":\"Catalog\"}]"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 41 AND BookId = 31 AND OpenLibraryEditionId = 'ol:OL123M';"), Is.EqualTo(1));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Links FROM Editions WHERE Id = 41;"), Is.EqualTo("[]"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT MediaType FROM BookFiles WHERE Id = 51;"), Is.EqualTo("ebook"));
            var remoteSearchTime = DateTime.Parse(
                QueryScalar<string>(_sourcePath, "SELECT LastRemoteSearchTime FROM BookFiles WHERE Id = 50;"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            Assert.That(remoteSearchTime,
                Is.EqualTo(new DateTime(2026, 1, 4, 5, 6, 7, DateTimeKind.Utc)),
                "successful catalog-search timestamps must survive BookshelfNG conversion");
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 51 AND LastRemoteSearchTime IS NULL;"), Is.EqualTo(1));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookFileId FROM ExtraFiles WHERE Id = 60;"), Is.EqualTo(999));
            Assert.That(QueryScalar<long>(_sourcePath, "SELECT seq FROM sqlite_sequence WHERE name = 'BookFiles';"), Is.GreaterThanOrEqualTo(999));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookId FROM MetadataFiles WHERE Id = 61;"), Is.EqualTo(31));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookId FROM History WHERE Id = 93;"), Is.EqualTo(31));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT EditionId FROM History WHERE Id = 93;"), Is.EqualTo(41));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE SeriesId = 70 AND BookId IN (30, 31);"), Is.EqualTo(2));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT AuthorProviderIds FROM Blocklist WHERE Id = 92;"), Is.EqualTo("[\"hc:author-10\"]"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT BookProviderIds FROM Blocklist WHERE Id = 92;"), Is.EqualTo("[\"hc:book-30\"]"));
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT BookId FROM DownloadHistory WHERE Id = 91;"), Is.Zero);
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Commands;"), Is.Zero);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books;"), Is.EqualTo(2), "a restart must not repeat the conversion");
        }

        [Test]
        public void should_persist_the_complete_bookshelf_compatibility_profile()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_URLBASE", "/legacy/books/");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGCompatibilityProfileVersion';"), Is.EqualTo("2"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGSourceProduct';"), Is.EqualTo("BookshelfNG"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGSourceSchemaVersion';"), Is.EqualTo("158"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGProviderDialect';"), Is.EqualTo("gr"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGApiBasePath';"), Is.EqualTo("/legacy/books"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGFormatSemantics';"), Is.EqualTo("audiobook-ebook-split-v1"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGIdentityMapVersion';"), Is.EqualTo("1"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGMigrationOutcome';"), Is.EqualTo("converted"));
            });
        }

        [Test]
        public void should_preserve_required_mam_freeleech_wedge_settings_during_conversion()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE Indexers SET Settings = @settings WHERE Id = 120;";
                command.Parameters.AddWithValue("@settings", "{\"baseUrl\":\"https://www.myanonamouse.net\",\"mamId\":\"fixture-token\",\"searchType\":2,\"useFreeleechWedge\":2}");
                Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var targetSettings = NzbDrone.Common.Serializer.STJson.Deserialize<MyAnonaMouseSettings>(
                QueryScalar<string>(_sourcePath, "SELECT Settings FROM Indexers WHERE Id = 120;"));
            var backupSettings = NzbDrone.Common.Serializer.STJson.Deserialize<MyAnonaMouseSettings>(
                QueryScalar<string>(backupPath, "SELECT Settings FROM Indexers WHERE Id = 120;"));

            Assert.Multiple(() =>
            {
                Assert.That(targetSettings.UseFreeleechWedge, Is.EqualTo((int)MyAnonaMouseFreeleechWedgeAction.Required),
                    "the converted ChaptarrNG configuration must retain Required");
                Assert.That(backupSettings.UseFreeleechWedge, Is.EqualTo((int)MyAnonaMouseFreeleechWedgeAction.Required),
                    "the preserved BookshelfNG backup must retain Required");
            });
        }

        [Test]
        public void should_preserve_booklore_notification_settings_and_release_import_trigger()
        {
            const string settings = "{\"BaseUrl\":\"https://booklore.example\",\"Username\":\"migration-fixture\",\"Password\":\"fixture-only-password\"}";
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO Notifications (Id, Name, OnGrab, Settings, Implementation, ConfigContract, OnUpgrade, Tags, OnRename, OnReleaseImport, OnHealthIssue, IncludeHealthWarnings, OnDownloadFailure, OnImportFailure, OnBookRetag, OnAuthorDelete, OnBookDelete, OnBookFileDelete, OnBookFileDeleteForUpgrade, OnApplicationUpdate, OnAuthorAdded)
                    VALUES (113, 'BookLore', 0, @settings, @implementation, @configContract, 0, '[]', 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);";
                command.Parameters.AddWithValue("@settings", settings);
                command.Parameters.AddWithValue("@implementation", typeof(BookLore).Name);
                command.Parameters.AddWithValue("@configContract", typeof(BookLoreSettings).Name);
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            const string preservedRow = @"SELECT COUNT(*) FROM Notifications
                WHERE Id = 113 AND Name = 'BookLore' AND Implementation = 'BookLore'
                    AND ConfigContract = 'BookLoreSettings'
                    AND Settings = '{""BaseUrl"":""https://booklore.example"",""Username"":""migration-fixture"",""Password"":""fixture-only-password""}'
                    AND OnReleaseImport = 1;";

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, preservedRow), Is.EqualTo(1), "the ChaptarrNG database must retain the BookLore provider, credentials, and import trigger");
                Assert.That(QueryScalar<int>(backupPath, preservedRow), Is.EqualTo(1), "the retained BookshelfNG backup must preserve the original BookLore settings and trigger");
            });
        }

        [Test]
        public void should_preserve_supplemental_catalog_ids_and_aliases_while_retaining_the_original_source()
        {
            const string sourceAuthorId = "person/123:456";
            const string sourceBookId = "work/123:456";
            const string sourceEditionId = "volume/123:456";
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE AuthorMetadata SET ForeignAuthorId = 'loc-author:person/123:456' WHERE Id = 10;
                    UPDATE Books SET ForeignBookId = 'loc:work/123:456' WHERE Id = 30;
                    UPDATE Editions SET ForeignEditionId = 'googlebooks:volume/123:456' WHERE Id = 40;";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var authorProviderId = AdditionalMetadataSources.CreateProviderId("loc-author", sourceAuthorId);
            var bookProviderId = AdditionalMetadataSources.CreateProviderId("loc", sourceBookId);
            var editionProviderId = AdditionalMetadataSources.CreateProviderId("googlebooks", sourceEditionId);
            var authorNormalizedId = authorProviderId.Substring("meta:".Length);
            var bookNormalizedId = bookProviderId.Substring("meta:".Length);
            var editionNormalizedId = editionProviderId.Substring("meta:".Length);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT AdditionalMetadataProviderId FROM Authors WHERE Id = 20;"), Is.EqualTo(authorProviderId));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT AdditionalMetadataProviderId FROM Books WHERE Id = 30;"), Is.EqualTo(bookProviderId));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT ForeignEditionId FROM Editions WHERE Id = 40;"), Is.EqualTo(editionProviderId));
                Assert.That(QueryScalar<int>(_sourcePath, $"SELECT COUNT(*) FROM ProviderAliasIndex WHERE EntityType = 'Author' AND EntityId = 20 AND Scope = 'author' AND Provider = 'meta' AND NormalizedProviderId = '{authorNormalizedId}';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, $"SELECT COUNT(*) FROM ProviderAliasIndex WHERE EntityType = 'Book' AND EntityId = 30 AND Scope = 'work' AND Provider = 'meta' AND NormalizedProviderId = '{bookNormalizedId}';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, $"SELECT COUNT(*) FROM ProviderAliasIndex WHERE EntityType = 'Edition' AND EntityId = 40 AND Scope = 'edition' AND Provider = 'meta' AND NormalizedProviderId = '{editionNormalizedId}';"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(backupPath, "SELECT ForeignAuthorId FROM AuthorMetadata WHERE Id = 10;"), Is.EqualTo("loc-author:person/123:456"));
                Assert.That(QueryScalar<string>(backupPath, "SELECT ForeignBookId FROM Books WHERE Id = 30;"), Is.EqualTo("loc:work/123:456"));
                Assert.That(QueryScalar<string>(backupPath, "SELECT ForeignEditionId FROM Editions WHERE Id = 40;"), Is.EqualTo("googlebooks:volume/123:456"));
            });
        }

        [Test]
        public void should_preserve_supplemental_catalog_preferences_and_credentials()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO Config (Key, Value) VALUES
                        ('MetadataCatalogSources', 'openlibrary,googlebooks'),
                        ('GoogleBooksApiKey', 'fixture-key-preserved'),
                        ('MetadataTitleSourcePreference', 'openlibrary'),
                        ('MetadataCoverSourcePreference', 'googlebooks');";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataCatalogSources';"), Is.EqualTo("openlibrary,googlebooks"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'GoogleBooksApiKey';"), Is.EqualTo("fixture-key-preserved"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataTitleSourcePreference';"), Is.EqualTo("openlibrary"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MetadataCoverSourcePreference';"), Is.EqualTo("googlebooks"));
                Assert.That(QueryScalar<string>(backupPath, "SELECT Value FROM Config WHERE Key = 'GoogleBooksApiKey';"), Is.EqualTo("fixture-key-preserved"));
            });
        }

        [Test]
        public void should_preserve_opt_in_format_specific_author_paths_and_map_their_roots()
        {
            AddConfigValue("AutoInitializeFormatSpecificAuthorPaths", "true");
            AddConfigValue("NewAuthorEbookRootFolderPath", "/library/ebooks");
            AddConfigValue("NewAuthorAudiobookRootFolderPath", "/library/audiobooks");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'AutoInitializeFormatSpecificAuthorPaths';"), Is.EqualTo("true"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'DefaultEbookRootFolderPath';"), Is.EqualTo("/library/ebooks"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'DefaultAudiobookRootFolderPath';"), Is.EqualTo("/library/audiobooks"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'NewAuthorEbookRootFolderPath';"), Is.EqualTo("/library/ebooks"));
                Assert.That(QueryScalar<string>(backupPath, "SELECT Value FROM Config WHERE Key = 'NewAuthorAudiobookRootFolderPath';"), Is.EqualTo("/library/audiobooks"));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'DefaultAudiobookRootFolderPath';"), Is.Zero);
            });
        }

        [Test]
        public void should_not_apply_bookshelf_author_root_overrides_when_auto_initialization_is_disabled()
        {
            AddConfigValue("AutoInitializeFormatSpecificAuthorPaths", "false");
            AddConfigValue("NewAuthorEbookRootFolderPath", "/library/ebooks");
            AddConfigValue("NewAuthorAudiobookRootFolderPath", "/library/audiobooks");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'AutoInitializeFormatSpecificAuthorPaths';"), Is.EqualTo("false"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Config WHERE Key = 'DefaultEbookRootFolderPath';"), Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Config WHERE Key = 'DefaultAudiobookRootFolderPath';"), Is.Zero);
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'NewAuthorEbookRootFolderPath';"), Is.EqualTo("/library/ebooks"));
            });
        }

        [Test]
        public void should_keep_explicit_chaptarr_author_root_defaults_over_bookshelf_values()
        {
            AddConfigValue("AutoInitializeFormatSpecificAuthorPaths", "true");
            AddConfigValue("NewAuthorEbookRootFolderPath", "/library/ebooks");
            AddConfigValue("DefaultEbookRootFolderPath", "/custom/ebooks");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'DefaultEbookRootFolderPath';"), Is.EqualTo("/custom/ebooks"));
        }

        [Test]
        public void should_migrate_a_saved_bookshelf_hardcover_token_to_the_chaptarr_setting()
        {
            AddConfigValue("HardcoverAuth", "Bearer fixture-hardcover-token");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverApiToken';"), Is.EqualTo("fixture-hardcover-token"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverAuth';"), Is.EqualTo("Bearer fixture-hardcover-token"));
                Assert.That(QueryScalar<string>(backupPath, "SELECT Value FROM Config WHERE Key = 'HardcoverAuth';"), Is.EqualTo("Bearer fixture-hardcover-token"));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'HardcoverApiToken';"), Is.Zero);
            });
        }

        [Test]
        public void should_preserve_an_explicit_chaptarr_hardcover_token_over_the_legacy_value()
        {
            AddConfigValue("HardcoverAuth", "fixture-legacy-token");
            AddConfigValue("HardcoverApiToken", "fixture-explicit-token");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverApiToken';"), Is.EqualTo("fixture-explicit-token"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverAuth';"), Is.EqualTo("fixture-legacy-token"));
            });
        }

        [Test]
        public void should_preserve_an_explicitly_cleared_chaptarr_hardcover_token()
        {
            AddConfigValue("HardcoverAuth", "fixture-legacy-token");
            AddConfigValue("HardcoverApiToken", string.Empty);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverApiToken';"), Is.Empty);
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'HardcoverAuth';"), Is.EqualTo("fixture-legacy-token"));
            });
        }

        [TestCase(50, "aggressive")]
        [TestCase(66, "aggressive")]
        [TestCase(67, "balanced")]
        [TestCase(70, "balanced")]
        [TestCase(82, "balanced")]
        [TestCase(83, "strict")]
        [TestCase(99, "strict")]
        public void should_translate_legacy_match_threshold_boundaries_and_retain_the_original_value(int threshold, string expectedMode)
        {
            AddConfigValue("bookimportminimummatchpercent", threshold.ToString(CultureInfo.InvariantCulture));

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'bookmatchingstrictness';"), Is.EqualTo(expectedMode));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfMatchCompatibility';"), Is.EqualTo("true"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'bookimportminimummatchpercent';"), Is.EqualTo(threshold.ToString(CultureInfo.InvariantCulture)));
                Assert.That(QueryScalar<string>(backupPath, "SELECT Value FROM Config WHERE Key = 'bookimportminimummatchpercent';"), Is.EqualTo(threshold.ToString(CultureInfo.InvariantCulture)));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'BookshelfMatchCompatibility';"), Is.Zero);
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'bookmatchingstrictness';"), Is.Zero);
            });
        }

        [Test]
        public void should_preserve_the_default_bookshelf_match_threshold_when_it_was_not_saved()
        {
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfMatchCompatibility';"), Is.EqualTo("true"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Config WHERE Key = 'BookImportMinimumMatchPercent';"), Is.Zero);
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Config WHERE Key = 'BookshelfMatchCompatibility';"), Is.Zero);
            });
        }

        [Test]
        public void should_preserve_the_move_extra_files_on_rename_setting()
        {
            AddConfigValue("MoveExtraFilesOnRename", "true");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'MoveExtraFilesOnRename';"), Is.EqualTo("true"));
        }

        [Test]
        public void should_preserve_an_explicit_chaptarr_match_mode_over_the_legacy_threshold()
        {
            AddConfigValue("bookimportminimummatchpercent", "50");
            AddConfigValue("bookmatchingstrictness", "strict");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'bookmatchingstrictness';"), Is.EqualTo("strict"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfMatchCompatibility';"), Is.EqualTo("true"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'bookimportminimummatchpercent';"), Is.EqualTo("50"));
            });
        }

        [Test]
        public void should_preserve_an_explicit_bookshelf_match_compatibility_override()
        {
            AddConfigValue("bookimportminimummatchpercent", "70");
            AddConfigValue("BookshelfMatchCompatibility", "false");

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfMatchCompatibility';"), Is.EqualTo("false"));
            Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'BookshelfNGDatabaseMigrated';"), Is.EqualTo("true"));
        }

        [Test]
        public void should_reject_an_invalid_legacy_threshold_even_when_an_explicit_mode_exists()
        {
            AddConfigValue("bookimportminimummatchpercent", "100");
            AddConfigValue("bookmatchingstrictness", "strict");
            var sourceBeforeConversion = File.ReadAllBytes(_sourcePath);

            var exception = Assert.Throws<InvalidDataException>(() =>
                new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("integer from 50 through 99"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBeforeConversion));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_reject_an_out_of_range_legacy_match_threshold_without_changing_the_source()
        {
            AddConfigValue("bookimportminimummatchpercent", "100");
            var sourceBeforeConversion = File.ReadAllBytes(_sourcePath);

            var exception = Assert.Throws<InvalidDataException>(() =>
                new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("integer from 50 through 99"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBeforeConversion));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_account_for_stale_series_links_and_preserve_them_for_rollback()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    PRAGMA foreign_keys = OFF;
                    INSERT INTO SeriesBookLink (Id, SeriesId, BookId, Position, IsPrimary, SeriesPosition)
                    VALUES (99, 70, 999, '9', 0, 9);
                    INSERT INTO SeriesBookLink (Id, SeriesId, BookId, Position, IsPrimary, SeriesPosition)
                    VALUES (100, 999, 30, '10', 0, 10);
                    INSERT INTO SeriesBookLink (Id, SeriesId, BookId, Position, IsPrimary, SeriesPosition)
                    VALUES (101, 999, 1000, '11', 0, 11);
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (96, 'Deleted work history fixture', '2026-01-05', '{}', '{}', 3, 'download-96', 20, 999);
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (97, 'Deleted author history fixture', '2026-01-05', '{}', '{}', 3, 'download-97', 999, 30);
                    INSERT INTO DownloadHistory (Id, EventType, AuthorId, DownloadId, SourceTitle, Date, Data)
                    VALUES (98, 1, 999, 'download-98', 'Deleted author download fixture', '2026-01-05', '{}');
                    INSERT INTO DownloadHistory (Id, EventType, AuthorId, DownloadId, SourceTitle, Date, Data)
                    VALUES (99, 1, 0, 'download-99', 'Unassociated download fixture', '2026-01-05', '{}');";
                command.ExecuteNonQuery();
            }

            var sourceBeforePreview = File.ReadAllBytes(_sourcePath);
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var preview = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);
            var seriesLinkReport = preview.ImportReport.Tables["SeriesBookLink"];
            var historyReport = preview.ImportReport.Tables["History"];
            var downloadHistoryReport = preview.ImportReport.Tables["DownloadHistory"];

            Assert.Multiple(() =>
            {
                Assert.That(preview.PreviewOnly, Is.True);
                Assert.That(seriesLinkReport.SourceRows, Is.EqualTo(4));
                Assert.That(seriesLinkReport.TransformedRows, Is.EqualTo(1));
                Assert.That(seriesLinkReport.ResetRows, Is.EqualTo(3));
                Assert.That(seriesLinkReport.AccountedRows, Is.EqualTo(seriesLinkReport.SourceRows));
                Assert.That(seriesLinkReport.TargetMapping, Does.Contain("missing Books references: 2; missing Series references: 2"));
                Assert.That(historyReport.SourceRows, Is.EqualTo(4));
                Assert.That(historyReport.TransformedRows, Is.EqualTo(historyReport.SourceRows));
                Assert.That(historyReport.TargetMapping, Does.Contain("events with an unmapped positive author ID left unlinked: 1; events with an unmapped positive book ID left unlinked: 1"));
                Assert.That(downloadHistoryReport.SourceRows, Is.EqualTo(3));
                Assert.That(downloadHistoryReport.TargetMapping, Does.Contain("events without a mapped author use required AuthorId=0: 2 (source rows without a positive author ID: 1; positive source author IDs without a mapped author: 1)"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBeforePreview),
                    "preview must leave the source database byte-identical");
            });

            var conversion = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);
            var backupPath = conversion.BackupPath;

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty,
                    "the promoted Chaptarr database must not carry the dangling source relationship");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE SeriesId = 70 AND BookId IN (30, 31);"), Is.EqualTo(2),
                    "valid series links must survive conversion");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE BookId = 999;"), Is.Zero,
                    "a relationship without a source book has no target link");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE SeriesId = 999;"), Is.Zero,
                    "a relationship without a source series has no target link");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 96 AND BookId = 0 AND AuthorId = 20;"), Is.EqualTo(1),
                    "history for a deleted book must survive without an unsafe book link");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 97 AND BookId = 30 AND AuthorId IS NULL;"), Is.EqualTo(1),
                    "history with a deleted author must retain its valid book link and clear the stale author link");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM DownloadHistory WHERE Id = 98 AND BookId = 0 AND AuthorId = 0;"), Is.EqualTo(1),
                    "download history from a deleted author must not alias a future Chaptarr author");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM DownloadHistory WHERE Id = 99 AND BookId = 0 AND AuthorId = 0;"), Is.EqualTo(1),
                    "source events without an author use the target's required no-author sentinel");
                Assert.That(DatabaseLineageGuard.Detect(ConnectionStringFor(backupPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<string>(backupPath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM SeriesBookLink WHERE Id = 99 AND SeriesId = 70 AND BookId = 999;"), Is.EqualTo(1),
                    "the original stale link must remain in the BookshelfNG rollback database");
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM SeriesBookLink WHERE Id = 100 AND SeriesId = 999 AND BookId = 30;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM SeriesBookLink WHERE Id = 101 AND SeriesId = 999 AND BookId = 1000;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM History WHERE Id = 96 AND AuthorId = 20 AND BookId = 999;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM History WHERE Id = 97 AND AuthorId = 999 AND BookId = 30;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM DownloadHistory WHERE Id = 98 AND AuthorId = 999;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM DownloadHistory WHERE Id = 99 AND AuthorId = 0;"), Is.EqualTo(1));
                Assert.That(QueryRows(backupPath, "PRAGMA foreign_key_check;").Length, Is.EqualTo(4));
            });

            var converter = new BookshelfDatabaseConverter();
            var restorePreview = converter.Restore(
                _sourceConnectionString,
                backupPath,
                previewOnly: true,
                migrateBookshelfSource: migrationController.MigrateBookshelfSource);

            Assert.Multiple(() =>
            {
                Assert.That(restorePreview.PreviewOnly, Is.True);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr),
                    "restore preview must not replace the converted database");
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });

            var restored = converter.Restore(
                _sourceConnectionString,
                backupPath,
                migrateBookshelfSource: migrationController.MigrateBookshelfSource);

            Assert.Multiple(() =>
            {
                Assert.That(restored.PreviewOnly, Is.False);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE Id = 99 AND SeriesId = 70 AND BookId = 999;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE Id = 100 AND SeriesId = 999 AND BookId = 30;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM SeriesBookLink WHERE Id = 101 AND SeriesId = 999 AND BookId = 1000;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 96 AND AuthorId = 20 AND BookId = 999;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 97 AND AuthorId = 999 AND BookId = 30;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM DownloadHistory WHERE Id = 98 AND AuthorId = 999;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM DownloadHistory WHERE Id = 99 AND AuthorId = 0;"), Is.EqualTo(1));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;").Length, Is.EqualTo(4));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(File.Exists(restored.ChaptarrBackupPath), Is.True,
                    "rollback must retain the converted Chaptarr database separately");
            });
        }

        [Test]
        public void should_upgrade_an_older_bookshelf_snapshot_and_keep_the_original_schema_for_rollback()
        {
            DowngradeSourceToMigration22();
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);

            var result = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            var backupPath = result.BackupPath;
            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id IN (30, 31);"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(backupPath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(22));
                Assert.That(QueryRows(backupPath, "SELECT name FROM pragma_table_info('Authors');"), Does.Not.Contain("EbookPath"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });

            var restored = new BookshelfDatabaseConverter().Restore(
                _sourceConnectionString,
                backupPath,
                migrateBookshelfSource: migrationController.MigrateBookshelfSource);

            Assert.Multiple(() =>
            {
                Assert.That(restored.PreviewOnly, Is.False);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(22));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id IN (30, 31);"), Is.EqualTo(1));
                Assert.That(File.Exists(restored.ChaptarrBackupPath), Is.True);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
            });
        }

        [Test]
        public void should_preview_older_schema_upgrade_without_modifying_the_source_or_leaving_staging_files()
        {
            DowngradeSourceToMigration22();
            var sourceBefore = File.ReadAllBytes(_sourcePath);
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);

            var result = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.True);
                Assert.That(result.BackupPath, Is.Null);
                Assert.That(result.ImportReport.Tables.Count, Is.GreaterThan(0));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(sourceBefore));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(22));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-migrate-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.chaptarr-stage-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-snapshot-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_upgrade_a_genuine_migration_22_schema_created_by_bookshelf_migrations()
        {
            CreateSourceThroughMigration(22);
            SeedMigration22SourceWithLibrary();

            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);
            var result = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(result.BackupPath), Is.True);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(QueryScalar<int>(result.BackupPath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(22));
                Assert.That(QueryScalar<int>(result.BackupPath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND Title = 'Migration 22 fixture book';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(result.BackupPath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND IsEbook = 0;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(result.BackupPath, "SELECT Path FROM BookFiles WHERE Id = 50;"), Is.EqualTo("/library/Migration 22 Author/book.m4b"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND Title = 'Migration 22 fixture book' AND MediaType = 0 AND AudiobookMonitored = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND Monitored = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Path FROM BookFiles WHERE Id = 50;"), Is.EqualTo("/library/Migration 22 Author/book.m4b"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'FixtureSetting';"), Is.EqualTo("preserved"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });

            var restored = new BookshelfDatabaseConverter().Restore(
                _sourceConnectionString,
                result.BackupPath,
                migrateBookshelfSource: migrationController.MigrateBookshelfSource);

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(22));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND Title = 'Migration 22 fixture book';"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(File.Exists(restored.ChaptarrBackupPath), Is.True);
            });
        }

        [Test]
        public void should_upgrade_the_oldest_bookshelf_schema_and_keep_the_original_schema_for_rollback()
        {
            CreateSourceThroughMigration(1);
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null);

            var result = migrationController.MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(QueryScalar<int>(result.BackupPath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books;"), Is.Zero);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });

            var restored = new BookshelfDatabaseConverter().Restore(
                _sourceConnectionString,
                result.BackupPath,
                migrateBookshelfSource: migrationController.MigrateBookshelfSource);

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT MAX(Version) FROM VersionInfo;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books;"), Is.Zero);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(File.Exists(restored.ChaptarrBackupPath), Is.True);
            });
        }

        [Test]
        public void should_reject_a_legacy_table_alias_after_its_bookshelf_rename_migration()
        {
            DowngradeSourceToMigration22();
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "ALTER TABLE Blocklist RENAME TO Blacklist;";
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(_sourcePath);
            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).MigrateBookshelf(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite,
                    previewOnly: false));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("unknown: Blacklist"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_reject_a_bookshelf_schema_newer_than_the_embedded_migration_chain()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE VersionInfo SET Version = 159 WHERE Version = 158;";
                Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
            }

            var before = File.ReadAllBytes(_sourcePath);
            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("outside the bundled migration chain (1-158)"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-migrate-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_preview_bookshelf_rollback_without_changing_either_database()
        {
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var bookshelfBackupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var currentBefore = File.ReadAllBytes(_sourcePath);
            var sourceBackupBefore = File.ReadAllBytes(bookshelfBackupPath);

            var result = new BookshelfDatabaseConverter().Restore(_sourceConnectionString, bookshelfBackupPath, previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.True);
                Assert.That(result.BookshelfBackupPath, Is.EqualTo(bookshelfBackupPath));
                Assert.That(result.ChaptarrBackupPath, Is.Null);
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(currentBefore));
                Assert.That(File.ReadAllBytes(bookshelfBackupPath), Is.EqualTo(sourceBackupBefore));
                Assert.That(Directory.GetFiles(_directory, "readarr.db.chaptarrng-backup-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-restore-stage-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_restore_the_source_and_retain_chaptarr_writes_for_rollback()
        {
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var bookshelfBackupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var sourceBackupBefore = File.ReadAllBytes(bookshelfBackupPath);

            using (var current = new SqliteConnection(_sourceConnectionString))
            {
                current.Open();
                using var command = current.CreateCommand();
                command.CommandText = "UPDATE Config SET Value = 'after-migration' WHERE Key = 'FixtureSetting';";
                Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
            }

            var result = new BookshelfDatabaseConverter().Restore(_sourceConnectionString, bookshelfBackupPath);

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.False);
                Assert.That(File.Exists(result.ChaptarrBackupPath), Is.True);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(DatabaseLineageGuard.Detect(ConnectionStringFor(result.ChaptarrBackupPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Value FROM Config WHERE Key = 'FixtureSetting';"), Is.EqualTo("preserved"));
                Assert.That(QueryScalar<string>(result.ChaptarrBackupPath, "SELECT Value FROM Config WHERE Key = 'FixtureSetting';"), Is.EqualTo("after-migration"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
                Assert.That(QueryScalar<string>(result.ChaptarrBackupPath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(result.ChaptarrBackupPath, "PRAGMA foreign_key_check;"), Is.Empty);
                Assert.That(File.ReadAllBytes(bookshelfBackupPath), Is.EqualTo(sourceBackupBefore));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-restore-stage-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_a_bookshelf_backup_with_the_wrong_identity_without_changing_the_current_database()
        {
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);
            var bookshelfBackupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var wrongIdentityPath = Path.Combine(_directory, "unrelated.db");
            File.Copy(bookshelfBackupPath, wrongIdentityPath);
            var currentBefore = File.ReadAllBytes(_sourcePath);
            var sourceBackupBefore = File.ReadAllBytes(bookshelfBackupPath);

            var exception = Assert.Throws<InvalidDataException>(() => new BookshelfDatabaseConverter().Restore(
                _sourceConnectionString,
                wrongIdentityPath));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("not a retained BookshelfNG conversion backup"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(currentBefore));
                Assert.That(File.ReadAllBytes(bookshelfBackupPath), Is.EqualTo(sourceBackupBefore));
                Assert.That(Directory.GetFiles(_directory, "readarr.db.chaptarrng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_an_unsupported_bookshelf_identity_without_changing_the_source()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE AuthorMetadata SET ForeignAuthorId = 'metadata-api-author:opaque-10' WHERE Id = 10;";
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(_sourcePath);
            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.That(exception.Message, Does.Contain("no recognized provider prefix"));
            Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
        }

        [Test]
        public void should_allocate_split_book_ids_above_all_source_book_ids()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO Books (Id, AuthorMetadataId, ForeignBookId, TitleSlug, Title, Links, Genres, Ratings, CleanTitle, Monitored, AnyEditionOk, Added, RelatedBooks, LastSearchTime)
                    VALUES (31, 10, 'hardcover:book-31', 'hardcover:book-31', 'Adjacent Source Book', '[]', '[]', '{}', 'adjacent source book', 0, 0, '2026-01-05', '[]', NULL);
                    INSERT INTO Editions (Id, BookId, ForeignEditionId, Isbn13, Asin, Title, TitleSlug, Images, Links, IsEbook, Monitored, ManualAdd, Narrator)
                    VALUES (42, 31, 'hardcover-edition:audio-42', NULL, NULL, 'Adjacent Source Audiobook', 'audio-42', '[]', NULL, 0, 0, 0, NULL);";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 31 AND MediaType = 0;"), Is.EqualTo(1),
                    "the adjacent source audiobook must keep its original ID");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 32 AND MediaType = 1;"), Is.EqualTo(1),
                    "the split ebook must use an ID above every source Books.Id");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'Book' AND SourceId = 30 AND TargetId = 32 AND MediaType = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'Book' AND SourceId = 31 AND TargetId = 31 AND MediaType = 0;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_preserve_one_monitored_edition_for_each_media_format()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE Editions SET Monitored = 1 WHERE Id = 41;";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND MediaType = 0 AND AudiobookMonitored = 1 AND EbookMonitored = 0 AND ForeignEditionId = 'hc:audio-40';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 31 AND MediaType = 1 AND AudiobookMonitored = 0 AND EbookMonitored = 1 AND ForeignEditionId = 'ol:OL123M';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND Monitored = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 41 AND BookId = 31 AND Monitored = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'Author' AND SourceId = 20 AND TargetId = 20;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'AuthorMetadata' AND SourceId = 10 AND TargetId = 20;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'Book' AND SourceId = 30 AND ((TargetId = 30 AND MediaType = 0) OR (TargetId = 31 AND MediaType = 1));"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'Edition' AND ((SourceId = 40 AND TargetId = 40 AND MediaType = 0) OR (SourceId = 41 AND TargetId = 41 AND MediaType = 1));"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'BookFile' AND ((SourceId = 50 AND TargetId = 50 AND MediaType = 0) OR (SourceId = 51 AND TargetId = 51 AND MediaType = 1));"), Is.EqualTo(2));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_route_a_print_edition_with_an_ebook_file_to_the_ebook_catalog()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE Editions SET IsEbook = 0, Format = 'Paperback', Narrator = NULL WHERE Id = 40;
                    UPDATE BookFiles SET Path = '/library/Fixture Author/print-edition.epub' WHERE Id = 50;";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE MediaType = 0;"), Is.Zero,
                    "IsEbook=false alone must not turn a print edition with an ebook file into an audiobook");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND MediaType = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND IsEbook = 0 AND MediaType = 1 AND ReadingFormatId = 1;"), Is.EqualTo(1),
                    "a paperback edition stays a physical edition in the ebook catalog");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id IN (50, 51) AND MediaType = 'ebook';"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 50 AND EditionId = 40;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_preserve_an_unclassified_edition_in_both_catalogs_by_default()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE Editions SET IsEbook = 0, Format = NULL, Narrator = NULL WHERE Id = 40;
                    UPDATE BookFiles SET Path = '/library/Fixture Author/book.unknown' WHERE Id = 50;";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            var review = File.ReadAllText(_sourcePath + ".bookshelf-media-review.json");
            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id IN (30, 31) AND (MediaType = 0 AND AudiobookMonitored = 1 OR MediaType = 1 AND EbookMonitored = 1);"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND MediaType = 0 AND ForeignEditionId = 'hc:audio-40';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id > 41 AND BookId = 31 AND MediaType = 1 AND ForeignEditionId IS NULL AND Links = '[]';"), Is.EqualTo(1));
                Assert.That(review, Does.Contain("no format metadata or recognized linked file"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_apply_an_explicit_media_map_to_an_unclassified_edition()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE Editions SET IsEbook = 0, Format = NULL, Narrator = NULL, Monitored = 0 WHERE Id = 40;
                    UPDATE BookFiles SET Path = '/library/Fixture Author/book.unknown' WHERE Id = 50;";
                command.ExecuteNonQuery();
            }

            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 30, "mediaType": "audiobook" }
                  ]
                }
                """);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND MediaType = 0 AND IsEbook = 0;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id > 41 AND BookId = 31;"), Is.Zero,
                    "an explicit audiobook choice must not create an ebook clone for an unknown edition");
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT MediaType FROM BookFiles WHERE Id = 50;"), Is.EqualTo("audiobook"));
                Assert.That(File.Exists(_sourcePath + ".bookshelf-media-review.json"), Is.False);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_clone_an_explicit_edition_when_its_files_cover_both_media_formats()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE Editions SET Isbn13 = '9780000000002', Asin = 'B000000002' WHERE Id = 40;
                    INSERT INTO BookFiles (Id, EditionId, CalibreId, Quality, Size, DateAdded, MediaInfo, Modified, Path, Part)
                    VALUES (52, 40, 0, '{}', 150, '2026-01-04', '{}', '2026-01-04', '/library/Fixture Author/ebook-copy.epub', 0);
                    INSERT INTO ExtraFiles (Id, AuthorId, BookId, BookFileId, RelativePath, Extension, Added, LastUpdated)
                    VALUES (62, 20, 30, 52, 'ebook-cover.jpg', 'jpg', '2026-01-04', '2026-01-04');
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (94, 'ebook-copy.epub', '2026-01-04', '{}', '{""FileId"":""52"",""ImportedPath"":""/library/Fixture Author/ebook-copy.epub""}', 3, 'download-94', 20, 30);
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (95, 'untracked.epub', '2026-01-05', '{}', '{}', 3, 'download-95', 20, 30);
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (96, 'ebook-copy.epub', '2026-01-05', '{}', '{""FileId"":""52""}', 3, 'download-96', 20, 0);";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND BookId = 30 AND IsEbook = 0 AND MediaType = 0 AND ReadingFormatId = 2 AND NarratorNames LIKE '%A Narrator%';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id = 40 AND Isbn13 = '9780000000002' AND Asin = 'B000000002';"), Is.EqualTo(1),
                    "the original edition retains its provider and catalog identifiers");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE Id > 41 AND BookId = 31 AND IsEbook = 1 AND MediaType = 1 AND ReadingFormatId = 3 AND ForeignEditionId IS NULL AND Isbn13 IS NULL AND Asin IS NULL AND NarratorNames = '[]';"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Links FROM Editions WHERE Id > 41 AND BookId = 31;"), Is.EqualTo("[]"),
                    "a synthetic media edition must not duplicate provider links");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookshelfLegacyIdentityMap WHERE Source = 'BookshelfNG' AND EntityType = 'Edition' AND SourceId = 40 AND ((TargetId = 40 AND MediaType = 0) OR (TargetId > 41 AND MediaType = 1));"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 50 AND EditionId = 40 AND MediaType = 'audiobook';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 52 AND EditionId > 41 AND MediaType = 'ebook';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM ExtraFiles WHERE Id = 62 AND BookId = 31 AND BookFileId = 52;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 94 AND BookId = 31 AND EditionId > 41 AND Data LIKE '%\"FileId\":\"52\"%';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 95 AND BookId = 31 AND EditionId IS NULL;"), Is.EqualTo(1),
                    "an extension-bearing history event without a linked file should still follow its media-specific book");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 96 AND BookId = 31 AND EditionId > 41;"), Is.EqualTo(1),
                    "a file-specific event with no source BookId should follow the linked file");
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_multiple_monitored_editions_of_the_same_media_format()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE Editions SET IsEbook = 0, Monitored = 1;
                    UPDATE BookFiles SET Path = '/library/Fixture Author/second.m4b' WHERE Id = 51;";
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(_sourcePath);
            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.That(exception.Message, Does.Contain("more than one monitored audiobook edition"));
            Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
        }

        [Test]
        public void should_preserve_a_monitored_mixed_format_book_without_a_selected_edition_as_both_monitored()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE Editions SET Monitored = 0;";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var reviewPath = _sourcePath + ".bookshelf-media-review.json";
            var review = File.ReadAllText(reviewPath);
            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE (Id = 30 AND MediaType = 0 AND AudiobookMonitored = 1 AND EbookMonitored = 0) OR (Id = 31 AND MediaType = 1 AND AudiobookMonitored = 0 AND EbookMonitored = 1);"), Is.EqualTo(2));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Editions WHERE BookId IN (30, 31);"), Is.EqualTo(2));
                Assert.That(review, Does.Contain("\"sourceBookId\": 30"));
                Assert.That(review, Does.Contain("\"mediaType\": \"both\""));
                Assert.That(review, Does.Contain("\"monitored\": true"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.That(File.GetUnixFileMode(reviewPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                }
                Assert.That(DatabaseLineageGuard.Detect(ConnectionStringFor(backupPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND Monitored = 1;"), Is.EqualTo(1));
            });
        }

        [Test]
        public void should_automatically_preserve_monitoring_for_a_book_without_editions()
        {
            InsertBookWithoutEdition(32, monitored: true);
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            var backupPath = Directory.GetFiles(_directory, "readarr.db.bookshelfng-backup-*.db").Single();
            var reviewPath = _sourcePath + ".bookshelf-media-review.json";
            var review = File.ReadAllText(reviewPath);
            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE (Id = 32 AND MediaType = 0 AND AudiobookMonitored = 1 AND EbookMonitored = 0) OR (Id = 34 AND MediaType = 1 AND AudiobookMonitored = 0 AND EbookMonitored = 1);"), Is.EqualTo(2));
                Assert.That(File.Exists(reviewPath), Is.True);
                Assert.That(review, Does.Contain("\"sourceBookId\": 32"));
                Assert.That(review, Does.Contain("\"mediaType\": \"both\""));
                Assert.That(review, Does.Contain("\"monitored\": true"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
                Assert.That(DatabaseLineageGuard.Detect(ConnectionStringFor(backupPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Books WHERE Id = 32 AND Monitored = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(backupPath, "SELECT COUNT(*) FROM Editions WHERE BookId = 32;"), Is.Zero);
            });
        }

        [Test]
        public void should_keep_an_unmonitored_book_without_editions_unmonitored()
        {
            InsertBookWithoutEdition(32, monitored: false);
            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            var review = File.ReadAllText(_sourcePath + ".bookshelf-media-review.json");
            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id IN (32, 34) AND AudiobookMonitored = 0 AND EbookMonitored = 0;"), Is.EqualTo(2));
                Assert.That(review, Does.Contain("\"mediaType\": \"both-unmonitored\""));
                Assert.That(review, Does.Contain("\"monitored\": false"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_accept_an_explicit_both_unmonitored_media_choice()
        {
            InsertBookWithoutEdition(32, monitored: true);
            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 32, "mediaType": "both-unmonitored" }
                  ]
                }
                """);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id IN (32, 34) AND AudiobookMonitored = 0 AND EbookMonitored = 0;"), Is.EqualTo(2));
                Assert.That(File.Exists(_sourcePath + ".bookshelf-media-review.json"), Is.False);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_import_a_book_without_editions_using_the_explicit_both_formats_choice()
        {
            InsertBookWithoutEdition(32, monitored: true);
            File.WriteAllText(_sourcePath + ".bookshelf-media-review.json", "{\"version\":1,\"books\":[]}");
            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 32, "mediaType": "both" }
                  ]
                }
                """);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id IN (32, 34);"), Is.EqualTo(2));
                Assert.That(File.Exists(_sourcePath + ".bookshelf-media-review.json"), Is.False);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 32 AND MediaType = 0 AND AudiobookMonitored = 1 AND EbookMonitored = 0;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 34 AND MediaType = 1 AND AudiobookMonitored = 0 AND EbookMonitored = 1;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_apply_an_explicit_monitoring_choice_to_a_mixed_format_book_without_a_selected_edition()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE Editions SET Monitored = 0;";
                command.ExecuteNonQuery();
            }

            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 30, "mediaType": "ebook" }
                  ]
                }
                """);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND MediaType = 0 AND AudiobookMonitored = 0 AND EbookMonitored = 0;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 31 AND MediaType = 1 AND EbookMonitored = 1 AND ForeignEditionId IS NULL;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_preserve_unmapped_source_files_without_inventing_book_or_edition_relationships()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE BookFiles SET EditionId = 0 WHERE Id = 50;
                    INSERT INTO BookFiles (Id, EditionId, CalibreId, Quality, Size, DateAdded, MediaInfo, Modified, Path, Part)
                    VALUES (53, 0, 0, '{}', 150, '2026-01-05', '{}', '2026-01-05', '/library/Fixture Author/unknown.bin', 0);
                    INSERT INTO ExtraFiles (Id, AuthorId, BookId, BookFileId, RelativePath, Extension, Added, LastUpdated)
                    VALUES (62, 20, 30, 50, 'linked-cover.jpg', 'jpg', '2026-01-05', '2026-01-05');
                    INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Extension)
                    VALUES (63, 20, 'AudioBookShelf', 0, 'unmapped.opf', '2026-01-05', 30, 50, 'opf');
                    INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Extension)
                    VALUES (67, 20, 'AudiobookshelfMetadata', 0, 'legacy-consumer.opf', '2026-01-05', 30, 50, 'opf');
                    INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Extension)
                    VALUES (68, 20, 'Readarr', 1, 'author-metadata.json', '2026-01-05', NULL, NULL, '.json');
                    INSERT INTO Metadata (Id, Enable, Name, Implementation, Settings, ConfigContract)
                    VALUES (101, 1, 'Legacy OPF sidecars', 'AudiobookshelfMetadata', '{}', 'NullConfig');
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (94, 'book.m4b', '2026-01-05', '{}', '{""FileId"":""50"",""ImportedPath"":""/library/Fixture Author/book.m4b""}', 3, 'download-94', 20, 30);";
                command.ExecuteNonQuery();
            }

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 50 AND EditionId = 0 AND MediaType = 'audiobook' AND UnmappedReason LIKE '%recognized path extension%' AND UnmappedReason LIKE '%relationships remain unassigned%';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 53 AND EditionId = 0 AND MediaType = '' AND UnmappedReason LIKE '%not inferred%';"), Is.EqualTo(1),
                    "an unrecognized extension must not be used to classify an orphan file");
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Path FROM BookFiles WHERE Id = 50;"), Is.EqualTo("/library/Fixture Author/book.m4b"));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM ExtraFiles WHERE Id = 62 AND BookId = 30 AND BookFileId = 50;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM MetadataFiles WHERE Id = 63 AND BookId = 30 AND BookFileId = 50;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Consumer FROM MetadataFiles WHERE Id = 63;"), Is.EqualTo("LegacyAudiobookshelfMetadata"));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Consumer FROM MetadataFiles WHERE Id = 67;"), Is.EqualTo("LegacyAudiobookshelfMetadata"),
                    "the provider-name sidecar alias must win over Chaptarr's case-insensitive AudioBookShelf provider ID");
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Consumer FROM MetadataFiles WHERE Id = 68;"), Is.EqualTo("LegacyAudiobookshelfMetadata"),
                    "the legacy Readarr author-sidecar consumer must remain tracked by the compatible OPF metadata consumer");
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Metadata WHERE Id = 101 AND Enable = 1 AND Implementation = 'LegacyAudiobookshelfMetadata' AND ConfigContract = 'NullConfig';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 94 AND BookId = 30 AND EditionId IS NULL AND Data LIKE '%\"FileId\":\"50\"%';"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_preserve_book_files_that_reference_a_missing_edition_as_unmapped()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO BookFiles (Id, EditionId, CalibreId, Quality, Size, DateAdded, MediaInfo, Modified, Path, Part)
                    VALUES (52, 9999, 0, '{}', 300, '2026-01-04', '{}', '2026-01-04', '/library/Fixture Author/unmapped.epub', 0);
                    INSERT INTO ExtraFiles (Id, AuthorId, BookId, BookFileId, RelativePath, Extension, Added, LastUpdated)
                    VALUES (64, 20, 30, 52, 'unmapped-cover.jpg', 'jpg', '2026-01-06', '2026-01-06');
                    INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Extension)
                    VALUES (65, 20, 'AudioBookShelf', 0, 'unmapped.opf', '2026-01-06', 30, 52, 'opf');
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (95, 'unmapped.epub', '2026-01-06', '{}', '{""FileId"":""52"",""ImportedPath"":""/library/Fixture Author/unmapped.epub""}', 3, 'download-95', 20, 30);";
                command.ExecuteNonQuery();
            }

            var result = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM BookFiles WHERE Id = 52 AND EditionId = 0 AND MediaType = 'ebook' AND UnmappedReason LIKE '%missing edition%' AND UnmappedReason LIKE '%relationships remain unassigned%' AND Path = '/library/Fixture Author/unmapped.epub';"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM ExtraFiles WHERE Id = 64 AND BookId = 31 AND BookFileId = 52;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM MetadataFiles WHERE Id = 65 AND BookId = 31 AND BookFileId = 52;"), Is.EqualTo(1));
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 95 AND BookId = 31 AND EditionId IS NULL AND Data LIKE '%\"FileId\":\"52\"%';"), Is.EqualTo(1));
                Assert.That(result.ImportReport.Tables["BookFiles"].TargetMapping, Does.Contain("missing edition 1"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_preserve_history_with_a_cross_book_file_reference_without_relinking_it()
        {
            InsertBookWithoutEdition(32, monitored: true);
            const string historyData = "{\"FileId\":\"50\",\"ImportedPath\":\"/library/Fixture Author/book.m4b\"}";
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
                    VALUES (96, 'book.m4b', '2026-01-07', '{}', $data, 3, 'download-96', 20, 32);";
                command.Parameters.AddWithValue("$data", historyData);
                command.ExecuteNonQuery();
            }

            var result = new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).MigrateBookshelf(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly: false);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM History WHERE Id = 96 AND BookId = 32 AND EditionId IS NULL;"), Is.EqualTo(1));
                Assert.That(QueryScalar<string>(_sourcePath, "SELECT Data FROM History WHERE Id = 96;"), Is.EqualTo(historyData));
                Assert.That(result.ImportReport.Tables["History"].TargetMapping, Does.Contain("cross-book file references: 1"));
                Assert.That(result.ImportReport.Tables["History"].TargetMapping, Does.Contain("remain unlinked"));
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_an_incomplete_media_map_without_changing_the_source()
        {
            InsertBookWithoutEdition(32, monitored: true);
            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": []
                }
                """);
            var before = File.ReadAllBytes(_sourcePath);

            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("1 BookshelfNG book(s) need an explicit media type"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_ignore_a_stale_media_choice_when_the_book_is_now_classified()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = @"
                    UPDATE Editions SET Monitored = 0;
                    UPDATE Editions SET Format = 'Paperback', Narrator = NULL WHERE Id = 40;
                    UPDATE BookFiles SET Path = '/library/Fixture Author/ebook-only.epub' WHERE Id = 50;";
                command.ExecuteNonQuery();
            }

            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 30, "mediaType": "both" }
                  ]
                }
                """);

            new MigrationController(LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                _sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE MediaType = 0;"), Is.Zero);
                Assert.That(QueryScalar<int>(_sourcePath, "SELECT COUNT(*) FROM Books WHERE Id = 30 AND MediaType = 1 AND EbookMonitored = 1;"), Is.EqualTo(1));
                Assert.That(File.Exists(_sourcePath + ".bookshelf-media-map.json"), Is.True);
                Assert.That(File.Exists(_sourcePath + ".bookshelf-media-review.json"), Is.False);
                Assert.That(QueryScalar<string>(_sourcePath, "PRAGMA integrity_check;"), Is.EqualTo("ok"));
                Assert.That(QueryRows(_sourcePath, "PRAGMA foreign_key_check;"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_a_media_map_id_missing_from_the_current_source()
        {
            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 9999, "mediaType": "ebook" }
                  ]
                }
                """);
            var before = File.ReadAllBytes(_sourcePath);

            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("do not exist in the current source"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_keep_completed_media_choices_in_the_review_when_a_map_is_incomplete()
        {
            InsertBookWithoutEdition(32, monitored: true);
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "UPDATE Editions SET Monitored = 0;";
                command.ExecuteNonQuery();
            }

            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 30, "mediaType": "ebook" }
                  ]
                }
                """);
            var before = File.ReadAllBytes(_sourcePath);

            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));
            var review = File.ReadAllText(_sourcePath + ".bookshelf-media-review.json");

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("1 BookshelfNG book(s) need an explicit media type"));
                Assert.That(review, Does.Contain("\"sourceBookId\": 30"));
                Assert.That(review, Does.Contain("\"mediaType\": \"ebook\""));
                Assert.That(review, Does.Contain("\"sourceBookId\": 32"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_a_media_map_with_an_invalid_choice_without_changing_the_source()
        {
            InsertBookWithoutEdition(32, monitored: true);
            File.WriteAllText(_sourcePath + ".bookshelf-media-map.json", """
                {
                  "version": 1,
                  "books": [
                    { "sourceBookId": 32, "mediaType": "audio" }
                  ]
                }
                """);
            var before = File.ReadAllBytes(_sourcePath);

            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("invalid book ID or mediaType"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_nonempty_unmapped_setting_columns_without_changing_the_source()
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "ALTER TABLE Config ADD COLUMN UnmappedValue TEXT; UPDATE Config SET UnmappedValue = 'keep-me' WHERE Key = 'FixtureSetting';";
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(_sourcePath);
            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.That(exception.Message, Does.Contain("Config.UnmappedValue"));
            Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
        }

        [TestCase("QualityProfiles", 1)]
        [TestCase("MetadataProfiles", 1)]
        [TestCase("RootFolders", 1)]
        [TestCase("AuthorMetadata", 10)]
        [TestCase("Authors", 20)]
        [TestCase("Books", 30)]
        [TestCase("Editions", 40)]
        [TestCase("BookFiles", 50)]
        [TestCase("ExtraFiles", 60)]
        [TestCase("MetadataFiles", 61)]
        [TestCase("Series", 70)]
        [TestCase("SeriesBookLink", 80)]
        [TestCase("Blocklist", 92)]
        [TestCase("DownloadHistory", 91)]
        [TestCase("History", 93)]
        [TestCase("DownloadClients", 109)]
        [TestCase("ImportLists", 110)]
        [TestCase("Indexers", 120)]
        [TestCase("Metadata", 135)]
        [TestCase("Notifications", 111)]
        [TestCase("NamingConfig", 5)]
        public void should_refuse_nonempty_unmapped_migration_columns_without_changing_the_source(string table, int rowId)
        {
            using (var source = new SqliteConnection(_sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = table switch
                {
                    "QualityProfiles" => """
                        ALTER TABLE "QualityProfiles" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "QualityProfiles" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "MetadataProfiles" => """
                        ALTER TABLE "MetadataProfiles" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "MetadataProfiles" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "RootFolders" => """
                        ALTER TABLE "RootFolders" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "RootFolders" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "AuthorMetadata" => """
                        ALTER TABLE "AuthorMetadata" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "AuthorMetadata" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Authors" => """
                        ALTER TABLE "Authors" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Authors" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Books" => """
                        ALTER TABLE "Books" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Books" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Editions" => """
                        ALTER TABLE "Editions" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Editions" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "BookFiles" => """
                        ALTER TABLE "BookFiles" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "BookFiles" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "ExtraFiles" => """
                        ALTER TABLE "ExtraFiles" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "ExtraFiles" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "MetadataFiles" => """
                        ALTER TABLE "MetadataFiles" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "MetadataFiles" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Series" => """
                        ALTER TABLE "Series" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Series" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "SeriesBookLink" => """
                        ALTER TABLE "SeriesBookLink" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "SeriesBookLink" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Blocklist" => """
                        ALTER TABLE "Blocklist" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Blocklist" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "DownloadHistory" => """
                        ALTER TABLE "DownloadHistory" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "DownloadHistory" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "History" => """
                        ALTER TABLE "History" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "History" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "DownloadClients" => """
                        ALTER TABLE "DownloadClients" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "DownloadClients" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "ImportLists" => """
                        ALTER TABLE "ImportLists" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "ImportLists" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Indexers" => """
                        ALTER TABLE "Indexers" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Indexers" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "Metadata" => """
                        ALTER TABLE "Metadata" ADD COLUMN UnmappedMigrationValue TEXT;
                        INSERT INTO "Metadata" (Id, Enable, Name, Implementation, Settings, ConfigContract, UnmappedMigrationValue)
                        VALUES ($rowId, 1, 'Legacy OPF sidecar', 'AudiobookshelfMetadata', '{}', 'NullConfig', 'keep-me');
                        """,
                    "Notifications" => """
                        ALTER TABLE "Notifications" ADD COLUMN UnmappedMigrationValue TEXT;
                        UPDATE "Notifications" SET UnmappedMigrationValue = 'keep-me' WHERE Id = $rowId;
                        """,
                    "NamingConfig" => """
                        ALTER TABLE "NamingConfig" ADD COLUMN UnmappedMigrationValue TEXT;
                        INSERT INTO "NamingConfig" (Id, ReplaceIllegalCharacters, AuthorFolderFormat, RenameBooks, StandardBookFormat, ColonReplacementFormat, UnmappedMigrationValue)
                        VALUES ($rowId, 0, '{Author Name}', 1, '{Book Title}', 4, 'keep-me');
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unexpected migration table fixture")
                };
                command.Parameters.AddWithValue("$rowId", rowId);
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(_sourcePath);
            var exception = Assert.Throws<InvalidDataException>(() => new MigrationController(
                LogManager.GetLogger(nameof(BookshelfDatabaseImporterFixture)), null).Migrate(
                    _sourceConnectionString,
                    new MigrationContext(MigrationType.Main),
                    DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain($"{table}.UnmappedMigrationValue"));
                Assert.That(File.ReadAllBytes(_sourcePath), Is.EqualTo(before));
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        private void CreateSourceDatabase()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().Single(x => x.EndsWith("BookshelfNG.Schema.158.sql", StringComparison.Ordinal));
            using var schema = assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(schema);
            using var connection = new SqliteConnection(_sourceConnectionString);
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = reader.ReadToEnd();
            create.ExecuteNonQuery();

            var seedResourceName = assembly.GetManifestResourceNames().Single(x => x.EndsWith("BookshelfNG.PopulatedMigration158.sql", StringComparison.Ordinal));
            using var seed = assembly.GetManifestResourceStream(seedResourceName);
            using var seedReader = new StreamReader(seed);
            using var data = connection.CreateCommand();
            data.CommandText = seedReader.ReadToEnd();
            data.ExecuteNonQuery();
        }

        private void SetBareNumericProviderIds()
        {
            using var source = new SqliteConnection(_sourceConnectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = @"
                UPDATE AuthorMetadata SET ForeignAuthorId = '10' WHERE Id = 10;
                UPDATE Books SET ForeignBookId = '30' WHERE Id = 30;
                UPDATE Editions SET ForeignEditionId = '40' WHERE Id = 40;
                UPDATE Series SET ForeignSeriesId = '70' WHERE Id = 70;";
            command.ExecuteNonQuery();
        }

        private void InsertBookWithoutEdition(int id, bool monitored)
        {
            using var source = new SqliteConnection(_sourceConnectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = @"
                INSERT INTO Books (Id, AuthorMetadataId, ForeignBookId, TitleSlug, Title, Links, Genres, Ratings, CleanTitle, Monitored, AnyEditionOk, Added, RelatedBooks, LastSearchTime)
                VALUES ($id, 10, $foreignId, $slug, 'Unresolved fixture book', '[]', '[]', '{}', 'unresolved fixture book', $monitored, 0, '2026-01-05', '[]', NULL);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$foreignId", $"hardcover:book-{id}");
            command.Parameters.AddWithValue("$slug", $"no-edition-{id}");
            command.Parameters.AddWithValue("$monitored", monitored ? 1 : 0);
            command.ExecuteNonQuery();
        }

        private void DowngradeSourceToMigration22()
        {
            using var source = new SqliteConnection(_sourceConnectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = @"
                ALTER TABLE Authors DROP COLUMN EbookMetadataProfileId;
                ALTER TABLE Authors DROP COLUMN AudiobookMetadataProfileId;
                ALTER TABLE Authors DROP COLUMN EbookQualityProfileId;
                ALTER TABLE Authors DROP COLUMN AudiobookQualityProfileId;
                ALTER TABLE Authors DROP COLUMN EbookPath;
                ALTER TABLE Authors DROP COLUMN AudiobookPath;
                ALTER TABLE Editions DROP COLUMN Narrator;
                ALTER TABLE MetadataProfiles DROP COLUMN PreferredSeries;
                ALTER TABLE MetadataProfiles DROP COLUMN PreferredEditionTerms;
                ALTER TABLE BookFiles DROP COLUMN LastRemoteSearchTime;
                ALTER TABLE BookFiles DROP COLUMN IndexerFlags;
                ALTER TABLE Blocklist DROP COLUMN IndexerFlags;
                ALTER TABLE Books DROP COLUMN LastSearchTime;
                ALTER TABLE Notifications DROP COLUMN OnAuthorAdded;
                DROP TABLE NotificationStatus;
                ALTER TABLE Commands DROP COLUMN Result;
                ALTER TABLE DownloadClients DROP COLUMN Tags;
                ALTER TABLE DownloadClients DROP COLUMN RemoveCompletedDownloads;
                ALTER TABLE DownloadClients DROP COLUMN RemoveFailedDownloads;
                ALTER TABLE NamingConfig DROP COLUMN ColonReplacementFormat;
                ALTER TABLE Indexers DROP COLUMN DownloadClientId;
                ALTER TABLE Notifications DROP COLUMN OnApplicationUpdate;
                ALTER TABLE DelayProfiles DROP COLUMN BypassIfHighestQuality;
                ALTER TABLE DelayProfiles DROP COLUMN BypassIfAboveCustomFormatScore;
                ALTER TABLE DelayProfiles DROP COLUMN MinimumCustomFormatScore;
                ALTER TABLE BookFiles DROP COLUMN OriginalFilePath;
                ALTER TABLE QualityProfiles DROP COLUMN FormatItems;
                ALTER TABLE QualityProfiles DROP COLUMN MinFormatScore;
                ALTER TABLE QualityProfiles DROP COLUMN CutoffFormatScore;
                ALTER TABLE ReleaseProfiles ADD COLUMN Preferred TEXT;
                ALTER TABLE ReleaseProfiles ADD COLUMN IncludePreferredWhenRenaming INTEGER;
                DROP TABLE CustomFormats;
                ALTER TABLE Indexers DROP COLUMN Tags;
                ALTER TABLE ImportListStatus DROP COLUMN LastInfoSync;
                ALTER TABLE ImportListStatus ADD COLUMN LastSyncListInfo DATETIME;
                DELETE FROM VersionInfo WHERE Version > 22;";
            command.ExecuteNonQuery();
        }

        private void CreateSourceThroughMigration(long targetVersion)
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_sourcePath))
            {
                File.Delete(_sourcePath);
            }

            using var serviceProvider = new ServiceCollection()
                .AddLogging(builder => builder.AddNLog())
                .AddFluentMigratorCore()
                .Configure<RunnerOptions>(options => options.IncludeUntaggedMaintenances = true)
                .ConfigureRunner(builder => builder
                    .AddNzbDroneSQLite()
                    .WithGlobalConnectionString(_sourceConnectionString)
                    .ScanIn(typeof(MigrationController).Assembly).For.All())
                .Configure<TypeFilterOptions>(options => options.Namespace = "NzbDrone.Core.Datastore.BookshelfMigration")
                .Configure<ProcessorOptions>(options =>
                {
                    options.PreviewOnly = false;
                    options.Timeout = TimeSpan.FromMinutes(5);
                })
                .Configure<SelectingProcessorAccessorOptions>(options => options.ProcessorId = "SQLite")
                .Configure<SelectingGeneratorAccessorOptions>(options => options.GeneratorId = "SQLite")
                .BuildServiceProvider();

            using var scope = serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            MigrationContext.Current = new MigrationContext(MigrationType.Main);
            try
            {
                runner.MigrateUp(targetVersion);
            }
            finally
            {
                MigrationContext.Current = null;
            }
        }

        private void SeedMigration22SourceWithLibrary()
        {
            using var source = new SqliteConnection(_sourceConnectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = @"
                INSERT INTO Config (Key, Value) VALUES ('FixtureSetting', 'preserved');
                INSERT INTO QualityProfiles (Id, Name, Cutoff, Items, UpgradeAllowed)
                VALUES (1, 'Migration 22 profile', 1, '[]', 1);
                INSERT INTO MetadataProfiles (Id, Name, MinPopularity, SkipMissingDate, SkipMissingIsbn, SkipPartsAndSets, SkipSeriesSecondary, AllowedLanguages)
                VALUES (1, 'Migration 22 metadata', 0, 0, 0, 0, 0, '[]');
                INSERT INTO RootFolders (Id, Path, Name, DefaultMetadataProfileId, DefaultQualityProfileId, DefaultMonitorOption, DefaultTags, IsCalibreLibrary)
                VALUES (1, '/library', 'Library', 1, 1, 0, '[]', 0);
                INSERT INTO AuthorMetadata (Id, ForeignAuthorId, TitleSlug, Name, Overview, Status, Images, Aliases, SortName, NameLastFirst, SortNameLastFirst)
                VALUES (10, 'hardcover-author:author-10', 'migration-22-author', 'Migration 22 Author', 'Older-schema author', 0, '[]', '[]', 'Migration 22 Author', 'Author, Migration 22', 'Author, Migration 22');
                INSERT INTO Authors (Id, CleanName, Path, Monitored, QualityProfileId, Tags, Added, MetadataProfileId, AuthorMetadataId)
                VALUES (20, 'migration22author', '/library/Migration 22 Author', 1, 1, '[]', '2026-01-01', 1, 10);
                INSERT INTO Books (Id, AuthorMetadataId, ForeignBookId, TitleSlug, Title, Links, Genres, Ratings, CleanTitle, Monitored, AnyEditionOk, Added)
                VALUES (30, 10, 'hardcover:book-30', 'migration-22-book', 'Migration 22 fixture book', '[]', '[]', '{}', 'migration 22 fixture book', 1, 0, '2026-01-02');
                INSERT INTO Editions (Id, BookId, ForeignEditionId, Title, TitleSlug, Images, IsEbook, Monitored, ManualAdd)
                VALUES (40, 30, 'hardcover-edition:edition-40', 'Migration 22 audiobook', 'migration-22-edition', '[]', 0, 1, 0);
                INSERT INTO BookFiles (Id, EditionId, CalibreId, Quality, Size, DateAdded, MediaInfo, Path)
                VALUES (50, 40, 0, '{}', 1024, '2026-01-02', '{}', '/library/Migration 22 Author/book.m4b');";
            command.ExecuteNonQuery();
        }

        private static string ConnectionStringFor(string path) => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString;

        private void AddConfigValue(string key, string value)
        {
            using var source = new SqliteConnection(_sourceConnectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = "INSERT INTO Config (Key, Value) VALUES (@key, @value);";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            command.ExecuteNonQuery();
        }

        private static T QueryScalar<T>(string databasePath, string sql)
        {
            using var connection = new SqliteConnection(ConnectionStringFor(databasePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- sql is fixed fixture query text authored in this test.
            return (T)Convert.ChangeType(command.ExecuteScalar(), typeof(T));
        }

        private static object[] QueryRows(string databasePath, string sql)
        {
            using var connection = new SqliteConnection(ConnectionStringFor(databasePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- sql is fixed fixture query text authored in this test.
            using var reader = command.ExecuteReader();
            var rows = new System.Collections.Generic.List<object>();
            while (reader.Read()) rows.Add(reader.GetValue(0));
            return rows.ToArray();
        }
    }
}
