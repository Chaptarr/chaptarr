using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServiceUserSelectedEditionFixture
    {
        [Test]
        public void user_selected_edition_should_resolve_only_inside_the_author_blob_work_alias_and_media_type()
        {
            var expectedEdition = new Edition
            {
                Title = "BOSCH: Schwarzes Echo",
                ForeignEditionId = "gr:229391768",
                ReadingFormatId = 2
            };
            var expectedBook = new Book
            {
                Title = "The Black Echo",
                HardcoverBookId = "hc:223021",
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hc:1987747" },
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { expectedEdition }
            };
            var ebookPocket = new Book
            {
                Title = "The Black Echo",
                HardcoverBookId = "hc:223021",
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hc:1987747" },
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new() { Title = "The Black Echo", ForeignEditionId = "gr:229391768", ReadingFormatId = 3 }
                }
            };
            var author = new Author
            {
                Name = "Michael Connelly",
                Books = new List<Book> { expectedBook, ebookPocket }
            };

            var result = AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                author,
                "hc:1987747",
                "gr:229391768",
                BookMediaType.Audiobook);

            Assert.Multiple(() =>
            {
                Assert.That(result.Book, Is.SameAs(expectedBook));
                Assert.That(result.Edition, Is.SameAs(expectedEdition));
            });
        }

        [Test]
        public void user_selected_edition_should_fail_closed_when_the_author_blob_is_ambiguous()
        {
            var author = new Author
            {
                Name = "Ambiguous Author",
                Books = new List<Book>
                {
                    CreateAudiobookPocket("First Pocket"),
                    CreateAudiobookPocket("Second Pocket")
                }
            };

            var error = Assert.Throws<InvalidOperationException>(() =>
                AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                    author,
                    "hc:1987747",
                    "gr:229391768",
                    BookMediaType.Audiobook));

            Assert.That(error.Message, Does.Contain("2 rows"));
        }

        [Test]
        public void user_selected_edition_should_not_be_resurrected_when_absent_from_the_author_blob_work()
        {
            var book = CreateAudiobookPocket("The Black Echo");
            book.Editions = new List<Edition>
            {
                new() { Title = "Different Edition", ForeignEditionId = "gr:111" }
            };
            var author = new Author { Books = new List<Book> { book } };

            var error = Assert.Throws<InvalidOperationException>(() =>
                AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                    author,
                    "hc:1987747",
                    "gr:229391768",
                    BookMediaType.Audiobook));

            Assert.That(error.Message, Does.Contain("does not contain edition"));
        }

        private static Book CreateAudiobookPocket(string title)
        {
            return new Book
            {
                Title = title,
                HardcoverBookId = "hc:223021",
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hc:1987747" },
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { Title = "BOSCH: Schwarzes Echo", ForeignEditionId = "gr:229391768", ReadingFormatId = 2 }
                }
            };
        }
    }
}
