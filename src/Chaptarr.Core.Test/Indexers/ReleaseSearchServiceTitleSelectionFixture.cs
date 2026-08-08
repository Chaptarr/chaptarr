using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseSearchServiceTitleSelectionFixture
    {
        [Test]
        public void should_use_selected_edition_title_when_any_edition_ok()
        {
            var englishEdition = new Edition { Title = "Harry Potter and the Philosopher's Stone", Language = "eng" };
            var frenchEdition = new Edition { Title = "l'Epreuve", Language = "fra" };

            var book = new Book
            {
                AnyEditionOk = true,
                Title = "Harry Potter and the Sorcerer's Stone, Book 1",
                Editions = new List<Edition> { frenchEdition, englishEdition }
            };

            var selected = englishEdition;
            var title = ReleaseSearchService.GetSearchBookTitle(book, selected);

            Assert.That(title, Is.EqualTo("Harry Potter and the Philosopher's Stone"));
        }

        [Test]
        public void should_fallback_to_book_title_when_selected_edition_is_null()
        {
            var book = new Book
            {
                Title = "Alanna: The First Adventure"
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, null);

            Assert.That(title, Is.EqualTo("Alanna: The First Adventure"));
        }

        [Test]
        public void should_fallback_to_book_title_when_selected_edition_title_is_blank()
        {
            var blankEdition = new Edition { Title = "   " };
            var book = new Book
            {
                Title = "Alanna: The First Adventure",
                Editions = new List<Edition> { blankEdition }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, blankEdition);

            Assert.That(title, Is.EqualTo("Alanna: The First Adventure"));
        }

        [Test]
        public void book_query_should_use_main_title_section()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Mitch Albom" },
                BookTitle = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Tuesdays+with+Morrie"));
        }

        [Test]
        public void book_query_should_remove_leading_author_prefix_before_splitting()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Mitch Albom" },
                BookTitle = "Mitch Albom: Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Tuesdays+with+Morrie"));
        }

        [Test]
        public void book_query_should_strip_marketing_subtitle_from_selected_edition_title()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "A.F. Kay" },
                BookTitle = "Shade's First Rule: A Fantasy LitRPG Adventure"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Shade's+First+Rule"));
        }

        [Test]
        public void book_query_should_strip_parenthetical_production_title()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Jim Butcher" },
                BookTitle = "Storm Front (Dramatized Adaptation)"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Storm+Front"));
        }
    }
}
