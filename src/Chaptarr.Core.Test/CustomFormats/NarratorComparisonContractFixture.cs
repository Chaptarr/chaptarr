using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;

namespace Chaptarr.Core.Test.CustomFormats
{
    [TestFixture]
    public class NarratorComparisonContractFixture
    {
        [TestCase("Robert Stevenson", "Robert Louis Stevenson", true)]
        [TestCase("Robert Stevenson", "Robert Louis Stevensons", true)]
        [TestCase("Robert Louis Stevensons", "Robert Stevenson", true)]
        [TestCase("Stephen Fry", "Stephen Frys", true)]
        [TestCase("Stephen Frys", "Stephen Fry", true)]
        [TestCase("Frank Herberts", "Frank Herbert", true)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("Jennifer Reader", "Jennifer L Reader", true)]
        [TestCase("Jennifer L Reader", "Jennifer Reader", true)]
        [TestCase("Jennifer L Reader", "Jennifer Q Reader", false)]
        [TestCase("Robert Andrew Stevenson", "Robert Louis Stevenson", false)]
        [TestCase("Stevenson, Robert Louis", "Robert Louis Stevenson", true)]
        [TestCase("John Smith", "John Smith Other", false)]
        [TestCase("Alex II Reader", "Alex 2 Reader", false)]
        [TestCase("Alex II Reader", "Alex Ⅱ Reader", true)]
        [TestCase("James Marster", "James Marsters", true)]
        [TestCase("Brian Herbert", "Frank Herbert", false)]
        [TestCase("Renée Ballard", "Renee Ballard", true)]
        [TestCase("Renée Ballard", "Rene\u0301e Ballard", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀁Smut", false)]
        [TestCase("がとう", "かとう", false)]
        [TestCase("がとう", "か\u3099とう", true)]
        [TestCase("Broken \uD800 Name", "Broken Name", true)]
        public void preferred_and_literal_narrator_comparison_should_share_exact_name_compatibility(string observed, string configured, bool expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(NarratorNameMatcher.IsMatch(observed, configured), Is.EqualTo(expected));
                Assert.That(PreferredNarratorMatcher.IsNarratorNameMatch(observed, configured), Is.EqualTo(expected));
            });
        }

        [TestCase("Pat Lee Smith", false)]
        [TestCase("Pat Lee Smith, Lee Pat Smith", true)]
        public void complete_cast_should_require_each_distinct_ordered_name(string observed, bool complete)
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Pat Lee Smith", "Lee Pat Smith" },
                Narrator = observed
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.TargetCount, Is.EqualTo(2));
                Assert.That(result.Complete, Is.EqualTo(complete));
            });
        }

        [Test]
        public void one_observed_name_should_not_fulfill_two_compatible_target_credits()
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Ann A Smith", "Ann Smith" },
                Narrator = "Ann Smith"
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.TargetCount, Is.EqualTo(2));
                Assert.That(result.OverlapCount, Is.EqualTo(1));
                Assert.That(result.Complete, Is.False);
            });
        }

        [TestCase("Ann Smith")]
        [TestCase("Smith, Ann")]
        public void one_observed_person_should_not_be_duplicated_by_known_name_reconstruction(string observed)
        {
            var book = new Book
            {
                MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new Edition { ReadingFormatId = 2, ManualAdd = true, NarratorNames = new List<string> { "Ann A Smith", "Ann Smith" } }
                }
            };
            var input = new CustomFormatInput { MediaType = BookMediaType.Audiobook, Narrator = observed };
            PreferredNarratorMatcher.ApplyTarget(input, book);
            var match = PreferredNarratorMatcher.Evaluate(input);
            Assert.Multiple(() =>
            {
                Assert.That(match.TargetCount, Is.EqualTo(2));
                Assert.That(match.OverlapCount, Is.EqualTo(1));
                Assert.That(match.Complete, Is.False);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void preferred_complete_cast_should_require_structured_target_names(bool structured, bool expectedComplete)
        {
            var book = new Book
            {
                MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new Edition { ReadingFormatId = 2, ManualAdd = true, Narrator = "Ann Reader; Pat Reader",
                        NarratorNames = structured ? new List<string> { "Ann Reader", "Pat Reader" } : new List<string>() }
                }
            };
            var input = new CustomFormatInput { MediaType = BookMediaType.Audiobook, Narrator = "Ann Reader; Pat Reader" };
            PreferredNarratorMatcher.ApplyTarget(input, book);
            var result = PreferredNarratorMatcher.Evaluate(input);
            Assert.Multiple(() =>
            {
                Assert.That(result.Any, Is.True);
                Assert.That(result.Complete, Is.EqualTo(expectedComplete));
                Assert.That(result.HasUnresolvedTargetNames, Is.EqualTo(!structured));
            });
        }

        [TestCase("Ana Bell", false)]
        [TestCase("Ana Bell, An Abell", true)]
        public void cast_deduplication_should_preserve_name_word_boundaries(string observed, bool complete)
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Ana Bell", "An Abell" },
                Narrator = observed
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.TargetCount, Is.EqualTo(2));
                Assert.That(result.Complete, Is.EqualTo(complete));
            });
        }

        [Test]
        public void an_unlabelled_supplementary_script_name_should_remain_narrator_evidence()
        {
            var result = ReleaseNarratorEvidenceExtractor.Extract(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                AudioProductionFields = new List<string> { "A Story [𐐀𐐁 𐐂𐐃]" },
                PreferredNarratorNames = new List<string> { "𐐀𐐁 𐐂𐐃" }
            });

            Assert.That(result.Names, Contains.Item("𐐀𐐁 𐐂𐐃"));
        }
        [TestCase("Anna Mueller + 2 more narrators")]
        [TestCase("Anna Mueller + 2 weitere Sprecher")]
        [TestCase("Anna Mueller + 2 autres voix")]
        [TestCase("Anna Mueller")]
        public void free_text_alone_must_never_establish_a_complete_cast(string narrator)
        {
            var edition = new Edition { Narrator = narrator };
            Assert.That(ReleaseNarratorEvidenceExtractor.TryGetCompleteCast(edition, out _), Is.False);
        }

        [TestCase("Anna Mueller, Ben Reader", true, true)]
        [TestCase("Anna Mueller und Ben Reader", true, true)]
        [TestCase("Anna Mueller + 2 weitere Sprecher", false, false)]
        [TestCase("Anna Mueller + 2 more narrators", false, false)]
        [TestCase("Anna Mueller", true, false)]
        [TestCase("Ben Reader", true, false)]
        [TestCase("", true, false)]
        [TestCase("Unknown Person", false, false)]
        public void structured_cast_consistency_and_raw_cast_adoption_require_different_evidence(string narrator, bool structuredExpected, bool adoptionExpected)
        {
            var names = new[] { "Anna Mueller", "Ben Reader" };
            Assert.Multiple(() =>
            {
                Assert.That(ReleaseNarratorEvidenceExtractor.TryGetCompleteCast(new Edition
                { NarratorNames = new List<string>(names), Narrator = narrator }, out _), Is.EqualTo(structuredExpected));
                Assert.That(ReleaseNarratorEvidenceExtractor.TryGetCompleteCast(new Edition
                { Narrator = narrator }, out _, names), Is.EqualTo(adoptionExpected));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void blank_structured_entries_without_raw_credit_must_not_adopt_a_known_cast(bool useCredits)
        {
            var edition = new Edition
            {
                NarratorNames = new List<string> { null, " " },
                NarratorCredits = useCredits
                    ? new List<NarratorCredit> { new NarratorCredit { Name = " " } }
                    : new List<NarratorCredit>()
            };
            Assert.That(ReleaseNarratorEvidenceExtractor.TryGetCompleteCast(edition, out _,
                new[] { "Anna Mueller", "Ben Reader" }), Is.False);
        }

        [Test]
        public void known_inverted_person_should_reconstruct_without_inventing_a_second_person()
        {
            Assert.That(ReleaseNarratorEvidenceExtractor.TryGetCompleteCast(new Edition
            { NarratorNames = new List<string> { "John Smith" }, Narrator = "Smith, John" }, out var names), Is.True);
            Assert.That(names, Is.EqualTo(new[] { "John Smith" }));
        }

        [TestCase("Lu par Anna Mueller")]
        [TestCase("Gelesen von Anna Mueller")]
        [TestCase("Anna Mueller, who cares about these words")]
        public void one_known_member_can_supply_presence_without_claiming_the_complete_cast(string field)
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Anna Mueller", "Ben Reader", "Cara Reader" },
                AudioProductionFields = new List<string> { field }
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Any, Is.True);
                Assert.That(result.OverlapCount, Is.EqualTo(1));
                Assert.That(result.Complete, Is.False);
            });
        }

    }
}
