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
        public void should_exclude_monitored_future_releases_from_progress_for_both_media_types()
        {
            WithRepository((repository, connectionString) =>
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""ReleaseDate"") VALUES
    (30, 3, 1, 0, 1, @released),
    (31, 3, 0, 1, 0, @released),
    (32, 3, 1, 0, 1, @future),
    (33, 3, 0, 1, 0, @future);
INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"") VALUES
    (300, 30, 1),
    (310, 31, 1);
INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""Size"") VALUES
    (3000, 300, 10),
    (3100, 310, 20);",
                        new
                        {
                            released = DateTime.UtcNow.AddDays(-1),
                            future = DateTime.UtcNow.AddDays(1)
                        });
                }

                var allStats = repository.AuthorStatistics(3);
                var ebookStats = repository.AuthorStatistics(3, "ebook");
                var audiobookStats = repository.AuthorStatistics(3, "audiobook");

                Assert.Multiple(() =>
                {
                    Assert.That(allStats.Sum(stat => stat.BookCount), Is.EqualTo(2));
                    Assert.That(allStats.Sum(stat => stat.AvailableBookCount), Is.EqualTo(2));
                    Assert.That(allStats.Sum(stat => stat.TotalBookCount), Is.EqualTo(4));

                    Assert.That(ebookStats.Sum(stat => stat.BookCount), Is.EqualTo(1));
                    Assert.That(ebookStats.Sum(stat => stat.AvailableBookCount), Is.EqualTo(1));
                    Assert.That(ebookStats.Sum(stat => stat.TotalBookCount), Is.EqualTo(2));

                    Assert.That(audiobookStats.Sum(stat => stat.BookCount), Is.EqualTo(1));
                    Assert.That(audiobookStats.Sum(stat => stat.AvailableBookCount), Is.EqualTo(1));
                    Assert.That(audiobookStats.Sum(stat => stat.TotalBookCount), Is.EqualTo(2));
                });
            });
        }

        [Test]
        public void aggregate_should_count_file_bearing_books_once()
        {
            WithRepository((repository, _) =>
            {
                var aggregate = repository.GetAggregateStatistics(new() { 1 }, "all");

                Assert.Multiple(() =>
                {
                    Assert.That(aggregate.BookCount, Is.EqualTo(2));
                    Assert.That(aggregate.BookFileCount, Is.EqualTo(3));
                    Assert.That(aggregate.SizeOnDisk, Is.EqualTo(60));
                });
            });
        }

        [Test]
        public void aggregate_should_tolerate_more_author_ids_than_sqlite_can_bind_at_once()
        {
            WithRepository((repository, _) =>
            {
                var authorIds = Enumerable.Range(1000, 32768).Prepend(1).ToList();
                var aggregate = repository.GetAggregateStatistics(authorIds, "all");

                Assert.Multiple(() =>
                {
                    Assert.That(aggregate.BookCount, Is.EqualTo(2));
                    Assert.That(aggregate.BookFileCount, Is.EqualTo(3));
                    Assert.That(aggregate.SizeOnDisk, Is.EqualTo(60));
                });
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
    ""Id"" INTEGER PRIMARY KEY
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
    ""Monitored"" INTEGER NOT NULL
);
CREATE TABLE ""BookFiles"" (
    ""Id"" INTEGER PRIMARY KEY,
    ""EditionId"" INTEGER NOT NULL,
    ""Size"" INTEGER NOT NULL
);

INSERT INTO ""Authors"" (""Id"") VALUES (1), (2), (3);
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
    }
}
