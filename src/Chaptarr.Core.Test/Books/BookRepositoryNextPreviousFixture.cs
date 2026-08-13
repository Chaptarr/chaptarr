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
    public class BookRepositoryNextPreviousFixture
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
        public void should_select_the_latest_past_book_from_the_same_row()
        {
            WithRepository((repository, seed) =>
            {
                var books = repository.GetLastBooks(new[] { 10 });

                Assert.That(books.Select(book => book.Id), Is.EqualTo(new[] { seed.LatestPastBookId }));
            });
        }

        [Test]
        public void should_select_the_nearest_future_book_from_the_same_row()
        {
            WithRepository((repository, seed) =>
            {
                var books = repository.GetNextBooks(new[] { 10 });

                Assert.That(books.Select(book => book.Id), Is.EqualTo(new[] { seed.NearestFutureBookId }));
            });
        }

        [Test]
        public void should_use_the_smallest_id_to_break_equal_release_date_ties()
        {
            WithRepository((repository, seed) =>
            {
                var last = repository.GetLastBooks(new[] { 20 });
                var next = repository.GetNextBooks(new[] { 20 });

                Assert.That(last.Select(book => book.Id), Is.EqualTo(new[] { seed.PastTieWinnerId }));
                Assert.That(next.Select(book => book.Id), Is.EqualTo(new[] { seed.FutureTieWinnerId }));
            });
        }

        [Test]
        public void should_preserve_null_empty_and_sqlite_chunking_behavior()
        {
            WithRepository((repository, seed) =>
            {
                Assert.That(repository.GetLastBooks(null), Is.Empty);
                Assert.That(repository.GetNextBooks(Array.Empty<int>()), Is.Empty);

                var authorIds = Enumerable.Range(1000, SqliteVariableLimit.MaxParameters).Append(10).ToList();

                Assert.That(repository.GetLastBooks(authorIds).Select(book => book.Id),
                    Is.EqualTo(new[] { seed.LatestPastBookId }));
                Assert.That(repository.GetNextBooks(authorIds).Select(book => book.Id),
                    Is.EqualTo(new[] { seed.NearestFutureBookId }));
            });
        }

        private static void WithRepository(Action<BookRepository, SeedResult> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_next_previous_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                SeedResult seed;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");
                    CreateSchema(connection);
                    seed = SeedBooks(connection);
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                action(new BookRepository(new MainDatabase(database), new StubEventAggregator()), seed);
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
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""Title"" TEXT NULL,
                    ""ReleaseDate"" TEXT NULL
                );
            ");
        }

        private static SeedResult SeedBooks(SqliteConnection connection)
        {
            var latestPast = DateTime.UtcNow.AddDays(-10);
            var nearestFuture = DateTime.UtcNow.AddDays(10);
            var pastTie = DateTime.UtcNow.AddDays(-20);
            var futureTie = DateTime.UtcNow.AddDays(20);

            connection.Execute(@"
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""ReleaseDate"")
                VALUES
                    (1, 10, 'Older lower-id book', @olderPast),
                    (2, 10, 'Latest past book', @latestPast),
                    (3, 10, 'Later lower-id book', @laterFuture),
                    (4, 10, 'Nearest future book', @nearestFuture),
                    (20, 20, 'Past tie winner', @pastTie),
                    (21, 20, 'Past tie loser', @pastTie),
                    (22, 20, 'Future tie winner', @futureTie),
                    (23, 20, 'Future tie loser', @futureTie);
            ", new
            {
                olderPast = latestPast.AddDays(-30),
                latestPast,
                laterFuture = nearestFuture.AddDays(30),
                nearestFuture,
                pastTie,
                futureTie
            });

            return new SeedResult
            {
                LatestPastBookId = 2,
                NearestFutureBookId = 4,
                PastTieWinnerId = 20,
                FutureTieWinnerId = 22
            };
        }

        private sealed class SeedResult
        {
            public int LatestPastBookId { get; init; }
            public int NearestFutureBookId { get; init; }
            public int PastTieWinnerId { get; init; }
            public int FutureTieWinnerId { get; init; }
        }
    }
}
