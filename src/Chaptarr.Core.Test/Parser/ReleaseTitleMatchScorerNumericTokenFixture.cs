using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleMatchScorerNumericTokenFixture
    {
        [Test]
        public void should_split_a_number_from_a_dotted_scene_year()
        {
            Assert.That(ReleaseTitleMatchScorer.Tokenize("Fahrenheit.451.2018.EPUB"),
                Is.EqualTo(new[] { "fahrenheit", "451", "2018", "epub" }));
        }

        [Test]
        public void should_keep_decimal_numbers_as_one_token()
        {
            Assert.That(ReleaseTitleMatchScorer.Tokenize("Volume 13.5"), Is.EqualTo(new[] { "volume", "13.5" }));
            Assert.That(ReleaseTitleMatchScorer.Tokenize("Book 2.0 Remastered"), Is.EqualTo(new[] { "book", "2.0", "remastered" }));
        }

        [TestCase("01", "1", true)]
        [TestCase("1", "001", true)]
        [TestCase("13.5", "013.5", true)]
        [TestCase("1", "2", false)]
        [TestCase("1.5", "1.50", false)]
        [TestCase("12345", "012345", false)]
        [TestCase("0", "00", true)]
        [TestCase("01", "one", false)]
        public void should_compare_numeric_tokens_by_value(string left, string right, bool expected)
        {
            Assert.That(ReleaseTitleMatchScorer.TokensMatch(left, right), Is.EqualTo(expected));
        }

        [TestCase("Ray Bradbury Fahrenheit.451.2018 EPUB", "Fahrenheit 451", true)]
        [TestCase("Ray Bradbury Fahrenheit 451 EPUB", "Fahrenheit 451", true)]
        [TestCase("Ray Bradbury Fahrenheit 452 EPUB", "Fahrenheit 451", false)]
        public void should_match_titles_when_a_number_is_followed_by_a_scene_year(string releaseTitle, string title, bool accepted)
        {
            var author = new Author { Name = "Ray Bradbury" };
            var target = new Book { Id = 1, Title = title, Author = author };

            var result = ReleaseTitleMatchScorer.FindBestMatch(releaseTitle, author.Name, new[] { target }, null, new[] { target });

            Assert.That(result?.IsMatch == true, Is.EqualTo(accepted));
        }

        [Test]
        public void should_match_zero_padded_volume_numbers_against_plain_numbers()
        {
            var author = new Author { Name = "Jane Writer" };
            var target = new Book { Id = 1, Title = "Saga Vol. 1", Author = author };

            var result = ReleaseTitleMatchScorer.FindBestMatch("Jane Writer Saga Vol.01.2019 EPUB", author.Name, new[] { target }, null, new[] { target });

            Assert.That(result?.IsMatch, Is.True);
        }
    }
}
