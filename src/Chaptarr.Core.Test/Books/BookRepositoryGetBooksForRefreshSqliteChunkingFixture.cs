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
    public class BookRepositoryGetBooksForRefreshSqliteChunkingFixture
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

        [TestCase(200)]
        [TestCase(901)]
        public void should_handle_large_provider_id_sets_without_sqlite_variable_limit(int providerIdCount)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_provider_id_chunking_{providerIdCount}_{Guid.NewGuid():N}.db");
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
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new BookRepository(mainDatabase, new StubEventAggregator());
                var providerIds = Enumerable.Range(1, providerIdCount).Select(i => $"gr:{i}").ToList();

                Assert.DoesNotThrow(() => sut.GetBooksForRefresh(authorId: 1, providerIds: providerIds));
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
