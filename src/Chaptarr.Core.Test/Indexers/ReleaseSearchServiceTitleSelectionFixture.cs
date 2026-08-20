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
        public void should_use_book_title_when_selected_edition_is_an_omnibus_containing_it()
        {
            var omnibus = new Edition { Title = "A Game of Thrones / A Clash of Kings" };

            var book = new Book
            {
                Title = "A Clash of Kings",
                Editions = new List<Edition> { omnibus }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, omnibus);

            Assert.That(title, Is.EqualTo("A Clash of Kings"));
        }

        [Test]
        public void should_use_book_title_when_omnibus_separator_has_no_surrounding_spaces()
        {
            var omnibus = new Edition { Title = "A Game of Thrones/A Clash of Kings" };

            var book = new Book
            {
                Title = "A Clash of Kings",
                Editions = new List<Edition> { omnibus }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, omnibus);

            Assert.That(title, Is.EqualTo("A Clash of Kings"));
        }

        [Test]
        public void should_match_omnibus_segment_ignoring_case_and_padding()
        {
            var omnibus = new Edition { Title = "A GAME OF THRONES  /   a clash of kings" };

            var book = new Book
            {
                Title = "A Clash of Kings",
                Editions = new List<Edition> { omnibus }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, omnibus);

            Assert.That(title, Is.EqualTo("A Clash of Kings"));
        }

        [Test]
        public void should_keep_edition_title_when_slash_is_part_of_a_phrase()
        {
            // "Horror/Sci-Fi" is one phrase, not two works. Splitting here would search for nonsense.
            var edition = new Edition { Title = "Stories of Fantasy, Horror/Sci-Fi, and a Man Called Tuf" };

            var book = new Book
            {
                Title = "Dreamsongs",
                Editions = new List<Edition> { edition }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, edition);

            Assert.That(title, Is.EqualTo("Stories of Fantasy, Horror/Sci-Fi, and a Man Called Tuf"));
        }

        [Test]
        public void should_keep_edition_title_when_no_segment_matches_the_book()
        {
            // A marketplace-style listing that happens to contain a slash. The book title is not a
            // clean segment, so there is nothing safe to fall back to.
            var edition = new Edition { Title = "Rare George R R Martin / A KNIGHT OF THE SEVEN KINGDOMS Signed 1st Edition 2015" };

            var book = new Book
            {
                Title = "A Knight of the Seven Kingdoms",
                Editions = new List<Edition> { edition }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, edition);

            Assert.That(title, Is.EqualTo("Rare George R R Martin / A KNIGHT OF THE SEVEN KINGDOMS Signed 1st Edition 2015"));
        }

        [Test]
        public void should_keep_edition_title_when_book_title_is_blank()
        {
            var omnibus = new Edition { Title = "A Game of Thrones / A Clash of Kings" };

            var book = new Book
            {
                Title = "   ",
                Editions = new List<Edition> { omnibus }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, omnibus);

            Assert.That(title, Is.EqualTo("A Game of Thrones / A Clash of Kings"));
        }

        [Test]
        public void omnibus_edition_should_produce_a_single_work_book_query()
        {
            var omnibus = new Edition { Title = "A Game of Thrones / A Clash of Kings" };

            var book = new Book
            {
                Title = "A Clash of Kings",
                Editions = new List<Edition> { omnibus }
            };

            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "George R.R. Martin" },
                BookTitle = ReleaseSearchService.GetSearchBookTitle(book, omnibus)
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("A+Clash+of+Kings"));
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
