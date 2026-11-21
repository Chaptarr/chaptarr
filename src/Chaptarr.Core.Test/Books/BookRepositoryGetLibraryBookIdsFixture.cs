using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookRepositoryGetLibraryBookIdsFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private sealed class CapturedCommand
        {
            public string CommandText { get; init; }
            public NpgsqlParameter[] Parameters { get; init; }
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
        public void should_find_monitored_or_owned_file_books_on_sqlite()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"library_book_ids_{Guid.NewGuid():N}.db");
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
                    Seed(connection);
                }

                var readerQueries = 0;
                var repository = new BookRepository(
                    new MainDatabase(new Database("library-book-ids-sqlite", () =>
                    {
                        var connection = OpenSqlite(connectionString);
                        SQLitePCL.raw.sqlite3_profile(connection.Handle, (_, statement, _) =>
                        {
                            if (statement.StartsWith("SELECT b.", StringComparison.Ordinal))
                            {
                                readerQueries++;
                            }
                        }, null);
                        return connection;
                    })),
                    new StubEventAggregator());

                AssertLookup(repository);
                Assert.That(readerQueries, Is.EqualTo(2), "Whole-library and scoped eligibility each require one reader; the empty input requires none.");
                readerQueries = 0;
                AssertSqliteLargeScope(repository, connectionString);
                Assert.That(readerQueries, Is.EqualTo(2), "A scope above the SQLite parameter limit must retain exactly two complete batches.");
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        [Test]
        public void should_find_monitored_or_owned_file_books_on_postgresql()
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL library book lookup");
            }

            var schema = "library_book_ids_" + Guid.NewGuid().ToString("N");
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
                    Seed(connection);
                }

                var repository = new BookRepository(
                    new MainDatabase(new Database("library-book-ids-postgres", () => OpenPostgres(connectionString))),
                    new StubEventAggregator());

                AssertLookup(repository);
            }
            finally
            {
                using var admin = new NpgsqlConnection(adminConnectionString);
                admin.Open();
                admin.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
        }

        private static SqliteConnection OpenSqlite(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static NpgsqlConnection OpenPostgres(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            connection.Execute("SET jit = off; SET work_mem = '4MB'; SET hash_mem_multiplier = 1; SET statement_timeout = '20s';");
            return connection;
        }

        private static IDbConnection OpenObservedPostgres(string connectionString, Action<IDbCommand> observe)
        {
            return CommandObserver.Wrap<IDbConnection>(OpenPostgres(connectionString), observe);
        }

        [TestCase(31700, 69500, 1700)]
        [TestCase(31700, 69500, 400000)]
        [TestCase(300000, 300000, 400000)]
        public void should_keep_library_queries_below_the_correlated_scan_cost_on_postgresql(int bookCount, int editionCount, int fileCount)
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL library query plans");
            }

            var schema = "library_book_plan_" + Guid.NewGuid().ToString("N");
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

                SeedPlanCatalog(connectionString, bookCount, editionCount, fileCount);

                var emittedCommands = new System.Collections.Generic.List<CapturedCommand>();
                var repository = new BookRepository(
                    new MainDatabase(new Database("library-book-plan-postgres", () => OpenObservedPostgres(connectionString, command =>
                    {
                        var captured = Capture(command);
                        var cost = ExplainTotalCost(connectionString, captured, rejectRepeatedMembership: true);
                        Assert.That(cost, Is.LessThan(1000000), "Reject a repeated membership scan before executing its expensive result.");
                        emittedCommands.Add(captured);
                    }))),
                    new StubEventAggregator());

                var elapsed = Stopwatch.StartNew();
                var libraryIds = repository.GetLibraryBookIds();
                TestContext.Progress.WriteLine($"Library IDs: books={bookCount}, editions={editionCount}, files={fileCount}, rows={libraryIds.Count}, elapsed={elapsed.Elapsed.TotalMilliseconds:0.##}ms.");
                var buckets = repository.GetBookBuckets("title", "ASC", includeUnmonitored: true, mediaType: null, downloaded: null, monitored: null, library: true);
                var paged = repository.GetBooksPaged(0, 25, "title", "ASC", includeUnmonitored: true, mediaType: null, downloaded: null, monitored: null, missing: null, wanted: null, library: true);
                var selectedIds = repository.GetBookIds(includeUnmonitored: true, library: true);

                var authorStatisticsRepository = new AuthorStatisticsRepository(new MainDatabase(
                    new Database("author-statistics-plan-postgres", () => OpenObservedPostgres(connectionString, command => emittedCommands.Add(Capture(command))))));
                var audiobookStatistics = authorStatisticsRepository.AuthorStatistics("audiobook");
                var ebookStatistics = authorStatisticsRepository.AuthorStatistics("ebook");
                var allStatistics = authorStatisticsRepository.AuthorStatistics();
                var authorStatistics = authorStatisticsRepository.AuthorStatistics(1);
                var authorAudiobookStatistics = authorStatisticsRepository.AuthorStatistics(1, "audiobook");

                Assert.That(libraryIds, Is.Not.Empty);
                Assert.That(buckets.TotalCount, Is.EqualTo(libraryIds.Count));
                Assert.That(buckets.FooterStatistics.TotalBooks, Is.EqualTo(libraryIds.Count));
                Assert.That(paged.TotalCount, Is.EqualTo(libraryIds.Count));
                Assert.That(selectedIds, Is.EquivalentTo(libraryIds));
                Assert.That(audiobookStatistics, Has.Count.EqualTo(bookCount / 2));
                Assert.That(ebookStatistics, Has.Count.EqualTo(bookCount / 2));
                Assert.That(allStatistics, Has.Count.EqualTo(bookCount));
                Assert.That(authorStatistics.Select(stat => stat.BookId), Is.EquivalentTo(allStatistics.Where(stat => stat.AuthorId == 1).Select(stat => stat.BookId)));
                Assert.That(authorAudiobookStatistics.Select(stat => stat.BookId), Is.EquivalentTo(audiobookStatistics.Where(stat => stat.AuthorId == 1).Select(stat => stat.BookId)));
                Assert.That(emittedCommands, Has.Count.EqualTo(11), "The batched library/statistics operations must retain their existing reader-query count.");

                Assert.That(repository.GetLibraryBookIds(Array.Empty<int>()), Is.Empty);
                Assert.That(emittedCommands, Has.Count.EqualTo(11), "An empty eligibility batch must not query the database.");
                var scope = new[] { 12345, 2, 12345, bookCount + 1 };
                elapsed.Restart();
                Assert.That(repository.GetLibraryBookIds(scope), Is.EquivalentTo(libraryIds.Intersect(scope)));
                TestContext.Progress.WriteLine($"Scoped Library IDs: candidates={scope.Distinct().Count()}, files={fileCount}, elapsed={elapsed.Elapsed.TotalMilliseconds:0.##}ms.");
                elapsed.Restart();
                Assert.That(repository.GetLibraryBookIds(new[] { 12345 }), Is.EquivalentTo(libraryIds.Where(id => id == 12345)));
                TestContext.Progress.WriteLine($"Singleton Library IDs: files={fileCount}, elapsed={elapsed.Elapsed.TotalMilliseconds:0.##}ms.");
                Assert.That(emittedCommands, Has.Count.EqualTo(13), "Each scoped eligibility operation must remain one reader query.");

                var currentCosts = emittedCommands.Select(command => ExplainTotalCost(connectionString, command)).ToArray();
                var correlatedCost = ExplainTotalCost(connectionString, new CapturedCommand
                {
                    CommandText = @"SELECT b.""Id"" FROM ""Books"" b WHERE (
                        b.""AudiobookMonitored"" = TRUE
                        OR b.""EbookMonitored"" = TRUE
                        OR EXISTS (
                            SELECT 1 FROM ""Editions"" e
                            INNER JOIN ""BookFiles"" bf ON bf.""EditionId"" = e.""Id""
                            WHERE e.""BookId"" = b.""Id""))",
                    Parameters = Array.Empty<NpgsqlParameter>()
                });

                var queryNames = new[] { "GetLibraryBookIds", "GetBookBuckets", "bucket footer", "paged count", "paged page", "GetBookIds select-all", "author audiobook stats", "author ebook stats", "all author stats", "single author stats", "single author audiobook stats", "scoped eligibility", "singleton eligibility" };
                var costSummary = string.Join(", ", currentCosts.Select((cost, index) => $"{queryNames[index]}={cost:0.##}"));
                TestContext.Progress.WriteLine($"Repository plan costs: {costSummary}; correlated EXISTS={correlatedCost:0.##}; gate=1000000.");

                Assert.That(currentCosts, Has.All.LessThan(1000000), $"Each captured repository query must stay under the fixed synthetic-fixture plan-cost gate. Costs: {costSummary}; correlated EXISTS={correlatedCost:0.##}.");
                Assert.That(currentCosts.Skip(11), Has.All.LessThan(1000), $"Scoped eligibility must read only candidate-owned evidence on this synthetic catalog. Costs: {costSummary}.");
            }
            finally
            {
                using var admin = new NpgsqlConnection(adminConnectionString);
                admin.Open();
                admin.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
        }

        private static void SeedPlanCatalog(string connectionString, int bookCount, int editionCount, int fileCount)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            connection.Execute($@"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY
                );
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""MediaType"" INTEGER NOT NULL,
                    ""AudiobookMonitored"" BOOLEAN NOT NULL,
                    ""EbookMonitored"" BOOLEAN NOT NULL,
                    ""Title"" TEXT NULL,
                    ""CleanTitle"" TEXT NULL,
                    ""ReleaseDate"" TIMESTAMP NULL,
                    ""Added"" TIMESTAMP NOT NULL
                );
                CREATE TABLE ""Editions"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""BookId"" INTEGER NOT NULL,
                    ""Monitored"" BOOLEAN NOT NULL,
                    ""ReleaseDate"" TIMESTAMP NULL
                );
                CREATE TABLE ""BookFiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""EditionId"" INTEGER NOT NULL,
                    ""MediaType"" TEXT NULL,
                    ""Size"" BIGINT NOT NULL
                );
                CREATE INDEX ""IX_Books_AuthorId"" ON ""Books"" (""AuthorId"");
                CREATE INDEX ""IX_Books_MediaType"" ON ""Books"" (""MediaType"");
                CREATE INDEX ""IX_Editions_BookId"" ON ""Editions"" (""BookId"");
                CREATE INDEX ""IX_Editions_BookId_Monitored_Id"" ON ""Editions"" (""BookId"", ""Monitored"", ""Id"");
                CREATE INDEX ""IX_BookFiles_EditionId"" ON ""BookFiles"" (""EditionId"");

                INSERT INTO ""Authors"" (""Id"")
                SELECT id FROM generate_series(1, 1500) AS id;
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""Title"", ""CleanTitle"", ""ReleaseDate"", ""Added"")
                SELECT id, (id % 1500) + 1, id % 2, id % 10 = 0, id % 10 = 1,
                       'Book ' || id, 'book ' || id, TIMESTAMP '2020-01-01 00:00:00', TIMESTAMP '2020-01-01 00:00:00'
                FROM generate_series(1, {bookCount}) AS id;
                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"", ""ReleaseDate"")
                SELECT id, ((id - 1) % {bookCount}) + 1, id <= {bookCount}, TIMESTAMP '2020-01-01 00:00:00'
                FROM generate_series(1, {editionCount}) AS id;
                INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""MediaType"", ""Size"")
                SELECT file_id, e.""Id"", CASE WHEN b.""MediaType"" = 0 THEN 'audiobook' ELSE 'ebook' END, 1048576
                FROM generate_series(1, {fileCount}) AS file_id
                INNER JOIN ""Editions"" e ON e.""Id"" = ((file_id * 37) % {editionCount}) + 1
                INNER JOIN ""Books"" b ON b.""Id"" = e.""BookId"";
                ANALYZE ""Authors"";
                ANALYZE ""Books"";
                ANALYZE ""Editions"";
                ANALYZE ""BookFiles"";");

            Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Authors"""), Is.EqualTo(1500));
            Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Books"""), Is.EqualTo(bookCount));
            Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Editions"""), Is.EqualTo(editionCount));
            Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""BookFiles"""), Is.EqualTo(fileCount));
            Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM (
                SELECT ""BookId"" FROM ""Editions"" WHERE ""Monitored"" GROUP BY ""BookId"" HAVING COUNT(*) = 1
            ) selected_editions"), Is.EqualTo(bookCount), "Each synthetic book must have exactly one selected edition.");
            Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(DISTINCT ""MediaType"") FROM ""BookFiles"""), Is.EqualTo(2));
        }

        private static CapturedCommand Capture(IDbCommand command)
        {
            return new CapturedCommand
            {
                CommandText = command.CommandText,
                Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(parameter => (NpgsqlParameter)parameter.Clone()).ToArray()
            };
        }

        private static double ExplainTotalCost(string connectionString, CapturedCommand captured, bool rejectRepeatedMembership = false)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            connection.Execute(@"
                SET jit = off;
                SET work_mem = '4MB';
                SET hash_mem_multiplier = 1;
                SET enable_seqscan = on;
                SET enable_nestloop = on;
                SET random_page_cost = 4;
                SET seq_page_cost = 1;
                SET cpu_tuple_cost = 0.01;
                SET cpu_index_tuple_cost = 0.005;
                SET cpu_operator_cost = 0.0025;");

            using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + captured.CommandText, connection);
            foreach (var parameter in captured.Parameters)
            {
                explain.Parameters.Add((NpgsqlParameter)parameter.Clone());
            }

            using var plan = JsonDocument.Parse((string)explain.ExecuteScalar());
            var root = plan.RootElement[0].GetProperty("Plan");
            if (rejectRepeatedMembership)
            {
                Assert.That(HasRepeatedMembership(root), Is.False, "Library membership must use a join or hashed set, without a materialized per-book SubPlan.");
            }

            return root.GetProperty("Total Cost").GetDouble();

            static bool HasRepeatedMembership(JsonElement node)
            {
                return (node.GetProperty("Node Type").GetString() == "Materialize" &&
                        node.TryGetProperty("Subplan Name", out var subplan) && subplan.GetString().StartsWith("SubPlan", StringComparison.Ordinal)) ||
                       (node.TryGetProperty("Plans", out var children) && children.EnumerateArray().Any(HasRepeatedMembership));
            }
        }

        private static void Seed(IDbConnection connection)
        {
            connection.Execute(@"
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""MediaType"" INTEGER NOT NULL,
                    ""AudiobookMonitored"" BOOLEAN NOT NULL,
                    ""EbookMonitored"" BOOLEAN NOT NULL
                );
                CREATE TABLE ""Editions"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""BookId"" INTEGER NOT NULL
                );
                CREATE TABLE ""BookFiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""EditionId"" INTEGER NOT NULL,
                    ""MediaType"" TEXT NULL
                );");

            connection.Execute(@"
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"")
                VALUES (@Id, @AuthorId, @MediaType, @AudiobookMonitored, @EbookMonitored)",
                new[]
                {
                    new { Id = 1, AuthorId = 700, MediaType = 0, AudiobookMonitored = true, EbookMonitored = false },
                    new { Id = 2, AuthorId = 700, MediaType = 1, AudiobookMonitored = false, EbookMonitored = true },
                    new { Id = 3, AuthorId = 700, MediaType = 0, AudiobookMonitored = false, EbookMonitored = false },
                    new { Id = 4, AuthorId = 700, MediaType = 0, AudiobookMonitored = false, EbookMonitored = false },
                    new { Id = 5, AuthorId = 700, MediaType = 0, AudiobookMonitored = false, EbookMonitored = false },
                    new { Id = 6, AuthorId = 700, MediaType = 0, AudiobookMonitored = false, EbookMonitored = true },
                    new { Id = 7, AuthorId = 700, MediaType = 1, AudiobookMonitored = true, EbookMonitored = false }
                });

            connection.Execute(@"INSERT INTO ""Editions"" (""Id"", ""BookId"") VALUES (30, 3), (40, 4)");
            connection.Execute(@"INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""MediaType"") VALUES (300, 30, 'ebook'), (400, 40, NULL), (401, 40, '')");
        }

        private static void AssertLookup(BookRepository repository)
        {
            Assert.That(repository.GetLibraryBookIds(), Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
            Assert.That(repository.GetLibraryBookIds(Array.Empty<int>()), Is.Empty);
            Assert.That(repository.GetLibraryBookIds(new[] { 4, 1, 3, 1, 6, 7, 999 }), Is.EquivalentTo(new[] { 1, 3, 4 }));
        }

        private static void AssertSqliteLargeScope(BookRepository repository, string connectionString)
        {
            using (var connection = OpenSqlite(connectionString))
            {
                connection.Execute(@"
                    INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"")
                    VALUES (100, 700, 0, TRUE, FALSE), (1000, 700, 0, TRUE, FALSE)");
            }

            var ids = Enumerable.Range(100, 900).Concat(new[] { 1000 }).ToArray();
            Assert.That(repository.GetLibraryBookIds(ids), Is.EquivalentTo(new[] { 100, 1000 }));
        }
    }
}
