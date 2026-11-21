using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Parser.Model;
using CoreParser = NzbDrone.Core.Parser.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParserTitleParsingFixture
    {
        [TestCase("（320kbps）", "(320kbps)")]
        [TestCase("［320kbps］", "[320kbps]")]
        [TestCase("【320kbps】", "[320kbps]")]
        [TestCase("{320kbps}", "(320kbps)")]
        [TestCase("{Unabridged}", "(Unabridged)")]
        [TestCase("&#65288;320kbps&#65289;", "(320kbps)")]
        public void catalog_brackets_reach_the_legacy_parser_as_ascii(string suffix, string asciiSuffix)
        {
            var actual = CoreParser.ParseBookTitle("Frank Herbert - Dune " + suffix);
            var expected = CoreParser.ParseBookTitle("Frank Herbert - Dune " + asciiSuffix);

            Assert.That(actual, Is.Not.Null);
            Assert.That(actual.AuthorName, Is.EqualTo("Frank Herbert"));
            Assert.That(actual.BookTitle, Is.EqualTo("Dune"));
            Assert.That(actual.ReleaseTitle, Is.EqualTo("Frank Herbert - Dune " + System.Net.WebUtility.HtmlDecode(suffix)));
            expected.ReleaseTitle = actual.ReleaseTitle;
            Assert.That(System.Text.Json.JsonSerializer.Serialize(actual),
                Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(expected)));
        }

        [Test]
        public void shared_cleaning_preserves_original_brackets()
        {
            const string title = "Frank Herbert - Dune （NMR 320 kbps）";

            Assert.That(CoreParser.CleanReleaseTitleForParsing(title), Is.EqualTo(title));
            Assert.That(CoreParser.CleanReleaseTitleForParsing("Frank Herbert - Dune (NMR 320 kbps)"), Is.EqualTo("Frank Herbert - Dune"));
        }

        [TestCase("Unabridged")]
        [TestCase("320kbps")]
        public void custom_formats_keep_original_brackets_and_group_matches_ascii_parsing(string note)
        {
            var original = $"Frank Herbert - Dune {{{note}}}-ReleaseGroup";
            var release = new ReleaseInfo { Title = original };
            var parsed = CoreParser.ParseBookTitle(release.Title);
            var ascii = CoreParser.ParseBookTitle($"Frank Herbert - Dune ({note})-ReleaseGroup");
            Assert.That(parsed, Is.Not.Null);
            Assert.That(ascii, Is.Not.Null);
            Assert.That(parsed.ReleaseGroup, Is.EqualTo(ascii.ReleaseGroup));
            Assert.That(parsed.ReleaseTitle, Is.EqualTo(original));
            var actualInput = new CustomFormatInput { BookInfo = parsed };
            var asciiInput = new CustomFormatInput { BookInfo = ascii };
            foreach (var pattern in new[] { "Dune", "ReleaseGroup" })
            {
                var title = new ReleaseTitleSpecification { Value = pattern };
                var group = new ReleaseGroupSpecification { Value = pattern };
                Assert.That(title.IsSatisfiedBy(actualInput), Is.EqualTo(title.IsSatisfiedBy(asciiInput)), pattern);
                Assert.That(group.IsSatisfiedBy(actualInput), Is.EqualTo(group.IsSatisfiedBy(asciiInput)), pattern);
            }

            var curlyTitle = new ReleaseTitleSpecification { Value = System.Text.RegularExpressions.Regex.Escape($"{{{note}}}") };
            var parenthesizedTitle = new ReleaseTitleSpecification { Value = System.Text.RegularExpressions.Regex.Escape($"({note})") };
            Assert.That(curlyTitle.IsSatisfiedBy(actualInput), Is.True);
            Assert.That(curlyTitle.IsSatisfiedBy(asciiInput), Is.False);
            Assert.That(parenthesizedTitle.IsSatisfiedBy(actualInput), Is.False);
            Assert.That(parenthesizedTitle.IsSatisfiedBy(asciiInput), Is.True);
            Assert.That(release.Title, Is.EqualTo(original), "Parsing must not rewrite the stored indexer title");
        }

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

            var books = new List<Book> { book };
            var result = CoreParser.ParseBookTitleWithSearchCriteria("Freida McFadden - Want to Know a Secret¿", author, books, books);

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

            var books = new List<Book> { book };
            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "J. K. Rowling - Harry Potter and the Philosopher's Stone (aka Harry Potter and the Sorcerer's Stone)",
                author,
                books,
                books);

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

            var books = new List<Book> { book };
            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "Harry Potter and the Philosopher's Stone",
                author,
                books,
                books);

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

            var books = new List<Book> { book };
            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "Brandon Sanderson - 'Mistborn', Bk 1 - The Final Empire  (NMR 64 kbps) [27/31] - \"Brandon Sanderson - Mistborn - The Final Empire 18 of 22.mp3\" yEnc",
                author,
                books,
                books);

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

            var books = new List<Book> { book };
            var result = CoreParser.ParseBookTitleWithSearchCriteria(
                "Brandon Sanderson-&039;mistborn&039;, Bk 1-The Final Empire (Nmr 64 Kbps)",
                author,
                books,
                books);

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

            var books = new List<Book> { book };
            var result = CoreParser.ParseBookTitleWithSearchCriteria(releaseTitle, author, books, books);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Brandon Sanderson"));
            Assert.That(result.BookTitle, Is.EqualTo("The Final Empire"));
        }
        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        public void should_use_explicit_sibling_catalog_independently_of_cached_author_books(BookMediaType mediaType, bool hasCachedBooks)
        {
            var author = new Author { Id = 1, Name = "Stephen King" };
            var target = new Book
            {
                Id = 1, Author = author, Title = "It", MediaType = mediaType,
                Editions = new List<Edition> { new Edition { Title = "It", Monitored = true } }
            };
            var sibling = new Book
            {
                Id = 2, Author = author, Title = "If It Bleeds", MediaType = mediaType,
                Editions = new List<Edition> { new Edition { Title = "If It Bleeds", Monitored = true } }
            };
            var books = new List<Book> { target };
            var catalog = new List<Book> { target, sibling };
            author.Books = hasCachedBooks ? books : null;

            var siblingResult = CoreParser.ParseBookTitleWithSearchCriteria("Stephen King - If It Bleeds", author, books, catalog);
            var targetResult = CoreParser.ParseBookTitleWithSearchCriteria("Stephen King - It", author, books, catalog);

            Assert.Multiple(() =>
            {
                Assert.That(siblingResult, Is.Null);
                Assert.That(targetResult, Is.Not.Null);
                Assert.That(targetResult?.BookTitle, Is.EqualTo("It"));
                Assert.That(author.Books, Is.SameAs(hasCachedBooks ? books : null));
            });
        }
    }
}
