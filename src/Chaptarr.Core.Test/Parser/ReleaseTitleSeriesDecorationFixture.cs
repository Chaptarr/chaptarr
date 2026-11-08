using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleSeriesDecorationFixture
    {
        private static List<Book> BuildSeries(Author author, string seriesName, params (string Title, string Position)[] books)
        {
            return books.Select((book, index) => new Book
            {
                Id = index + 1,
                Author = author,
                Title = book.Title,
                SeriesName = seriesName,
                SeriesPosition = book.Position,
                Editions = new List<Edition> { new Edition { Id = index + 1, Title = book.Title, Monitored = true } }
            }).ToList();
        }

        private static TitleMatchResult Match(Author author, List<Book> catalog, Book target, string release)
        {
            return ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { target }, author.Name, catalog);
        }

        private static (Author Author, List<Book> Catalog, Book Target) PocketPotters()
        {
            var author = new Author { Name = "J.K. Rowling" };
            var catalog = BuildSeries(author, "Pocket Potters",
                ("Pocket Potters: Harry Potter", "1"),
                ("Pocket Potters: Ron Weasley", "2"),
                ("Pocket Potters: Hermione Granger", "3"),
                ("Pocket Potters: Professor Dumbledore", "4"));

            return (author, catalog, catalog[2]);
        }

        [TestCase("J.K. Rowling - Pocket Potters 3 - Hermione Granger", true)]
        [TestCase("J.K. Rowling - Pocket Potters - Hermione Granger", true)]
        [TestCase("J.K. Rowling - Pocket Potters: Hermione Granger", true)]
        [TestCase("J.K. Rowling - Hermione Granger", true)]
        [TestCase("J.K. Rowling - Pocket Potters Book 3 - Hermione Granger", true)]
        [TestCase("J.K. Rowling - Pocket Potters 99 - Hermione Granger", true)]
        // A stated position we BELIEVE belongs to a sibling is a derived guess and never overrules a
        // present book title. Only a sibling's own title blocks.
        [TestCase("J.K. Rowling - Pocket Potters 2 - Hermione Granger", true)]
        [TestCase("J.K. Rowling - Pocket Potters 4 - Hermione Granger", true)]
        [TestCase("J.K. Rowling - Pocket Potters 2 - Ron Weasley", false)]
        public void a_derived_series_position_must_not_overrule_a_present_book_title(string release, bool expected)
        {
            var (author, catalog, target) = PocketPotters();

            Assert.That(Match(author, catalog, target, release)?.IsMatch == true, Is.EqualTo(expected), release);
        }

        [Test]
        public void an_ambiguous_series_prefix_should_not_hide_a_unique_tail()
        {
            var author = new Author { Name = "Adrian Tchaikovsky" };
            var catalog = BuildSeries(author, "Terrible Worlds",
                ("Terrible Worlds: Revolutions", "1"),
                ("Terrible Worlds: Destinations", "2"));
            var target = catalog[1];

            Assert.That(Match(author, catalog, target, "Adrian Tchaikovsky - Terrible Worlds 2 - Destinations")?.IsMatch == true, Is.True);

            // Wrong derived position, right title: the title wins.
            Assert.That(Match(author, catalog, target, "Adrian Tchaikovsky - Terrible Worlds 1 - Destinations")?.IsMatch == true, Is.True);

            // The sibling's own title is present, so that blocks.
            Assert.That(Match(author, catalog, target, "Adrian Tchaikovsky - Terrible Worlds 2 - Revolutions")?.IsMatch == true, Is.False);
        }

        [TestCase("Ben Aaronovitch - Rivers of London 5.5 - Tales from the Folly", true)]
        [TestCase("Ben Aaronovitch - Rivers of London 6.5 - Tales from the Folly", true)]
        [TestCase("Ben Aaronovitch - Rivers of London 7.5 - Tales from the Folly", true)]
        [TestCase("Ben Aaronovitch - Rivers of London 2 - Tales from the Folly", true)]
        public void every_member_of_a_multi_position_set_should_agree(string release, bool expected)
        {
            var author = new Author { Name = "Ben Aaronovitch" };
            var catalog = BuildSeries(author, "Rivers of London",
                ("Rivers of London: Tales from the Folly", "5.5,6.5,7.5"),
                ("Rivers of London: Moon Over Soho", "2"));

            Assert.That(Match(author, catalog, catalog[0], release)?.IsMatch == true, Is.EqualTo(expected), release);
        }

        [TestCase("1-3", "Sarah J. Maas - Crescent City 2 - The Omnibus", true)]
        [TestCase("1-3", "Sarah J. Maas - Crescent City 3 - The Omnibus", true)]
        [TestCase("1-3", "Sarah J. Maas - Crescent City 7 - The Omnibus", true)]
        public void a_range_position_should_agree_with_any_position_inside_it(string storedPosition, string release, bool expected)
        {
            var author = new Author { Name = "Sarah J. Maas" };
            var catalog = BuildSeries(author, "Crescent City",
                ("Crescent City: The Omnibus", storedPosition),
                ("Crescent City: House of Sky and Breath", "7"));

            Assert.That(Match(author, catalog, catalog[0], release)?.IsMatch == true, Is.EqualTo(expected), release);
        }

        [Test]
        public void should_fail_open_when_the_stored_position_cannot_be_parsed()
        {
            var author = new Author { Name = "Terry Pratchett" };
            var catalog = BuildSeries(author, "Discworld",
                ("Discworld: The Companion", "specials"),
                ("Discworld: Mort", "4"));

            // The target has no parseable position, so a stated one is never treated as a contradiction.
            Assert.That(Match(author, catalog, catalog[0], "Terry Pratchett - Discworld 4 - The Companion")?.IsMatch == true, Is.True);
        }

        [Test]
        public void a_number_beside_a_title_that_is_also_the_series_name_still_rejects()
        {
            var author = new Author { Name = "Frank Herbert" };
            var catalog = BuildSeries(author, "Dune",
                ("Dune", "1"),
                ("Dune Messiah", "2"),
                ("God Emperor of Dune", "4"));

            Assert.That(Match(author, catalog, catalog[0], "Frank Herbert - Dune 4")?.IsMatch == true, Is.False);
            Assert.That(Match(author, catalog, catalog[0], "Frank Herbert - Dune 1")?.IsMatch == true, Is.True);
        }

        [TestCase("J.K. Rowling - Pocket Potters 2 - Hermione Granger")]
        [TestCase("J.K. Rowling - Pocket Potters 99 - Hermione Granger")]
        [TestCase("J.K. Rowling - Pocket Potters 3 - Hermione Granger")]
        public void a_position_that_identifies_no_known_book_should_not_block_a_unique_tail(string release)
        {
            var author = new Author { Name = "J.K. Rowling" };

            // Same work, two rows (the audiobook/ebook pair). Neither can contradict the other, so no
            // stated position identifies a different book and the unique tail must carry the match.
            Book Clone(int id) => new Book
            {
                Id = id, Author = author, Title = "Pocket Potters: Hermione Granger",
                SeriesName = "Pocket Potters", SeriesPosition = "3",
                GoodreadsBookId = "gr:111", HardcoverBookId = "hc:111",
                Editions = new List<Edition> { new Edition { Id = id, Title = "Pocket Potters: Hermione Granger", Monitored = true } }
            };

            var target = Clone(1);
            var catalog = new List<Book> { target, Clone(2) };

            Assert.That(Match(author, catalog, target, release)?.IsMatch == true, Is.True, release);
            Assert.That(Match(author, new List<Book> { target }, target, release)?.IsMatch == true, Is.True, release + " (solo catalog)");
        }


        [Test]
        public void sibling_titles_present_should_block_even_when_the_position_looks_right()
        {
            var author = new Author { Name = "J.K. Rowling" };
            var catalog = BuildSeries(author, "Pocket Potters",
                ("Pocket Potters: Harry Potter", "1"),
                ("Pocket Potters: Ron Weasley", "2"),
                ("Pocket Potters: Hermione Granger", "3"),
                ("Pocket Potters: Professor Dumbledore", "4"));
            var target = catalog[2];

            // Blocking comes from the other books' TITLES being present, not from the number.
            Assert.That(Match(author, catalog, target,
                "J.K. Rowling - Pocket Potters 3 - Hermione Granger, Ron Weasley, Professor Dumbledore")?.IsMatch == true, Is.False);

            // The same release without the sibling titles is fine, wrong number and all.
            Assert.That(Match(author, catalog, target,
                "J.K. Rowling - Pocket Potters 3 - Hermione Granger")?.IsMatch == true, Is.True);
        }

        [Test]
        public void a_full_book_title_should_survive_a_contradicting_derived_position()
        {
            var author = new Author { Name = "J.K. Rowling" };
            var catalog = BuildSeries(author, "Harry Potter",
                ("Harry Potter and the Sorcerer's Stone", "1"),
                ("Harry Potter and the Chamber of Secrets", "2"),
                ("Harry Potter and the Order of the Phoenix", "5"));
            var target = catalog[0];

            // The release says position 5, which we believe belongs to Order of the Phoenix. The full
            // title of book 1 is present, so the release is book 1.
            Assert.That(Match(author, catalog, target,
                "Harry Potter 5 - Harry Potter and the Sorcerer's Stone JK Rowling")?.IsMatch == true, Is.True);

            // The other book's own title present means the other book.
            Assert.That(Match(author, catalog, target,
                "Harry Potter 1 - Harry Potter and the Order of the Phoenix JK Rowling")?.IsMatch == true, Is.False);
        }

    }
}
