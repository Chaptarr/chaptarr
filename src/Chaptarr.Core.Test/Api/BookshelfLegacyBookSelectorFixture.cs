using System.Collections.Generic;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookshelfLegacyBookSelectorFixture
    {
        [TestCase(BookMediaType.Ebook)]
        [TestCase(BookMediaType.Audiobook)]
        public void should_choose_the_monitored_source_edition_over_a_synthetic_format_copy(BookMediaType originalFormat)
        {
            var books = new List<Book>
            {
                new Book { Id = 1, MediaType = BookMediaType.Audiobook },
                new Book { Id = 2, MediaType = BookMediaType.Ebook }
            };
            var originalBookId = originalFormat == BookMediaType.Audiobook ? 1 : 2;
            var editions = new List<Edition>
            {
                new Edition { BookId = 1, Monitored = true, ForeignEditionId = originalBookId == 1 ? "hc:edition:1" : null },
                new Edition { BookId = 2, Monitored = true, ForeignEditionId = originalBookId == 2 ? "hc:edition:1" : null }
            };

            Assert.That(new BookshelfLegacyBookSelector(editions).Select(books), Is.EqualTo(originalBookId));
        }
    }
}
