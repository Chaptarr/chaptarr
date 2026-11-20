using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Http;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class CachedHttpResponseRepositoryFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private string _databasePath;
        private string _connectionString;
        private CachedHttpResponseRepository _sut;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"http_cache_{Guid.NewGuid():N}.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                connection.Execute(@"CREATE TABLE ""HttpResponse"" (""Id"" INTEGER PRIMARY KEY, ""Url"" TEXT NOT NULL, ""LastRefresh"" TEXT NOT NULL, ""Expiry"" TEXT NOT NULL, ""Value"" TEXT NOT NULL, ""StatusCode"" INTEGER NOT NULL);");
            }

            var database = new Database("cache", () =>
            {
                var conn = new SqliteConnection(_connectionString);
                conn.Open();
                return conn;
            });

            _sut = new CachedHttpResponseRepository(new CacheDatabase(database), new StubEventAggregator());
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }
            }
            catch
            {
            }
        }

        private void Insert(string url, DateTime lastRefresh, string value)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                connection.Execute(
                    @"INSERT INTO ""HttpResponse"" (""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"") VALUES (@Url, @LastRefresh, @Expiry, @Value, 200);",
                    new { Url = url, LastRefresh = lastRefresh, Expiry = lastRefresh.AddHours(1), Value = value });
            }
        }

        private int RowCount(string url)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""HttpResponse"" WHERE ""Url"" = @url", new { url });
            }
        }

        [Test]
        public void should_return_null_when_url_is_not_cached()
        {
            Assert.That(_sut.FindByUrl("https://example.test/none"), Is.Null);
        }

        [Test]
        public void should_return_the_only_row_and_leave_it_alone()
        {
            Insert("https://example.test/a", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "one");

            var found = _sut.FindByUrl("https://example.test/a");

            Assert.That(found.Value, Is.EqualTo("one"));
            Assert.That(RowCount("https://example.test/a"), Is.EqualTo(1));
        }

        [Test]
        public void should_return_the_newest_duplicate_and_delete_the_stale_ones()
        {
            var url = "https://example.test/dupes";
            Insert(url, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "old");
            Insert(url, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), "newest");
            Insert(url, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "middle");
            Insert("https://example.test/other", new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc), "unrelated");

            var found = _sut.FindByUrl(url);

            Assert.That(found.Value, Is.EqualTo("newest"));
            Assert.That(RowCount(url), Is.EqualTo(1));
            Assert.That(RowCount("https://example.test/other"), Is.EqualTo(1));
            Assert.That(_sut.FindByUrl(url).Value, Is.EqualTo("newest"));
        }

        [Test]
        public void should_prefer_the_highest_id_when_last_refresh_ties()
        {
            var url = "https://example.test/tie";
            var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Insert(url, stamp, "first");
            Insert(url, stamp, "second");

            var found = _sut.FindByUrl(url);

            Assert.That(found.Value, Is.EqualTo("second"));
            Assert.That(RowCount(url), Is.EqualTo(1));
        }

        [Test]
        public void should_still_return_the_newest_row_when_deleting_stale_rows_fails()
        {
            var url = "https://example.test/locked";
            Insert(url, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "old");
            Insert(url, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "new");

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();

                // Make every DELETE fail, as a locked or read-only cache database would.
                connection.Execute(@"CREATE TRIGGER block_delete BEFORE DELETE ON ""HttpResponse"" BEGIN SELECT RAISE(ABORT, 'delete blocked'); END;");

                var found = _sut.FindByUrl(url);

                Assert.That(found.Value, Is.EqualTo("new"));
                Assert.That(RowCount(url), Is.EqualTo(2));
            }
        }
    }
}
