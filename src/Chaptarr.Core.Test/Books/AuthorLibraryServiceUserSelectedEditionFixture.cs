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

        [Test]
        public void suggested_work_without_edition_id_should_infer_the_only_edition_of_the_media_type()
        {
            var expectedEdition = new Edition { Title = "Nightfall", ForeignEditionId = "gr:111" };
            var book = CreateAudiobookPocket("Nightfall");
            book.Editions = new List<Edition> { expectedEdition };
            var ebook = CreateAudiobookPocket("Nightfall");
            ebook.MediaType = BookMediaType.Ebook;
            ebook.Editions = new List<Edition> { new() { Title = "Nightfall", ForeignEditionId = "gr:222" } };
            var author = new Author { Books = new List<Book> { book, ebook } };

            var result = AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                author, "hc:1987747", null, BookMediaType.Audiobook);

            Assert.That(result.Edition, Is.SameAs(expectedEdition));
        }

        [Test]
        public void suggested_work_without_edition_id_should_use_the_suggested_edition_title_to_break_ties()
        {
            var wanted = new Edition { Title = "The Defenders", ForeignEditionId = "gr:1" };
            var book = CreateAudiobookPocket("The Defenders");
            book.Editions = new List<Edition>
            {
                new() { Title = "The Defenders (Unabridged)", ForeignEditionId = "gr:2" },
                wanted
            };
            var author = new Author { Books = new List<Book> { book } };

            var result = AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                author, "hc:1987747", "", BookMediaType.Audiobook, "the defenders");

            Assert.That(result.Edition, Is.SameAs(wanted));
        }

        [Test]
        public void suggested_work_without_edition_id_should_fail_closed_when_several_editions_remain()
        {
            var book = CreateAudiobookPocket("The Defenders");
            book.Editions = new List<Edition>
            {
                new() { Title = "Edition A", ForeignEditionId = "gr:1" },
                new() { Title = "Edition B", ForeignEditionId = "gr:2" }
            };
            var author = new Author { Books = new List<Book> { book } };

            var error = Assert.Catch<InvalidOperationException>(() =>
                AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                    author, "hc:1987747", null, BookMediaType.Audiobook, "Something Else"));

            Assert.That(error.Message, Does.Contain("2 rows"));
        }

        [Test]
        public void suggested_work_without_edition_id_should_fall_back_to_the_providers_default_edition()
        {
            var providerDefault = new Edition { Title = "Nightfall (Audible)", ForeignEditionId = "gr:2" };
            var book = CreateAudiobookPocket("Nightfall");
            book.ForeignEditionId = "gr:2";
            book.Editions = new List<Edition>
            {
                new() { Title = "Nightfall (Other)", ForeignEditionId = "gr:1" },
                providerDefault,
                new() { Title = "Nightfall (Third)", ForeignEditionId = "gr:3" }
            };
            var author = new Author { Books = new List<Book> { book } };

            var result = AuthorLibraryService.ResolveUniqueRemoteUserSelection(
                author, "hc:1987747", null, BookMediaType.Audiobook, "No Such Title");

            Assert.That(result.Edition, Is.SameAs(providerDefault));
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
