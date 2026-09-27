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
    public class HttpResponseUniqueUrlMigrationFixture
    {
        [Test]
        public void should_keep_the_newest_row_per_url_and_make_the_url_index_unique()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"http_response_unique_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                connection.Execute(@"
                    CREATE TABLE ""VersionInfo"" (""Version"" INTEGER PRIMARY KEY, ""AppliedOn"" TEXT NULL, ""Description"" TEXT NULL);
                    WITH RECURSIVE versions(version) AS (SELECT 1 UNION ALL SELECT version + 1 FROM versions WHERE version < 107)
                    INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"") SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
                    CREATE TABLE ""HttpResponse"" (""Id"" INTEGER PRIMARY KEY, ""Url"" TEXT NOT NULL, ""LastRefresh"" TEXT NOT NULL, ""Expiry"" TEXT NOT NULL, ""Value"" TEXT NOT NULL, ""StatusCode"" INTEGER NOT NULL);
                    CREATE INDEX ""IX_HttpResponse_Url"" ON ""HttpResponse"" (""Url"");
                    INSERT INTO ""HttpResponse"" (""Id"", ""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"") VALUES
                        (1, 'a', '2026-01-01 00:00:00', '2026-01-02 00:00:00', 'old', 200),
                        (2, 'a', '2026-01-03 00:00:00', '2026-01-04 00:00:00', 'newest', 200),
                        (3, 'a', '2026-01-02 00:00:00', '2026-01-03 00:00:00', 'middle', 200),
                        (4, 'b', '2026-01-01 00:00:00', '2026-01-02 00:00:00', 'tie-low-id', 200),
                        (5, 'b', '2026-01-01 00:00:00', '2026-01-02 00:00:00', 'tie-high-id', 200),
                        (6, 'c', '2026-01-01 00:00:00', '2026-01-02 00:00:00', 'single', 200);
                ");

                var migrationController = new MigrationController(LogManager.GetLogger("HttpResponseUniqueUrlMigrationFixture"), null);
                migrationController.Migrate(connectionString, new MigrationContext(MigrationType.Cache, 109), DatabaseType.SQLite);

                Assert.That(connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""HttpResponse"""), Is.EqualTo(3));
                Assert.That(connection.QuerySingle<string>(@"SELECT ""Value"" FROM ""HttpResponse"" WHERE ""Url"" = 'a'"), Is.EqualTo("newest"));
                Assert.That(connection.QuerySingle<string>(@"SELECT ""Value"" FROM ""HttpResponse"" WHERE ""Url"" = 'b'"), Is.EqualTo("tie-high-id"));
                Assert.That(connection.QuerySingle<string>(@"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_HttpResponse_Url'").ToUpperInvariant(), Does.Contain("UNIQUE"));
                Assert.Throws<SqliteException>(() => connection.Execute(@"INSERT INTO ""HttpResponse"" (""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"") VALUES ('c', '2026-02-01', '2026-02-02', 'dup', 200)"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }
    }
}
