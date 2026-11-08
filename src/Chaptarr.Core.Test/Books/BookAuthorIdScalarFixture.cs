using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookAuthorIdScalarFixture
    {
        private sealed class ThrowingLazyAuthor : LazyLoaded<Author>
        {
            public override void LazyLoad()
            {
                throw new AssertionException("AuthorId should use the scalar backing field without lazy-loading Author");
            }
        }

        private sealed class InspectableBookRepository : BookRepository
        {
            public InspectableBookRepository(IMainDatabase database, IEventAggregator eventAggregator)
                : base(database, eventAggregator)
            {
            }

            public List<string> UpdatePropertyNames()
            {
                return GetUpdateProperties().Select(property => property.Name).ToList();
            }
        }

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
        public void author_id_getter_should_not_lazy_load_author()
        {
            var book = new Book
            {
                LazyAuthor = new ThrowingLazyAuthor(),
                AuthorId = 42
            };

            Assert.That(book.AuthorId, Is.EqualTo(42));
        }

        [Test]
        public void author_and_author_id_setters_should_keep_loaded_author_stub_in_sync()
        {
            var book = new Book();

            book.Author = new Author { Id = 77 };

            Assert.That(book.AuthorId, Is.EqualTo(77));

            book.AuthorId = 88;

            Assert.That(book.Author.Id, Is.EqualTo(88));
        }

        [Test]
        public void normal_book_updates_should_not_include_author_id()
        {
            WithRepository((repository, connectionString) =>
            {
                var updateProperties = repository.UpdatePropertyNames();

                Assert.That(updateProperties, Does.Not.Contain(nameof(Book.AuthorId)));
                Assert.That(updateProperties, Does.Contain(nameof(Book.Title)));
            });
        }

        [Test]
        public void explicit_author_reassignment_should_write_author_id()
        {
            WithRepository((repository, connectionString) =>
            {
                repository.SetAuthorId(new Book { Id = 1, AuthorId = 20 });

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var authorId = connection.ExecuteScalar<int>("SELECT \"AuthorId\" FROM \"Books\" WHERE \"Id\" = 1");

                    Assert.That(authorId, Is.EqualTo(20));
                }
            });
        }

        [Test]
        public void full_row_update_should_preserve_author_id_rehomed_after_fetch()
        {
            WithRepository((repository, connectionString) =>
            {
                var inserted = repository.Insert(new Book
                {
                    AuthorId = 10,
                    Title = "Original",
                    CleanTitle = "original",
                    MediaType = BookMediaType.Audiobook,
                    Added = DateTime.UtcNow
                });

                var staleBook = repository.Get(inserted.Id);
                Assert.That(staleBook.AuthorId, Is.EqualTo(10));

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("UPDATE \"Books\" SET \"AuthorId\" = 20 WHERE \"Id\" = @id", new { id = inserted.Id });
                }

                staleBook.Title = "Updated after stale fetch";
                repository.Update(staleBook);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var row = connection.QuerySingle<(int AuthorId, string Title)>("SELECT \"AuthorId\", \"Title\" FROM \"Books\" WHERE \"Id\" = @id", new { id = inserted.Id });

                    Assert.That(row.AuthorId, Is.EqualTo(20));
                    Assert.That(row.Title, Is.EqualTo("Updated after stale fetch"));
                }
            });
        }

        [Test]
        public void reassign_author_should_update_only_persisted_books_that_need_rehome()
        {
            WithRepository((repository, connectionString) =>
            {
                var source = repository.Insert(new Book
                {
                    AuthorId = 10,
                    Title = "Source",
                    CleanTitle = "source",
                    MediaType = BookMediaType.Audiobook,
                    Added = DateTime.UtcNow
                });
                var alreadyTarget = repository.Insert(new Book
                {
                    AuthorId = 20,
                    Title = "Already Target",
                    CleanTitle = "already target",
                    MediaType = BookMediaType.Audiobook,
                    Added = DateTime.UtcNow
                });
                var duplicateInput = new Book { Id = source.Id, AuthorId = 10, Title = "Duplicate Input" };
                var unsaved = new Book { AuthorId = 10, Title = "Unsaved" };
                var target = new Author { Id = 20, Name = "Target Author" };

                var service = new BookService(
                    repository,
                    editionService: null,
                    eventAggregator: new StubEventAggregator(),
                    authorService: null,
                    mediaFileService: null,
                    rootFolderService: null,
                    seriesBookLinkRepository: null,
                    multiCopySeriesService: null,
                    logger: LogManager.GetCurrentClassLogger());

                service.ReassignAuthor(new List<Book> { source, duplicateInput, alreadyTarget, unsaved, null }, target);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var sourceAuthorId = connection.ExecuteScalar<int>("SELECT \"AuthorId\" FROM \"Books\" WHERE \"Id\" = @id", new { id = source.Id });
                    var alreadyTargetAuthorId = connection.ExecuteScalar<int>("SELECT \"AuthorId\" FROM \"Books\" WHERE \"Id\" = @id", new { id = alreadyTarget.Id });
                    var rowCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Books\"");

                    Assert.That(sourceAuthorId, Is.EqualTo(20));
                    Assert.That(alreadyTargetAuthorId, Is.EqualTo(20));
                    Assert.That(rowCount, Is.EqualTo(3));
                }

                Assert.That(source.AuthorId, Is.EqualTo(20));
                Assert.That(source.Author, Is.SameAs(target));
                Assert.That(duplicateInput.AuthorId, Is.EqualTo(10));
                Assert.That(unsaved.AuthorId, Is.EqualTo(10));
            });
        }

        private static void WithRepository(Action<InspectableBookRepository, string> action)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(false, database =>
            {
                var repository = new InspectableBookRepository(database, new StubEventAggregator());
                repository.Insert(new Book
                {
                    AuthorId = 10, Title = "Existing", CleanTitle = "existing", TitleSlug = "existing",
                    MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                });
                using var connection = database.OpenConnection();
                action(repository, connection.ConnectionString);
            });
        }
    }
}
