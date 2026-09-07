using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NzbDrone.Core.Utilities;

namespace Chaptarr.Core.Test.Utilities
{
    [TestFixture]
    public class UnicodeComparisonNormalizerFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void word_spans_should_preserve_existing_bmp_punctuation_rules(bool connectors, bool possessive)
        {
            var pattern = connectors ? @"[\p{L}\p{Nd}]+(?:[-\.][\p{L}\p{Nd}]+)*"
                : possessive ? @"[\p{L}\p{Nd}]+(?:['\u2018\u2019][sS])?" : @"[\p{L}\p{Nd}]+";
            foreach (var value in new[] { "Renée’s high-opp 125", "a..b -a- a.-b a-.b a--b", "Fred'son Fred’sS", "村上 春樹 العربية", "élève & book 🌶️" })
            {
                var expected = Regex.Matches(value, pattern).Select(m => (m.Value, m.Index, m.Length));
                Assert.That(UnicodeComparisonNormalizer.WordSpans(value, connectors, possessive), Is.EqualTo(expected), value);
            }
        }

        [TestCase("Astorias𠀀Smut", "Astorias𠀀Smut")]
        [TestCase("Astorias𠀁Smut", "Astorias𠀁Smut")]
        [TestCase("𠀀", "𠀀")]
        [TestCase("𐐀𐐨 𝟙𝟚", "𐐀𐐨|𝟙𝟚")]
        [TestCase("ab🌶️cd", "ab|cd")]
        [TestCase("ab\uD800cd\uDC00ef", "ab|cd|ef")]
        public void word_spans_should_classify_whole_characters_and_keep_source_offsets(string input, string expected)
        {
            var spans = UnicodeComparisonNormalizer.WordSpans(input).ToList();
            Assert.That(string.Join("|", spans.Select(s => s.Value)), Is.EqualTo(expected));
            foreach (var span in spans)
            {
                Assert.That(input.Substring(span.Index, span.Length), Is.EqualTo(span.Value));
            }
        }

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

        [TestCase("Ready Player 𝟚", "ready player 2")]
        [TestCase("Ready Player ٢", "ready player 2")]
        [TestCase("Ready Player ２", "ready player 2")]
        [TestCase("Ready Player ②", "ready player 2")]
        [TestCase("Ready Player ²", "ready player 2")]
        [TestCase("Ready Player Ⅱ", "ready player 2")]
        [TestCase("Ready Player Two", "ready player 2")]
        [TestCase("The Thirty-Nine Steps", "the 39 steps")]
        [TestCase("A Memory of Light ⅩⅣ", "a memory of light 14")]
        [TestCase("I, Robot", "i robot")]
        [TestCase("1Q84", "1q84")]
        [TestCase("一九八四", "一九八四")]
        [TestCase("The First Law", "the first law")]
        public void numeric_title_forms_should_share_one_comparison_representation(string input, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(expected.Replace(" ", "")));
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, null);
            Assert.That(mapped.Text, Is.EqualTo(expected));
            Assert.That(mapped.SourceSpans.Count, Is.EqualTo(mapped.Text.Length));
            Assert.That(mapped.SourceSpans.All(span => span.Start >= 0 && span.End <= input.Length && span.End > span.Start), Is.True);
        }

        [Test]
        public void expanded_numeric_glyph_should_retain_its_original_source_span()
        {
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans("A Memory of Light ⅩⅣ", null);
            Assert.That(mapped.SourceSpans.TakeLast(2), Is.EqualTo(new[] { (18, 20), (18, 20) }));
        }


        [TestCase("Rene\u0301e Ahdieh", "Rene\u0301e|Ahdieh")]
        [TestCase("हिन्दी", "हिन्दी")]
        public void combining_marks_should_stay_with_their_word(string text, string expected)
        {
            Assert.That(string.Join("|", UnicodeComparisonNormalizer.WordSpans(text).Select(span => span.Value)), Is.EqualTo(expected));
        }

        [Test]
        public void trailing_combining_mark_should_belong_to_the_mapped_character()
        {
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans("Café".Normalize(System.Text.NormalizationForm.FormD), null);
            Assert.That(mapped.Text, Is.EqualTo("cafe"));
            Assert.That(mapped.SourceSpans[3], Is.EqualTo((3, 5)));
        }

        [TestCase("Ready Player Twó")]
        [TestCase("Óne Flew Over the Cuckoo's Nest")]
        public void numeric_word_folding_should_be_idempotent_with_diacritics(string input)
        {
            var once = UnicodeComparisonNormalizer.NormalizeText(input);
            Assert.That(UnicodeComparisonNormalizer.NormalizeText(once), Is.EqualTo(once));
            Assert.That(once, Does.Not.Contain("two").And.Not.Contain("one"));
        }

        [TestCase("The 7 ½ Deaths of Evelyn Hardcastle", "the 7.5 deaths of evelyn hardcastle")]
        [TestCase("The 7½ Deaths of Evelyn Hardcastle", "the 7.5 deaths of evelyn hardcastle")]
        [TestCase("23 ½ Lies", "23.5 lies")]
        [TestCase("23 ½.Lies", "23.5 lies")]
        [TestCase("23 1/2 Lies", "23.5 lies")]
        [TestCase("２３ １/２ Lies", "23.5 lies")]
        [TestCase("Dune 3 3/4", "dune 3.75")]
        [TestCase("Dune 3 1/3", "dune 3.333333333333333")]
        [TestCase("Dune 3 ⅓", "dune 3.333333333333333")]
        [TestCase("Dune twenty one ½", "dune 21.5")]
        [TestCase("Dune Ⅳ ½", "dune 4.5")]
        [TestCase("Dune 3½", "dune 3.5")]
        [TestCase("Dune 13½", "dune 13.5")]
        [TestCase("Dune Ⅳ½", "dune 4.5")]
        public void mixed_fraction_should_not_become_a_different_whole_number(string input, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(expected), Is.EqualTo(expected));
        }

        [Test]
        public void mixed_fraction_should_preserve_source_span_and_compact_key()
        {
            const string input = "23 ½ Lies";
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, null);
            Assert.That(mapped.Text, Is.EqualTo("23.5 lies"));
            Assert.That(mapped.SourceSpans.Take(4), Is.All.EqualTo((0, 4)));
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo("235lies"));
        }

        [TestCase("79228162514264337593543950335 1/2", "79228162514264337593543950335 1/2")]
        [TestCase("1/2", "1/2")]
        [TestCase("Dune 1/2/2026", "dune 1/2/2026")]
        [TestCase("Dune 23 1/2/2026", "dune 23 1/2/2026")]
        [TestCase("Dune 23 1/2.2026", "dune 23 1/2.2026")]
        [TestCase("Dune 23 1/2a", "dune 23 1/2a")]
        [TestCase("Dune 23 1/0", "dune 23 1/0")]
        [TestCase("Dune 23 2/1", "dune 23 2/1")]
        public void slash_notation_outside_a_proper_mixed_fraction_should_remain_literal(string input, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeText(input), Is.EqualTo(expected));
        }

    }
}
