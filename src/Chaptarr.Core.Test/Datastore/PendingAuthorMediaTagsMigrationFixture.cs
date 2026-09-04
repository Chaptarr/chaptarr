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
    public class PendingAuthorMediaTagsMigrationFixture
    {
        [Test]
        public void should_copy_legacy_tags_only_to_requested_media_sides_without_overwriting_existing_values()
        {
            WithDatabase((connection, connectionString) =>
            {
                connection.Execute(@"
                    INSERT INTO ""PendingAuthorImport"" (""Id"", ""AudiobookStatus"", ""EbookStatus"", ""Tags"", ""AudiobookTags"") VALUES
                        (1, 1, 0, '[10]', NULL),
                        (2, 0, 1, '[20]', NULL),
                        (3, 1, 1, '[30]', NULL),
                        (4, 0, 0, '[40]', NULL),
                        (5, 1, 1, '[50]', '[99]'),
                        (6, 1, 1, '[]', NULL);");

                ApplyMigration(connectionString);

                var rows = connection.Query<Row>(@"
                    SELECT ""Id"", ""AudiobookTags"", ""EbookTags""
                    FROM ""PendingAuthorImport""
                    ORDER BY ""Id"";").ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(rows[0].AudiobookTags, Is.EqualTo("[10]"));
                    Assert.That(rows[0].EbookTags, Is.Null);
                    Assert.That(rows[1].AudiobookTags, Is.Null);
                    Assert.That(rows[1].EbookTags, Is.EqualTo("[20]"));
                    Assert.That(rows[2].AudiobookTags, Is.EqualTo("[30]"));
                    Assert.That(rows[2].EbookTags, Is.EqualTo("[30]"));
                    Assert.That(rows[3].AudiobookTags, Is.Null);
                    Assert.That(rows[3].EbookTags, Is.Null);
                    Assert.That(rows[4].AudiobookTags, Is.EqualTo("[99]"));
                    Assert.That(rows[4].EbookTags, Is.EqualTo("[50]"));
                    Assert.That(rows[5].AudiobookTags, Is.EqualTo("[]"));
                    Assert.That(rows[5].EbookTags, Is.EqualTo("[]"));
                });
            });
        }

        private static void ApplyMigration(string connectionString)
        {
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(PendingAuthorMediaTagsMigrationFixture)), null);
            migrationController.Migrate(
                connectionString,
                new MigrationContext(MigrationType.Main, 106),
                DatabaseType.SQLite);
        }

        private static void WithDatabase(Action<SqliteConnection, string> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"pending_author_media_tags_{Guid.NewGuid():N}.db");
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
                        SELECT version + 1 FROM versions WHERE version < 105
                    )
                    INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                    SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;

                    CREATE TABLE ""PendingAuthorImport"" (
                        ""Id"" INTEGER PRIMARY KEY,
                        ""AudiobookStatus"" INTEGER NOT NULL,
                        ""EbookStatus"" INTEGER NOT NULL,
                        ""Tags"" TEXT NULL,
                        ""AudiobookTags"" TEXT NULL
                    );");

                test(connection, connectionString);
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private sealed class Row
        {
            public int Id { get; set; }
            public string AudiobookTags { get; set; }
            public string EbookTags { get; set; }
        }
    }
}
