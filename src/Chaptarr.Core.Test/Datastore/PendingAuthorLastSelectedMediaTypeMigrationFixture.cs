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
    public class PendingAuthorLastSelectedMediaTypeMigrationFixture
    {
        [Test]
        public void should_add_nullable_last_selected_media_type_to_existing_pending_imports()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"pending_author_last_selected_{Guid.NewGuid():N}.db");
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
                        SELECT version + 1 FROM versions WHERE version < 106
                    )
                    INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                    SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;

                    CREATE TABLE ""PendingAuthorImport"" (
                        ""Id"" INTEGER PRIMARY KEY,
                        ""ProviderId"" TEXT NOT NULL
                    );
                    INSERT INTO ""PendingAuthorImport"" (""Id"", ""ProviderId"") VALUES (1, 'hc:123');");

                var migrationController = new MigrationController(LogManager.GetLogger(nameof(PendingAuthorLastSelectedMediaTypeMigrationFixture)), null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Main, 107),
                    DatabaseType.SQLite);

                var value = connection.QuerySingle<string>(@"
                    SELECT CASE
                        WHEN ""LastSelectedMediaType"" IS NULL THEN 'null'
                        ELSE ""LastSelectedMediaType""
                    END
                    FROM ""PendingAuthorImport""
                    WHERE ""Id"" = 1;");

                Assert.That(value, Is.EqualTo("null"));
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
