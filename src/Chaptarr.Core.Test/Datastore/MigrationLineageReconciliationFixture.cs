using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class MigrationLineageReconciliationFixture
    {
        [Test]
        public void should_repair_active_backlog_features_when_114_and_115_were_used_for_ported_features()
        {
            WithDatabase(115, @"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AudiobookRootFolderPath"" TEXT NULL,
                    ""EbookRootFolderPath"" TEXT NULL
                );
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""MediaType"" TEXT NOT NULL
                );
                CREATE TABLE ""RootFolders"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Path"" TEXT NOT NULL,
                    ""ReapCalibreDuplicates"" INTEGER NOT NULL DEFAULT 0,
                    ""AutoPushCalibreMetadata"" INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE ""MetadataProfiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Name"" TEXT NOT NULL,
                    ""PreferredEditionTerms"" TEXT NULL,
                    ""PreferredSeries"" TEXT NULL
                );
                INSERT INTO ""Authors"" (""Id"", ""AudiobookRootFolderPath"", ""EbookRootFolderPath"")
                    VALUES (1, '/audio', '/ebooks');
                INSERT INTO ""Books"" (""Id"", ""MediaType"") VALUES (1, 'ebook');
                INSERT INTO ""RootFolders"" (""Id"", ""Path"") VALUES (1, '/library');
                INSERT INTO ""MetadataProfiles"" (""Id"", ""Name"") VALUES (1, 'Existing profile');
            ");
        }

        [Test]
        public void should_keep_active_backlog_schema_when_applying_the_new_migrations()
        {
            WithDatabase(116, @"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AudiobookRootFolderPath"" TEXT NULL,
                    ""EbookRootFolderPath"" TEXT NULL,
                    ""RanobeDbAuthorId"" TEXT NULL
                );
                CREATE UNIQUE INDEX ""UX_Authors_RanobeDbAuthorId"" ON ""Authors"" (""RanobeDbAuthorId"");
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""MediaType"" TEXT NOT NULL,
                    ""RanobeDbBookId"" TEXT NULL
                );
                CREATE UNIQUE INDEX ""UX_Books_RanobeDbBookId_MediaType""
                    ON ""Books"" (""RanobeDbBookId"", ""MediaType"");
                CREATE TABLE ""ImportListSyncHistory"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""ImportListId"" INTEGER NOT NULL,
                    ""ImportListName"" TEXT NOT NULL,
                    ""StartedAt"" TEXT NOT NULL,
                    ""EndedAt"" TEXT NULL,
                    ""DurationMilliseconds"" INTEGER NOT NULL DEFAULT 0,
                    ""Result"" TEXT NOT NULL,
                    ""FailureStage"" TEXT NULL,
                    ""DryRun"" INTEGER NOT NULL DEFAULT 0,
                    ""ProcessedCount"" INTEGER NOT NULL DEFAULT 0,
                    ""NewCount"" INTEGER NOT NULL DEFAULT 0,
                    ""ExistingCount"" INTEGER NOT NULL DEFAULT 0,
                    ""ChangedCount"" INTEGER NOT NULL DEFAULT 0,
                    ""ExcludedCount"" INTEGER NOT NULL DEFAULT 0,
                    ""UnresolvedCount"" INTEGER NOT NULL DEFAULT 0,
                    ""SearchEligibleCount"" INTEGER NOT NULL DEFAULT 0,
                    ""ProviderStateCommitted"" INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX ""IX_ImportListSyncHistory_ImportListId_StartedAt""
                    ON ""ImportListSyncHistory"" (""ImportListId"", ""StartedAt"" DESC);
                CREATE INDEX ""IX_ImportListSyncHistory_StartedAt""
                    ON ""ImportListSyncHistory"" (""StartedAt"" DESC);
                CREATE TABLE ""RootFolders"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Path"" TEXT NOT NULL
                );
                CREATE TABLE ""MetadataProfiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Name"" TEXT NOT NULL
                );
                INSERT INTO ""Authors"" (""Id"", ""AudiobookRootFolderPath"", ""EbookRootFolderPath"", ""RanobeDbAuthorId"")
                    VALUES (1, '/audio', '/ebooks', 'author-1');
                INSERT INTO ""Books"" (""Id"", ""MediaType"", ""RanobeDbBookId"") VALUES (1, 'ebook', 'book-1');
                INSERT INTO ""ImportListSyncHistory"" (""Id"", ""ImportListId"", ""ImportListName"", ""StartedAt"", ""Result"")
                    VALUES (1, 9, 'Existing list', '2026-10-12', 'completed');
                INSERT INTO ""RootFolders"" (""Id"", ""Path"") VALUES (1, '/library');
                INSERT INTO ""MetadataProfiles"" (""Id"", ""Name"") VALUES (1, 'Existing profile');
            ");
        }

        private static void WithDatabase(int appliedVersion, string schema)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"migration_lineage_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        CREATE TABLE ""VersionInfo"" (
                            ""Version"" INTEGER PRIMARY KEY,
                            ""AppliedOn"" TEXT NULL,
                            ""Description"" TEXT NULL
                        );
                        WITH RECURSIVE versions(version) AS
                        (
                            SELECT 1
                            UNION ALL
                            SELECT version + 1 FROM versions WHERE version < @appliedVersion
                        )
                        INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                        SELECT version, CURRENT_TIMESTAMP, 'existing branch migration' FROM versions;
                    ", new { appliedVersion });
                    connection.Execute(schema);
                }

                var migrationController = new MigrationController(
                    LogManager.GetLogger(nameof(MigrationLineageReconciliationFixture)),
                    null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Main, 119),
                    DatabaseType.SQLite);

                using var resultConnection = new SqliteConnection(connectionString);
                resultConnection.Open();
                var authorColumns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('Authors');").AsList();
                var bookColumns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('Books');").AsList();
                var rootColumns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('RootFolders');").AsList();
                var profileColumns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('MetadataProfiles');").AsList();
                var historyColumns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('ImportListSyncHistory');").AsList();
                var indexNames = resultConnection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'index';").AsList();

                Assert.Multiple(() =>
                {
                    Assert.That(authorColumns, Does.Contain("RanobeDbAuthorId"));
                    Assert.That(bookColumns, Does.Contain("RanobeDbBookId"));
                    Assert.That(rootColumns, Does.Contain("ReapCalibreDuplicates"));
                    Assert.That(rootColumns, Does.Contain("AutoPushCalibreMetadata"));
                    Assert.That(profileColumns, Does.Contain("PreferredEditionTerms"));
                    Assert.That(profileColumns, Does.Contain("PreferredSeries"));
                    Assert.That(historyColumns, Does.Contain("ProviderStateCommitted"));
                    Assert.That(resultConnection.QuerySingle<string>("SELECT Name FROM MetadataProfiles WHERE Id = 1;"), Is.EqualTo("Existing profile"));
                    Assert.That(resultConnection.QuerySingle<int>("SELECT COUNT(*) FROM Authors WHERE Id = 1 AND AudiobookRootFolderPath = '/audio' AND EbookRootFolderPath = '/ebooks';"), Is.EqualTo(1));
                    Assert.That(resultConnection.QuerySingle<int>("SELECT COUNT(*) FROM Books WHERE Id = 1 AND MediaType = 'ebook';"), Is.EqualTo(1));
                    Assert.That(resultConnection.QuerySingle<int>("SELECT COUNT(*) FROM RootFolders WHERE Id = 1 AND Path = '/library';"), Is.EqualTo(1));
                    Assert.That(indexNames, Does.Contain("UX_Books_RanobeDbBookId_MediaType"));
                    Assert.That(indexNames, Does.Contain("UX_Authors_RanobeDbAuthorId"));
                    Assert.That(indexNames, Does.Contain("IX_ImportListSyncHistory_ImportListId_StartedAt"));
                    Assert.That(indexNames, Does.Contain("IX_ImportListSyncHistory_StartedAt"));
                });
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }
    }
}
