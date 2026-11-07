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
    public class CachedHttpResponseCacheMigrationFixture
    {
        [Test]
        public void should_keep_the_latest_cached_response_and_enforce_unique_urls()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"http_response_cache_migration_{Guid.NewGuid():N}.db");
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
                        SELECT version + 1 FROM versions WHERE version < 115
                        )
                        INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                        SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;

                        CREATE TABLE ""HttpResponse"" (
                            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                            ""Url"" TEXT NOT NULL,
                            ""LastRefresh"" TEXT NOT NULL,
                            ""Expiry"" TEXT NOT NULL,
                            ""Value"" TEXT NOT NULL,
                            ""StatusCode"" INTEGER NOT NULL
                        );
                        CREATE INDEX ""IX_HttpResponse_Url"" ON ""HttpResponse"" (""Url"");
                        CREATE INDEX ""IX_HttpResponse_Expiry"" ON ""HttpResponse"" (""Expiry"");
                        INSERT INTO ""HttpResponse"" (""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"")
                        VALUES
                            ('https://www.goodreads.com/book/auto_complete?q=baskische%20trag%C3%B6die', '2026-10-10 10:00:00', '2026-10-15 10:00:00', 'older response', 200),
                            ('https://www.goodreads.com/book/auto_complete?q=baskische%20trag%C3%B6die', '2026-10-11 10:00:00', '2026-10-16 10:00:00', 'newer response', 200),
                            ('https://metadata.test/another', '2026-10-11 11:00:00', '2026-10-16 11:00:00', 'unrelated response', 200);
                    ");
                }

                var migrationController = new MigrationController(LogManager.GetLogger(nameof(CachedHttpResponseCacheMigrationFixture)), null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Cache, 118),
                    DatabaseType.SQLite);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var duplicateUrl = "https://www.goodreads.com/book/auto_complete?q=baskische%20trag%C3%B6die";
                    var count = connection.QuerySingle<int>(@"
                        SELECT COUNT(*)
                        FROM ""HttpResponse""
                        WHERE ""Url"" = @Url;", new { Url = duplicateUrl });
                    var value = connection.QuerySingle<string>(@"
                        SELECT ""Value""
                        FROM ""HttpResponse""
                        WHERE ""Url"" = @Url;", new { Url = duplicateUrl });
                    var uniqueIndexCount = connection.QuerySingle<int>(@"
                        SELECT COUNT(*)
                        FROM pragma_index_list('HttpResponse')
                        WHERE name = 'UX_HttpResponse_Url' AND ""unique"" = 1;");
                    var legacyIndexCount = connection.QuerySingle<int>(@"
                        SELECT COUNT(*)
                        FROM pragma_index_list('HttpResponse')
                        WHERE name = 'IX_HttpResponse_Url';");

                    Assert.Multiple(() =>
                    {
                        Assert.That(count, Is.EqualTo(1));
                        Assert.That(value, Is.EqualTo("newer response"));
                        Assert.That(uniqueIndexCount, Is.EqualTo(1));
                        Assert.That(legacyIndexCount, Is.Zero);
                    });

                    Assert.Throws<SqliteException>(() => connection.Execute(@"
                        INSERT INTO ""HttpResponse"" (""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"")
                        VALUES (@Url, '2026-10-12 10:00:00', '2026-10-17 10:00:00', 'duplicate', 200);",
                        new { Url = duplicateUrl }));
                }
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
