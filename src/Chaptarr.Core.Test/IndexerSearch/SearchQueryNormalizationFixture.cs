using NUnit.Framework;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Chaptarr.Core.Test.IndexerSearch
{
    [TestFixture]
    public class SearchQueryNormalizationFixture
    {
        [TestCase("Renée Ballard", "Renee+Ballard")]
        [TestCase("Rene\u0301e Ballard", "Renee+Ballard")]
        [TestCase("J. K. Rowling", "J+K+Rowling")]
        [TestCase("John A Smith", "John+A+Smith")]
        [TestCase("The Story of War", "Story+of+War")]
        [TestCase("Dune 42", "Dune+42")]
        [TestCase("00123", "00123")]
        [TestCase("23½ Lies", "23+Lies")]
        [TestCase("Dune Ⅱ", "Dune")]
        [TestCase("がとう 読者", "がとう+読者")]
        [TestCase("か\u3099とう 読者", "がとう+読者")]
        public void indexer_query_should_share_character_folding_after_existing_transport_cleanup(string title, string expected)
        {
            Assert.That(SearchCriteriaBase.GetQueryTitle(title), Is.EqualTo(expected));
        }
    }
}
