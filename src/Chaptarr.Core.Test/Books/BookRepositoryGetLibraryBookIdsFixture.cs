using System;
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

                var repository = new BookRepository(
                    new MainDatabase(new Database("library-book-ids-sqlite", () => OpenSqlite(connectionString))),
                    new StubEventAggregator());

                AssertLookup(repository);
                AssertSqliteLargeScope(repository, connectionString);
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
            return connection;
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
                    new { Id = 4, AuthorId = 700, MediaType = 0, AudiobookMonitored = false, EbookMonitored = false }
                });

            connection.Execute(@"INSERT INTO ""Editions"" (""Id"", ""BookId"") VALUES (30, 3)");
            connection.Execute(@"INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""MediaType"") VALUES (300, 30, 'ebook')");
        }

        private static void AssertLookup(BookRepository repository)
        {
            Assert.That(repository.GetLibraryBookIds(), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(repository.GetLibraryBookIds(Array.Empty<int>()), Is.Empty);
            Assert.That(repository.GetLibraryBookIds(new[] { 4, 1, 3, 1, 999 }), Is.EquivalentTo(new[] { 1, 3 }));
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
