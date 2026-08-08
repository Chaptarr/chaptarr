using System;
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
    public class BookRepositoryGetBooksForRefreshAuthorScopeFixture
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
        public void should_not_return_books_from_other_authors_when_provider_ids_overlap()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_refresh_author_scope_{Guid.NewGuid():N}.db");
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

                    connection.Execute(@"
                        CREATE TABLE ""Authors"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Name"" TEXT NULL
                        );
                    ");

                    connection.Execute(@"
                        CREATE TABLE ""Books"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""AuthorId"" INTEGER NOT NULL,
                            ""BaseBookId"" TEXT NULL,
                            ""HardcoverBookId"" TEXT NULL,
                            ""GoodreadsBookId"" TEXT NULL,
                            ""GoodreadsWorkId"" TEXT NULL,
                            ""OpenLibraryWorkId"" TEXT NULL,
                            ""GoogleBooksId"" TEXT NULL,
                            ""ASIN"" TEXT NULL,
                            ""AudibleASIN"" TEXT NULL
                        );
                    ");

                    const string sharedProviderId = "gr:good-omens";

                    connection.Execute(@"
                        INSERT INTO ""Authors"" (""Id"", ""Name"")
                        VALUES (10, 'Author One'), (20, 'Author Two');
                    ");

                    connection.Execute(@"
                        INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""GoodreadsWorkId"")
                        VALUES (1, 10, @providerId);
                    ", new { providerId = sharedProviderId });

                    connection.Execute(@"
                        INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""GoodreadsWorkId"")
                        VALUES (2, 20, @providerId);
                    ", new { providerId = sharedProviderId });

                    var database = new Database("main", () =>
                    {
                        var conn = new SqliteConnection(connectionString);
                        conn.Open();
                        return conn;
                    });

                    var mainDatabase = new MainDatabase(database);
                    var sut = new BookRepository(mainDatabase, new StubEventAggregator());

                    var result = sut.GetBooksForRefresh(authorId: 10, providerIds: new[] { sharedProviderId });

                    Assert.Multiple(() =>
                    {
                        Assert.That(result, Is.Not.Null);
                        Assert.That(result.All(b => b.AuthorId == 10), Is.True);
                        Assert.That(result.Any(b => b.Id == 2), Is.False);
                    });
                }
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
