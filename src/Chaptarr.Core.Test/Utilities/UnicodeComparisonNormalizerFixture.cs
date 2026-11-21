using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

namespace Chaptarr.Core.Test.Utilities
{
    [TestFixture]
    public class UnicodeComparisonNormalizerFixture
    {
        [Test]
        public void valid_unicode_scalars_should_never_throw_during_comparison_normalization()
        {
            for (var point = 0; point <= 0x10FFFF; point++)
            {
                if (Rune.IsValid(point))
                {
                    UnicodeComparisonNormalizer.NormalizeText("A" + new Rune(point) + "B");
                }
            }
        }

        [Test]
        public void noncharacters_should_reuse_existing_punctuation_and_word_boundaries()
        {
            var points = Enumerable.Range(0xFDD0, 32).Concat(Enumerable.Range(0, 17)
                .SelectMany(plane => new[] { (plane << 16) | 0xFFFE, (plane << 16) | 0xFFFF }));
            foreach (var point in points)
            {
                var character = new Rune(point).ToString();
                var input = "A" + character + "B";
                var expectedSpans = new[] { (0, 1) }.Concat(Enumerable.Repeat((1, 1 + character.Length), character.Length))
                    .Append((1 + character.Length, 2 + character.Length));
                Assert.Multiple(() =>
                {
                    Assert.That(StringSuperNormalizer.ComputeMatchingTitle(input), Is.EqualTo("a" + character + "b"));
                    Assert.That(input.CleanAuthorName(), Is.EqualTo("ab"));
                    Assert.That(input.CleanNarratorName(), Is.EqualTo("ab"));
                    Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo("a b"));
                    Assert.That(UnicodeComparisonNormalizer.NormalizeText(input, preserveCase: true), Is.EqualTo(input));
                    Assert.That(UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(input).SourceSpans, Is.EqualTo(expectedSpans));
                });
            }
        }

        [Test]
        public void noncharacter_mark_sequences_should_preserve_aggregated_source_spans()
        {
            const string input = "A\uFFFE\u0301\u093EB";
            var text = UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(input);
            var supplied = Enumerable.Range(0, input.Length).Select(index => (index * 2, index * 2 + 2)).ToList();
            var words = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, supplied);
            Assert.Multiple(() =>
            {
                Assert.That(text.Text, Is.EqualTo("a\uFFFE\u093Eb"));
                Assert.That(text.SourceSpans, Is.EqualTo(new[] { (0, 1), (1, 4), (1, 4), (4, 5) }));
                Assert.That(words.Text, Is.EqualTo("a b"));
                Assert.That(words.SourceSpans, Is.EqualTo(new[] { (0, 2), (2, 8), (8, 10) }));
            });
        }

        [Test]
        public void circled_capitals_should_keep_the_existing_nonletter_lowercase_policy()
        {
            foreach (var point in Enumerable.Range(0x24B6, 26))
            {
                var upper = new Rune(point).ToString();
                var lower = Rune.ToLowerInvariant(new Rune(point)).ToString();
                Assert.Multiple(() =>
                {
                    Assert.That(StringSuperNormalizer.ComputeMatchingTitle(upper), Is.EqualTo(lower));
                    Assert.That(StringSuperNormalizer.ComputeMatchingTitle(lower), Is.EqualTo(lower));
                    Assert.That(UnicodeComparisonNormalizer.NormalizeText(upper, preserveCase: true), Is.EqualTo(upper));
                    Assert.That(upper.CleanAuthorName(), Is.Empty);
                });
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void word_spans_should_preserve_existing_bmp_punctuation_rules(bool connectors)
        {
            var pattern = connectors ? @"[\p{L}\p{Nd}]+(?:[-\.][\p{L}\p{Nd}]+)*"
                : @"[\p{L}\p{Nd}]+";
            foreach (var value in new[] { "Renée’s high-opp 125", "a..b -a- a.-b a-.b a--b", "Fred'son Fred’sS", "村上 春樹 العربية", "élève & book 🌶️" })
            {
                var expected = Regex.Matches(value, pattern).Select(m => (m.Value, m.Index, m.Length));
                Assert.That(UnicodeComparisonNormalizer.WordSpans(value, connectors), Is.EqualTo(expected), value);
            }
        }

        [TestCase("Fred's", "Fred's")]
        [TestCase("Fred’s", "Fred’s")]
        [TestCase("Fred‘s", "Fred‘s")]
        [TestCase("Fredʼs", "Fredʼs")]
        [TestCase("Fred'son", "Fred|son")]
        [TestCase("O'Sullivan", "O|Sullivan")]
        [TestCase("Fred’sS", "Fred|sS")]
        public void possessive_highlights_require_the_shared_word_boundary(string value, string expected)
        {
            Assert.That(string.Join("|", UnicodeComparisonNormalizer.WordSpans(value, includePossessive: true)
                .Select(span => span.Value)), Is.EqualTo(expected));
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

        [TestCase("Nguyễn", "nguyen")]
        [TestCase("Nguye\u0302\u0303n", "nguyen")]
        [TestCase("がとう", "がとう")]
        [TestCase("か\u3099とう", "がとう")]
        [TestCase("हिन्दी", "हिन्दी")]
        [TestCase("بِ", "بِ")]
        [TestCase("Й", "й")]
        [TestCase("И\u0306", "й")]
        [TestCase("ά", "α")]
        [TestCase("α\u0301", "α")]
        [TestCase("ᴦ\u0301", "ᴦ")]
        [TestCase("𐐀\u0301", "𐐨")]
        [TestCase("𠀀\U000E0100", "𠀀\U000E0100")]
        [TestCase("ø\u0301", "ǿ")]
        [TestCase("ǿ", "ǿ")]
        public void every_comparison_form_should_use_the_same_fold_and_preserve_unlisted_marks(string input, string expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeText(input), Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, null).Text, Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(input).Text, Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeWords(expected), Is.EqualTo(expected));
            });
        }

        [TestCase("がとう", "かとう")]
        [TestCase("हिन्दी", "हन्द")]
        [TestCase("بِ", "ب")]
        [TestCase("Й", "И")]
        [TestCase("ї", "і")]
        [TestCase("Ў", "У")]
        [TestCase("ѓ", "г")]
        [TestCase("เก้า", "เกา")]
        [TestCase("हाँ", "हा")]
        [TestCase("ग़", "ग")]
        public void unlisted_script_distinctions_should_remain_identity_evidence(string marked, string unmarked)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(marked),
                Is.Not.EqualTo(UnicodeComparisonNormalizer.NormalizeKey(unmarked)));
        }

        [Test]
        public void preserved_combining_sequences_should_keep_supplied_utf16_source_spans()
        {
            const string input = "Aか\u3099𠀀\u0301B";
            var supplied = Enumerable.Range(0, input.Length).Select(index => (index * 2, index * 2 + 2)).ToList();
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, supplied);

            Assert.That(mapped.Text, Is.EqualTo("aが𠀀b"));
            Assert.That(mapped.SourceSpans, Is.EqualTo(new[]
            {
                (0, 2), (2, 6), (6, 12), (6, 12), (12, 14)
            }));
        }

        [TestCase("Søren", "soren")]
        [TestCase("Łódź", "lodz")]
        [TestCase("Straße", "strasse")]
        [TestCase("Ægir", "aegir")]
        [TestCase("Ʀ", "r")]
        [TestCase("ʀ", "r")]
        [TestCase("Ё", "е")]
        [TestCase("b\u0327", "b")]
        [TestCase("ǽ", "ǽ")]
        [TestCase("가", "가")]
        [TestCase("각", "각")]
        public void maintained_letter_folding_should_match_in_every_comparison_form(string input, string expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeText(input), Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, null).Text, Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(input).Text, Is.EqualTo(expected));
            });
        }

        [TestCase("Powerless James Hunt", "Powerless James Hunt")]
        [TestCase("Renée Ballard", "Renee Ballard")]
        [TestCase("Rene\u0301e Ballard", "Renee Ballard")]
        [TestCase("É Æ ẞ Ʀ", "E AE SS R")]
        [TestCase("é æ ß ʀ", "e ae ss r")]
        [TestCase("ǅ ǈ ǋ ǲ", "Dz Lj Nj Dz")]
        [TestCase("J. K. Rowling", "J. K. Rowling")]
        [TestCase("𐐀 𐐨 がとう か\u3099とう", "𐐀 𐐨 がとう がとう")]
        [TestCase("각", "각")]
        [TestCase("Dune Ⅱ ⅱ 23½ ٢½", "Dune II ii 23.5 2.5")]
        [TestCase("A\u0327", "A")]
        public void text_output_should_optionally_retain_casing_through_the_shared_fold(string input, string expected)
        {
            var folded = UnicodeComparisonNormalizer.NormalizeText(input, preserveCase: true);
            Assert.Multiple(() =>
            {
                Assert.That(folded, Is.EqualTo(expected));
                Assert.That(UnicodeComparisonNormalizer.NormalizeText(folded, preserveCase: true), Is.EqualTo(folded));
                Assert.That(UnicodeComparisonNormalizer.NormalizeText(input), Is.EqualTo(UnicodeComparisonNormalizer.NormalizeText(input, preserveCase: false)));
            });
        }

        [Test]
        public void canonical_grapheme_composition_and_expansion_should_preserve_supplied_source_spans()
        {
            const string input = "A각ßB";
            var supplied = Enumerable.Range(0, input.Length).Select(index => (index * 2, index * 2 + 2)).ToList();
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, supplied);

            Assert.That(mapped.Text, Is.EqualTo("a각ssb"));
            Assert.That(mapped.SourceSpans, Is.EqualTo(new[] { (0, 2), (2, 8), (8, 10), (8, 10), (10, 12) }));
        }

        [TestCase("aʼb", "a'b", "a b", "ab")]
        [TestCase("aːb", "a:b", "a b", "ab")]
        [TestCase("ŉ", "'n", "n", "n")]
        public void letter_replacements_containing_punctuation_should_keep_existing_punctuation_rules(string input, string text, string words, string key)
        {
            Assert.Multiple(() =>
            {
                Assert.That(UnicodeComparisonNormalizer.NormalizeText(input), Is.EqualTo(text));
                Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(words));
                Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(key));
            });
        }

        [TestCase("1\u20E3", "1\u20E3")]
        [TestCase("٢\u20E3", "2\u20E3")]
        [TestCase("a©b", "a b")]
        [TestCase("a±b", "a b")]
        public void imported_letter_data_should_not_replace_number_marks_or_symbols(string input, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
        }

        [Test]
        public void generated_data_should_retain_its_pinned_fingerprint_and_only_letter_mark_inputs()
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var codepointBytes = new byte[4];
            foreach (var (codepoint, replacement) in UnicodeComparisonFoldData.Replacements.OrderBy(pair => pair.Key))
            {
                var rune = new Rune(codepoint);
                Assert.That(Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark,
                    Is.True, $"U+{codepoint:X}");
                BinaryPrimitives.WriteInt32BigEndian(codepointBytes, codepoint);
                digest.AppendData(codepointBytes);
                digest.AppendData(Encoding.UTF8.GetBytes(replacement));
                digest.AppendData(new byte[] { 0 });
            }

            Assert.Multiple(() =>
            {
                Assert.That(UnicodeComparisonFoldData.Replacements.Count, Is.EqualTo(3174));
                Assert.That(UnicodeComparisonFoldData.MappingCount, Is.EqualTo(3174));
                Assert.That(UnicodeComparisonFoldData.ClosedMappingCount, Is.EqualTo(9));
                Assert.That(Convert.ToHexStringLower(digest.GetHashAndReset()), Is.EqualTo(UnicodeComparisonFoldData.DataSHA256));
                Assert.That(UnicodeComparisonFoldData.SourceSHA256, Is.EqualTo("5e71838f9bc555fdd2f9da151bdc99138d7df6b550b457cc6a75ddb65951d00e"));
            });
        }

        [Test]
        public void every_generated_mapping_should_follow_the_shared_character_contract()
        {
            foreach (var (codepoint, replacement) in UnicodeComparisonFoldData.Replacements)
            {
                var rune = new Rune(codepoint);
                var prefix = Rune.IsLetter(rune) ? string.Empty : "b";
                var text = UnicodeComparisonNormalizer.NormalizeText(prefix + rune);
                var casePreservedText = UnicodeComparisonNormalizer.NormalizeText(prefix + rune, preserveCase: true);
                var words = UnicodeComparisonNormalizer.NormalizeWords(prefix + rune);
                var key = UnicodeComparisonNormalizer.NormalizeKey(prefix + rune);
                Assert.Multiple(() =>
                {
                    Assert.That(text, Is.EqualTo(UnicodeComparisonNormalizer.NormalizeText(prefix + replacement)), $"U+{codepoint:X}");
                    Assert.That(UnicodeComparisonNormalizer.NormalizeText(text), Is.EqualTo(text), $"U+{codepoint:X}");
                    Assert.That(UnicodeComparisonNormalizer.NormalizeText(casePreservedText, preserveCase: true), Is.EqualTo(casePreservedText), $"U+{codepoint:X}");
                    Assert.That(UnicodeComparisonNormalizer.NormalizeWords(words), Is.EqualTo(words), $"U+{codepoint:X}");
                    Assert.That(UnicodeComparisonNormalizer.NormalizeKey(key), Is.EqualTo(key), $"U+{codepoint:X}");
                });
            }
        }

        [Test]
        public void all_defined_letters_should_compare_equally_in_canonical_encodings()
        {
            var mismatches = new List<string>();
            for (var codepoint = 0; codepoint <= 0x10ffff; codepoint++)
            {
                if (!Rune.IsValid(codepoint) || !Rune.IsLetter(new Rune(codepoint)))
                {
                    continue;
                }

                var canonical = new Rune(codepoint).ToString();
                var decomposed = canonical.Normalize(NormalizationForm.FormD);
                if (canonical == decomposed)
                {
                    continue;
                }

                if (UnicodeComparisonNormalizer.NormalizeKey(canonical) != UnicodeComparisonNormalizer.NormalizeKey(decomposed) ||
                    UnicodeComparisonNormalizer.NormalizeWords(canonical) != UnicodeComparisonNormalizer.NormalizeWords(decomposed) ||
                    UnicodeComparisonNormalizer.NormalizeText(canonical) != UnicodeComparisonNormalizer.NormalizeText(decomposed) ||
                    UnicodeComparisonNormalizer.NormalizeText(canonical, preserveCase: true) != UnicodeComparisonNormalizer.NormalizeText(decomposed, preserveCase: true) ||
                    UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(canonical, null).Text != UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(decomposed, null).Text ||
                    UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(canonical).Text != UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(decomposed).Text)
                {
                    mismatches.Add($"U+{codepoint:X}");
                }
            }

            Assert.That(mismatches, Is.Empty);
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

        [TestCase("II", true, 2)]
        [TestCase("XIV", true, 14)]
        [TestCase("MIX", true, 1009)]
        [TestCase("VI", true, 6)]
        [TestCase("DL", true, 550)]
        [TestCase("DVD", false, 0)]
        [TestCase("MID", false, 0)]
        [TestCase("Mild", false, 0)]
        [TestCase("Civil", false, 0)]
        [TestCase("IL", false, 0)]
        [TestCase("IM", false, 0)]
        [TestCase("", false, 0)]
        public void roman_number_parser_should_accept_only_canonical_forms(string raw, bool expectedSuccess, int expectedValue)
        {
            var success = UnicodeComparisonNormalizer.TryParseRomanNumeral(raw, out var value);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.EqualTo(expectedSuccess));
                Assert.That(value, Is.EqualTo(expectedValue));
            });
        }

        [TestCase("VI", "vi")]
        [TestCase("MIX", "mix")]
        [TestCase("Book VI", "book vi")]
        public void ascii_roman_words_should_remain_ordinary_title_tokens(string title, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(title), Is.EqualTo(expected));
        }

        [TestCase("Ⅱ", "ii")]
        [TestCase("ⅮⅤⅮ", "dvd")]
        [TestCase("ⅯⅠⅮ", "mid")]
        [TestCase("ⅮⅬ", "dl")]
        [TestCase("Ⅵ", "vi")]
        [TestCase("ⅩⅣ", "xiv")]
        public void unicode_roman_sequences_should_normalize_as_letter_tokens(string title, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(title), Is.EqualTo(expected));
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
        [TestCase("Ready Player Ⅱ", "ready player ii")]
        [TestCase("Ready Player Two", "ready player two")]
        [TestCase("The Thirty-Nine Steps", "the thirty nine steps")]
        [TestCase("One Flew Over the Cuckoo’s Nest", "one flew over the cuckoo s nest")]
        [TestCase("Un mundo feliz", "un mundo feliz")]
        [TestCase("A Memory of Light ⅩⅣ", "a memory of light xiv")]
        [TestCase("I, Robot", "i robot")]
        [TestCase("1Q84", "1q84")]
        [TestCase("一九八四", "一九八四")]
        [TestCase("The First Law", "the first law")]
        public void comparison_text_should_normalize_numeric_characters_without_rewriting_roman_glyphs(string input, string expected)
        {
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(expected.Replace(" ", "")));
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, null);
            Assert.That(mapped.Text, Is.EqualTo(expected));
            Assert.That(mapped.SourceSpans.Count, Is.EqualTo(mapped.Text.Length));
            Assert.That(mapped.SourceSpans.All(span => span.Start >= 0 && span.End <= input.Length && span.End > span.Start), Is.True);
        }

        [Test]
        public void expanded_roman_glyphs_should_retain_their_original_source_span()
        {
            var mapped = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans("A Memory of Light ⅩⅣ", null);
            Assert.That(mapped.Text, Is.EqualTo("a memory of light xiv"));
            Assert.That(mapped.SourceSpans.TakeLast(3), Is.EqualTo(new[] { (18, 20), (18, 20), (18, 20) }));
        }

        [Test]
        public void unicode_roman_glyphs_should_match_ascii_letter_tokens_and_preserve_source_spans()
        {
            const string ascii = "II";
            const string unicode = "Ⅱ";

            Assert.That(UnicodeComparisonNormalizer.NormalizeText(unicode), Is.EqualTo(UnicodeComparisonNormalizer.NormalizeText(ascii)));
            Assert.That(UnicodeComparisonNormalizer.NormalizeWords(unicode), Is.EqualTo(UnicodeComparisonNormalizer.NormalizeWords(ascii)));
            Assert.That(UnicodeComparisonNormalizer.NormalizeKey(unicode), Is.EqualTo(UnicodeComparisonNormalizer.NormalizeKey(ascii)));

            var mapped = UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans("II Ⅱ");
            Assert.That(mapped.Text, Is.EqualTo("ii ii"));
            Assert.That(mapped.SourceSpans, Is.EqualTo(new[] { (0, 1), (1, 2), (2, 3), (3, 4), (3, 4) }));
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
        public void word_normalization_should_be_idempotent_with_diacritics(string input)
        {
            var once = UnicodeComparisonNormalizer.NormalizeText(input);
            Assert.That(UnicodeComparisonNormalizer.NormalizeText(once), Is.EqualTo(once));
            Assert.That(once, Does.Contain(input.StartsWith("Ready") ? "two" : "one"));
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
        [TestCase("Dune twenty one ½", "dune twenty one 0.5")]
        [TestCase("Dune Ⅳ ½", "dune iv 0.5")]
        [TestCase("Dune 3½", "dune 3.5")]
        [TestCase("Dune 13½", "dune 13.5")]
        [TestCase("Dune Ⅳ½", "dune iv0.5")]
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
