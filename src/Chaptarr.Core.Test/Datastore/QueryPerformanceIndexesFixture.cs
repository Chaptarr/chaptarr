using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    // Runs the real migrations into a fresh database, then checks the plans SQLite picks for
    // the queries migration 108 was written for. With no ANALYZE statistics, SQLite plans from
    // the schema alone, so an empty database plans these queries the same way a full one does.
    [TestFixture]
    public class QueryPerformanceIndexesFixture
    {
        private string _databasePath;
        private string _connectionString;

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, NzbDrone.Common.Messaging.IEvent
            {
            }
        }

        private sealed class CapturedStatements
        {
            public readonly List<string> Sql = new List<string>();
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"query_performance_{Guid.NewGuid():N}.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            new MigrationController(LogManager.GetLogger(nameof(QueryPerformanceIndexesFixture)), null)
                .Migrate(_connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            using var connection = Open();
            connection.Execute(@"
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"")
                VALUES (1, 'Author One', 'authorone', '/library/Author One', 1, '2026-01-01 00:00:00Z'),
                       (2, 'Author Two', 'authortwo', '/library/Author Two', 1, '2026-01-01 00:00:00Z');

                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""AnyEditionOk"", ""Added"", ""MediaType"", ""ReleaseDate"")
                VALUES (10, 1, 'Book Ten', 'bookten', 1, '2026-01-01 00:00:00Z', 0, '2001-01-01 00:00:00Z'),
                       (20, 2, 'Book Twenty', 'booktwenty', 1, '2026-01-01 00:00:00Z', 0, '2002-01-01 00:00:00Z');

                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""Monitored"")
                VALUES (100, 10, 'Book Ten', 1),
                       (101, 10, 'Book Ten (Other)', 0),
                       (200, 20, 'Book Twenty', 1);

                INSERT INTO ""BookFiles"" (""Id"", ""Path"", ""Size"", ""Modified"", ""DateAdded"", ""Quality"", ""MediaInfo"", ""EditionId"", ""CalibreId"", ""Part"")
                VALUES (1, '/library/Author One/Book Ten/part1.mp3', 10, '2026-01-01 00:00:00Z', '2026-01-01 00:00:00Z', '{""quality"": 12}', '{}', 100, 0, 1),
                       (2, '/library/Author One/Book Ten Other/book.m4b', 20, '2026-01-01 00:00:00Z', '2026-01-01 00:00:00Z', '{""quality"": 12}', '{}', 101, 0, 1),
                       (3, '/library/Author Two/Book Twenty/book.m4b', 30, '2026-01-01 00:00:00Z', '2026-01-01 00:00:00Z', '{""quality"": 12}', '{}', 200, 0, 1),
                       (4, '/library/Author One/unmapped.m4b', 40, '2026-01-01 00:00:00Z', '2026-01-01 00:00:00Z', '{""quality"": 12}', '{}', 0, 0, 1);");
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            SqliteConnection.ClearAllPools();

            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        [Test]
        public void should_replace_the_single_column_indexes_with_the_wider_ones()
        {
            using var connection = Open();

            Assert.Multiple(() =>
            {
                Assert.That(IndexColumns(connection, "IX_BookFiles_EditionId_Size"), Is.EqualTo(new[] { "EditionId", "Size" }));
                Assert.That(IndexColumns(connection, "IX_Books_AuthorId_ReleaseDate_Id"), Is.EqualTo(new[] { "AuthorId", "ReleaseDate", "Id" }));
                Assert.That(IndexColumns(connection, "IX_Editions_Monitored_BookId"), Is.EqualTo(new[] { "Monitored", "BookId" }));

                // The title sort index's middle key is an expression, which pragma_index_info reports without a name.
                Assert.That(IndexColumns(connection, "IX_Books_MediaType_TitleSort"), Is.EqualTo(new[] { "MediaType", null, "Id", "CleanTitle", "Title" }));

                Assert.That(IndexColumns(connection, "IX_BookFiles_EditionId"), Is.Empty, "covered by IX_BookFiles_EditionId_Size");
                Assert.That(IndexColumns(connection, "IX_Editions_Monitored"), Is.Empty, "covered by IX_Editions_Monitored_BookId");
            });
        }

        [Test]
        public void should_return_only_the_mapped_files_of_the_requested_author_or_book()
        {
            var repository = new MediaFileRepository(new MainDatabase(CreateDatabase(new CapturedStatements())), new StubEventAggregator());

            var authorFiles = repository.GetFilesByAuthor(1);
            var bookFiles = repository.GetFilesByBook(10);

            Assert.Multiple(() =>
            {
                Assert.That(authorFiles.Select(f => f.Id), Is.EquivalentTo(new[] { 1, 2 }), "the unmapped file (EditionId 0) and author 2's file are not included");
                Assert.That(bookFiles.Select(f => f.Id), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(repository.GetFilesByBook(20).Select(f => f.Id), Is.EqualTo(new[] { 3 }));
                Assert.That(authorFiles.All(f => f.LazyEdition.IsLoaded && f.LazyAuthor.IsLoaded), Is.True, "the edition and author come back with each file, not as lazy loads");
                Assert.That(authorFiles.Select(f => f.Edition.Book.Id), Is.All.EqualTo(10));
                Assert.That(authorFiles.Select(f => f.Author.Id), Is.All.EqualTo(1));
            });
        }

        [Test]
        public void should_find_an_author_or_book_files_without_reading_every_file_row()
        {
            var captured = new CapturedStatements();
            var repository = new MediaFileRepository(new MainDatabase(CreateDatabase(captured)), new StubEventAggregator());

            repository.GetFilesByAuthor(1);
            var byAuthorPlan = PlanFor(captured.Sql.Last(sql => sql.Contains("FROM \"BookFiles\"")));

            repository.GetFilesByBook(10);
            var byBookPlan = PlanFor(captured.Sql.Last(sql => sql.Contains("FROM \"BookFiles\"")));

            Assert.Multiple(() =>
            {
                Assert.That(byAuthorPlan, Has.None.StartsWith("SCAN BookFiles"), string.Join(" | ", byAuthorPlan));
                Assert.That(byAuthorPlan, Has.Some.Contains("IX_BookFiles_EditionId_Size"), string.Join(" | ", byAuthorPlan));
                Assert.That(byBookPlan, Has.None.StartsWith("SCAN BookFiles"), string.Join(" | ", byBookPlan));
                Assert.That(byBookPlan, Has.Some.Contains("IX_BookFiles_EditionId_Size"), string.Join(" | ", byBookPlan));
            });
        }

        [Test]
        public void should_read_a_title_sorted_book_page_from_the_index_instead_of_sorting()
        {
            var captured = new CapturedStatements();
            var repository = new BookRepository(new MainDatabase(CreateDatabase(captured)), new StubEventAggregator());

            repository.GetBooksPaged(0, 200, "title", "ASC", true, "audiobook", null, null);
            var plan = PlanFor(captured.Sql.Last(sql => sql.Contains("LIMIT 200")));

            Assert.Multiple(() =>
            {
                Assert.That(plan, Has.Some.Contains("IX_Books_MediaType_TitleSort"), string.Join(" | ", plan));
                Assert.That(plan, Has.None.Contains("TEMP B-TREE"), string.Join(" | ", plan));
            });
        }

        [Test]
        public void should_count_the_title_jump_bar_from_the_index_alone()
        {
            var captured = new CapturedStatements();
            var repository = new BookRepository(new MainDatabase(CreateDatabase(captured)), new StubEventAggregator());

            repository.GetBookBuckets("title", "ASC", true, "audiobook", null, null);
            var plan = PlanFor(captured.Sql.Last(sql => sql.Contains("Bucket")));

            Assert.That(plan, Has.Some.Contains("COVERING INDEX IX_Books_MediaType_TitleSort"), string.Join(" | ", plan));
        }

        [Test]
        public void should_rank_next_and_last_books_from_the_release_date_index()
        {
            var captured = new CapturedStatements();
            var repository = new BookRepository(new MainDatabase(CreateDatabase(captured)), new StubEventAggregator());

            repository.GetNextBooks(new[] { 1, 2 });
            var nextPlan = PlanFor(captured.Sql.Last(sql => sql.Contains("ROW_NUMBER()")));

            repository.GetLastBooks(new[] { 1, 2 });
            var lastPlan = PlanFor(captured.Sql.Last(sql => sql.Contains("ROW_NUMBER()")));

            Assert.Multiple(() =>
            {
                Assert.That(nextPlan, Has.Some.Contains("COVERING INDEX IX_Books_AuthorId_ReleaseDate_Id"), string.Join(" | ", nextPlan));
                Assert.That(lastPlan, Has.Some.Contains("COVERING INDEX IX_Books_AuthorId_ReleaseDate_Id"), string.Join(" | ", lastPlan));
            });
        }

        [Test]
        public void should_total_file_sizes_for_author_progress_from_the_index_alone()
        {
            var plan = PlanFor(AuthorStatisticsRepository.BuildBaseSql(DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(plan, Has.Some.Contains("COVERING INDEX IX_BookFiles_EditionId_Size"), string.Join(" | ", plan));
                Assert.That(plan, Has.None.EqualTo("SCAN BookFiles"), string.Join(" | ", plan));
            });
        }

        [Test]
        public void should_find_monitored_editions_for_wanted_from_the_index_alone()
        {
            var plan = PlanFor(@"
                SELECT ""Books"".""Id""
                FROM ""Books""
                JOIN ""Editions"" ON (""Books"".""Id"" = ""Editions"".""BookId"")
                LEFT JOIN ""BookFiles"" ON (""Editions"".""Id"" = ""BookFiles"".""EditionId"")
                WHERE (""BookFiles"".""Id"" IS NULL) AND (""Editions"".""Monitored"" = 1)");

            Assert.That(plan, Has.Some.Contains("COVERING INDEX IX_Editions_Monitored_BookId"), string.Join(" | ", plan));
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private Database CreateDatabase(CapturedStatements captured)
        {
            return new Database("main", () =>
            {
                var connection = Open();
                SQLitePCL.raw.sqlite3_profile(connection.Handle, (_, statement, _) =>
                {
                    lock (captured.Sql)
                    {
                        captured.Sql.Add(statement);
                    }
                }, null);

                return connection;
            });
        }

        private string[] PlanFor(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;

            // Unbound parameters plan as NULL, which is enough to see which indexes are used.
            var plan = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                plan.Add(reader.GetString(3));
            }

            return plan.ToArray();
        }

        private static string[] IndexColumns(SqliteConnection connection, string indexName)
        {
            return connection.Query<string>(
                "SELECT name FROM pragma_index_info(@indexName) ORDER BY seqno",
                new { indexName }).ToArray();
        }
    }
}
