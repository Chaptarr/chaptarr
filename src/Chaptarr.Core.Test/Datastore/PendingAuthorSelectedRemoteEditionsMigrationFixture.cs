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
    public class PendingAuthorSelectedRemoteEditionsMigrationFixture
    {
        [Test]
        public void should_add_nullable_selected_remote_editions_to_existing_pending_imports()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"pending_author_selected_editions_{Guid.NewGuid():N}.db");
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
                            SELECT version + 1 FROM versions WHERE version < 123
                        )
                        INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                        SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;

                        CREATE TABLE ""Authors"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""AudiobookRootFolderPath"" TEXT NULL,
                            ""EbookRootFolderPath"" TEXT NULL
                        );

                        CREATE TABLE ""PendingAuthorImport"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""ProviderId"" TEXT NOT NULL
                        );
                        INSERT INTO ""PendingAuthorImport"" (""Id"", ""ProviderId"") VALUES (1, 'hc:123');");
                }

                var migrationController = new MigrationController(
                    LogManager.GetLogger(nameof(PendingAuthorSelectedRemoteEditionsMigrationFixture)),
                    null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Main, 124),
                    DatabaseType.SQLite);

                using var resultConnection = new SqliteConnection(connectionString);
                resultConnection.Open();
                var columns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('PendingAuthorImport');").AsList();

                Assert.Multiple(() =>
                {
                    Assert.That(columns, Does.Contain("SelectedRemoteEditions"));
                    Assert.That(resultConnection.QuerySingle<string>(@"
                        SELECT CASE
                            WHEN ""SelectedRemoteEditions"" IS NULL THEN 'null'
                            ELSE ""SelectedRemoteEditions""
                        END
                        FROM ""PendingAuthorImport""
                        WHERE ""Id"" = 1;"), Is.EqualTo("null"));
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
