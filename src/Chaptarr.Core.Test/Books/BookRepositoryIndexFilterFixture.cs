using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
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
        public void missing_filter_should_return_only_monitored_books_without_owned_row_files()
        {
            WithRepository(sut =>
            {
                var ids = sut.GetBookIds(
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    missing: true);

                Assert.That(ids.OrderBy(id => id), Is.EqualTo(new[] { 1, 5 }));

                var buckets = sut.GetBookBuckets(
                    sortKey: "title",
                    sortDirection: "ASC",
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    missing: true);

                Assert.That(buckets.TotalCount, Is.EqualTo(2));
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

                Assert.That(ids.OrderBy(id => id), Is.EqualTo(new[] { 1 }));

                var buckets = sut.GetBookBuckets(
                    sortKey: "title",
                    sortDirection: "ASC",
                    includeUnmonitored: true,
                    mediaType: "ebook",
                    downloaded: null,
                    monitored: null,
                    wanted: true);

                Assert.That(buckets.TotalCount, Is.EqualTo(1));
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

                // Every file attached through an edition contributes to its owning book's size.
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
                    Is.EqualTo(new[] { 8, 4, 3 }));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void library_filter_should_scope_books_and_full_footer_statistics(DatabaseType databaseType)
        {
            WithRepository(databaseType, sut =>
            {
                var ids = sut.GetBookIds(includeUnmonitored: false, library: true);
                Assert.That(ids.OrderBy(id => id), Is.EqualTo(new[] { 1, 3, 4, 5, 6, 7, 8, 9, 10 }));

                var page = sut.GetBooksPaged(0, 100, "id", "ASC", includeUnmonitored: false, mediaType: null,
                    downloaded: null, monitored: null, library: true);
                Assert.That(page.Records.Select(book => book.Id), Is.EqualTo(ids.OrderBy(id => id)));
                Assert.That(page.TotalCount, Is.EqualTo(9));

                var smallPage = sut.GetBooksPaged(0, 2, "id", "ASC", includeUnmonitored: false, mediaType: null,
                    downloaded: null, monitored: null, library: true);
                Assert.That(smallPage.Records.Select(book => book.Id), Is.EqualTo(new[] { 1, 3 }));
                Assert.That(smallPage.TotalCount, Is.EqualTo(9), "the count should cover the complete filtered population");

                var buckets = sut.GetBookBuckets("title", "ASC", includeUnmonitored: false, mediaType: null,
                    downloaded: null, monitored: null, library: true);
                Assert.That(buckets.TotalCount, Is.EqualTo(9));
                AssertFooter(buckets.FooterStatistics, 9, 4, 7, 6, 4058);

                AssertMediaSide(sut, "ebook", new[] { 1, 3, 4, 5, 8, 10 }, 6, 3, 5, 5, 3158);
                AssertMediaSide(sut, "audiobook", new[] { 6, 7, 9 }, 3, 3, 2, 1, 900);

                var allIds = sut.GetBookIds(includeUnmonitored: true);
                Assert.That(allIds.OrderBy(id => id), Is.EqualTo(Enumerable.Range(1, 14)));
                Assert.That(sut.GetBookIds(includeUnmonitored: true, library: false).OrderBy(id => id), Is.EqualTo(allIds.OrderBy(id => id)));
                Assert.That(sut.GetBookIds(includeUnmonitored: true, library: null).OrderBy(id => id), Is.EqualTo(allIds.OrderBy(id => id)));

                var allPage = sut.GetBooksPaged(0, 100, "id", "ASC", includeUnmonitored: true);
                var falseLibraryPage = sut.GetBooksPaged(0, 100, "id", "ASC", includeUnmonitored: true,
                    mediaType: null, downloaded: null, monitored: null, library: false);
                var unspecifiedLibraryPage = sut.GetBooksPaged(0, 100, "id", "ASC", includeUnmonitored: true,
                    mediaType: null, downloaded: null, monitored: null, library: null);
                Assert.That(falseLibraryPage.Records.Select(book => book.Id), Is.EqualTo(allPage.Records.Select(book => book.Id)));
                Assert.That(falseLibraryPage.TotalCount, Is.EqualTo(allPage.TotalCount));
                Assert.That(unspecifiedLibraryPage.Records.Select(book => book.Id), Is.EqualTo(allPage.Records.Select(book => book.Id)));
                Assert.That(unspecifiedLibraryPage.TotalCount, Is.EqualTo(allPage.TotalCount));

                var allBuckets = sut.GetBookBuckets("title", "ASC", includeUnmonitored: true);
                var falseLibraryBuckets = sut.GetBookBuckets("title", "ASC", includeUnmonitored: true, mediaType: null, downloaded: null, monitored: null, library: false);
                var unspecifiedLibraryBuckets = sut.GetBookBuckets("title", "ASC", includeUnmonitored: true, mediaType: null, downloaded: null, monitored: null, library: null);
                Assert.That(falseLibraryBuckets.TotalCount, Is.EqualTo(allBuckets.TotalCount));
                Assert.That(unspecifiedLibraryBuckets.TotalCount, Is.EqualTo(allBuckets.TotalCount));
                AssertFooter(falseLibraryBuckets.FooterStatistics,
                    allBuckets.FooterStatistics.TotalBooks, allBuckets.FooterStatistics.AuthorCount,
                    allBuckets.FooterStatistics.MonitoredBooks, allBuckets.FooterStatistics.FileCount,
                    allBuckets.FooterStatistics.TotalFileSize);
                AssertFooter(unspecifiedLibraryBuckets.FooterStatistics,
                    allBuckets.FooterStatistics.TotalBooks, allBuckets.FooterStatistics.AuthorCount,
                    allBuckets.FooterStatistics.MonitoredBooks, allBuckets.FooterStatistics.FileCount,
                    allBuckets.FooterStatistics.TotalFileSize);
            }, includeLibraryRows: true);
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void downloaded_and_missing_filters_should_use_files_owned_by_the_book_row(DatabaseType databaseType)
        {
            WithRepository(databaseType, sut =>
            {
                var downloaded = sut.GetBookIds(includeUnmonitored: true, mediaType: "ebook", downloaded: true);
                Assert.That(downloaded, Is.EquivalentTo(new[] { 3, 4, 8, 10 }));
                var page = sut.GetBooksPaged(0, 100, "id", "ASC", true, "ebook", true);
                Assert.That(page.Records.Select(book => book.Id), Is.EquivalentTo(downloaded));
                var buckets = sut.GetBookBuckets("title", "ASC", true, "ebook", true);
                Assert.That(buckets.TotalCount, Is.EqualTo(downloaded.Count));
                Assert.That(buckets.FooterStatistics.FileCount, Is.EqualTo(5));
                Assert.That(sut.GetBookIds(true, "audiobook", true), Is.EquivalentTo(new[] { 9 }));
                Assert.That(sut.GetBookIds(true, "ebook", missing: true), Is.EquivalentTo(new[] { 1, 5 }));
            }, includeLibraryRows: true);
        }

        private static void AssertMediaSide(BookRepository repository, string mediaType, int[] expectedIds,
            int totalBooks, int authors, int monitoredBooks, int files, long totalFileSize)
        {
            var ids = repository.GetBookIds(includeUnmonitored: false, mediaType: mediaType, library: true);
            Assert.That(ids.OrderBy(id => id), Is.EqualTo(expectedIds), $"{mediaType} results should follow the book media enum");

            var page = repository.GetBooksPaged(0, 100, "id", "ASC", includeUnmonitored: false, mediaType: mediaType,
                downloaded: null, monitored: null, library: true);
            Assert.That(page.Records.Select(book => book.Id), Is.EqualTo(expectedIds));
            Assert.That(page.TotalCount, Is.EqualTo(totalBooks));

            var buckets = repository.GetBookBuckets("title", "ASC", includeUnmonitored: false, mediaType: mediaType,
                downloaded: null, monitored: null, library: true);
            Assert.That(buckets.TotalCount, Is.EqualTo(totalBooks));
            AssertFooter(buckets.FooterStatistics, totalBooks, authors, monitoredBooks, files, totalFileSize);
        }

        private static void AssertFooter(BookFooterStatistics footer, int totalBooks, int authors,
            int monitoredBooks, int files, long totalFileSize)
        {
            Assert.That(footer.TotalBooks, Is.EqualTo(totalBooks));
            Assert.That(footer.AuthorCount, Is.EqualTo(authors));
            Assert.That(footer.MonitoredBooks, Is.EqualTo(monitoredBooks));
            Assert.That(footer.FileCount, Is.EqualTo(files));
            Assert.That(footer.TotalFileSize, Is.EqualTo(totalFileSize));
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

        [Test]
        public void cutoff_pages_and_total_count_books_instead_of_parts(
            [Values(DatabaseType.SQLite, DatabaseType.PostgreSQL)] DatabaseType databaseType,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            WithRepository(databaseType, repository =>
            {
                var profileId = mediaType == BookMediaType.Audiobook ? 601 : 501;
                var profileType = mediaType == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook;
                var qualities = new List<QualitiesBelowCutoff> { new(profileId, profileType, new[] { 1 }) };
                var page = repository.BooksWhereCutoffUnmet(new PagingSpec<Book> { Page = 1, PageSize = 5 }, qualities);
                Assert.That(page.TotalRecords, Is.EqualTo(1));
                Assert.That(page.Records.Select(book => book.Id), Is.EqualTo(new[] { 3 }));
                Assert.That(page.TotalRecords, Is.LessThanOrEqualTo(5));
                qualities[0] = new QualitiesBelowCutoff(profileId, profileType, new[] { 1, 2 });
                page = repository.BooksWhereCutoffUnmet(new PagingSpec<Book> { Page = 2, PageSize = 1 }, qualities);
                Assert.That(page.TotalRecords, Is.EqualTo(2));
                Assert.That(page.Records.Select(book => book.Id), Is.EqualTo(new[] { 4 }));
            }, seed: connection =>
            {
                connection.Execute("UPDATE \"Books\" SET \"MediaType\" = @media WHERE \"Id\" IN (3, 4)", new { media = (int)mediaType });
                for (var part = 1; part < 6; part++)
                {
                    connection.Execute(@"INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""MediaType"", ""Size"", ""Quality"")
                        SELECT @id, ""EditionId"", ""MediaType"", ""Size"", ""Quality"" FROM ""BookFiles"" WHERE ""Id"" = 300", new { id = 300 + part });
                }
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void cutoff_sort_on_child_table_falls_back_to_book_id(DatabaseType databaseType)
        {
            WithRepository(databaseType, repository =>
            {
                var qualities = new List<QualitiesBelowCutoff> { new(501, ProfileType.Ebook, new[] { 1, 2 }) };
                var page = repository.BooksWhereCutoffUnmet(new PagingSpec<Book>
                {
                    Page = 1, PageSize = 5, SortKey = "editions.title", SortDirection = SortDirection.Descending
                }, qualities);
                Assert.That(page.TotalRecords, Is.EqualTo(2));
                Assert.That(page.Records.Select(book => book.Id), Is.EqualTo(new[] { 4, 3 }));
            }, seed: connection => connection.Execute("UPDATE \"Editions\" SET \"Title\" = CASE WHEN \"BookId\" = 3 THEN 'Zulu' ELSE 'Alpha' END"));
        }

        private static void WithRepository(Action<BookRepository> action)
        {
            WithRepository(DatabaseType.SQLite, action);
        }

        private static void WithRepository(DatabaseType databaseType, Action<BookRepository> action, bool includeLibraryRows = false, Action<IDbConnection> seed = null)
        {
            if (databaseType == DatabaseType.PostgreSQL)
            {
                WithPostgresRepository(action, includeLibraryRows, seed);
                return;
            }

            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_index_filter_{Guid.NewGuid():N}.db");
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
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");
                    CreateSchema(connection, databaseType);
                    SeedBooks(connection, databaseType);
                    seed?.Invoke(connection);
                    if (includeLibraryRows)
                    {
                        SeedLibraryFilterBooks(connection, databaseType);
                    }
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
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private static void WithPostgresRepository(Action<BookRepository> action, bool includeLibraryRows, Action<IDbConnection> seed)
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL book library filtering");
            }

            var schema = "book_index_filter_" + Guid.NewGuid().ToString("N");
            using (var admin = new NpgsqlConnection(adminConnectionString))
            {
                admin.Open();
                admin.Execute($"CREATE SCHEMA \"{schema}\"");
            }

            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
                {
                    SearchPath = schema,
                    Pooling = false
                }.ConnectionString;

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    CreateSchema(connection, DatabaseType.PostgreSQL);
                    SeedBooks(connection, DatabaseType.PostgreSQL);
                    seed?.Invoke(connection);
                    if (includeLibraryRows)
                    {
                        SeedLibraryFilterBooks(connection, DatabaseType.PostgreSQL);
                    }
                }

                var database = new Database("book_index_filter_postgres", () =>
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                action(new BookRepository(new MainDatabase(database), new StubEventAggregator()));
            }
            finally
            {
                using var admin = new NpgsqlConnection(adminConnectionString);
                admin.Open();
                admin.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
        }

        private static void CreateSchema(IDbConnection connection, DatabaseType databaseType)
        {
            var booleanType = databaseType == DatabaseType.PostgreSQL ? "BOOLEAN" : "INTEGER";
            var dateTimeType = databaseType == DatabaseType.PostgreSQL ? "TIMESTAMP" : "TEXT";
            connection.Execute($@"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AudiobookMonitored"" {booleanType} NULL,
                    ""AudiobookMonitorNewItems"" INTEGER NULL,
                    ""EbookMonitored"" {booleanType} NULL,
                    ""EbookMonitorNewItems"" INTEGER NULL,
                    ""AudiobookQualityProfileId"" INTEGER NULL,
                    ""EbookQualityProfileId"" INTEGER NULL
                );
            ");

            connection.Execute($@"
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""Title"" TEXT NULL,
                    ""CleanTitle"" TEXT NULL,
                    ""MediaType"" INTEGER NOT NULL,
                    ""AudiobookMonitored"" {booleanType} NOT NULL,
                    ""EbookMonitored"" {booleanType} NOT NULL,
                    ""ReleaseDate"" {dateTimeType} NULL
                );
            ");

            connection.Execute($@"
                CREATE TABLE ""Editions"" (
                    ""Title"" TEXT NULL,
                    ""Id"" INTEGER PRIMARY KEY,
                    ""BookId"" INTEGER NOT NULL,
                    ""Monitored"" {booleanType} NOT NULL
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

        private static void SeedBooks(IDbConnection connection, DatabaseType databaseType)
        {
            var past = DateTime.UtcNow.AddDays(-1);
            var future = DateTime.UtcNow.AddDays(7);
            var oldRelease = DateTime.UtcNow.AddDays(-100);
            var trueValue = databaseType == DatabaseType.PostgreSQL ? "TRUE" : "1";
            var falseValue = databaseType == DatabaseType.PostgreSQL ? "FALSE" : "0";

            connection.Execute($@"
                INSERT INTO ""Authors"" (""Id"", ""AudiobookMonitored"", ""AudiobookMonitorNewItems"", ""EbookMonitored"", ""EbookMonitorNewItems"", ""AudiobookQualityProfileId"", ""EbookQualityProfileId"")
                VALUES
                    (10, {falseValue}, 1, {trueValue}, 1, 601, 501),
                    (20, {trueValue}, 2, {falseValue}, 1, 601, 501),
                    (30, {falseValue}, 1, {trueValue}, 1, 602, 502);
            ");

            connection.Execute($@"
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""ReleaseDate"")
                VALUES
                    (1, 10, 'Missing Now', 'missing now', @ebook, {falseValue}, {trueValue}, @past),
                    (2, 10, 'Unmonitored Missing', 'unmonitored missing', @ebook, {falseValue}, {falseValue}, @past),
                    (3, 10, 'Existing Ebook', 'existing ebook', @ebook, {falseValue}, {trueValue}, @past),
                    (4, 10, 'Audio Only File', 'audio only file', @ebook, {falseValue}, {trueValue}, @past),
                    (5, 10, 'Future Missing', 'future missing', @ebook, {falseValue}, {trueValue}, @future),
                    (6, 10, 'Paused Audiobook', 'paused audiobook', @audiobook, {trueValue}, {falseValue}, @past),
                    (7, 20, 'Future-Monitored Audiobook', 'future monitored audiobook', @audiobook, {trueValue}, {falseValue}, @past),
                    (8, 30, 'Different Profile Ebook', 'different profile ebook', @ebook, {falseValue}, {trueValue}, @oldRelease);
            ", new
            {
                audiobook = (int)BookMediaType.Audiobook,
                ebook = (int)BookMediaType.Ebook,
                past,
                future,
                oldRelease
            });

            connection.Execute($@"
                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"")
                VALUES
                    (10, 1, {trueValue}),
                    (11, 1, {trueValue}),
                    (20, 2, {trueValue}),
                    (30, 3, {trueValue}),
                    (40, 4, {trueValue}),
                    (50, 5, {trueValue}),
                    (60, 6, {trueValue}),
                    (70, 7, {trueValue}),
                    (80, 8, {trueValue}),
                    (81, 8, {trueValue});
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

        private static void SeedLibraryFilterBooks(IDbConnection connection, DatabaseType databaseType)
        {
            var trueValue = databaseType == DatabaseType.PostgreSQL ? "TRUE" : "1";
            var falseValue = databaseType == DatabaseType.PostgreSQL ? "FALSE" : "0";

            connection.Execute($@"
                INSERT INTO ""Authors"" (""Id"", ""AudiobookMonitored"", ""AudiobookMonitorNewItems"", ""EbookMonitored"", ""EbookMonitorNewItems"", ""AudiobookQualityProfileId"", ""EbookQualityProfileId"")
                VALUES (40, {falseValue}, 1, {falseValue}, 1, 601, 501);
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""ReleaseDate"")
                VALUES
                    (9, 40, 'Unmonitored Audio File', 'unmonitored audio file', {(int)BookMediaType.Audiobook}, {falseValue}, {falseValue}, NULL),
                    (10, 40, 'Unmonitored Ebook File', 'unmonitored ebook file', {(int)BookMediaType.Ebook}, {falseValue}, {falseValue}, NULL),
                    (11, 40, 'Unmonitored Audio Empty', 'unmonitored audio empty', {(int)BookMediaType.Audiobook}, {falseValue}, {falseValue}, NULL),
                    (12, 40, 'Unmonitored Ebook Empty', 'unmonitored ebook empty', {(int)BookMediaType.Ebook}, {falseValue}, {falseValue}, NULL),
                    (13, 40, 'Wrong Side Audio Flag', 'wrong side audio flag', {(int)BookMediaType.Audiobook}, {falseValue}, {trueValue}, NULL),
                    (14, 40, 'Wrong Side Ebook Flag', 'wrong side ebook flag', {(int)BookMediaType.Ebook}, {trueValue}, {falseValue}, NULL);
                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"") VALUES (90, 9, {trueValue}), (100, 10, {trueValue}), (110, 11, {trueValue}), (120, 12, {trueValue});
                INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""MediaType"", ""Size"", ""Quality"")
                VALUES
                    (900, 90, 'ebook', 900, NULL),
                    (1000, 100, 'audiobook', 1000, NULL);
            ");
        }
    }
}
