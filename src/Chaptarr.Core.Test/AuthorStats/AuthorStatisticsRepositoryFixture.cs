using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Datastore;

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

        [Test]
        public void should_use_the_composite_index_for_each_monitored_edition_release_date_lookup()
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
                    plan.Count(row => row.Detail.Contains("IX_Editions_BookId_Monitored_Id")),
                    Is.EqualTo(2),
                    "The release-date expression appears twice and both lookups must use the BookId-first composite index");
            });
        }

        private static void WithRepository(Action<AuthorStatisticsRepository, string> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_stats_{Guid.NewGuid():N}.db");
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
