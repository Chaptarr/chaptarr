using NUnit.Framework;
using NzbDrone.Core.Utilities;

namespace Chaptarr.Core.Test.Utilities
{
    [TestFixture]
    public class UnicodeComparisonNormalizerFixture
    {
        [TestCase("Renée Ballard", "reneeballard")]
        [TestCase("Gabriel García Márquez", "gabrielgarciamarquez")]
        [TestCase("J. K. Rowling", "jkrowling")]
        public void normalize_key_should_fold_diacritics_without_losing_letters(string input, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(expected));
        }

        [Test]
        public void normalize_key_should_preserve_non_latin_scripts()
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey("村上 春樹"), Is.EqualTo("村上春樹"));
        }

        [Test]
        public void normalize_words_should_preserve_word_boundaries_for_non_latin_scripts()
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("Renée Ballard"), Is.EqualTo("renee ballard"));
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("村上 春樹"), Is.EqualTo("村上 春樹"));
        }
    }
}
