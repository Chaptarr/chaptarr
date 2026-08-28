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
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookRepositoryIndexFilterFixture
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
        public void missing_filter_should_return_only_monitored_books_without_selected_media_files()
        {
            WithRepository(sut =>
            {
                var ids = sut.GetBookIds(
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    missing: true);

                Assert.That(ids.OrderBy(id => id), Is.EqualTo(new[] { 1, 4, 5 }));

                var buckets = sut.GetBookBuckets(
                    sortKey: "title",
                    sortDirection: "ASC",
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    missing: true);

                Assert.That(buckets.TotalCount, Is.EqualTo(3));
            });
        }

        [Test]
        public void missing_search_targets_should_snapshot_ids_grouped_by_local_author()
        {
            WithRepository(sut =>
            {
                var ebookTargets = sut.GetMissingBookSearchTargets(BookMediaType.Ebook, authorId: null);

                Assert.That(ebookTargets.Select(target => (target.AuthorId, target.BookId)),
                    Is.EqualTo(new[] { (10, 1) }));

                var scopedAudiobookTargets = sut.GetMissingBookSearchTargets(BookMediaType.Audiobook, authorId: 20);

                Assert.That(scopedAudiobookTargets.Select(target => (target.AuthorId, target.BookId)),
                    Is.EqualTo(new[] { (20, 7) }));
            });
        }

        [Test]
        public void cutoff_unmet_search_targets_should_reuse_cutoff_and_monitoring_semantics()
        {
            WithRepository(sut =>
            {
                var qualitiesBelowCutoff = new List<QualitiesBelowCutoff>
                {
                    new(501, ProfileType.Ebook, new[] { 1 }),
                    new(502, ProfileType.Ebook, new[] { 3 })
                };

                var ebookTargets = sut.GetCutoffUnmetSearchTargets(qualitiesBelowCutoff, BookMediaType.Ebook, authorId: null);
                Assert.That(ebookTargets.Select(target => (target.AuthorId, target.BookId)),
                    Is.EqualTo(new[] { (10, 3), (30, 8) }));

                var otherAuthorTargets = sut.GetCutoffUnmetSearchTargets(qualitiesBelowCutoff, BookMediaType.Ebook, authorId: 30);
                Assert.That(otherAuthorTargets.Select(target => (target.AuthorId, target.BookId)),
                    Is.EqualTo(new[] { (30, 8) }));
            });
        }

        [Test]
        public void calendar_filter_should_use_the_author_setting_for_the_book_media_side()
        {
            WithRepository(sut =>
            {
                var start = DateTime.UtcNow.AddDays(-2);
                var end = DateTime.UtcNow.AddDays(8);

                var allAuthors = sut.BooksBetweenDates(start, end, includeUnmonitored: false);
                Assert.That(allAuthors.Select(book => book.Id).OrderBy(id => id), Is.EqualTo(new[] { 1, 3, 4, 5, 7 }));

                var oneAuthor = sut.AuthorBooksBetweenDates(new Author { Id = 10 }, start, end, includeUnmonitored: false);
                Assert.That(oneAuthor.Select(book => book.Id).OrderBy(id => id), Is.EqualTo(new[] { 1, 3, 4, 5 }));
            });
        }

        [Test]
        public void wanted_filter_should_apply_missing_filter_and_release_date_cutoff()
        {
            WithRepository(sut =>
            {
                var ids = sut.GetBookIds(
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    wanted: true);

                Assert.That(ids.OrderBy(id => id), Is.EqualTo(new[] { 1, 4 }));

                var buckets = sut.GetBookBuckets(
                    sortKey: "title",
                    sortDirection: "ASC",
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    wanted: true);

                Assert.That(buckets.TotalCount, Is.EqualTo(2));
            });
        }

        [Test]
        public void missing_and_wanted_filters_should_honor_the_author_media_gate()
        {
            WithRepository(sut =>
            {
                var missingIds = sut.GetBookIds(
                    includeUnmonitored: true,
                    mediaType: "audiobook",
                    downloaded: null,
                    monitored: null,
                    missing: true);

                Assert.That(missingIds, Is.EqualTo(new[] { 7 }));

                var wantedIds = sut.GetBookIds(
                    includeUnmonitored: true,
                    mediaType: "audiobook",
                    downloaded: null,
                    monitored: null,
                    wanted: true);

                Assert.That(wantedIds, Is.EqualTo(new[] { 7 }));

                var missingBuckets = sut.GetBookBuckets(
                    sortKey: "title",
                    sortDirection: "ASC",
                    includeUnmonitored: true,
                    mediaType: "audiobook",
                    downloaded: null,
                    monitored: null,
                    missing: true);

                Assert.That(missingBuckets.TotalCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void paged_books_should_sort_by_size_on_disk()
        {
            WithRepository(sut =>
            {
                var descending = sut.GetBooksPaged(
                    offset: 0,
                    pageSize: 100,
                    sortKey: "sizeOnDisk",
                    sortDirection: "DESC",
                    includeUnmonitored: true);

                // Sizes: book 8 = 789 + 790, book 4 = 456, book 3 = 123, the rest have no files.
                Assert.That(descending.Records.Select(book => book.Id),
                    Is.EqualTo(new[] { 8, 4, 3, 7, 6, 5, 2, 1 }));

                var ascending = sut.GetBooksPaged(
                    offset: 0,
                    pageSize: 100,
                    sortKey: "sizeOnDisk",
                    sortDirection: "ASC",
                    includeUnmonitored: true);

                Assert.That(ascending.Records.Select(book => book.Id),
                    Is.EqualTo(new[] { 1, 2, 5, 6, 7, 3, 4, 8 }));
            });
        }

        [Test]
        public void paged_size_sort_should_preserve_media_type_and_downloaded_filters()
        {
            WithRepository(sut =>
            {
                var ebooks = sut.GetBooksPaged(
                    offset: 0,
                    pageSize: 100,
                    sortKey: "sizeOnDisk",
                    sortDirection: "DESC",
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null);

                Assert.That(ebooks.Records.Select(book => book.Id),
                    Is.EqualTo(new[] { 8, 4, 3, 5, 2, 1 }));

                var audiobooks = sut.GetBooksPaged(
                    offset: 0,
                    pageSize: 100,
                    sortKey: "sizeOnDisk",
                    sortDirection: "DESC",
                    includeUnmonitored: true,
                    mediaType: "audiobook",
                    downloaded: null);

                Assert.That(audiobooks.Records.Select(book => book.Id),
                    Is.EqualTo(new[] { 7, 6 }));

                var downloadedEbooks = sut.GetBooksPaged(
                    offset: 0,
                    pageSize: 100,
                    sortKey: "sizeOnDisk",
                    sortDirection: "DESC",
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: true);

                Assert.That(downloadedEbooks.Records.Select(book => book.Id),
                    Is.EqualTo(new[] { 8, 3 }));
            });
        }

        [Test]
        public void find_existing_should_return_partial_results_without_weakening_strict_get()
        {
            WithRepository(sut =>
            {
                Assert.That(
                    sut.FindExisting(new[] { 1, 999, 4 }).Select(book => book.Id).OrderBy(id => id),
                    Is.EqualTo(new[] { 1, 4 }));
                Assert.Throws<ApplicationException>(() => sut.Get(new[] { 1, 999, 4 }).ToList());
            });
        }

        private static void WithRepository(Action<BookRepository> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_index_filter_{Guid.NewGuid():N}.db");
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
                    SeedBooks(connection);
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                action(new BookRepository(new MainDatabase(database), new StubEventAggregator()));
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
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AudiobookMonitored"" INTEGER NULL,
                    ""AudiobookMonitorNewItems"" INTEGER NULL,
                    ""EbookMonitored"" INTEGER NULL,
                    ""EbookMonitorNewItems"" INTEGER NULL,
                    ""AudiobookQualityProfileId"" INTEGER NULL,
                    ""EbookQualityProfileId"" INTEGER NULL
                );
            ");

            connection.Execute(@"
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""Title"" TEXT NULL,
                    ""CleanTitle"" TEXT NULL,
                    ""MediaType"" INTEGER NOT NULL,
                    ""AudiobookMonitored"" INTEGER NOT NULL,
                    ""EbookMonitored"" INTEGER NOT NULL,
                    ""ReleaseDate"" TEXT NULL
                );
            ");

            connection.Execute(@"
                CREATE TABLE ""Editions"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""BookId"" INTEGER NOT NULL,
                    ""Monitored"" INTEGER NOT NULL
                );
            ");

            connection.Execute(@"
                CREATE TABLE ""BookFiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""EditionId"" INTEGER NOT NULL,
                    ""MediaType"" TEXT NULL,
                    ""Size"" INTEGER NOT NULL,
                    ""Quality"" TEXT NULL
                );
            ");
        }

        private static void SeedBooks(SqliteConnection connection)
        {
            var past = DateTime.UtcNow.AddDays(-1);
            var future = DateTime.UtcNow.AddDays(7);
            var oldRelease = DateTime.UtcNow.AddDays(-100);

            connection.Execute(@"
                INSERT INTO ""Authors"" (""Id"", ""AudiobookMonitored"", ""AudiobookMonitorNewItems"", ""EbookMonitored"", ""EbookMonitorNewItems"", ""AudiobookQualityProfileId"", ""EbookQualityProfileId"")
                VALUES
                    (10, 0, 1, 1, 1, 601, 501),
                    (20, 1, 2, 0, 1, 601, 501),
                    (30, 0, 1, 1, 1, 602, 502);
            ");

            connection.Execute(@"
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""ReleaseDate"")
                VALUES
                    (1, 10, 'Missing Now', 'missing now', @ebook, 0, 1, @past),
                    (2, 10, 'Unmonitored Missing', 'unmonitored missing', @ebook, 0, 0, @past),
                    (3, 10, 'Existing Ebook', 'existing ebook', @ebook, 0, 1, @past),
                    (4, 10, 'Audio Only File', 'audio only file', @ebook, 0, 1, @past),
                    (5, 10, 'Future Missing', 'future missing', @ebook, 0, 1, @future),
                    (6, 10, 'Paused Audiobook', 'paused audiobook', @audiobook, 1, 0, @past),
                    (7, 20, 'Future-Monitored Audiobook', 'future monitored audiobook', @audiobook, 1, 0, @past),
                    (8, 30, 'Different Profile Ebook', 'different profile ebook', @ebook, 0, 1, @oldRelease);
            ", new
            {
                audiobook = (int)BookMediaType.Audiobook,
                ebook = (int)BookMediaType.Ebook,
                past,
                future,
                oldRelease
            });

            connection.Execute(@"
                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"")
                VALUES
                    (10, 1, 1),
                    (11, 1, 1),
                    (20, 2, 1),
                    (30, 3, 1),
                    (40, 4, 1),
                    (50, 5, 1),
                    (60, 6, 1),
                    (70, 7, 1),
                    (80, 8, 1),
                    (81, 8, 1);
            ");

            connection.Execute(@"
                INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""MediaType"", ""Size"", ""Quality"")
                VALUES
                    (300, 30, 'ebook', 123, '{""quality"": 1, ""revision"": {}}'),
                    (400, 40, 'audiobook', 456, '{""quality"": 2, ""revision"": {}}'),
                    (800, 80, 'ebook', 789, '{""quality"": 3, ""revision"": {}}'),
                    (801, 81, 'ebook', 790, '{""quality"": 3, ""revision"": {}}');
            ");
        }
    }
}
