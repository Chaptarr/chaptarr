using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class IgnoredDownloadHistoryFixture
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
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void currently_ignored_should_only_return_downloads_where_latest_significant_event_is_ignored()
        {
            WithRepository(repository =>
            {
                var page = repository.CurrentlyIgnored(new PagingSpec<DownloadHistory>
                {
                    Page = 1,
                    PageSize = 20,
                    SortKey = "date",
                    SortDirection = SortDirection.Descending
                });

                Assert.Multiple(() =>
                {
                    Assert.That(page.TotalRecords, Is.EqualTo(2));
                    Assert.That(page.Records.Select(r => r.DownloadId), Is.EqualTo(new[] { "DUPLICATE", "CURRENT" }));
                    Assert.That(page.Records.Select(r => r.Id), Is.EqualTo(new[] { 7, 3 }));
                });
            });
        }

        [Test]
        public void currently_ignored_should_use_postgres_array_predicate()
        {
            var sql = DownloadHistoryRepository.BuildCurrentSignificantEventsCte(DatabaseType.PostgreSQL);

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Contain(@"""EventType"" = ANY(@significantEvents)"));
                Assert.That(sql, Does.Not.Contain(@"""EventType"" IN @significantEvents"));
            });
        }

        [Test]
        public void currently_ignored_should_use_sqlite_in_predicate()
        {
            var sql = DownloadHistoryRepository.BuildCurrentSignificantEventsCte(DatabaseType.SQLite);

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Contain(@"""EventType"" IN @significantEvents"));
                Assert.That(sql, Does.Not.Contain(@"""EventType"" = ANY(@significantEvents)"));
            });
        }

        [Test]
        public void remove_ignored_should_use_postgres_array_predicate()
        {
            var sql = DownloadHistoryRepository.BuildDeleteIgnoredByDownloadIdsSql(DatabaseType.PostgreSQL);

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Contain(@"""DownloadId"" = ANY(@downloadIds)"));
                Assert.That(sql, Does.Not.Contain(@"""DownloadId"" IN @downloadIds"));
            });
        }

        [Test]
        public void remove_ignored_should_delete_all_ignore_rows_for_the_download_id()
        {
            WithRepository(repository =>
            {
                var service = new DownloadHistoryService(repository, historyService: null);
                var removedDownloadIds = service.RemoveIgnored(7);
                var remaining = repository.FindByDownloadId("DUPLICATE");

                Assert.Multiple(() =>
                {
                    Assert.That(removedDownloadIds, Is.EqualTo(new[] { "DUPLICATE" }));
                    Assert.That(remaining.Any(h => h.EventType == DownloadHistoryEventType.DownloadIgnored), Is.False);
                    Assert.That(remaining.Any(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed), Is.True);
                });
            });
        }

        [Test]
        public void remove_ignored_should_ignore_missing_ids_in_bulk()
        {
            WithRepository(repository =>
            {
                var service = new DownloadHistoryService(repository, historyService: null);
                var removedDownloadIds = service.RemoveIgnored(new List<int> { 7, 404 });
                var remaining = repository.FindByDownloadId("DUPLICATE");

                Assert.Multiple(() =>
                {
                    Assert.That(removedDownloadIds, Is.EqualTo(new[] { "DUPLICATE" }));
                    Assert.That(remaining.Any(h => h.EventType == DownloadHistoryEventType.DownloadIgnored), Is.False);
                    Assert.That(remaining.Any(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed), Is.True);
                });
            });
        }

        private static void WithRepository(Action<DownloadHistoryRepository> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"ignored_download_history_{Guid.NewGuid():N}.db");
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
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");
                    CreateSchema(connection);
                    SeedHistory(connection);
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                action(new DownloadHistoryRepository(new MainDatabase(database), new StubEventAggregator()));
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

        private static void CreateSchema(SqliteConnection connection)
        {
            connection.Execute(@"
                CREATE TABLE ""DownloadHistory"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""EventType"" INTEGER NOT NULL,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""BookId"" INTEGER NOT NULL,
                    ""DownloadId"" TEXT NOT NULL,
                    ""SourceTitle"" TEXT NOT NULL,
                    ""Date"" TEXT NOT NULL,
                    ""Protocol"" INTEGER NULL,
                    ""IndexerId"" INTEGER NULL,
                    ""DownloadClientId"" INTEGER NULL,
                    ""Release"" TEXT NULL,
                    ""Data"" TEXT NULL
                );
            ");
        }

        private static void SeedHistory(SqliteConnection connection)
        {
            var now = DateTime.UtcNow;

            Insert(1, DownloadHistoryEventType.DownloadIgnored, "REGABBED", "Regabbed", now.AddMinutes(-30));
            Insert(2, DownloadHistoryEventType.DownloadGrabbed, "REGABBED", "Regabbed", now.AddMinutes(-20));
            Insert(3, DownloadHistoryEventType.DownloadIgnored, "CURRENT", "Current ignored", now.AddMinutes(-10));
            Insert(4, DownloadHistoryEventType.DownloadIgnored, "IMPORTED", "Imported", now.AddMinutes(-15));
            Insert(5, DownloadHistoryEventType.DownloadImported, "IMPORTED", "Imported", now.AddMinutes(-5));
            Insert(6, DownloadHistoryEventType.DownloadIgnored, "DUPLICATE", "Duplicate ignored older", now.AddMinutes(-25));
            Insert(7, DownloadHistoryEventType.DownloadIgnored, "DUPLICATE", "Duplicate ignored newer", now.AddMinutes(-1));
            Insert(8, DownloadHistoryEventType.DownloadGrabbed, "DUPLICATE", "Duplicate ignored grab", now.AddMinutes(-40));
            Insert(9, DownloadHistoryEventType.FileImported, "FILEONLY", "File-only event", now);

            void Insert(int id, DownloadHistoryEventType eventType, string downloadId, string sourceTitle, DateTime date)
            {
                connection.Execute(@"
                    INSERT INTO ""DownloadHistory""
                        (""Id"", ""EventType"", ""AuthorId"", ""BookId"", ""DownloadId"", ""SourceTitle"", ""Date"", ""Protocol"", ""IndexerId"", ""DownloadClientId"", ""Release"", ""Data"")
                    VALUES
                        (@id, @eventType, 0, 0, @downloadId, @sourceTitle, @date, @protocol, 0, 1, NULL, @data);",
                    new
                    {
                        id,
                        eventType = (int)eventType,
                        downloadId,
                        sourceTitle,
                        date,
                        protocol = (int)DownloadProtocol.Torrent,
                        data = "{}"
                    });
            }
        }
    }
}
