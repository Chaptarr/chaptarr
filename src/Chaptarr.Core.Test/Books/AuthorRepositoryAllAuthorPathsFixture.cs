using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorRepositoryAllAuthorPathsFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [Test]
        public void all_author_paths_should_project_all_three_stored_paths_on_sqlite()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_paths_{Guid.NewGuid():N}.db");
            try
            {
                using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
                {
                    connection.Open();
                    Seed(connection);
                }

                var repository = new AuthorRepository(
                    new MainDatabase(new Database("author-paths-sqlite", () => OpenSqlite(databasePath))),
                    new StubEventAggregator(),
                    LogManager.GetCurrentClassLogger());

                AssertProjection(repository.AllAuthorPaths());
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
        public void all_author_paths_should_project_all_three_stored_paths_on_postgresql()
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL author path projection");
            }

            var schema = "author_paths_" + Guid.NewGuid().ToString("N");
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

                var repository = new AuthorRepository(
                    new MainDatabase(new Database("author-paths-postgres", () => OpenPostgres(connectionString))),
                    new StubEventAggregator(),
                    LogManager.GetCurrentClassLogger());

                AssertProjection(repository.AllAuthorPaths());
            }
            finally
            {
                using var admin = new NpgsqlConnection(adminConnectionString);
                admin.Open();
                admin.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
        }

        private static SqliteConnection OpenSqlite(string databasePath)
        {
            var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            return connection;
        }

        private static NpgsqlConnection OpenPostgres(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static void Seed(System.Data.IDbConnection connection)
        {
            connection.Execute(@"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Name"" TEXT NULL,
                    ""Path"" TEXT NULL,
                    ""AudiobookPath"" TEXT NULL,
                    ""EbookPath"" TEXT NULL
                );
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""Path"", ""AudiobookPath"", ""EbookPath"") VALUES
                    (1189, 'Pascale Lacelle', 'C:\legacy\Pascale Lacelle', 'C:\audio\Pascale Lacelle', 'C:\ebooks\Pascale Lacelle'),
                    (1190, 'Audio Only', NULL, 'C:\audio\Only', NULL);");
        }

        private static void AssertProjection(List<Author> authors)
        {
            var pascale = authors.Single(author => author.Id == 1189);
            Assert.That(pascale.Name, Is.EqualTo("Pascale Lacelle"));
            Assert.That(pascale.Path, Is.EqualTo(@"C:\legacy\Pascale Lacelle"));
            Assert.That(pascale.AudiobookPath, Is.EqualTo(@"C:\audio\Pascale Lacelle"));
            Assert.That(pascale.EbookPath, Is.EqualTo(@"C:\ebooks\Pascale Lacelle"));

            var audioOnly = authors.Single(author => author.Id == 1190);
            Assert.That(audioOnly.Name, Is.EqualTo("Audio Only"));
            Assert.That(audioOnly.Path, Is.Null);
            Assert.That(audioOnly.AudiobookPath, Is.EqualTo(@"C:\audio\Only"));
            Assert.That(audioOnly.EbookPath, Is.Null);
        }
    }
}
