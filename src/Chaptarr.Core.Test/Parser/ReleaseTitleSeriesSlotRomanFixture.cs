using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleSeriesSlotRomanFixture
    {
        private static Book Make(int id, Author author, string title, string series, string position, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            return new Book
            {
                Id = id, AuthorId = author.Id, Author = author, Title = title,
                SeriesName = series, SeriesPosition = position, MediaType = mediaType, HardcoverBookId = $"hc:{id}",
                Editions = new List<Edition> { new Edition { Id = id, Title = title, Monitored = true } }
            };
        }

        private static TitleMatchResult Match(Author author, Book target, List<Book> catalog, string releaseTitle, string structuredBookTitle = null)
        {
            return ReleaseTitleMatchScorer.FindBestMatchForSearch(
                new ReleaseInfo { Title = releaseTitle, Author = author.Name, Book = structuredBookTitle },
                author.Name, new[] { target }, author.Name, catalog,
                new ReleaseTitleMatchScorer.CatalogPreparation());
        }

        private static TitleMatchResult DirectMatch(Author author, Book target, List<Book> catalog, string releaseTitle)
        {
            return ReleaseTitleMatchScorer.FindBestMatch(
                releaseTitle, author.Name, new[] { target }, author.Name, catalog);
        }

        private static bool Matches(Author author, Book target, List<Book> catalog, string releaseTitle)
        {
            return Match(author, target, catalog, releaseTitle)?.IsMatch == true;
        }

        private static (Author Author, Book Target, List<Book> Catalog) Dune()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1");
            var catalog = new List<Book>
            {
                target,
                Make(39408, author, "Dune Messiah", "Dune", "2"),
                Make(39409, author, "Children of Dune", "Dune", "3"),
                Make(39410, author, "God Emperor of Dune", "Dune", "4"),
                Make(39411, author, "Heretics of Dune", "Dune", "5")
            };

            return (author, target, catalog);
        }

        // When the stored title IS the series name, the following token is the work discriminator.
        [TestCase("Herbert  Frank Dune I [ebook][spanish]", true)]
        [TestCase("Herbert  Frank Dune II [ebook][spanish]", false)]
        [TestCase("Herbert  Frank Dune III [ebook][spanish]", false)]
        [TestCase("Herbert  Frank Dune IV [ebook][spanish]", false)]
        [TestCase("Herbert  Frank Dune V [ebook][spanish]", false)]
        [TestCase("Herbert Frank Dune III", false)]
        [TestCase("Herbert Frank Dune III EPUB", false)]
        [TestCase("Dune III (2009) MP3", false)]
        [TestCase("Frank Herbert - Dune 3 (2009) MP3", false)]
        [TestCase("Frank Herbert - Dune II-Der Herr Des Wüstenplaneten (2009) MP3", false)]
        [TestCase("Frank.Herbert-Dune.III.Die.Kinder.Des.Wuestenplaneten-17CD-Audiobook-DE-2009-iTSx", false)]
        [TestCase("Dune III - Frank Herbert", false)]
        [TestCase("Frank Herbert - Dune 1", true)]
        [TestCase("Frank Herbert - Dune 3", false)]
        [TestCase("Frank Herbert - Dune (epub)", true)]
        public void a_roman_position_in_the_series_slot_should_behave_like_a_digit(string release, bool expected)
        {
            var (author, target, catalog) = Dune();

            Assert.That(Matches(author, target, catalog, release), Is.EqualTo(expected), release);
        }

        [Test]
        public void structured_book_title_overrides_a_conflicting_raw_release_title()
        {
            var (author, target, catalog) = Dune();

            var rawWithIncidentalNumber = Match(author, target, catalog, "Frank Herbert - Dune \"Dune 12.mp3\" MP3", "Dune");
            var rawWithMatchingTitle = Match(author, target, catalog, "Frank Herbert - Dune", "Dune 3");

            Assert.Multiple(() =>
            {
                Assert.That(rawWithIncidentalNumber?.IsMatch, Is.True);
                Assert.That(rawWithIncidentalNumber?.EvidenceTitle, Is.EqualTo("Dune"));
                Assert.That(rawWithMatchingTitle?.IsMatch, Is.False);
                Assert.That(rawWithMatchingTitle?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SeriesPositionMismatch));
            });
        }

        [TestCase("Frank Herbert - Dune \"Dune 12.mp3\" MP3", true)]
        [TestCase("Frank Herbert - Dune “Dune 12.mp3” MP3", true)]
        [TestCase("Frank Herbert - Dune \"Dune 12.mp3\"", true)]
        [TestCase("Frank Herbert - Dune “Dune 12.mp3”", true)]
        [TestCase("Frank Herbert - Dune 4", false)]
        [TestCase("Frank Herbert - Dune \"Dune Book 12.mp3\" MP3", false)]
        [TestCase("Frank Herbert - Dune \"Dune #12.mp3\" MP3", false)]
        [TestCase("Frank Herbert - Dune \"Dune #12.mp3\"", false)]
        [TestCase("Frank Herbert - Dune \"Dune 12.epub\"", false)]
        [TestCase("Frank Herbert - Dune \"Dune 12 chapter.mp3\"", false)]
        [TestCase("Frank Herbert - \"Dune 12.mp3\"", false)]
        public void an_incidental_attachment_number_requires_separate_title_evidence(string release, bool expected)
        {
            var (author, target, _) = Dune();
            Assert.That(Matches(author, target, new List<Book> { target }, release), Is.EqualTo(expected));
        }

        [TestCase("Frank Herbert - Dune \"Dune 4.mp3\"")]
        [TestCase("Frank Herbert - Dune \"Dune 4.mp3\" MP3")]
        [TestCase("Frank Herbert - Dune \"Dune Messiah.mp3\"")]
        public void quoted_attachments_must_not_hide_known_sibling_identity(string release)
        {
            var (author, target, catalog) = Dune();
            Assert.That(Matches(author, target, catalog, release), Is.False);
        }

        // Tokens that merely look like numerals, or are numerals identifying nobody, must not reject.
        [TestCase("Frank.P.Herbert.-.Dune.2014.Italian.Retail.EPUB.eBook-BitBook")]
        [TestCase("Frank Herbert Dune Il graphic novel MuadDib Vol 2 2022 pdf")]
        [TestCase("Frank Herbert - Dune CD")]
        [TestCase("Frank Herbert - Dune XXX")]
        [TestCase("Frank Herbert - Dune MIX")]
        [TestCase("Frank Herbert - Dune DL")]
        public void a_lookalike_or_unclaimed_numeral_must_not_reject(string release)
        {
            var (author, target, catalog) = Dune();

            Assert.That(Matches(author, target, catalog, release), Is.True, release);
        }


        [TestCase("Bron vi jagar")]
        [TestCase("Bron VI jagar")]
        [TestCase("Bron vi and")]
        [TestCase("Bron vi the")]
        public void a_known_position_after_a_series_title_rejects_unless_it_belongs_to_the_full_edition_title(string release)
        {
            var author = new Author { Id = 700, Name = "Example Author" };
            var target = Make(7001, author, "Bron", "Bron", "1");
            var sibling = Make(7006, author, "Other Story", "Bron", "6");

            var match = Match(author, target, new List<Book> { target, sibling }, release);

            var fullTitleTarget = Make(7002, author, release, "Bron", "1");
            var fullTitleMatch = Match(author, fullTitleTarget, new List<Book> { fullTitleTarget, sibling }, release);

            Assert.Multiple(() =>
            {
                Assert.That(match?.IsMatch, Is.False, release);
                Assert.That(match?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SeriesPositionMismatch), release);
                Assert.That(fullTitleMatch?.IsMatch, Is.True, release);
            });
        }

        [Test]
        public void a_following_sibling_title_does_not_hide_a_conflicting_series_position()
        {
            var (author, target, catalog) = Dune();

            var match = Match(author, target, catalog, "Frank Herbert - Dune III Children of Dune");

            Assert.Multiple(() =>
            {
                Assert.That(match?.IsMatch, Is.False);
                Assert.That(match?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));
                Assert.That(match?.Problems.Any(problem => problem.Code == TitleMatchProblemCode.SeriesPositionMismatch), Is.True);
            });
        }

        [Test]
        public void a_terminal_position_requires_series_context_and_a_matching_sibling()
        {
            var author = new Author { Id = 701, Name = "Example Author" };
            var noSeriesTarget = Make(7011, author, "Bron", string.Empty, "1");
            var noSeriesSibling = Make(7016, author, "Other Story", string.Empty, "6");
            var seriesTarget = Make(7021, author, "Bron", "Bron", "1");
            var otherPositionSibling = Make(7025, author, "Other Story", "Bron", "5");

            Assert.Multiple(() =>
            {
                Assert.That(Matches(author, noSeriesTarget, new List<Book> { noSeriesTarget, noSeriesSibling }, "Bron VI"), Is.True);
                Assert.That(Matches(author, seriesTarget, new List<Book> { seriesTarget, otherPositionSibling }, "Bron VI"), Is.True);
            });
        }

        [Test]
        public void a_sibling_on_the_other_media_side_does_not_veto_a_roman_position()
        {
            var author = new Author { Id = 703, Name = "Example Author" };
            var target = Make(7031, author, "Bron", "Bron", "1", BookMediaType.Audiobook);
            var ebookSibling = Make(7036, author, "Other Story", "Bron", "6", BookMediaType.Ebook);

            Assert.That(Matches(author, target, new List<Book> { target, ebookSibling }, "Bron VI"), Is.True);
        }

        [TestCase("3")]
        [TestCase("1,3")]
        [TestCase("1-3")]
        public void a_target_position_set_containing_the_roman_value_does_not_veto(string storedPosition)
        {
            var author = new Author { Id = 704, Name = "Example Author" };
            var target = Make(7041, author, "Dune", "Dune", storedPosition);
            var sibling = Make(7043, author, "Other Story", "Dune", "3");
            var catalog = new List<Book> { target, sibling };

            Assert.That(DirectMatch(author, target, catalog, "Dune III")?.IsMatch, Is.True, storedPosition);
        }

        [Test]
        public void an_edition_title_number_remains_required_and_can_match_as_title_evidence()
        {
            var author = new Author { Id = 705, Name = "Example Author" };
            var target = Make(7051, author, "Casual Farmer 5", "Casual Farmer", "1");
            var sibling = Make(7055, author, "Another Work", "Casual Farmer", "5");
            var catalog = new List<Book> { target, sibling };

            Assert.Multiple(() =>
            {
                Assert.That(Matches(author, target, catalog, "Casual Farmer 5"), Is.True);
                Assert.That(Matches(author, target, catalog, "Casual Farmer"), Is.False);
            });
        }

        [Test]
        public void a_known_sibling_position_rejects_a_series_title_followed_by_its_roman_value()
        {
            var author = new Author { Id = 707, Name = "Example Author" };
            var target = Make(7071, author, "Casual Farmer", "Casual Farmer", "1");
            var sibling = Make(7075, author, "Another Work", "Casual Farmer", "5");
            var catalog = new List<Book> { target, sibling };

            Assert.Multiple(() =>
            {
                Assert.That(Matches(author, target, catalog, "Casual Farmer 5"), Is.False);
                Assert.That(Matches(author, target, catalog, "Casual Farmer V"), Is.False);
            });
        }

        [Test]
        public void a_roman_numeral_inside_the_full_edition_title_is_not_a_position_suffix()
        {
            var author = new Author { Id = 706, Name = "Example Author" };
            var target = Make(7061, author, "Bron VI", "Bron", "1");
            var sibling = Make(7066, author, "Other Story", "Bron", "6");

            Assert.That(Matches(author, target, new List<Book> { target, sibling }, "Bron VI"), Is.True);
        }

        [Test]
        public void roman_numerals_stay_inert_when_the_title_is_not_the_series_name()
        {
            // Numerals inside a distinctive title must not open a series-position slot.
            var mortimer = new Author { Id = 900, Name = "Ian Mortimer" };
            var henryV = Make(1, mortimer, "Henry V", "Plantagenets", "3");
            var plantagenets = new List<Book> { henryV, Make(2, mortimer, "Henry IV", "Plantagenets", "2") };

            Assert.That(Matches(mortimer, henryV, plantagenets, "Ian Mortimer - Henry V- The Warrior King of 1415.epub"), Is.True);

            var gavin = new Author { Id = 901, Name = "Gavin Mortimer" };
            var lrdg = Make(3, gavin, "The Long Range Desert Group in World War II", string.Empty, string.Empty);

            Assert.That(Matches(gavin, lrdg, new List<Book> { lrdg }, "Gavin Mortimer - The Long Range Desert Group in World War II (retail)"), Is.True);
        }

        [Test]
        public void a_distinctive_title_is_never_overruled_by_a_slot_numeral()
        {
            // A derived position never overrules a present book title. That
            // holds because the slot only opens when the stored title IS the series name.
            var rowling = new Author { Id = 617, Name = "J.K. Rowling" };
            var stone = Make(1, rowling, "Harry Potter and the Sorcerer's Stone", "Harry Potter", "1");
            var catalog = new List<Book>
            {
                stone, Make(2, rowling, "Harry Potter and the Order of the Phoenix", "Harry Potter", "5")
            };

            Assert.That(Matches(rowling, stone, catalog, "Harry Potter 5 - Harry Potter and the Sorcerer's Stone JK Rowling"), Is.True);
            Assert.That(Matches(rowling, stone, catalog, "Harry Potter V - Harry Potter and the Sorcerer's Stone JK Rowling"), Is.True);
        }

        [TestCase("The Silent Patient", "X", "10 - The Silent Patient", true)]
        [TestCase("The Silent Patient", "X", "The Silent Patient - X", false)]
        [TestCase("Wild Cards", "Wild Cards VII: Dead Man's Hand", "Wild Cards 7 Dead Man's Hand", false)]
        [TestCase("A Woman Named Vi", "Unrelated Book", "A Woman Named Vi", true)]
        [TestCase("A Woman Named Vi", "Unrelated Book", "A Woman Named 6", true)]
        [TestCase("A Woman Named Vi", "A Woman Named 6", "A Woman Named 6", false)]
        [TestCase("Un livre Tome IV", "Unrelated Book", "Un livre Tome 4", true)]
        [TestCase("Ein Buch Band II", "Unrelated Book", "Ein Buch Band 2", true)]
        public void numeric_fallback_must_explain_the_target_title_to_contradict_it(
            string title, string siblingTitle, string observed, bool expected)
        {
            var author = new Author { Id = 710, Name = "Example Author" };
            var target = Make(7101, author, title, string.Empty, string.Empty);
            var sibling = Make(7102, author, siblingTitle, string.Empty, string.Empty);
            var catalog = new List<Book> { target, sibling };
            var release = "Example Author - " + observed;
            Assert.Multiple(() =>
            {
                Assert.That(DirectMatch(author, target, catalog, release)?.IsMatch == true, Is.EqualTo(expected), "Direct/RSS title matching");
                Assert.That(Match(author, target, catalog, release)?.IsMatch == true, Is.EqualTo(expected), "Targeted search title matching");
            });
        }

        [Test]
        public void numeric_rival_must_not_replace_the_existing_unexplained_number_rejection()
        {
            var author = new Author { Id = 711, Name = "Example Author" };
            var target = Make(7111, author, "The Silent Patient", string.Empty, string.Empty);
            var sibling = Make(7112, author, "X", string.Empty, string.Empty);
            var release = "Example Author - The Silent Patient - 10";
            var withoutSibling = DirectMatch(author, target, new List<Book> { target }, release);
            var withSibling = DirectMatch(author, target, new List<Book> { target, sibling }, release);
            Assert.That(withoutSibling?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SuspiciousAdjacentNumber));
            Assert.That(withSibling?.ProblemCode, Is.EqualTo(withoutSibling.ProblemCode));
            Assert.That(withSibling?.IsMatch, Is.False);
        }

        [TestCase("Vi", "vi")]
        [TestCase("MIX", "mix")]
        public void canonical_roman_looking_words_remain_exact_book_title_evidence(string title, string releaseTitle)
        {
            var author = new Author { Id = 708, Name = "Example Author" };
            var book = Make(7081, author, title, string.Empty, string.Empty);

            Assert.That(Matches(author, book, new List<Book> { book }, $"Example Author - {releaseTitle}"), Is.True);
        }

        [Test]
        public void a_full_roman_looking_sibling_title_wins_while_its_numbered_book_remains_matchable()
        {
            var author = new Author { Id = 709, Name = "Example Author" };
            var baseTitle = Make(7091, author, "Return of the Jedi", "Star Wars", "4");
            var numberedTitle = Make(7096, author, "Return of the Jedi VI", "Star Wars", "6");
            var catalog = new List<Book> { baseTitle, numberedTitle };
            var release = "Example Author - Return of the Jedi VI";

            var baseTitleMatch = Match(author, baseTitle, catalog, release);
            var numberedTitleMatch = Match(author, numberedTitle, catalog, release);

            Assert.Multiple(() =>
            {
                Assert.That(baseTitleMatch?.IsMatch, Is.False);
                Assert.That(baseTitleMatch?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));
                Assert.That(numberedTitleMatch?.IsMatch, Is.True);
            });
        }
    }
}
