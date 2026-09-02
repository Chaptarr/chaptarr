using System.Collections.Generic;
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

        [Test]
        public void normalize_words_should_preserve_supplementary_plane_letters()
        {
            // U+20000 is a CJK Extension B ideograph - a real letter outside the Basic Multilingual Plane,
            // represented in .NET as a surrogate pair (two UTF-16 code units).
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("Title 𠀀 Word"), Is.EqualTo("title 𠀀 word"));
        }

        [Test]
        public void normalize_words_should_not_throw_on_emoji()
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("Fourth Wing 🔥 Rebecca Yarros"), Is.EqualTo("fourth wing rebecca yarros"));
        }

        [Test]
        public void normalize_words_should_treat_a_symbol_outside_the_bmp_as_a_word_boundary()
        {
            // Before this fix, a supplementary-plane character sitting directly between two letters
            // (no surrounding whitespace) was silently dropped without inserting a word boundary,
            // e.g. "ab🔥cd" -> "abcd", because each half of its surrogate pair was individually
            // classified as UnicodeCategory.Surrogate rather than as the symbol it actually represents.
            // Classifying it correctly as a symbol now makes it act as a separator, like any other
            // punctuation/symbol character.
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("ab🔥cd"), Is.EqualTo("ab cd"));
        }

        [Test]
        public void normalize_key_should_not_throw_on_unpaired_surrogate()
        {
            Assert.DoesNotThrow(() => UnicodeComparisonNormalizer.NormalizeKey("Broken \uD800 Title"));
        }

        [Test]
        public void normalize_words_with_source_spans_should_merge_the_source_span_of_a_surrogate_pair()
        {
            var text = "ab🔥cd";
            var sourceSpans = new List<(int Start, int End)>();
            for (var i = 0; i < text.Length; i++)
            {
                sourceSpans.Add((i, i + 1));
            }

            var result = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(text, sourceSpans);

            Assert.That(result.Text, Is.EqualTo("ab cd"));
            Assert.That(result.SourceSpans, Has.Count.EqualTo(result.Text.Length));

            // "ab" (0,1)(1,2), the synthetic separator space takes the emoji's merged (2,4) span, then "cd" (4,5)(5,6).
            Assert.That(result.SourceSpans, Is.EqualTo(new List<(int Start, int End)>
            {
                (0, 1), (1, 2), (2, 4), (4, 5), (5, 6)
            }));
        }

        [Test]
        public void normalize_words_with_source_spans_should_not_throw_on_unpaired_surrogate()
        {
            var text = "Broken \uD800 Title";
            var sourceSpans = new List<(int Start, int End)>();
            for (var i = 0; i < text.Length; i++)
            {
                sourceSpans.Add((i, i + 1));
            }

            Assert.DoesNotThrow(() => UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(text, sourceSpans));
        }
    }
}
