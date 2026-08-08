using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookLazyNavigationCompatibilityFixture
    {
        [Test]
        public void public_navigation_properties_should_round_trip_through_lazy_slots()
        {
            var author = new Author { Id = 7, Name = "Test Author" };
            var edition = new Edition { Id = 11, Title = "Edition" };
            var file = new BookFile { Id = 13, Path = "/books/test.epub" };
            var link = new SeriesBookLink { Id = 17, BookId = 19, SeriesId = 23 };

            var book = new Book
            {
                Author = author,
                Editions = new List<Edition> { edition },
                BookFiles = new List<BookFile> { file },
                SeriesLinks = new List<SeriesBookLink> { link }
            };

            Assert.That(book.LazyAuthor?.Value, Is.SameAs(author));
            Assert.That(book.LazyEditions?.Value, Has.Count.EqualTo(1));
            Assert.That(book.LazyBookFiles?.Value, Has.Count.EqualTo(1));
            Assert.That(book.LazySeriesLinks?.Value, Has.Count.EqualTo(1));
            Assert.That(book.Author, Is.SameAs(author));
            Assert.That(book.Editions[0], Is.SameAs(edition));
            Assert.That(book.BookFiles[0], Is.SameAs(file));
            Assert.That(book.SeriesLinks[0], Is.SameAs(link));
        }

        [Test]
        public void compatibility_accessors_should_resolve_existing_lazy_values()
        {
            var author = new Author { Id = 29, Name = "Lazy Author" };
            var edition = new Edition { Id = 31, Title = "Lazy Edition" };

            var book = new Book
            {
                LazyAuthor = new LazyLoaded<Author>(author),
                LazyEditions = new LazyLoaded<List<Edition>>(new List<Edition> { edition })
            };

            Assert.That(book.Author, Is.SameAs(author));
            Assert.That(book.AuthorId, Is.EqualTo(29));
            Assert.That(book.Editions[0], Is.SameAs(edition));
        }
    }
}
