using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using CoreParser = NzbDrone.Core.Parser.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParserTitleParsingFixture
    {
        [Test]
        public void should_parse_author_and_book_from_author_dash_title_with_suffix()
        {
            var result = CoreParser.ParseBookTitle("Freida McFadden - Want to Know a Secret (epub)");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Freida McFadden"));
            Assert.That(result.BookTitle, Is.EqualTo("Want to Know a Secret"));
        }

        [Test]
        public void should_parse_author_from_series_bracketed_titles()
        {
            var result = CoreParser.ParseBookTitle("Brandon Mull - [Beyonders 01] - A World Without Heroes (v5.0)");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Brandon Mull"));
            Assert.That(result.BookTitle, Does.Contain("A World Without Heroes"));
        }

        [Test]
        public void should_parse_author_and_book_from_title_by_author_with_format_suffix()
        {
            var result = CoreParser.ParseBookTitle("Want to Know a Secret by Freida McFadden EPUB");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Freida McFadden"));
            Assert.That(result.BookTitle, Is.EqualTo("Want to Know a Secret"));
        }

        [Test]
        public void should_parse_author_and_book_from_title_by_author_without_format_suffix()
        {
            var result = CoreParser.ParseBookTitle("Want to Know a Secret by Freida McFadden");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Freida McFadden"));
            Assert.That(result.BookTitle, Is.EqualTo("Want to Know a Secret"));
        }

        [Test]
        public void should_match_search_criteria_book_even_with_punctuation_variants()
        {
            var author = new Author { Name = "Freida McFadden" };
            var book = new Book { Author = author, Title = "Want to Know a Secret?" };

            var result = CoreParser.ParseBookTitleWithSearchCriteria("Freida McFadden - Want to Know a Secret¿", author, new List<Book> { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Freida McFadden"));
            Assert.That(result.BookTitle, Is.EqualTo("Want to Know a Secret?"));
        }

        [Test]
        public void should_return_selected_edition_title_when_primary_title_is_present_with_alias_tokens()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Author = author,
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Sorcerer's Stone", Monitored = true },
                    new Edition { Id = 2, Title = "Harry Potter and the Philosopher's Stone" }
                }
            };

            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "J. K. Rowling - Harry Potter and the Philosopher's Stone (aka Harry Potter and the Sorcerer's Stone)",
                author,
                new List<Book> { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("J. K. Rowling"));
            Assert.That(result.BookTitle, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
        }

        [Test]
        public void should_not_parse_an_unmonitored_sibling_title_when_any_edition_ok_is_enabled()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Author = author,
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Sorcerer's Stone", Monitored = true },
                    new Edition { Id = 2, Title = "Harry Potter and the Philosopher's Stone" }
                }
            };

            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "Harry Potter and the Philosopher's Stone",
                author,
                new List<Book> { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_match_search_criteria_book_for_usenet_subject_with_filename_and_yenc_noise()
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Author = author,
                Title = "Mistborn: The Final Empire",
                SeriesName = "Mistborn",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "The Final Empire", Monitored = true },
                    new Edition { Id = 2, Title = "Mistborn: The Final Empire" }
                }
            };

            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "Brandon Sanderson - 'Mistborn', Bk 1 - The Final Empire  (NMR 64 kbps) [27/31] - \"Brandon Sanderson - Mistborn - The Final Empire 18 of 22.mp3\" yEnc",
                author,
                new List<Book> { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Brandon Sanderson"));
            Assert.That(result.BookTitle, Is.EqualTo("The Final Empire"));
        }

        [Test]
        public void should_match_search_criteria_book_when_title_contains_malformed_apostrophe_entities()
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Author = author,
                Title = "Mistborn: The Final Empire",
                SeriesName = "Mistborn",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "The Final Empire", Monitored = true },
                    new Edition { Id = 2, Title = "Mistborn: The Final Empire" }
                }
            };

            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "Brandon Sanderson-&039;mistborn&039;, Bk 1-The Final Empire (Nmr 64 Kbps)",
                author,
                new List<Book> { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Brandon Sanderson"));
            Assert.That(result.BookTitle, Is.EqualTo("The Final Empire"));
        }

        [TestCase("Brandon Sanderson - Mistborn 01 - The Final Empire")]
        [TestCase("Brandon Sanderson - [Mistborn 01] - Mistborn-The Final Empire (epub)")]
        [TestCase("Sanderson, Brandon - Mistborn 01 - The Final Empire")]
        [TestCase("Mistborn The Final Empire - Brandon Sanderson")]
        [TestCase("Mistborn 01 - The Final Empire - Brandon Sanderson")]
        public void should_match_search_criteria_book_for_existing_newznab_title_shapes(string releaseTitle)
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Author = author,
                Title = "Mistborn: The Final Empire",
                SeriesName = "Mistborn",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "The Final Empire", Monitored = true },
                    new Edition { Id = 2, Title = "Mistborn: The Final Empire" }
                }
            };

            var result = CoreParser.ParseBookTitleWithSearchCriteria(releaseTitle, author, new List<Book> { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Brandon Sanderson"));
            Assert.That(result.BookTitle, Is.EqualTo("The Final Empire"));
        }
    }
}
