using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class ExtraFileAuthorBookIndexMigrationFixture
    {
        private string _databasePath;
        private string _connectionString;
        private SqliteConnection _connection;

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"extra_file_index_{Guid.NewGuid():N}.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            _connection.Execute(@"
                CREATE TABLE ""VersionInfo"" (
                    ""Version"" INTEGER PRIMARY KEY,
                    ""AppliedOn"" TEXT NULL,
                    ""Description"" TEXT NULL
                );
                WITH RECURSIVE versions(version) AS
                (
                    SELECT 1
                    UNION ALL
                    SELECT version + 1 FROM versions WHERE version < 109
                )
                INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
            ");
        }

        [TearDown]
        public void TearDown()
        {
            _connection?.Dispose();
            SqliteConnection.ClearAllPools();

            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        private void Migrate()
        {
            var migrationController = new MigrationController(LogManager.GetLogger("ExtraFileAuthorBookIndexMigrationFixture"), null);
            migrationController.Migrate(_connectionString, new MigrationContext(MigrationType.Main, 110), DatabaseType.SQLite);
        }

        private string[] IndexColumns(string tableName, string indexName)
        {
            var exists = _connection.QuerySingle<int>($"SELECT COUNT(*) FROM pragma_index_list('{tableName}') WHERE name = '{indexName}';");
            return exists == 0
                ? Array.Empty<string>()
                : _connection.Query<string>($"SELECT name FROM pragma_index_info('{indexName}') ORDER BY seqno;").ToArray();
        }

        [Test]
        public void should_index_author_and_book_on_both_extra_file_tables_in_query_order()
        {
            _connection.Execute(@"
                CREATE TABLE ""MetadataFiles"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""BookId"" INTEGER NULL);
                CREATE TABLE ""ExtraFiles"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""BookId"" INTEGER NULL);");

            Migrate();

            Assert.Multiple(() =>
            {
                Assert.That(IndexColumns("MetadataFiles", "IX_MetadataFiles_AuthorId_BookId"), Is.EqualTo(new[] { "AuthorId", "BookId" }));
                Assert.That(IndexColumns("ExtraFiles", "IX_ExtraFiles_AuthorId_BookId"), Is.EqualTo(new[] { "AuthorId", "BookId" }));
            });
        }

        [Test]
        public void should_make_the_per_book_delete_use_the_index_instead_of_scanning()
        {
            _connection.Execute(@"CREATE TABLE ""MetadataFiles"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""BookId"" INTEGER NULL);");

            Migrate();

            Assert.That(IndexColumns("MetadataFiles", "IX_MetadataFiles_AuthorId_BookId"), Is.EqualTo(new[] { "AuthorId", "BookId" }));

            // A fresh connection: the migration ran on its own connection, and a connection opened before
            // it keeps planning against the schema it saw then.
            using var fresh = new SqliteConnection(_connectionString);
            fresh.Open();

            // EXPLAIN QUERY PLAN returns (id, parent, notused, detail); the detail column names the access path.
            var plan = string.Join(" | ", fresh.Query(
                @"EXPLAIN QUERY PLAN DELETE FROM ""MetadataFiles"" WHERE ""AuthorId"" = 1 AND ""BookId"" = 2;")
                .Select(row => (string)row.detail));

            Assert.That(plan, Does.Contain("IX_MetadataFiles_AuthorId_BookId"), "the delete must be planned as an index search, not a table scan");
        }

        [Test]
        public void should_tolerate_missing_tables_and_missing_columns()
        {
            // ExtraFiles present but without the columns (older/odd schema): nothing to index, must not throw.
            _connection.Execute(@"CREATE TABLE ""ExtraFiles"" (""Id"" INTEGER PRIMARY KEY, ""Path"" TEXT NULL);");

            Assert.DoesNotThrow(Migrate);
            Assert.That(IndexColumns("ExtraFiles", "IX_ExtraFiles_AuthorId_BookId"), Is.Empty);
        }

        [Test]
        public void should_not_fail_when_the_index_already_exists()
        {
            _connection.Execute(@"
                CREATE TABLE ""MetadataFiles"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""BookId"" INTEGER NULL);
                CREATE INDEX ""IX_MetadataFiles_AuthorId_BookId"" ON ""MetadataFiles"" (""AuthorId"", ""BookId"");");

            Assert.DoesNotThrow(Migrate);
            Assert.That(IndexColumns("MetadataFiles", "IX_MetadataFiles_AuthorId_BookId"), Is.EqualTo(new[] { "AuthorId", "BookId" }));
        }
    }
}
