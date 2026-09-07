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
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("Astorias Smutty Smut 🌶️"), Is.EqualTo("astorias smutty smut"));
        }

        [Test]
        public void normalize_words_should_treat_a_symbol_outside_the_bmp_as_a_word_boundary()
        {
            // A supplementary symbol separates words just like a BMP symbol.
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords("ab🔥cd"), Is.EqualTo("ab cd"));
        }

        [Test]
        public void normalize_key_should_not_throw_on_unpaired_surrogate()
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey("Broken \uD800 Title"), Is.EqualTo("brokentitle"));
        }

        [TestCase(0xD800)]
        [TestCase(0xDC00)]
        public void malformed_utf16_should_preserve_following_character_offsets(int codeUnit)
        {
            var text = "a" + (char)codeUnit + "🌶️b";
            var result = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(text, null);

            Assert.That(result.Text, Is.EqualTo("a b"));
            Assert.That(result.SourceSpans, Is.EqualTo(new[] { (0, 1), (1, 2), (5, 6) }));
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(text), Is.EqualTo(result.Text));
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(text), Is.EqualTo("ab"));
        }

        [Test]
        public void supplementary_letter_should_preserve_both_original_source_spans()
        {
            var result = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans("a𠀀b", new[] { (10, 11), (11, 12), (12, 13), (13, 14) });

            Assert.That(result.Text, Is.EqualTo("a𠀀b"));
            Assert.That(result.SourceSpans, Is.EqualTo(new[] { (10, 11), (11, 13), (11, 13), (13, 14) }));
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

            var result = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(text, sourceSpans);
            Assert.That(result.Text, Is.EqualTo("broken title"));
            Assert.That(result.SourceSpans, Has.Count.EqualTo(result.Text.Length));
        }
    }
}
