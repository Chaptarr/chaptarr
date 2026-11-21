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
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class EditionFtsDeletionMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void repair_should_remove_historical_entries_without_changing_library_records(bool missingIndex)
        {
            WithDatabase(false, (connection, connectionString) =>
            {
                connection.Execute("INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (99, 'ready player two');");
                var before = Snapshot(connection);
                if (missingIndex) connection.Execute("DROP TABLE edition_fts;");
                Apply(connectionString, false, 110);

                Assert.Multiple(() =>
                {
                    Assert.That(Snapshot(connection), Is.EqualTo(before));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts;"), Is.EqualTo(2));
                    Assert.That(Hits(connection, 99, "ready"), Is.Zero);
                    Assert.That(Hits(connection, 1, "ready"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "Narrator:(wil AND wheaton)"), Is.EqualTo(1));
                    Assert.That(connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name='edition_fts';"), Does.Contain("contentless_delete=1"));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM VersionInfo WHERE Version=110;"), Is.EqualTo(1));
                });

                Apply(connectionString, false, 110);
                Assert.That(Snapshot(connection), Is.EqualTo(before));
            });
        }

        [Test]
        public void missing_parent_should_reproduce_the_old_leak_and_delete_cleanly_after_repair()
        {
            WithDatabase(false, (connection, connectionString) =>
            {
                connection.Execute("SAVEPOINT old_delete; DELETE FROM Books WHERE Id=1; DELETE FROM Editions WHERE Id=1;");
                Assert.That(Hits(connection, 1, "ready"), Is.EqualTo(1));
                connection.Execute("ROLLBACK TO old_delete; RELEASE old_delete;");

                Apply(connectionString, false, 110);
                connection.Execute("DELETE FROM Books WHERE Id=1; DELETE FROM Editions WHERE Id=1;");
                Assert.That(Hits(connection, 1, "ready"), Is.Zero);
                Assert.That(Hits(connection, 2, "ready"), Is.EqualTo(1));
            });
        }

        [TestCase("DELETE FROM Editions WHERE Id=1;")]
        [TestCase("DELETE FROM Authors WHERE Id=1; DELETE FROM Editions WHERE Id=1;")]
        [TestCase("UPDATE Authors SET Name='Ernest Cline Updated' WHERE Id=1; DELETE FROM Editions WHERE Id=1;")]
        [TestCase("UPDATE Books SET SeriesName='Updated Series' WHERE Id=1; DELETE FROM Editions WHERE Id=1;")]
        [TestCase("UPDATE Editions SET MatchingTitle='ready player 2' WHERE Id=1; DELETE FROM Editions WHERE Id=1;")]
        [TestCase("UPDATE Editions SET NarratorNames='[\"Wil Wheaton\",\"Ernest Cline\"]' WHERE Id=1; DELETE FROM Editions WHERE Id=1;")]
        [TestCase("DELETE FROM edition_fts WHERE rowid=1; INSERT INTO edition_fts(rowid, MatchingTitle) VALUES(1, 'ready player two'); DELETE FROM Editions WHERE Id=1;")]
        public void lifecycle_should_remove_the_index_entry(string mutation)
        {
            WithDatabase(false, (connection, connectionString) =>
            {
                Apply(connectionString, false, 110);
                connection.Execute(mutation);
                Assert.That(Hits(connection, 1, "ready"), Is.Zero);
            });
        }

        [Test]
        public void updates_should_replace_indexed_values_and_keep_all_fields_searchable()
        {
            WithDatabase(false, (connection, connectionString) =>
            {
                Apply(connectionString, false, 110);
                connection.Execute(@"
                    UPDATE Authors SET Name='Ernest Cline Updated' WHERE Id=1;
                    UPDATE Books SET SeriesName='Oasis Updated' WHERE Id=1;
                    UPDATE Editions SET MatchingTitle='ready player 2', Subtitle='Updated subtitle',
                        NarratorNames='[""Wil Wheaton"", ""Ernest Cline""]', Publisher='Updated publisher' WHERE Id=1;");
                Assert.Multiple(() =>
                {
                    Assert.That(Hits(connection, 1, "MatchingTitle:two"), Is.Zero);
                    Assert.That(Hits(connection, 1, "MatchingTitle:2"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "SeriesName:updated AuthorName:updated Subtitle:updated Publisher:updated"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "Narrator:wheaton Narrator:cline"), Is.EqualTo(1));
                });

                connection.Execute(@"
                    INSERT INTO Editions(Id, BookId, Title, MatchingTitle, Narrator)
                    VALUES(3, 2, 'Ready Player One', 'ready player one', 'Wil Wheaton');");
                Assert.That(Hits(connection, 3, "MatchingTitle:one Narrator:wheaton"), Is.EqualTo(1));
            });
        }

        [TestCase("Authors", "Name", "AuthorName", "Ernest Cline", "cline")]
        [TestCase("Books", "SeriesName", "SeriesName", "Ready Player", "ready")]
        public void parent_updates_preserve_search_values_but_skip_unchanged_values(string table, string column, string field, string original, string term)
        {
            WithDatabase(false, (connection, connectionString) =>
            {
                var updateSame = $"UPDATE {table} SET {column}={column} WHERE Id=1;";
                var changes = connection.ExecuteScalar<long>("SELECT total_changes();");
                connection.Execute(updateSame);
                Assert.That(connection.ExecuteScalar<long>("SELECT total_changes();") - changes, Is.GreaterThan(1));

                var before = Snapshot(connection);
                Apply(connectionString, false, 110);
                Assert.That(Snapshot(connection), Is.EqualTo(before));
                Apply(connectionString, false, 110);
                changes = connection.ExecuteScalar<long>("SELECT total_changes();");
                connection.Execute(updateSame);
                Assert.That(connection.ExecuteScalar<long>("SELECT total_changes();") - changes, Is.EqualTo(1));
                Assert.That(Hits(connection, 1, field + ":" + term), Is.EqualTo(1));

                connection.Execute($"UPDATE {table} SET {column}='Updated' WHERE Id=1;");
                Assert.That(Hits(connection, 1, field + ":updated"), Is.EqualTo(1));
                Assert.That(Hits(connection, 1, field + ":" + term), Is.Zero);
                connection.Execute($"UPDATE {table} SET {column}=NULL WHERE Id=1;");
                Assert.That(Hits(connection, 1, field + ":updated"), Is.Zero);
                Assert.That(Hits(connection, 1, "MatchingTitle:two"), Is.EqualTo(1));

                changes = connection.ExecuteScalar<long>("SELECT total_changes();");
                connection.Execute(updateSame);
                Assert.That(connection.ExecuteScalar<long>("SELECT total_changes();") - changes, Is.EqualTo(1));
                connection.Execute($"UPDATE {table} SET {column}=@original WHERE Id=1;", new { original });
                Assert.That(Hits(connection, 1, field + ":" + term), Is.EqualTo(1));
                Assert.That(Snapshot(connection), Is.EqualTo(before));
            });
        }

        [Test]
        public void author_merge_should_rebuild_historical_rows_and_refresh_future_changes()
        {
            WithDatabase(false, (connection, connectionString) =>
            {
                connection.Execute("INSERT INTO Authors(Id, Name, ForeignAuthorId) VALUES(2, 'Virginia Woolf', 'gr:1');");

                // Exercise the set-wise database update used by author merges.
                MergeAuthorBooks(connectionString, 1, 2);
                Assert.Multiple(() =>
                {
                    Assert.That(Hits(connection, 1, "AuthorName:cline"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "AuthorName:woolf"), Is.Zero);
                    Assert.That(Hits(connection, 2, "AuthorName:cline"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 2, "AuthorName:woolf"), Is.Zero);
                });

                var beforeRepair = Snapshot(connection);
                Apply(connectionString, false, 110);
                Assert.Multiple(() =>
                {
                    Assert.That(Snapshot(connection), Is.EqualTo(beforeRepair));
                    Assert.That(Hits(connection, 1, "AuthorName:cline"), Is.Zero);
                    Assert.That(Hits(connection, 1, "AuthorName:woolf"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 2, "AuthorName:cline"), Is.Zero);
                    Assert.That(Hits(connection, 2, "AuthorName:woolf"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "MatchingTitle:two"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "SeriesName:ready"), Is.EqualTo(1));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts;"), Is.EqualTo(2));
                });

                MergeAuthorBooks(connectionString, 2, 1);
                Assert.Multiple(() =>
                {
                    Assert.That(Hits(connection, 1, "AuthorName:cline"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 1, "AuthorName:woolf"), Is.Zero);
                    Assert.That(Hits(connection, 2, "AuthorName:cline"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 2, "AuthorName:woolf"), Is.Zero);
                });

                MergeAuthorBooks(connectionString, 1, 2);
                Assert.Multiple(() =>
                {
                    Assert.That(Hits(connection, 1, "AuthorName:cline"), Is.Zero);
                    Assert.That(Hits(connection, 1, "AuthorName:woolf"), Is.EqualTo(1));
                    Assert.That(Hits(connection, 2, "AuthorName:cline"), Is.Zero);
                    Assert.That(Hits(connection, 2, "AuthorName:woolf"), Is.EqualTo(1));
                });

                var changes = connection.ExecuteScalar<long>("SELECT total_changes();");
                connection.Execute("UPDATE Books SET AuthorId=AuthorId WHERE Id=1;");
                Assert.That(connection.ExecuteScalar<long>("SELECT total_changes();") - changes, Is.EqualTo(1));

                connection.Execute("UPDATE Books SET SeriesName=NULL WHERE Id=1;");
                Assert.Multiple(() =>
                {
                    Assert.That(Hits(connection, 1, "SeriesName:ready"), Is.Zero);
                    Assert.That(Hits(connection, 1, "MatchingTitle:two"), Is.EqualTo(1));
                });

                connection.Execute("UPDATE Books SET SeriesName='Oasis Restored' WHERE Id=1;");
                Assert.That(Hits(connection, 1, "SeriesName:restored"), Is.EqualTo(1));
            });
        }

        [Test]
        public void postgres_migration_should_be_a_no_op_for_fts_and_preserve_rows_and_expression_indexes()
        {
            WithDatabase(true, (connection, connectionString) =>
            {
                connection.Execute(@"CREATE INDEX test_expression_index_before_110 ON ""Editions"" USING GIN
                    (to_tsvector('simple', COALESCE(""MatchingTitle"", '')));");
                var before = Snapshot(connection);
                var indexes = connection.Query<string>("SELECT indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY indexname;").ToArray();
                Apply(connectionString, true, 110);
                Assert.Multiple(() =>
                {
                    Assert.That(Snapshot(connection), Is.EqualTo(before));
                    Assert.That(connection.Query<string>("SELECT indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY indexname;"), Is.EqualTo(indexes));
                    Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""VersionInfo"" WHERE ""Version""=110;"), Is.EqualTo(1));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM pg_tables WHERE tablename='edition_fts';"), Is.Zero);
                    Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Editions"" WHERE to_tsvector('simple', COALESCE(""MatchingTitle"", '')) @@ to_tsquery('simple', 'ready & two');"), Is.EqualTo(1));
                });
            });
        }

        private static int Hits(IDbConnection connection, int id, string query)
        {
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH @query AND rowid=@id;", new { query, id });
        }

        private static string Snapshot(IDbConnection connection)
        {
            return JsonSerializer.Serialize(new[] { "Authors", "Books", "Editions", "BookFiles" }
                .Select(table => connection.Query($"SELECT * FROM \"{table}\" ORDER BY \"Id\";").ToArray()).ToArray());
        }

        private static SqliteConnection OpenSqlite(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static void MergeAuthorBooks(string connectionString, int sourceAuthorId, int targetAuthorId)
        {
            using var connection = OpenSqlite(connectionString);
            using var transaction = connection.BeginTransaction();
            connection.Execute(
                @"UPDATE ""Books""
                  SET ""AuthorId"" = @TargetAuthorId
                  WHERE ""AuthorId"" = @SourceAuthorId;",
                new { SourceAuthorId = sourceAuthorId, TargetAuthorId = targetAuthorId },
                transaction);
            transaction.Commit();
        }

        private static void Apply(string connectionString, bool postgres, int version)
        {
            new MigrationController(LogManager.GetCurrentClassLogger(), null)
                .Migrate(connectionString, new MigrationContext(MigrationType.Main, version), postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
        }

        private static void WithDatabase(bool postgres, Action<IDbConnection, string> verify)
        {
            var name = "fts_deletion_" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name + ".db");
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(adminString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL migration compatibility");
            }

            if (postgres)
            {
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{name}\";");
            }

            var connectionString = postgres
                ? new NpgsqlConnectionStringBuilder(adminString) { Database = name, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
            try
            {
                using IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
                connection.Open();
                connection.Execute(@"
                    CREATE TABLE ""VersionInfo"" (""Version"" BIGINT PRIMARY KEY, ""AppliedOn"" TIMESTAMP, ""Description"" TEXT);
                    CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT, ""ForeignAuthorId"" TEXT);
                    CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER, ""Title"" TEXT, ""SeriesName"" TEXT, ""ForeignBookId"" TEXT);
                    CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER, ""Title"" TEXT,
                        ""MatchingTitle"" TEXT, ""Subtitle"" TEXT, ""Narrator"" TEXT, ""NarratorNames"" TEXT, ""Publisher"" TEXT);
                    CREATE TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""EditionId"" INTEGER, ""Path"" TEXT);
                    WITH RECURSIVE versions(version) AS (SELECT 1 UNION ALL SELECT version+1 FROM versions WHERE version<49)
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;");
                if (!postgres)
                {
                    connection.Execute(@"CREATE VIRTUAL TABLE edition_fts USING fts5(MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher,
                        content='', content_rowid='rowid', tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');");
                }

                Apply(connectionString, postgres, 50);
                connection.Execute(@"
                    WITH RECURSIVE versions(version) AS (SELECT 51 UNION ALL SELECT version+1 FROM versions WHERE version<109)
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
                    INSERT INTO ""Authors"" VALUES(1, 'Ernest Cline', 'gr:31712');
                    INSERT INTO ""Books"" VALUES(1, 1, 'Ready Player Two', 'Ready Player', 'hc:427565'), (2, 1, 'Ready Player One', 'Ready Player', 'hc:26363');
                    INSERT INTO ""Editions"" VALUES(1, 1, 'Ready Player Two', 'ready player two', 'A Novel', 'Wil Wheaton', '[""Wil Wheaton""]', 'Ballantine'),
                        (2, 2, 'Ready Player One', 'ready player one', '', 'Wil Wheaton', '[]', 'Broadway');
                    INSERT INTO ""BookFiles"" VALUES(1, 2, 'Ready Player One.m4b');");
                verify(connection, connectionString);
            }
            finally
            {
                if (postgres)
                {
                    using var admin = new NpgsqlConnection(adminString);
                    admin.Open();
                    admin.Execute($"DROP DATABASE IF EXISTS \"{name}\";");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
