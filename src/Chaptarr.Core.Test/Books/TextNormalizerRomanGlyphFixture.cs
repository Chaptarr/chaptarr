using NUnit.Framework;
using NzbDrone.Core.Books.Services;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class TextNormalizerRomanGlyphFixture
    {
        [TestCase("II Alpha", "Ⅱ Alpha", "ii alpha")]
        [TestCase("Alpha II Beta", "Alpha Ⅱ Beta", "alpha 2 beta")]
        [TestCase("Alpha II", "Alpha Ⅱ", "alpha ii")]
        public void unicode_roman_glyphs_should_match_bounded_ascii_roman_normalization(string asciiTitle, string glyphTitle, string expected)
        {
            var asciiTokens = TextNormalizer.NormalizeAndTokenize(asciiTitle);
            var glyphTokens = TextNormalizer.NormalizeAndTokenize(glyphTitle);

            Assert.That(string.Join(" ", asciiTokens), Is.EqualTo(expected));
            Assert.That(glyphTokens, Is.EqualTo(asciiTokens));
        }

        [TestCase("MIX", "ⅯⅨ", "mix")]
        [TestCase("XIII", "ⅩⅢ", "xiii")]
        public void unicode_roman_glyph_runs_should_not_expand_the_existing_ascii_number_range(string asciiTitle, string glyphTitle, string expected)
        {
            var asciiTokens = TextNormalizer.NormalizeAndTokenize(asciiTitle);
            var glyphTokens = TextNormalizer.NormalizeAndTokenize(glyphTitle);

            Assert.That(string.Join(" ", asciiTokens), Is.EqualTo(expected));
            Assert.That(glyphTokens, Is.EqualTo(asciiTokens));
        }
    }
}
