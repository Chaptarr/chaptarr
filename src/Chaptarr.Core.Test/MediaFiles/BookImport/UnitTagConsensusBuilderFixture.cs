using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NLog;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class UnitTagConsensusBuilderFixture
    {
        [Test]
        public void build_consensus_should_keep_stable_values_and_drop_singletons_for_repeated_keys()
        {
            var tags = new[]
            {
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Track 1" },
                    ["ALBUMARTIST"] = new List<string> { "Frank Herbert" }
                },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Track 2" },
                    ["ALBUMARTIST"] = new List<string> { "Frank Herbert" }
                },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Track 3" },
                    ["ALBUMARTIST"] = new List<string> { "Frank Herbert" }
                }
            };

            var result = UnitTagConsensusBuilder.BuildConsensus(tags, totalFileCount: tags.Length);

            Assert.That(result.ContainsKey("ALBUM"), Is.True);
            Assert.That(result["ALBUM"], Is.EquivalentTo(new[] { "Dune" }));
            Assert.That(result.ContainsKey("ALBUMARTIST"), Is.True);
            Assert.That(result["ALBUMARTIST"], Is.EquivalentTo(new[] { "Frank Herbert" }));
            Assert.That(result.ContainsKey("TITLE"), Is.False, "singleton per-file titles should be removed for repeated keys");
        }

        [Test]
        public void build_consensus_should_fallback_to_single_tagset_when_only_one_is_available()
        {
            var single = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = new List<string> { "Dreamer of Dune" },
                ["TITLE"] = new List<string> { "Chapter 1" }
            };

            var result = UnitTagConsensusBuilder.BuildConsensus(new[] { single }, totalFileCount: 1);

            Assert.That(result["ALBUM"], Is.EquivalentTo(new[] { "Dreamer of Dune" }));
            Assert.That(result["TITLE"], Is.EquivalentTo(new[] { "Chapter 1" }));
        }

        [Test]
        public void build_consensus_should_not_accept_sparse_two_file_repeats_for_large_units()
        {
            var sparse = new[]
            {
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Part 1" }
                },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Part 1" }
                }
            };

            var result = UnitTagConsensusBuilder.BuildConsensus(sparse, totalFileCount: 6);

            Assert.That(result, Is.Empty, "two tagged files should not define consensus for a six-file unit");
        }

        [TestCase("Frank Herbert's Dune Messiah Chapter 1", "Frank Herbert's Dune Messiah Chapter 2", "frank herberts dune messiah chapter")]
        [TestCase("Arrival — Dune Messiah", "Dune Messiah — Departure", "dune messiah")]
        [TestCase("Chapter One — Author Book Title", "Author Book Title — Chapter Two", "author book title")]
        [TestCase("أول فصل — اسم المؤلف عنوان الكتاب", "اسم المؤلف عنوان الكتاب — فصل آخر", "اسم المؤلف عنوان الكتاب")]
        [TestCase("序章「宮沢賢治・銀河鉄道の夜」", "「宮沢賢治・銀河鉄道の夜」終章", "宮沢賢治 銀河鉄道の夜")]
        [TestCase("One Renée Book", "Rene\u0301e Book Two", "renee book")]
        [TestCase("One Never Never Land", "Never Never Land Two", "never never land")]
        public void matching_should_retain_shared_ordered_text_anywhere_in_the_same_field(string first, string second, string expected)
        {
            var fields = new[]
            {
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["CUSTOM"] = new() { first } },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["custom"] = new() { second } }
            };

            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields, includeSharedText: true)["CUSTOM"].Select(value => string.Join(" ", ReleaseTitleMatchScorer.Tokenize(value))), Does.Contain(expected));
            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields), Is.Empty, "conversion metadata must retain complete source values");
            Array.Reverse(fields);
            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields, includeSharedText: true)["CUSTOM"].Select(value => string.Join(" ", ReleaseTitleMatchScorer.Tokenize(value))), Does.Contain(expected));
        }

        [Test]
        public void shared_request_must_preserve_author_possessive_punctuation()
        {
            var tags = new[] { "Frank Herbert's Dune Messiah — Opening", "Arrival — Frank Herbert's Dune Messiah" }
                .Select(value => new Dictionary<string, List<string>> { ["FIELD1"] = new() { value } }).ToArray();
            var shared = UnitTagConsensusBuilder.BuildConsensus(tags, includeSharedText: true);
            Assert.That(shared["FIELD1"], Is.EqualTo(new[] { "Frank Herbert's Dune Messiah" }));
            var validator = new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
            Assert.That(validator.ValidateAuthorInTags("Frank Herbert", shared, new[] { "Dune Messiah" }), Is.True);
        }

        [TestCase("Red Blue", "Blue Red")]
        [TestCase("Red extra Blue", "Red different Blue")]
        [TestCase("Red Blues", "Red Blue")]
        public void shared_text_must_not_reorder_stitch_or_truncate_words(string first, string second)
        {
            var fields = new[]
            {
                new Dictionary<string, List<string>> { ["TITLE"] = new() { first } },
                new Dictionary<string, List<string>> { ["TITLE"] = new() { second } }
            };

            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields, includeSharedText: true)["TITLE"]
                .Select(value => string.Join(" ", ReleaseTitleMatchScorer.Tokenize(value))), Does.Not.Contain("red blue"));
        }

        [Test]
        public void shared_text_must_not_cross_field_names_or_missing_members()
        {
            var fields = new[]
            {
                new Dictionary<string, List<string>> { ["TITLE"] = new() { "Author Book One" } },
                new Dictionary<string, List<string>> { ["ALBUM"] = new() { "Author Book Two" } }
            };

            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields, includeSharedText: true), Is.Empty);
            fields[1] = new Dictionary<string, List<string>> { ["TITLE"] = new() { "Author Book Two" } };
            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields, totalFileCount: 3, includeSharedText: true), Is.Empty);
        }

        [Test]
        public void shared_request_fields_must_be_present_on_every_member()
        {
            var tags = Enumerable.Range(0, 8).Select(index => new Dictionary<string, List<string>>
            {
                [index == 7 ? "OTHER" : "TITLE"] = new() { "Dune Messiah" }
            }).ToArray();
            Assert.That(UnitTagConsensusBuilder.BuildConsensus(tags, includeSharedText: true), Is.Empty);
            Assert.That(UnitTagConsensusBuilder.BuildConsensus(tags)["TITLE"], Is.EqualTo(new[] { "Dune Messiah" }));
        }

        [Test]
        public void matching_should_keep_the_shared_title_even_when_one_chapter_value_has_a_majority()
        {
            var fields = new[]
            {
                new Dictionary<string, List<string>> { ["TITLE"] = new() { "First Chapter Book Title" } },
                new Dictionary<string, List<string>> { ["TITLE"] = new() { "First Chapter Book Title" } },
                new Dictionary<string, List<string>> { ["TITLE"] = new() { "Book Title Final Chapter" } }
            };

            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields, includeSharedText: true)["TITLE"], Does.Contain("Book Title"));
            Assert.That(UnitTagConsensusBuilder.BuildConsensus(fields)["TITLE"], Is.EqualTo(new[] { "First Chapter Book Title" }));
        }
    }
}
