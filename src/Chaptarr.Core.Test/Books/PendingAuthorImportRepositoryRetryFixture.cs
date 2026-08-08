using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class PendingAuthorImportRepositoryRetryFixture
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
        public void due_query_should_select_transient_rows_at_or_beyond_the_legacy_attempt_ceiling()
        {
            WithRepository((repository, connectionString) =>
            {
                var due = repository.GetDueForProcessing(DateTime.UtcNow, 10);

                Assert.That(due.Select(x => x.Id), Is.EqualTo(new[] { 1, 4 }));
            });
        }

        private static void WithRepository(Action<PendingAuthorImportRepository, string> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"pending_author_retry_{Guid.NewGuid():N}.db");
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
                        CREATE TABLE ""PendingAuthorImport"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""ProviderId"" TEXT,
                            ""NextAttemptAt"" DATETIME,
                            ""OverallStatus"" INTEGER,
                            ""AttemptCount"" INTEGER,
                            ""MaxAttempts"" INTEGER,
                            ""CreatedAt"" DATETIME
                        );

                        INSERT INTO ""PendingAuthorImport""
                            (""Id"", ""ProviderId"", ""NextAttemptAt"", ""OverallStatus"", ""AttemptCount"", ""MaxAttempts"", ""CreatedAt"")
                        VALUES
                            (1, 'gr:1', @DueFirst, @Retrying, 100, 100, @Created),
                            (2, 'gr:2', @Future, @Retrying, 2, 100, @Created),
                            (3, 'gr:3', @DueSecond, @Failed, 100, 100, @Created),
                            (4, 'gr:4', @DueSecond, @Pending, 500, 1, @Created);",
                        new
                        {
                            DueFirst = DateTime.UtcNow.AddMinutes(-2),
                            DueSecond = DateTime.UtcNow.AddMinutes(-1),
                            Future = DateTime.UtcNow.AddMinutes(5),
                            Created = DateTime.UtcNow.AddDays(-1),
                            Retrying = PendingImportStatus.Retrying,
                            Pending = PendingImportStatus.Pending,
                            Failed = PendingImportStatus.Failed
                        });
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                action(new PendingAuthorImportRepository(new MainDatabase(database), new StubEventAggregator()), connectionString);
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
