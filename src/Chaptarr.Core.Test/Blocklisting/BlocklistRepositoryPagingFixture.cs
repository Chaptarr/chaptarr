using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistRepositoryPagingFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // TableMapping.Map() is normally invoked via DbFactory static ctor.
            // Integration tests that use repositories directly should ensure mapping/type handlers are registered.
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void should_return_correct_total_records_for_paged_blocklist()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"blocklist_paging_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"CREATE TABLE ""Blocklist"" (""Id"" INTEGER PRIMARY KEY, ""Date"" TEXT NOT NULL);");

                    connection.Execute(@"INSERT INTO ""Blocklist"" (""Date"") VALUES (@Date);", new { Date = "2020-01-01T00:00:00Z" });
                    connection.Execute(@"INSERT INTO ""Blocklist"" (""Date"") VALUES (@Date);", new { Date = "2020-01-02T00:00:00Z" });
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new BlocklistRepository(mainDatabase, new StubEventAggregator());

                var pagingSpec = new PagingSpec<Blocklist>
                {
                    Page = 1,
                    PageSize = 20,
                    SortKey = "date",
                    SortDirection = SortDirection.Descending
                };

                var result = sut.GetPaged(pagingSpec);

                Assert.That(result.TotalRecords, Is.EqualTo(2));
                Assert.That(result.Records, Has.Count.EqualTo(2));
                Assert.That(result.Records[0].Date, Is.GreaterThan(result.Records[1].Date));
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }
    }
}

