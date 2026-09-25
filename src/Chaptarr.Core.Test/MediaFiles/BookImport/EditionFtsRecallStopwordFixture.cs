using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class EditionFtsRecallStopwordFixture
    {
        [Test]
        public void common_function_words_should_not_be_used_as_recall_keys()
        {
            var kept = EditionFtsRepository.DropRecallStopwords(new List<string> { "the", "hobbit", "and", "there", "and", "back", "again", "of", "1" });

            Assert.That(kept, Is.EqualTo(new[] { "hobbit", "there", "back", "again", "1" }));
        }

        [Test]
        public void series_words_and_digits_should_be_kept()
        {
            var kept = EditionFtsRepository.DropRecallStopwords(new List<string> { "part", "book", "volume", "2" });

            Assert.That(kept, Is.EqualTo(new[] { "part", "book", "volume", "2" }));
        }

        [Test]
        public void a_title_made_only_of_stopwords_should_keep_its_terms()
        {
            var terms = new List<string> { "the", "and" };

            Assert.That(EditionFtsRepository.DropRecallStopwords(terms), Is.EqualTo(terms));
        }

        [Test]
        public void stopwords_should_match_regardless_of_case()
        {
            Assert.That(EditionFtsRepository.DropRecallStopwords(new List<string> { "The", "AND", "Odyssey" }), Is.EqualTo(new[] { "Odyssey" }));
        }
    }
}
