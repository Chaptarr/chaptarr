using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleMatchScorerNumericTokenFixture
    {
        [Test]
        public void should_not_fuse_a_number_with_a_following_dotted_scene_year()
        {
            var tokens = ReleaseTitleMatchScorer.Tokenize("Fahrenheit.451.2018.RETAIL.EPUB");

            Assert.That(tokens, Does.Contain("451"));
            Assert.That(tokens, Does.Contain("2018"));
            Assert.That(tokens, Does.Not.Contain("451.2018"));
        }

        [Test]
        public void should_keep_genuine_decimal_positions_fused()
        {
            var tokens = ReleaseTitleMatchScorer.Tokenize("Series Book 13.5 Novella");

            Assert.That(tokens, Does.Contain("13.5"));
        }

        [Test]
        public void should_match_zero_padded_dotted_release_against_unpadded_catalog_title()
        {
            var author = new Author { Name = "Jane Doe" };
            var book = new Book
            {
                Title = "Example Saga, Vol. 1",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Example Saga, Vol. 1", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Jane.Doe.Example.Saga.Vol.01.2019.RETAIL.EPUB",
                "Jane Doe",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
        }

        [Test]
        public void should_not_equate_different_numbers()
        {
            var author = new Author { Name = "Jane Doe" };
            var book = new Book
            {
                Title = "Example Saga, Vol. 5",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Example Saga, Vol. 5", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Jane.Doe.Example.Saga.Vol.03.2019.RETAIL.EPUB",
                "Jane Doe",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result == null || !result.IsMatch, Is.True);
        }
    }
}
