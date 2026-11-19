using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ComparisonKeyContractFixture
    {
        [TestCase("John A Smith", "johnasmith")]
        [TestCase("John Or Smith", "johnorsmith")]
        [TestCase("Ann and Pat Smith", "annandpatsmith")]
        [TestCase("The Art of War", "theartofwar")]
        [TestCase("A Tale of Two Cities", "ataleoftwocities")]
        [TestCase("Le Livre de la Vie", "lelivredelavie")]
        [TestCase("100% Proof", "100percentproof")]
        [TestCase("00123", "00123")]
        [TestCase(" 00123 ", "00123")]
        [TestCase(" +00123 ", "+00123")]
        public void stored_person_and_title_keys_should_retain_every_word(string text, string expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(text.CleanAuthorName(), Is.EqualTo(expected));
                Assert.That(text.CleanNarratorName(), Is.EqualTo(expected));
                Assert.That(text.CleanBookTitle().CleanAuthorName(), Is.EqualTo(expected));
            });
        }

        [TestCase(" +00123 ", "+00123")]
        [TestCase(" -00123 ", "-00123")]
        [TestCase("00123", "00123")]
        [NonParallelizable]
        public void numeric_comparison_keys_should_ignore_server_number_signs(string text, string expected)
        {
            var previous = CultureInfo.CurrentCulture;
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.PositiveSign = "~";
            culture.NumberFormat.NegativeSign = "!";
            try
            {
                CultureInfo.CurrentCulture = culture;
                Assert.Multiple(() =>
                {
                    Assert.That(text.CleanAuthorName(), Is.EqualTo(expected));
                    Assert.That(text.CleanNarratorName(), Is.EqualTo(expected));
                    Assert.That(text.CleanBookTitle().CleanAuthorName(), Is.EqualTo(expected));
                });
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [TestCase("Signal (Book Two)", "signalbooktwo")]
        [TestCase("Signal [Book of Dreams]", "signalbookofdreams")]
        [TestCase("Signal (Clean Water)", "signalcleanwater")]
        [TestCase("Signal (Special Delivery)", "signalspecialdelivery")]
        [TestCase("Signal (Limited Edition)", "signallimitededition")]
        public void stored_book_key_should_preserve_parenthetical_title_words(string title, string expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(title.CleanBookTitle().CleanAuthorName(), Is.EqualTo(expected));
                Assert.That(title.CleanBookTitle().CleanAuthorName(), Is.EqualTo(title.CleanAuthorName()));
                Assert.That(title.CleanBookTitle().CleanAuthorName(), Is.Not.EqualTo("Signal".CleanAuthorName()));
            });
        }

        [TestCase("John A Smith", "John Or Smith")]
        [TestCase("The Art of War", "The Art War")]
        [TestCase("I Married a Naga", "I Married Naga")]
        [TestCase("Signal (Book Two)", "Signal (Book Three)")]
        public void a_missing_or_conflicting_word_should_not_create_the_same_stored_key(string left, string right)
        {
            Assert.That(left.CleanBookTitle().CleanAuthorName(), Is.Not.EqualTo(right.CleanBookTitle().CleanAuthorName()));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void absent_title_should_produce_an_empty_key(string title)
        {
            Assert.That(title.CleanBookTitle().CleanAuthorName(), Is.Empty);
        }

        [TestCase("がとう 読者 - Story MP3", true)]
        [TestCase("か\u3099とう 読者 - Story MP3", true)]
        [TestCase("かとう 読者 - Story MP3", false)]
        public void search_criteria_parser_should_keep_script_marks_for_shared_author_comparison(string release, bool expected)
        {
            var author = new Author { Id = 1, Name = "がとう 読者" };
            var book = new Book
            {
                Id = 1,
                AuthorId = author.Id,
                Author = author,
                Title = "Story",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:1",
                Editions = new List<Edition> { new() { Id = 1, Title = "Story", Monitored = true } }
            };
            var books = new List<Book> { book };

            Assert.That(NzbDrone.Core.Parser.Parser.ParseBookTitleWithSearchCriteria(release, author, books, books) != null,
                Is.EqualTo(expected));
        }
    }
}
