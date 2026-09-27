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
    public class CachedHttpResponseRepositoryUniqueUrlFixture
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
                connection.Execute(@"CREATE TABLE ""HttpResponse"" (""Id"" INTEGER PRIMARY KEY, ""Url"" TEXT NOT NULL, ""LastRefresh"" TEXT NOT NULL, ""Expiry"" TEXT NOT NULL, ""Value"" TEXT NOT NULL, ""StatusCode"" INTEGER NOT NULL); CREATE UNIQUE INDEX ""IX_HttpResponse_Url"" ON ""HttpResponse"" (""Url"");");
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

        private static CachedHttpResponse Response(string url, string value)
        {
            return new CachedHttpResponse
            {
                Url = url,
                LastRefresh = DateTime.UtcNow,
                Expiry = DateTime.UtcNow.AddHours(1),
                Value = value,
                StatusCode = 200
            };
        }

        private int RowCount(string url)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""HttpResponse"" WHERE ""Url"" = @url", new { url });
        }

        [Test]
        public void plain_insert_of_a_second_row_for_one_url_is_rejected_by_the_unique_index()
        {
            _sut.Insert(Response("https://example.test/a", "one"));

            Assert.Throws<SqliteException>(() => _sut.Insert(Response("https://example.test/a", "two")));
        }

        [Test]
        public void upsert_by_url_should_update_the_existing_row_when_the_url_is_already_cached()
        {
            _sut.UpsertByUrl(Response("https://example.test/a", "one"));
            _sut.UpsertByUrl(Response("https://example.test/a", "two"));

            Assert.That(RowCount("https://example.test/a"), Is.EqualTo(1));
            Assert.That(_sut.FindByUrl("https://example.test/a").Value, Is.EqualTo("two"));
        }

        [Test]
        public void concurrent_upserts_for_one_url_should_leave_a_single_row()
        {
            var tasks = Enumerable.Range(0, 16)
                .Select(i => System.Threading.Tasks.Task.Run(() => _sut.UpsertByUrl(Response("https://example.test/race", $"v{i}"))))
                .ToArray();

            System.Threading.Tasks.Task.WaitAll(tasks);

            Assert.That(RowCount("https://example.test/race"), Is.EqualTo(1));
            Assert.That(_sut.FindByUrl("https://example.test/race"), Is.Not.Null);
        }
    }
}
