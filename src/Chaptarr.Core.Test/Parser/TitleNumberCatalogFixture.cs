using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class TitleNumberCatalogFixture
    {
        [TestCase("two", "eng", 2)]
        [TestCase("2", "ita", 2)]
        [TestCase("ii", "und", 2)]
        [TestCase("Ⅱ", null, 2)]
        public void should_parse_supported_number_forms_without_language_leakage(string token, string language, decimal expected)
        {
            var matches = TitleNumberCatalog.GetNumberMatches(new[] { token }, 0, language).ToArray();

            Assert.That(matches, Has.Length.EqualTo(1));
            Assert.That(matches[0].TokenCount, Is.EqualTo(1));
            Assert.That(matches[0].Value, Is.EqualTo(expected));
        }

        [TestCase("ita")]
        [TestCase("und")]
        [TestCase(null)]
        public void should_not_parse_english_cardinals_for_other_or_unknown_languages(string language)
        {
            Assert.That(TitleNumberCatalog.GetNumberMatches(new[] { "two" }, 0, language), Is.Empty);
        }

        [Test]
        public void should_consume_a_compound_cardinal_as_one_longest_span()
        {
            var matches = TitleNumberCatalog.GetNumberMatches(new[] { "twenty", "one" }, 0, "eng").ToArray();

            Assert.That(matches, Has.Length.EqualTo(1));
            Assert.That(matches[0].TokenCount, Is.EqualTo(2));
            Assert.That(matches[0].Value, Is.EqualTo(21));
        }

        [TestCase("first")]
        [TestCase("second")]
        [TestCase("twentyfirst")]
        public void should_not_parse_ordinals_or_cardinal_substrings(string token)
        {
            Assert.That(TitleNumberCatalog.GetNumberMatches(new[] { token }, 0, "eng"), Is.Empty);
        }

        [Test]
        public void should_expand_only_a_whole_english_cardinal_phrase()
        {
            var alternatives = TitleNumberCatalog.GetLookupAlternatives("twenty one", "eng");
            var longerPhrase = TitleNumberCatalog.GetLookupAlternatives("twenty onefold", "eng");

            Assert.That(alternatives, Does.Contain("21"));
            Assert.That(alternatives, Does.Contain("xxi"));
            Assert.That(alternatives, Does.Contain("twenty one"));
            Assert.That(longerPhrase, Is.EqualTo(new[] { "twenty onefold" }));
        }

        [Test]
        public void should_expand_numeric_and_roman_forms_without_cardinals_for_unsupported_languages()
        {
            var alternatives = TitleNumberCatalog.GetLookupAlternatives("II", "ita");

            Assert.That(alternatives, Does.Contain("2"));
            Assert.That(alternatives, Does.Contain("ii"));
            Assert.That(alternatives, Does.Not.Contain("two"));
        }

        [Test]
        public void should_leave_non_integral_numbers_as_numeric_literals()
        {
            var matches = TitleNumberCatalog.GetNumberMatches(new[] { "2.5" }, 0, "eng").ToArray();
            var alternatives = TitleNumberCatalog.GetLookupAlternatives("2.5", "eng");

            Assert.That(matches, Has.Length.EqualTo(1));
            Assert.That(matches[0].Value, Is.EqualTo(2.5m));
            Assert.That(alternatives, Is.EqualTo(new[] { "2.5" }));
        }

        [Test]
        public void should_preserve_the_series_position_roman_output_boundary()
        {
            Assert.That(SeriesPositionTokenHelper.ToRomanNumeral(39), Is.EqualTo("xxxix"));
            Assert.That(SeriesPositionTokenHelper.ToRomanNumeral(40), Is.Null);
            Assert.That(SeriesPositionTokenHelper.GetPositionTokens("40"), Does.Not.Contain("xl"));
        }

        [TestCase("Wild Cards VII", "Wild Cards 7")]
        [TestCase("Wild Cards 7", "Wild Cards VII")]
        [TestCase("Wild Cards Ⅶ", "Wild Cards ７")]
        [TestCase("Vi", "6")]
        [TestCase("المجلد VII", "المجلد 7")]
        public void roman_fallback_is_comparison_only_and_language_independent(string title, string observed)
        {
            var expectedTokens = ReleaseTitleMatchScorer.Tokenize(title);
            var observedTokens = ReleaseTitleMatchScorer.Tokenize(observed);
            var beforeExpected = expectedTokens.ToArray();
            var beforeObserved = observedTokens.ToArray();

            Assert.That(ReleaseTitleMatchScorer.FindNumberEquivalentTitleSpans(observedTokens, expectedTokens, null),
                Does.Contain((0, observedTokens.Count - 1)));
            Assert.That(ReleaseTitleMatchScorer.FindExactTitleSpans(expectedTokens, expectedTokens),
                Does.Contain((0, expectedTokens.Count - 1)));
            Assert.That(expectedTokens, Is.EqualTo(beforeExpected));
            Assert.That(observedTokens, Is.EqualTo(beforeObserved));
        }

        [TestCase("VII", "6")]
        [TestCase("VI", "7")]
        [TestCase("VII", "VIIa")]
        [TestCase("Vi", "Violet")]
        public void numeric_fallback_does_not_erase_different_numbers_or_match_partial_words(string title, string observed)
        {
            Assert.That(ReleaseTitleMatchScorer.FindNumberEquivalentTitleSpans(
                ReleaseTitleMatchScorer.Tokenize(observed), ReleaseTitleMatchScorer.Tokenize(title), null), Is.Empty);
        }
    }
}
