using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AddBookServiceFormatSiblingFixture
    {
        [TestCase(BookMediaType.Ebook, true)]
        [TestCase(BookMediaType.Audiobook, false)]
        public void should_only_reuse_sibling_editions_for_the_requested_format(BookMediaType mediaType, bool wantEbook)
        {
            var audiobook = new Edition { Id = 10, BookId = 5, IsEbook = false, Format = "Audiobook" };
            var ebook = new Edition { Id = 11, BookId = 5, IsEbook = true, Format = "EPUB" };

            var selected = AddBookService.FilterSiblingEditionsForMediaType(
                new List<Edition> { audiobook, ebook }, mediaType);

            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0], Is.SameAs(wantEbook ? ebook : audiobook));
        }

        [Test]
        public void should_not_create_sibling_from_missing_or_null_editions()
        {
            Assert.That(AddBookService.FilterSiblingEditionsForMediaType(null, BookMediaType.Ebook), Is.Empty);
            Assert.That(
                AddBookService.FilterSiblingEditionsForMediaType(new List<Edition> { null }, BookMediaType.Ebook),
                Is.Empty);
        }

        [Test]
        public void automatic_sibling_add_should_not_pin_an_arbitrary_edition()
        {
            var author = new Author { Id = 39, Name = "Noelle W. Ihli" };
            var sibling = new Book { Id = 2846, AuthorId = author.Id, Title = "Forget You Saw Her", TitleSlug = "forget-you-saw-her" };
            var book = new Book { Id = 999, MediaType = BookMediaType.Ebook, AnyEditionOk = false };
            var edition = new Edition { Id = 13865, BookId = sibling.Id, IsEbook = true, Format = "EPUB" };

            AddBookService.PrepareAutomaticSiblingFormatAdd(book, sibling, author, new List<Edition> { edition });
            edition.Monitored = true; // AddBookService selects one edition automatically after preparation.

            Assert.That(book.Id, Is.Zero);
            Assert.That(book.AuthorId, Is.EqualTo(author.Id));
            Assert.That(book.AnyEditionOk, Is.True);
            Assert.That(EditionPinPolicy.CanAutomationSelectEdition(book, new[] { edition }), Is.True);
            Assert.That(edition.Id, Is.Zero);
            Assert.That(edition.BookId, Is.Zero);
            Assert.That(edition.ManualAdd, Is.False);
        }
    }
}
