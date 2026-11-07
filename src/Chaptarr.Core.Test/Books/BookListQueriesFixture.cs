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
    public class BookListQueriesFixture
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

        [TestCase(BookMediaType.Audiobook, new[] { 1 })]
        [TestCase(BookMediaType.Ebook, new[] { 2 })]
        public void book_list_editions_should_be_the_monitored_editions_of_the_requested_media_type(BookMediaType mediaType, int[] expectedEditionIds)
        {
            WithDatabase(database =>
            {
                var repository = new EditionRepository(database, new StubEventAggregator());

                var editions = repository.GetMonitoredEditionsForBookList(mediaType);

                Assert.That(editions.Select(e => e.Id).OrderBy(id => id), Is.EqualTo(expectedEditionIds));
            });
        }

        [Test]
        public void book_list_editions_without_media_type_should_include_both_media_types()
        {
            WithDatabase(database =>
            {
                var repository = new EditionRepository(database, new StubEventAggregator());

                var editions = repository.GetMonitoredEditionsForBookList(null);

                Assert.That(editions.Select(e => e.Id).OrderBy(id => id), Is.EqualTo(new[] { 1, 2 }));
            });
        }

        [Test]
        public void book_list_editions_should_not_load_chapters()
        {
            WithDatabase(database =>
            {
                var repository = new EditionRepository(database, new StubEventAggregator());

                var edition = repository.GetMonitoredEditionsForBookList(BookMediaType.Audiobook).Single();

                Assert.That(edition.Title, Is.EqualTo("Audiobook Edition"));
                Assert.That(edition.BookId, Is.EqualTo(1));
                Assert.That(edition.Monitored, Is.True);
                Assert.That(edition.Chapters, Is.Null.Or.Empty);
            });
        }

        [TestCase(BookMediaType.Audiobook, new[] { 1 })]
        [TestCase(BookMediaType.Ebook, new[] { 2 })]
        public void books_by_media_type_should_return_only_that_media_type(BookMediaType mediaType, int[] expectedBookIds)
        {
            WithDatabase(database =>
            {
                var repository = new BookRepository(database, new StubEventAggregator());

                var books = repository.GetBooksByMediaType(mediaType);

                Assert.That(books.Select(b => b.Id), Is.EqualTo(expectedBookIds));
            });
        }

        private static void WithDatabase(Action<MainDatabase> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_list_queries_{Guid.NewGuid():N}.db");
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
                    CreateSchema<Author>(connection);
                    CreateSchema<Book>(connection);
                    CreateSchema<Edition>(connection);

                    connection.Execute("INSERT INTO \"Authors\" (\"Id\") VALUES (1);");
                    connection.Execute("INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"MediaType\") VALUES " +
                                       "(1, 1, 'Foundation', 0), " +
                                       "(2, 1, 'Foundation', 1);");
                    connection.Execute("INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Title\", \"Monitored\", \"Chapters\") VALUES " +
                                       "(1, 1, 'Audiobook Edition', 1, '[{\"title\":\"Chapter 1\"}]'), " +
                                       "(2, 2, 'Ebook Edition', 1, '[{\"title\":\"Chapter 1\"}]'), " +
                                       "(3, 2, 'Unmonitored Ebook Edition', 0, NULL);");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                action(new MainDatabase(database));
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

        private static void CreateSchema<T>(SqliteConnection connection)
            where T : ModelBase
        {
            var excluded = TableMapping.Mapper.ExcludeProperties(typeof(T))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tableName = TableMapping.Mapper.TableNameMapping(typeof(T));
            var columns = typeof(T)
                .GetProperties()
                .Where(property => property.Name == nameof(ModelBase.Id) || (property.IsMappableProperty() && !excluded.Contains(property.Name)))
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(property => property.Name == nameof(ModelBase.Id)
                    ? "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT"
                    : $"\"{property.Name}\" {GetSqliteColumnType(property)} NULL")
                .ToList();

            connection.Execute($"CREATE TABLE \"{tableName}\" ({string.Join(", ", columns)});");
        }

        private static string GetSqliteColumnType(System.Reflection.PropertyInfo property)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(bool))
            {
                return "INTEGER";
            }

            return "TEXT";
        }
    }
}
