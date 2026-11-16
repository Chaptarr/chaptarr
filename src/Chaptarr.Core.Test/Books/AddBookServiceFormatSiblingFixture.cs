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
    }
}
