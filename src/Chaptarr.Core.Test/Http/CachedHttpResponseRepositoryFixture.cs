using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class CachedHttpResponseRepositoryFixture
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void should_return_the_most_recent_row_when_duplicate_urls_exist()
        {
            WithRepository((repository, connectionString) =>
            {
                const string url = "https://www.goodreads.com/book/auto_complete?q=baskische%20trag%C3%B6die";

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        INSERT INTO ""HttpResponse"" (""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"")
                        VALUES
                            (@Url, '2026-10-10 10:00:00', '2026-10-15 10:00:00', 'older response', 200),
                            (@Url, '2026-10-11 10:00:00', '2026-10-16 10:00:00', 'newer response', 200);",
                        new { Url = url });
                }

                var response = repository.FindByUrl(url);

                Assert.That(response.Value, Is.EqualTo("newer response"));
            });
        }

        [Test]
        public void upsert_by_url_should_keep_one_row_for_concurrent_cache_misses()
        {
            WithRepository((repository, connectionString) =>
            {
                const string url = "https://www.goodreads.com/book/auto_complete?q=baskische%20trag%C3%B6die";
                var refreshTime = DateTime.UtcNow;

                Parallel.For(0, 8, index => repository.UpsertByUrl(new CachedHttpResponse
                {
                    Url = url,
                    LastRefresh = refreshTime.AddSeconds(index),
                    Expiry = refreshTime.AddDays(1),
                    Value = $"response {index}",
                    StatusCode = 200
                }));

                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                var count = connection.QuerySingle<int>(@"
                    SELECT COUNT(*)
                    FROM ""HttpResponse""
                    WHERE ""Url"" = @Url;", new { Url = url });

                Assert.That(count, Is.EqualTo(1));
            }, createUniqueUrlIndex: true);
        }

        private static void WithRepository(Action<CachedHttpResponseRepository, string> action, bool createUniqueUrlIndex = false)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"http_response_repository_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        CREATE TABLE ""HttpResponse"" (
                            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                            ""Url"" TEXT NOT NULL,
                            ""LastRefresh"" TEXT NOT NULL,
                            ""Expiry"" TEXT NOT NULL,
                            ""Value"" TEXT NOT NULL,
                            ""StatusCode"" INTEGER NOT NULL
                        );
                        CREATE INDEX ""IX_HttpResponse_Url"" ON ""HttpResponse"" (""Url"");
                    ");

                    if (createUniqueUrlIndex)
                    {
                        connection.Execute(@"
                            CREATE UNIQUE INDEX ""UX_HttpResponse_Url""
                            ON ""HttpResponse"" (""Url"");");
                    }
                }

                var database = new Database("HttpResponseTest", () => OpenDatabaseConnection(connectionString));
                var repository = new CachedHttpResponseRepository(new CacheDatabase(database), null);
                action(repository, connectionString);
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

        private static SqliteConnection OpenDatabaseConnection(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();

            try
            {
                connection.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            }
            catch
            {
                // Match the application's best-effort WAL setup.
            }

            connection.Execute("PRAGMA busy_timeout=5000;");
            return connection;
        }
    }
}
