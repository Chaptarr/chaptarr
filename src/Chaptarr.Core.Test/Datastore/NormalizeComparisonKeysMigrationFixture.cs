using System;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class NormalizeComparisonKeysMigrationFixture
    {
        [Test]
        public void should_rekey_persisted_keys_and_keep_sqlite_edition_fts_current()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"normalize_comparison_keys_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                CreateSchema(connection, postgres: false);
                InsertRows(connection);

                ApplyMigration(connectionString, DatabaseType.SQLite);

                Assert.That(connection.QuerySingle<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(108));
                AssertPersistedValues(connection);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM CleanTitleUpdates WHERE Id = 3;"), Is.Zero,
                    "an already-correct key should not be rewritten");
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM EditionUpdates WHERE Id = 1;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM EditionUpdates WHERE Id = 4;"), Is.Zero,
                    "an already-correct edition key should not be rewritten");
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '\"ready player 2\"' AND rowid IN (1, 2);"), Is.EqualTo(1),
                    "the edition update trigger should distinguish words from normalized numerals");
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '1q84' AND rowid = 3;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'dune' AND rowid = 4;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'robot' AND rowid = 5;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '\"23.5 lies\"' AND rowid IN (6, 7);"), Is.EqualTo(2));
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        [Test]
        public void should_rekey_postgres_rows_and_keep_expression_index_search_results()
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL comparison-key migration");
            }

            var databaseName = "comparison_keys_" + Guid.NewGuid().ToString("N");
            using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                adminConnection.Open();
                adminConnection.Execute($"CREATE DATABASE \"{databaseName}\"");
            }

            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
                {
                    Database = databaseName,
                    Pooling = false
                }.ConnectionString;

                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                CreateSchema(connection, postgres: true);
                InsertRows(connection);
                connection.Execute(@"
                    CREATE INDEX test_books_clean_title ON ""Books"" USING GIN (to_tsvector('simple', COALESCE(""CleanTitle"", '')));
                    CREATE INDEX test_authors_clean_name ON ""Authors"" USING GIN (to_tsvector('simple', COALESCE(""CleanName"", '')));
                    CREATE INDEX test_editions_matching_title ON ""Editions"" USING GIN (to_tsvector('simple', COALESCE(""MatchingTitle"", '')));");

                ApplyMigration(connectionString, DatabaseType.PostgreSQL);

                Assert.That(connection.QuerySingle<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(108));
                AssertPersistedValues(connection);
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Books""
                    WHERE to_tsvector('simple', COALESCE(""CleanTitle"", '')) @@ plainto_tsquery('simple', 'readyplayer2');"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Authors""
                    WHERE to_tsvector('simple', COALESCE(""CleanName"", '')) @@ plainto_tsquery('simple', 'jane1');"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Editions""
                    WHERE to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ plainto_tsquery('simple', 'ready player 2');"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Editions""
                    WHERE to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ plainto_tsquery('simple', '1q84');"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Editions""
                    WHERE to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ plainto_tsquery('simple', '23.5 lies');"), Is.EqualTo(2));
            }
            finally
            {
                using var adminConnection = new NpgsqlConnection(adminConnectionString);
                adminConnection.Open();
                adminConnection.Execute($"DROP DATABASE IF EXISTS \"{databaseName}\"");
            }
        }

        private static void CreateSchema(IDbConnection connection, bool postgres)
        {
            connection.Execute(postgres
                ? @"
                    CREATE TABLE ""VersionInfo"" (""Version"" INTEGER PRIMARY KEY, ""AppliedOn"" TIMESTAMP NULL, ""Description"" TEXT NULL);
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM generate_series(1, 107) AS version;
                    CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""CleanTitle"" TEXT NOT NULL);
                    CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL, ""CleanName"" TEXT NOT NULL);
                    CREATE TABLE ""NarratorMetadata"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL);
                    CREATE TABLE ""Narrators"" (""Id"" INTEGER PRIMARY KEY, ""NarratorMetadataId"" INTEGER NOT NULL, ""CleanName"" TEXT NULL);
                    CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""MatchingTitle"" TEXT NULL);"
                : @"
                    CREATE TABLE ""VersionInfo"" (""Version"" INTEGER PRIMARY KEY, ""AppliedOn"" TEXT NULL, ""Description"" TEXT NULL);
                    WITH RECURSIVE versions(version) AS
                    (
                        SELECT 1
                        UNION ALL SELECT version + 1 FROM versions WHERE version < 107
                    )
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
                    CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""CleanTitle"" TEXT NOT NULL);
                    CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL, ""CleanName"" TEXT NOT NULL);
                    CREATE TABLE ""NarratorMetadata"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL);
                    CREATE TABLE ""Narrators"" (""Id"" INTEGER PRIMARY KEY, ""NarratorMetadataId"" INTEGER NOT NULL, ""CleanName"" TEXT NULL);
                    CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""MatchingTitle"" TEXT NULL);
                    CREATE TABLE CleanTitleUpdates (Id INTEGER NOT NULL);
                    CREATE TABLE EditionUpdates (Id INTEGER NOT NULL);
                    CREATE VIRTUAL TABLE edition_fts USING fts5(MatchingTitle, content='');
                    CREATE TRIGGER edition_fts_au AFTER UPDATE OF MatchingTitle ON Editions BEGIN
                        INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle) VALUES('delete', old.Id, old.MatchingTitle);
                        INSERT INTO edition_fts(rowid, MatchingTitle) VALUES(new.Id, new.MatchingTitle);
                    END;
                    CREATE TRIGGER clean_title_audit AFTER UPDATE OF CleanTitle ON Books BEGIN
                        INSERT INTO CleanTitleUpdates(Id) VALUES(new.Id);
                    END;
                    CREATE TRIGGER edition_audit AFTER UPDATE OF MatchingTitle ON Editions BEGIN
                        INSERT INTO EditionUpdates(Id) VALUES(new.Id);
                    END;");
        }

        private static void InsertRows(IDbConnection connection)
        {
            connection.Execute(@"
                INSERT INTO ""Books"" (""Id"", ""Title"", ""CleanTitle"") VALUES
                    (1, 'Ready Player Two', 'readyplayer2'),
                    (2, 'Ready Player 𝟚', 'readyplayer𝟚'),
                    (3, 'Dune 1', 'dune1'),
                    (4, '23 ½ Lies', '23lies'),
                    (5, '23 1/2 Lies', '2312lies');
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"") VALUES (1, 'Jane One', 'jane1'), (2, 'Jane 𝟙', 'jane𝟙');
                INSERT INTO ""NarratorMetadata"" (""Id"", ""Name"") VALUES (1, 'Mary Three'), (2, 'Mary 𝟛');
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") VALUES (1, 1, 'mary3'), (2, 2, 'mary𝟛'), (3, 99, 'orphanpreserved');
                INSERT INTO ""Editions"" (""Id"", ""Title"", ""MatchingTitle"") VALUES
                    (1, 'Ready Player Two', 'ready player 2'),
                    (2, 'Ready Player 𝟚', 'ready player 𝟚'),
                    (3, '１Q84', '１q84'),
                    (4, 'Dune 1', 'dune 1'),
                    (5, 'I, Robot', 'i, robot'),
                    (6, '23 ½ Lies', '23 lies'),
                    (7, '23 1/2 Lies', '23 1 2 lies');");

            if (connection is SqliteConnection)
            {
                connection.Execute(@"
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (1, 'ready player 2');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (2, 'ready player 𝟚');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (3, '１q84');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (4, 'dune 1');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (5, 'i, robot');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (6, '23 lies');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (7, '23 1 2 lies');");
            }
        }

        private static void AssertPersistedValues(IDbConnection connection)
        {
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\" = 1;"), Is.EqualTo("readyplayertwo"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\" = 2;"), Is.EqualTo("readyplayer2"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\" = 3;"), Is.EqualTo("dune1"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\" = 4;"), Is.EqualTo("235lies"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\" = 5;"), Is.EqualTo("235lies"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\" = 1;"), Is.EqualTo("janeone"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\" = 2;"), Is.EqualTo("jane1"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\" = 1;"), Is.EqualTo("marythree"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\" = 2;"), Is.EqualTo("mary3"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\" = 3;"), Is.EqualTo("orphanpreserved"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 1;"), Is.EqualTo("ready player two"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 2;"), Is.EqualTo("ready player 2"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 3;"), Is.EqualTo("1q84"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 4;"), Is.EqualTo("dune 1"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 5;"), Is.EqualTo("i, robot"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 6;"), Is.EqualTo("23.5 lies"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" = 7;"), Is.EqualTo("23.5 lies"));
        }

        private static void ApplyMigration(string connectionString, DatabaseType databaseType)
        {
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(NormalizeComparisonKeysMigrationFixture)), null);
            migrationController.Migrate(connectionString, new MigrationContext(MigrationType.Main, 108), databaseType);
        }
    }
}
