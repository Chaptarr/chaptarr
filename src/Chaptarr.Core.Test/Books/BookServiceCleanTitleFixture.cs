using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceCleanTitleFixture
    {
        [Test]
        public void should_derive_clean_title_from_title_when_missing()
        {
            var book = new Book { Title = "Pending Import", CleanTitle = null };

            BookService.EnsureCleanTitle(book);

            Assert.That(book.CleanTitle, Is.Not.Null.And.Not.Empty);
            Assert.That(book.CleanTitle, Does.Contain("pending"));
        }

        [Test]
        public void should_default_to_empty_clean_title_when_title_is_also_missing()
        {
            var book = new Book { Title = null, CleanTitle = null };

            BookService.EnsureCleanTitle(book);

            Assert.That(book.CleanTitle, Is.EqualTo(string.Empty));
        }

        [Test]
        public void should_keep_an_existing_clean_title()
        {
            var book = new Book { Title = "Some Title", CleanTitle = "custom" };

            BookService.EnsureCleanTitle(book);

            Assert.That(book.CleanTitle, Is.EqualTo("custom"));
        }
    }
}
