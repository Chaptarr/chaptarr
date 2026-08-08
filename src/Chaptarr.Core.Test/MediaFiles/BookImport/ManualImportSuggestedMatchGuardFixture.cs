using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportSuggestedMatchGuardFixture
    {
        [Test]
        public void should_accept_local_match_for_suggested_author_and_work()
        {
            var author = new Author { Id = 7, Name = "Suggested Author" };
            var book = new Book
            {
                Id = 10,
                AuthorId = author.Id,
                Title = "Expected Book",
                HardcoverBookId = "hc:463791"
            };
            var match = new FileMatch
            {
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookId = book.Id,
                BookTitle = book.Title,
                EditionId = 100
            };

            var accepted = ManualImportService.SuggestedLocalMatchMatchesSuggestion(match, author, book, "hc:463791", out var rejectionReason);

            Assert.That(accepted, Is.True);
            Assert.That(rejectionReason, Is.Null);
        }

        [Test]
        public void should_reject_unscoped_match_for_different_author()
        {
            var author = new Author { Id = 7, Name = "Suggested Author" };
            var book = new Book
            {
                Id = 10,
                AuthorId = 99,
                Title = "Other Author Book",
                HardcoverBookId = "hc:463791"
            };
            var match = new FileMatch
            {
                AuthorId = 99,
                AuthorName = "Other Author",
                BookId = book.Id,
                BookTitle = book.Title,
                EditionId = 100
            };

            var accepted = ManualImportService.SuggestedLocalMatchMatchesSuggestion(match, author, book, "hc:463791", out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("does not belong to suggested author"));
        }

        [Test]
        public void should_reject_same_author_match_for_different_suggested_work()
        {
            var author = new Author { Id = 7, Name = "Suggested Author" };
            var book = new Book
            {
                Id = 10,
                AuthorId = author.Id,
                Title = "Wrong Work",
                HardcoverBookId = "hc:111111"
            };
            var match = new FileMatch
            {
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookId = book.Id,
                BookTitle = book.Title,
                EditionId = 100
            };

            var accepted = ManualImportService.SuggestedLocalMatchMatchesSuggestion(match, author, book, "hc:463791", out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("does not match suggested metadata work"));
        }

        [Test]
        public void should_accept_same_author_match_when_suggested_work_is_absent()
        {
            var author = new Author { Id = 7, Name = "Suggested Author" };
            var book = new Book
            {
                Id = 10,
                AuthorId = author.Id,
                Title = "Expected Book"
            };
            var match = new FileMatch
            {
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookId = book.Id,
                BookTitle = book.Title,
                EditionId = 100
            };

            var accepted = ManualImportService.SuggestedLocalMatchMatchesSuggestion(match, author, book, null, out var rejectionReason);

            Assert.That(accepted, Is.True);
            Assert.That(rejectionReason, Is.Null);
        }

        [Test]
        public void should_reject_manual_selection_when_book_belongs_to_different_author()
        {
            var author = new Author { Id = 7, Name = "Selected Author" };
            var book = new Book
            {
                Id = 10,
                AuthorId = 99,
                Title = "Wrong Author Book"
            };

            var accepted = ManualImportService.ManualSelectionMatchesAuthorBook(author, book, out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("does not belong to selected author"));
        }

        [Test]
        public void should_treat_edition_from_another_book_as_not_belonging_to_selected_book()
        {
            var book = new Book { Id = 10, AuthorId = 7, Title = "Selected Book" };
            var edition = new Edition { Id = 44, BookId = 11, Title = "Other Book Edition" };

            Assert.That(ManualImportService.EditionBelongsToBook(edition, book), Is.False);
        }

        [Test]
        public void should_reject_manual_selection_without_selected_edition()
        {
            var book = new Book { Id = 10, AuthorId = 7, Title = "Selected Book" };

            var accepted = ManualImportService.ManualEditionSelectionMatchesBook(null, book, 0, out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("Edition must be selected"));
        }

        [Test]
        public void should_reject_manual_selection_when_selected_edition_belongs_to_different_book()
        {
            var book = new Book { Id = 10, AuthorId = 7, Title = "Selected Book" };
            var edition = new Edition { Id = 44, BookId = 11, Title = "Other Book Edition" };

            var accepted = ManualImportService.ManualEditionSelectionMatchesBook(edition, book, edition.Id, out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("does not belong to selected book"));
        }

        [Test]
        public void should_reject_suggested_match_when_local_match_has_no_edition()
        {
            var book = new Book { Id = 10, AuthorId = 7, Title = "Matched Book" };

            var accepted = ManualImportService.SuggestedLocalMatchEditionMatchesBook(null, book, 0, out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("did not resolve a local edition"));
        }

        [Test]
        public void should_reject_suggested_match_when_local_match_edition_belongs_to_different_book()
        {
            var book = new Book { Id = 10, AuthorId = 7, Title = "Matched Book" };
            var edition = new Edition { Id = 44, BookId = 11, Title = "Other Book Edition" };

            var accepted = ManualImportService.SuggestedLocalMatchEditionMatchesBook(edition, book, edition.Id, out var rejectionReason);

            Assert.That(accepted, Is.False);
            Assert.That(rejectionReason, Does.Contain("does not belong to matched book"));
        }
    }
}
