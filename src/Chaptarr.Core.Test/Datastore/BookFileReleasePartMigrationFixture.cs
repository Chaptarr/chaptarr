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
    public class BookFileReleasePartMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void should_add_release_part_columns_defaulting_existing_files_to_zero(bool columnsAlreadyPresent)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_file_release_part_{Guid.NewGuid():N}.db");
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
                            SELECT version + 1 FROM versions WHERE version < 119
                        )
                        INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                        SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
                        CREATE TABLE ""Authors"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""AudiobookRootFolderPath"" TEXT NULL,
                            ""EbookRootFolderPath"" TEXT NULL
                        );
                        CREATE TABLE ""BookFiles"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Path"" TEXT NOT NULL,
                            ""Part"" INTEGER NOT NULL DEFAULT 0
                        );
                        INSERT INTO ""BookFiles"" (""Id"", ""Path"", ""Part"") VALUES (1, '/audio/a.m4b', 1);");

                    if (columnsAlreadyPresent)
                    {
                        connection.Execute(@"
                            ALTER TABLE ""BookFiles"" ADD COLUMN ""ReleasePart"" INTEGER NOT NULL DEFAULT 0;
                            ALTER TABLE ""BookFiles"" ADD COLUMN ""ReleasePartCount"" INTEGER NOT NULL DEFAULT 0;");
                    }
                }

                var migrationController = new MigrationController(
                    LogManager.GetLogger(nameof(BookFileReleasePartMigrationFixture)),
                    null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Main, 120),
                    DatabaseType.SQLite);

                using var resultConnection = new SqliteConnection(connectionString);
                resultConnection.Open();
                var columns = resultConnection.Query<string>("SELECT name FROM pragma_table_info('BookFiles');").AsList();

                Assert.Multiple(() =>
                {
                    Assert.That(columns, Does.Contain("ReleasePart"));
                    Assert.That(columns, Does.Contain("ReleasePartCount"));
                    Assert.That(resultConnection.QuerySingle<int>(
                        "SELECT COUNT(*) FROM BookFiles WHERE Id = 1 AND Part = 1 AND ReleasePart = 0 AND ReleasePartCount = 0;"), Is.EqualTo(1));
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
