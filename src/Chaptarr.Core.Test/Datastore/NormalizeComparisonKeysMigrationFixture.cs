using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
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
                CreateKeyAudit(connection, postgres: false);
                InsertRows(connection);
                var before = SnapshotSourceValues(connection);

                ApplyMigration(connectionString, DatabaseType.SQLite);

                Assert.That(connection.QuerySingle<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(113));
                AssertPersistedValues(connection);
                AssertPreservedAndIdempotent(connection, connectionString, before, DatabaseType.SQLite);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName='Books' AND RowId = 3;"), Is.Zero,
                    "an already-correct key should not be rewritten");
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName='Editions' AND RowId = 1;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName='Editions' AND RowId = 4;"), Is.Zero,
                    "an already-correct edition key should not be rewritten");
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '\"ready player 2\"' AND rowid IN (1, 2);"), Is.EqualTo(1),
                    "the edition update trigger should distinguish words from normalized numerals");
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '\"dune ii\"' AND rowid = 10;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '1q84' AND rowid = 3;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'dune' AND rowid = 4;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'robot' AND rowid = 5;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH '\"23.5 lies\"' AND rowid IN (6, 7);"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'がとう' AND rowid IN (11, 12);"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'かとう' AND rowid IN (11, 12);"), Is.EqualTo(1));
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
                CreateKeyAudit(connection, postgres: true);
                InsertRows(connection);
                var before = SnapshotSourceValues(connection);
                connection.Execute(@"
                    CREATE INDEX test_books_clean_title ON ""Books"" USING GIN (to_tsvector('simple', COALESCE(""CleanTitle"", '')));
                    CREATE INDEX test_authors_clean_name ON ""Authors"" USING GIN (to_tsvector('simple', COALESCE(""CleanName"", '')));
                    CREATE INDEX test_editions_matching_title ON ""Editions"" USING GIN (to_tsvector('simple', COALESCE(""MatchingTitle"", '')));");

                ApplyMigration(connectionString, DatabaseType.PostgreSQL);

                Assert.That(connection.QuerySingle<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(113));
                AssertPersistedValues(connection);
                AssertPreservedAndIdempotent(connection, connectionString, before, DatabaseType.PostgreSQL);
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
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Editions""
                    WHERE to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ plainto_tsquery('simple', 'dune ii');"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Editions""
                    WHERE ""Id"" IN (11, 12) AND to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ plainto_tsquery('simple', 'がとう');"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>(@"
                    SELECT COUNT(*) FROM ""Editions""
                    WHERE ""Id"" IN (11, 12) AND to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ plainto_tsquery('simple', 'かとう');"), Is.EqualTo(1));
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
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM generate_series(1, 112) AS version;
                    CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""CleanTitle"" TEXT NOT NULL, ""AuthorId"" INTEGER NOT NULL DEFAULT 1, ""MediaType"" INTEGER NOT NULL DEFAULT 0, ""ForeignBookId"" TEXT);
                    CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL, ""CleanName"" TEXT NOT NULL, ""ForeignAuthorId"" TEXT);
                    CREATE TABLE ""NarratorMetadata"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL);
                    CREATE TABLE ""Narrators"" (""Id"" INTEGER PRIMARY KEY, ""NarratorMetadataId"" INTEGER NOT NULL, ""CleanName"" TEXT NULL);
                    CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""MatchingTitle"" TEXT NULL, ""BookId"" INTEGER NOT NULL DEFAULT 1, ""ForeignEditionId"" TEXT);
                    CREATE TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER NOT NULL, ""EditionId"" INTEGER NOT NULL, ""RelativePath"" TEXT NOT NULL);"
                : @"
                    CREATE TABLE ""VersionInfo"" (""Version"" INTEGER PRIMARY KEY, ""AppliedOn"" TEXT NULL, ""Description"" TEXT NULL);
                    WITH RECURSIVE versions(version) AS
                    (
                        SELECT 1
                        UNION ALL SELECT version + 1 FROM versions WHERE version < 112
                    )
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
                    CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""CleanTitle"" TEXT NOT NULL, ""AuthorId"" INTEGER NOT NULL DEFAULT 1, ""MediaType"" INTEGER NOT NULL DEFAULT 0, ""ForeignBookId"" TEXT);
                    CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL, ""CleanName"" TEXT NOT NULL, ""ForeignAuthorId"" TEXT);
                    CREATE TABLE ""NarratorMetadata"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL);
                    CREATE TABLE ""Narrators"" (""Id"" INTEGER PRIMARY KEY, ""NarratorMetadataId"" INTEGER NOT NULL, ""CleanName"" TEXT NULL);
                    CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""MatchingTitle"" TEXT NULL, ""BookId"" INTEGER NOT NULL DEFAULT 1, ""ForeignEditionId"" TEXT);
                    CREATE TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER NOT NULL, ""EditionId"" INTEGER NOT NULL, ""RelativePath"" TEXT NOT NULL);
                    CREATE VIRTUAL TABLE edition_fts USING fts5(MatchingTitle, content='');
                    CREATE TRIGGER edition_fts_au AFTER UPDATE OF MatchingTitle ON Editions BEGIN
                        INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle) VALUES('delete', old.Id, old.MatchingTitle);
                        INSERT INTO edition_fts(rowid, MatchingTitle) VALUES(new.Id, new.MatchingTitle);
                    END;");
        }

        private static void CreateKeyAudit(IDbConnection connection, bool postgres)
        {
            connection.Execute("CREATE TABLE KeyUpdates (TableName TEXT NOT NULL, RowId INTEGER NOT NULL);");
            if (postgres)
            {
                connection.Execute(@"
                    CREATE FUNCTION audit_comparison_key_update() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        INSERT INTO KeyUpdates(TableName, RowId) VALUES (TG_TABLE_NAME, NEW.""Id"");
                        RETURN NEW;
                    END;
                    $$;");
            }

            foreach (var (table, column) in new[] { ("Books", "CleanTitle"), ("Authors", "CleanName"), ("Narrators", "CleanName"), ("Editions", "MatchingTitle") })
            {
                var action = postgres
                    ? "FOR EACH ROW EXECUTE FUNCTION audit_comparison_key_update();"
                    : $"BEGIN INSERT INTO KeyUpdates(TableName, RowId) VALUES ('{table}', new.Id); END;";
                connection.Execute($"CREATE TRIGGER key_audit_{table} AFTER UPDATE OF \"{column}\" ON \"{table}\" {action}");
            }
        }

        private static string SnapshotSourceValues(IDbConnection connection)
        {
            var queries = new[]
            {
                "SELECT \"Id\", \"Title\", \"AuthorId\", \"ForeignBookId\" FROM \"Books\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"Title\", \"BookId\", \"ForeignEditionId\" FROM \"Editions\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"Name\", \"ForeignAuthorId\" FROM \"Authors\" ORDER BY \"Id\";",
                "SELECT * FROM \"NarratorMetadata\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"NarratorMetadataId\" FROM \"Narrators\" ORDER BY \"Id\";",
                "SELECT * FROM \"BookFiles\" ORDER BY \"Id\";"
            };
            return JsonSerializer.Serialize(queries.Select(sql => connection.Query(sql).ToArray()).ToArray());
        }

        private static void AssertPreservedAndIdempotent(IDbConnection connection, string connectionString, string before, DatabaseType databaseType)
        {
            Assert.That(SnapshotSourceValues(connection), Is.EqualTo(before));
            foreach (var (table, count) in new[] { ("Books", 9), ("Authors", 5), ("Narrators", 5), ("Editions", 10) })
            {
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName=@table;", new { table }), Is.EqualTo(count), table);
            }

            var updates = connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;");
            ApplyMigration(connectionString, databaseType);
            Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(updates));
            Assert.That(SnapshotSourceValues(connection), Is.EqualTo(before));
        }

        private static void InsertRows(IDbConnection connection)
        {
            connection.Execute(@"
                INSERT INTO ""Books"" (""Id"", ""Title"", ""CleanTitle"") VALUES
                    (1, 'Ready Player Two', 'readyplayer2'),
                    (2, 'Ready Player 𝟚', 'readyplayer𝟚'),
                    (3, 'Dune 1', 'dune1'),
                    (4, '23 ½ Lies', '23lies'),
                    (5, '23 1/2 Lies', '2312lies'),
                    (6, 'Dune ⅩⅣ', 'dunewrong'),
                    (7, 'Dune ⅯⅠⅮ', 'dunemidwrong'),
                    (8, 'Dune Ⅱ', 'dune2'),
                    (9, 'がとう', 'かとう'),
                    (10, 'かとう', 'かとう'),
                    (11, 'Café', 'café');
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"") VALUES (1, 'Jane One', 'jane1'), (2, 'Jane 𝟙', 'jane𝟙'), (3, 'Correct Author', 'correctauthor'), (4, 'Author ⅮⅤⅮ', 'authorwrong'), (5, 'がとう', 'かとう'), (6, 'かとう', 'かとう'), (7, 'José', 'josé');
                INSERT INTO ""NarratorMetadata"" (""Id"", ""Name"") VALUES (1, 'Mary Three'), (2, 'Mary 𝟛'), (3, 'Correct Narrator'), (4, 'Narrator ⅮⅤⅮ'), (5, 'がとう'), (6, 'かとう'), (7, 'José');
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") VALUES (1, 1, 'mary3'), (2, 2, 'mary𝟛'), (3, 99, 'orphanpreserved'), (4, 3, 'correctnarrator'), (5, 4, 'narratorwrong'), (6, 5, 'かとう'), (7, 6, 'かとう'), (8, 7, 'josé');
                INSERT INTO ""Editions"" (""Id"", ""Title"", ""MatchingTitle"") VALUES
                    (1, 'Ready Player Two', 'ready player 2'),
                    (2, 'Ready Player 𝟚', 'ready player 𝟚'),
                    (3, '１Q84', '１q84'),
                    (4, 'Dune 1', 'dune 1'),
                    (5, 'I, Robot', 'i, robot'),
                    (6, '23 ½ Lies', '23 lies'),
                    (7, '23 1/2 Lies', '23 1 2 lies'),
                    (8, 'Dune ⅩⅣ', 'dune x iv'),
                    (9, 'Dune ⅯⅠⅮ', 'dune m i d'),
                    (10, 'Dune Ⅱ', 'dune 2'),
                    (11, 'がとう', 'かとう'),
                    (12, 'かとう', 'かとう'),
                    (13, 'Café', 'café');
                UPDATE ""Books"" SET ""ForeignBookId"" = 'test:book-one' WHERE ""Id""=1;
                UPDATE ""Authors"" SET ""ForeignAuthorId"" = 'test:author-one' WHERE ""Id""=1;
                UPDATE ""Editions"" SET ""ForeignEditionId"" = 'test:edition-one' WHERE ""Id""=1;
                INSERT INTO ""BookFiles"" (""Id"", ""BookId"", ""EditionId"", ""RelativePath"") VALUES (1, 1, 1, 'ready-player-two.m4b');");

            if (connection is SqliteConnection)
            {
                connection.Execute(@"
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (1, 'ready player 2');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (2, 'ready player 𝟚');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (3, '１q84');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (4, 'dune 1');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (5, 'i, robot');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (6, '23 lies');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (7, '23 1 2 lies');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (8, 'dune x iv');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (9, 'dune m i d');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (10, 'dune 2');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (11, 'かとう');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (12, 'かとう');
                    INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (13, 'café');");
            }
        }

        private static void AssertPersistedValues(IDbConnection connection)
        {
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=9;"), Is.EqualTo("がとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=10;"), Is.EqualTo("かとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=11;"), Is.EqualTo("cafe"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\"=5;"), Is.EqualTo("がとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\"=6;"), Is.EqualTo("かとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\"=7;"), Is.EqualTo("jose"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\"=6;"), Is.EqualTo("がとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\"=7;"), Is.EqualTo("かとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\"=8;"), Is.EqualTo("jose"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=11;"), Is.EqualTo("がとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=12;"), Is.EqualTo("かとう"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=13;"), Is.EqualTo("cafe"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=6;"), Is.EqualTo("dunexiv"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=7;"), Is.EqualTo("dunemid"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=8;"), Is.EqualTo("duneii"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\"=3;"), Is.EqualTo("correctauthor"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\"=4;"), Is.EqualTo("authordvd"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\"=4;"), Is.EqualTo("correctnarrator"));
            Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\"=5;"), Is.EqualTo("narratordvd"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=8;"), Is.EqualTo("dune xiv"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=9;"), Is.EqualTo("dune mid"));
            Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=10;"), Is.EqualTo("dune ii"));
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
            migrationController.Migrate(connectionString, new MigrationContext(MigrationType.Main, 113), databaseType);
        }
    }
}
