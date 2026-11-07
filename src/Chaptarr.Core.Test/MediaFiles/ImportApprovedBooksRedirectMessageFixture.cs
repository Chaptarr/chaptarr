using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksRedirectMessageFixture
    {
        [Test]
        public void redirect_message_should_name_both_books_and_the_reason()
        {
            var message = ImportApprovedBooks.FormatSelectionRedirectMessage(
                new Book { Id = 12 }, new Book { Id = 99 }, "the book already has files and upgrades are not allowed");

            Assert.That(message, Does.Contain("12").And.Contain("99").And.Contain("already has files"));
        }
    }
}
