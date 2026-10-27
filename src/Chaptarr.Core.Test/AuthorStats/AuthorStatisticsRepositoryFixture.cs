using System;
using System.IO;
using System.Data;
using Chaptarr.Core.Test.Common;
using Npgsql;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Datastore;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.AuthorStats
{
    [TestFixture]
    public class AuthorStatisticsRepositoryFixture
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
        public void should_count_books_without_provider_editions_and_not_multiply_file_totals()
        {
            WithRepository((repository, connectionString) =>
            {
                var stats = repository.AuthorStatistics(1).OrderBy(stat => stat.BookId).ToList();

                Assert.That(stats.Select(stat => stat.BookId), Is.EqualTo(new[] { 10, 11, 12 }));

                Assert.Multiple(() =>
                {
                    Assert.That(stats[0].BookFileCount, Is.EqualTo(2));
                    Assert.That(stats[0].SizeOnDisk, Is.EqualTo(30));
                    Assert.That(stats[0].TotalBookCount, Is.EqualTo(1));
                    Assert.That(stats[0].AvailableBookCount, Is.EqualTo(1));
                    Assert.That(stats[0].BookCount, Is.EqualTo(1));

                    Assert.That(stats[1].BookFileCount, Is.Zero);
                    Assert.That(stats[1].SizeOnDisk, Is.Zero);
                    Assert.That(stats[1].TotalBookCount, Is.EqualTo(1));
                    Assert.That(stats[1].AvailableBookCount, Is.Zero);
                    Assert.That(stats[1].BookCount, Is.EqualTo(1));

                    Assert.That(stats[2].BookFileCount, Is.EqualTo(1));
                    Assert.That(stats[2].SizeOnDisk, Is.EqualTo(30));
                    Assert.That(stats[2].TotalBookCount, Is.Zero);
                    Assert.That(stats[2].AvailableBookCount, Is.Zero);
                    Assert.That(stats[2].BookCount, Is.Zero);
                });
            });
        }

        [Test]
        public void should_follow_edition_repoints_and_count_files_on_unmonitored_editions()
        {
            WithRepository((repository, connectionString) =>
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"UPDATE ""Editions"" SET ""BookId"" = 11 WHERE ""Id"" = 100;");
                }

                var stats = repository.AuthorStatistics(1).ToDictionary(stat => stat.BookId);

                Assert.Multiple(() =>
                {
                    Assert.That(stats[10].BookFileCount, Is.EqualTo(1));
                    Assert.That(stats[10].SizeOnDisk, Is.EqualTo(20));
                    Assert.That(stats[11].BookFileCount, Is.EqualTo(1));
                    Assert.That(stats[11].SizeOnDisk, Is.EqualTo(10));
                    Assert.That(stats[11].AvailableBookCount, Is.EqualTo(1));
                });
            });
        }

        [Test]
        public void should_use_monitored_edition_date_and_file_escape_with_book_date_fallback()
        {
            WithRepository((repository, connectionString) =>
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""ReleaseDate"") VALUES
    (30, 1, 0, 1, 0, '2000-01-01'),
    (31, 1, 0, 1, 0, '2099-01-01'),
    (32, 1, 0, 1, 0, '2000-01-01'),
    (33, 1, 0, 1, 0, '2099-01-01');
INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"", ""ReleaseDate"") VALUES
    (300, 30, 1, '2099-01-01'),
    (310, 31, 0, '2099-01-01'),
    (320, 32, 0, NULL),
    (330, 33, 1, '2000-01-01');
INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""Size"") VALUES
    (3000, 300, 50);
");
                }

                var stats = repository.AuthorStatistics(1, "audiobook").ToDictionary(stat => stat.BookId);

                Assert.Multiple(() =>
                {
                    Assert.That(stats[30].TotalBookCount, Is.EqualTo(1));
                    Assert.That(stats[30].BookCount, Is.EqualTo(1), "A file makes a future monitored edition count as available");
                    Assert.That(stats[30].AvailableBookCount, Is.EqualTo(1));
                    Assert.That(stats[31].TotalBookCount, Is.EqualTo(1));
                    Assert.That(stats[31].BookCount, Is.Zero, "A future book date is not yet in the progress denominator");
                    Assert.That(stats[31].AvailableBookCount, Is.Zero);
                    Assert.That(stats[32].TotalBookCount, Is.EqualTo(1));
                    Assert.That(stats[32].BookCount, Is.EqualTo(1), "The book date is used when no monitored edition exists");
                    Assert.That(stats[32].AvailableBookCount, Is.Zero);
                    Assert.That(stats[33].TotalBookCount, Is.EqualTo(1));
                    Assert.That(stats[33].BookCount, Is.EqualTo(1), "The monitored edition date takes precedence over the book date");
                    Assert.That(stats[33].AvailableBookCount, Is.Zero);
                });
            });
        }

        [Test]
        public void progress_should_preserve_book_row_selections_while_the_author_side_is_paused()
        {
            WithRepository((repository, connectionString) =>
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"UPDATE ""Authors"" SET ""AudiobookMonitored"" = 0, ""EbookMonitored"" = NULL WHERE ""Id"" = 1;");
                }

                var stats = repository.AuthorStatistics(1).ToDictionary(stat => stat.BookId);

                Assert.Multiple(() =>
                {
                    Assert.That(stats[10].TotalBookCount, Is.EqualTo(1), "progress describes the saved audiobook row selection");
                    Assert.That(stats[11].TotalBookCount, Is.EqualTo(1), "an unconfigured author side does not erase its saved ebook row selection");
                    Assert.That(stats[10].AvailableBookCount, Is.EqualTo(1));
                });
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void statistics_should_use_selected_edition_and_null_fallback_in_one_query(DatabaseType databaseType)
        {
            var postgres = databaseType == DatabaseType.PostgreSQL;
            var pgConnection = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(pgConnection))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL statistics");
            }

            using IDbConnection connection = postgres
                ? new NpgsqlConnection(pgConnection)
                : new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            var booleanType = postgres ? "BOOLEAN" : "INTEGER";
            var dateType = postgres ? "TIMESTAMP" : "TEXT";
            connection.Execute($@"
                CREATE TEMP TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY);
                CREATE TEMP TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER, ""MediaType"" INTEGER,
                    ""AudiobookMonitored"" {booleanType}, ""EbookMonitored"" {booleanType}, ""ReleaseDate"" {dateType});
                CREATE TEMP TABLE ""Editions"" (
                    ""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER, ""Monitored"" {booleanType}, ""ReleaseDate"" {dateType});
                CREATE UNIQUE INDEX ""IX_Editions_OneMonitoredPerBook"" ON ""Editions"" (""BookId"") WHERE ""Monitored"" = TRUE;
                CREATE TEMP TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""EditionId"" INTEGER, ""Size"" BIGINT);
                INSERT INTO ""Authors"" VALUES (1), (2);
                INSERT INTO ""Books"" VALUES
                    (1, 1, 0, TRUE, FALSE, '2099-01-01'),
                    (2, 1, 1, FALSE, TRUE, '2000-01-01'),
                    (3, 1, 0, TRUE, FALSE, '2099-01-01'),
                    (4, 1, 1, FALSE, TRUE, NULL),
                    (5, 1, 0, TRUE, FALSE, '2000-01-01'),
                    (6, 1, 0, TRUE, FALSE, '2099-01-01'),
                    (7, 2, 0, FALSE, FALSE, NULL);
                INSERT INTO ""Editions"" VALUES
                    (10, 1, TRUE, '2000-01-01'), (11, 1, FALSE, '2099-01-01'),
                    (20, 2, TRUE, NULL), (21, 2, FALSE, '2099-01-01'),
                    (30, 3, FALSE, '2000-01-01'),
                    (50, 5, TRUE, '2099-01-01'), (51, 5, FALSE, '2000-01-01'),
                    (60, 6, TRUE, '2099-01-01'), (61, 6, FALSE, NULL);
                INSERT INTO ""BookFiles"" VALUES (1, 60, 10), (2, 61, 20);");

            // Keep session-local tables alive across repository calls on both engines.
            var lease = Proxy<IDbConnection>((method, args) =>
                method.Name == nameof(IDisposable.Dispose) ? null : method.Invoke(connection, args));
            var commandCount = 0;
            var observed = CommandObserver.Wrap(lease, _ => commandCount++);
            var database = Proxy<IMainDatabase>((method, args) => method.Name switch
            {
                nameof(IMainDatabase.OpenConnection) => observed,
                "get_DatabaseType" => databaseType,
                _ => throw new NotImplementedException(method.Name)
            });
            var repository = new AuthorStatisticsRepository(database);
            var all = repository.AuthorStatistics().OrderBy(stat => stat.BookId).ToList();
            var author = repository.AuthorStatistics(1);
            var audio = repository.AuthorStatistics("audiobook");
            var authorEbook = repository.AuthorStatistics(1, "ebook");

            Assert.Multiple(() =>
            {
                Assert.That(commandCount, Is.EqualTo(4), "Each repository call must remain one query");
                Assert.That(all.Select(stat => stat.BookId), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7 }));
                Assert.That(all.Select(stat => stat.BookCount), Is.EqualTo(new[] { 1, 1, 0, 1, 0, 1, 0 }));
                Assert.That(all.Select(stat => stat.TotalBookCount), Is.EqualTo(new[] { 1, 1, 1, 1, 1, 1, 0 }));
                Assert.That(all.Select(stat => stat.BookFileCount), Is.EqualTo(new[] { 0, 0, 0, 0, 0, 2, 0 }));
                Assert.That(all[5].SizeOnDisk, Is.EqualTo(30));
                Assert.That(all[5].AvailableBookCount, Is.EqualTo(1));
                Assert.That(author.Select(stat => stat.BookId), Is.EquivalentTo(new[] { 1, 2, 3, 4, 5, 6 }));
                Assert.That(audio.Select(stat => stat.BookId), Is.EquivalentTo(new[] { 1, 3, 5, 6, 7 }));
                Assert.That(authorEbook.Select(stat => stat.BookId), Is.EquivalentTo(new[] { 2, 4 }));
            });
        }

        [Test]
        public void should_use_one_indexed_lookup_for_the_monitored_edition_join()
        {
            WithRepository((repository, connectionString) =>
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                var sql = AuthorStatisticsRepository.BuildBaseSql(DatabaseType.SQLite);
                var plan = connection.Query<SqliteQueryPlanRow>(
                    "EXPLAIN QUERY PLAN " + sql,
                    new { currentDate = DateTime.UtcNow }).ToList();

                Assert.That(
                    plan.Count(row => row.Detail.Contains("IX_Editions_BookId_Monitored_Id") || row.Detail.Contains("IX_Editions_OneMonitoredPerBook")),
                    Is.EqualTo(1),
                    "The selected-edition join needs one indexed lookup and no duplicate-selection check");
            });
        }

        private static void WithRepository(Action<AuthorStatisticsRepository, string> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_stats_{Guid.NewGuid():N}.db");
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
CREATE TABLE ""Authors"" (
    ""Id"" INTEGER PRIMARY KEY,
    ""AudiobookMonitored"" INTEGER NULL,
    ""EbookMonitored"" INTEGER NULL
);
CREATE TABLE ""Books"" (
    ""Id"" INTEGER PRIMARY KEY,
    ""AuthorId"" INTEGER NOT NULL,
    ""MediaType"" INTEGER NOT NULL,
    ""AudiobookMonitored"" INTEGER NOT NULL,
    ""EbookMonitored"" INTEGER NOT NULL,
    ""ReleaseDate"" TEXT NULL
);
CREATE TABLE ""Editions"" (
    ""Id"" INTEGER PRIMARY KEY,
    ""BookId"" INTEGER NOT NULL,
    ""Monitored"" INTEGER NOT NULL,
    ""ReleaseDate"" TEXT NULL
);
CREATE UNIQUE INDEX ""IX_Editions_OneMonitoredPerBook"" ON ""Editions"" (""BookId"") WHERE ""Monitored"" = TRUE;
CREATE TABLE ""BookFiles"" (
    ""Id"" INTEGER PRIMARY KEY,
    ""EditionId"" INTEGER NOT NULL,
    ""Size"" INTEGER NOT NULL
);

CREATE INDEX ""IX_Editions_BookId_Monitored_Id""
    ON ""Editions"" (""BookId"", ""Monitored"", ""Id"");

INSERT INTO ""Authors"" (""Id"", ""AudiobookMonitored"", ""EbookMonitored"") VALUES
    (1, 1, 1),
    (2, 1, 1);
INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"") VALUES
    (10, 1, 0, 1, 0),
    (11, 1, 1, 0, 1),
    (12, 1, 0, 0, 0),
    (20, 2, 0, 1, 0);
INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"") VALUES
    (100, 10, 0),
    (101, 10, 1),
    (120, 12, 0),
    (200, 20, 1);
INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""Size"") VALUES
    (1000, 100, 10),
    (1001, 101, 20),
    (1200, 120, 30),
    (2000, 200, 40);
");
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                action(new AuthorStatisticsRepository(new MainDatabase(database)), connectionString);
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

        private sealed class SqliteQueryPlanRow
        {
            public string Detail { get; set; }
        }
    }
}
