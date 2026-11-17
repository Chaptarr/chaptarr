using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerNextPreviousBookBatchingFixture
    {
        [TestCase(1)]
        [TestCase(50)]
        [TestCase(500)]
        public void preload_should_issue_one_edition_and_one_book_file_lookup_regardless_of_book_count(int authorCount)
        {
            var authors = Enumerable.Range(1, authorCount).Select(id => new Author { Id = id }).ToList();
            var books = authors.SelectMany(author => new[]
            {
                UnloadedBook(author.Id * 1000, author.Id),
                UnloadedBook((author.Id * 1000) + 1, author.Id)
            }).ToList();

            var editionCalls = new List<List<int>>();
            var bookFileCalls = new List<List<int>>();

            AuthorController.PreloadAuthorIndexBooks(
                authors,
                books,
                bookIds =>
                {
                    editionCalls.Add(bookIds);
                    return bookIds.Select(bookId => new Edition { Id = bookId + 500000, BookId = bookId }).ToList();
                },
                bookIds =>
                {
                    bookFileCalls.Add(bookIds);
                    return bookIds.Select(bookId => new BookFile { Id = bookId + 900000, EditionId = bookId + 500000 }).ToList();
                });

            Assert.Multiple(() =>
            {
                Assert.That(editionCalls, Has.Count.EqualTo(1),
                    "The edition lookup must be batched into a single call for the whole author list");
                Assert.That(bookFileCalls, Has.Count.EqualTo(1),
                    "The book file lookup must be batched into a single call for the whole author list");
                Assert.That(editionCalls[0], Has.Count.EqualTo(books.Count));
                Assert.That(bookFileCalls[0], Has.Count.EqualTo(books.Count));
            });
        }

        [Test]
        public void preload_should_hydrate_author_editions_and_files_without_lazy_loading()
        {
            var author = new Author { Id = 7, Name = "Preloaded" };
            var book = UnloadedBook(42, author.Id);

            AuthorController.PreloadAuthorIndexBooks(
                new[] { author },
                new[] { book },
                bookIds => bookIds.Select(bookId => new Edition { Id = 900, BookId = bookId, Title = "Edition" }).ToList(),
                bookIds => new List<BookFile> { new BookFile { Id = 11, EditionId = 900 } });

            Assert.Multiple(() =>
            {
                Assert.That(book.LazyAuthor.IsLoaded, Is.True);
                Assert.That(book.Author, Is.SameAs(author));
                Assert.That(book.LazyEditions.IsLoaded, Is.True);
                Assert.That(book.Editions.Select(x => x.Id), Is.EquivalentTo(new[] { 900 }));
                Assert.That(book.LazyBookFiles.IsLoaded, Is.True);
                Assert.That(book.BookFiles.Select(x => x.Id), Is.EquivalentTo(new[] { 11 }));
                Assert.That(book.HasFiles, Is.True);
            });
        }

        [Test]
        public void preload_should_not_query_when_there_are_no_books()
        {
            var editionCalls = 0;
            var bookFileCalls = 0;

            AuthorController.PreloadAuthorIndexBooks(
                new[] { new Author { Id = 1 } },
                new List<Book>(),
                _ =>
                {
                    editionCalls++;
                    return new List<Edition>();
                },
                _ =>
                {
                    bookFileCalls++;
                    return new List<BookFile>();
                });

            Assert.Multiple(() =>
            {
                Assert.That(editionCalls, Is.Zero);
                Assert.That(bookFileCalls, Is.Zero);
            });
        }

        [Test]
        public void preload_should_deduplicate_books_shared_between_next_and_last()
        {
            var author = new Author { Id = 3 };
            var book = UnloadedBook(15, author.Id);

            var requestedBookIds = new List<int>();

            AuthorController.PreloadAuthorIndexBooks(
                new[] { author },
                new[] { book, book },
                bookIds =>
                {
                    requestedBookIds.AddRange(bookIds);
                    return new List<Edition>();
                },
                _ => new List<BookFile>());

            Assert.That(requestedBookIds, Is.EquivalentTo(new[] { 15 }));
        }

        private static Book UnloadedBook(int bookId, int authorId)
        {
            var book = new Book
            {
                Id = bookId,
                Title = $"Book {bookId}"
            };

            book.LazyAuthor = new LazyLoaded<Author>();
            book.LazyEditions = new LazyLoaded<List<Edition>>();
            book.LazyBookFiles = new LazyLoaded<List<BookFile>>();
            book.AuthorId = authorId;

            return book;
        }
    }
}
