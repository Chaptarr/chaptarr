using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class NearExactTitleEvidenceFixture
    {
        private static ContainmentValidator CreateValidator()
        {
            return new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_accept_single_suffix_typo_when_full_title_still_aligns_in_order()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Fantastic Beasts and Where to Find Them",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Fantastic Beast and Where to Find Them" } }
                });

            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence.Single().IsNearExact, Is.True);
        }

        [Test]
        public void should_accept_near_exact_title_evidence_from_custom_tag()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Fantastic Beasts and Where to Find Them",
                new Dictionary<string, List<string>>
                {
                    { "BOOKIDENTITY", new List<string> { "Fantastic Beast and Where to Find Them" } }
                });

            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence.Single().FieldName, Is.EqualTo("BOOKIDENTITY"));
            Assert.That(evidence.Single().IsNearExact, Is.True);
        }

        [Test]
        public void should_reject_missing_candidate_title_tokens()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Fifty Shades Darker",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Fifty Shades" } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [Test]
        public void should_accept_missing_internal_structural_glue()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Harry Potter and the Order of the Phoenix",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Harry Potter Order of the Phoenix" } }
                });

            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence.Single().IsNearExact, Is.False);
        }

        [Test]
        public void should_accept_omitted_leading_article_and_ampersand_connector()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "The World of Ice & Fire",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "World of Ice and Fire" } }
                });

            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence.Single().IsNearExact, Is.True);
        }

        [Test]
        public void should_reject_same_title_tokens_in_a_different_order()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "The Fall of the House of Usher",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "The House of Usher Fall" } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [Test]
        public void should_require_each_duplicate_title_token_occurrence()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Go Go Gone",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Go Gone" } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [Test]
        public void should_reject_numeric_mismatches()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Dungeon Crawler Carl 6",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Dungeon Crawler Carl 5" } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [Test]
        public void should_reject_multi_character_suffix_expansion()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "Fifty Shades of Grayer",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Fifty Shades of Gray" } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [Test]
        public void should_accept_one_short_title_suffix_typo_without_magic_token_count()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "It Takes Two",
                new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "It Take Two" } }
                });

            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence.Single().IsNearExact, Is.True);
        }

        [TestCase("The Cat in the Hat", "The Act in the Hat")]
        [TestCase("A Letter From Home", "A Letter Form Home")]
        [TestCase("Salt to the Sea", "Slat to the Sea")]
        public void should_reject_real_word_transpositions(string editionTitle, string tagTitle)
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                editionTitle,
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { tagTitle } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [Test]
        public void should_reject_more_than_one_near_token_in_a_title()
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                "The Rings and Towers",
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "The Ring and Tower" } }
                });

            Assert.That(evidence, Is.Empty);
        }

        [TestCase("King's League: An Epic LitRPG Adventure", "King's League: An Epic Lit RPG Adventure")]
        [TestCase("King's League: An Epic Lit RPG Adventure", "King's League: An Epic LitRPG Adventure")]
        public void should_accept_compact_and_split_litrpg_title_tokens(string editionTitle, string tagTitle)
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                editionTitle,
                new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { tagTitle } }
                });

            Assert.That(evidence, Has.Count.EqualTo(1));
            Assert.That(evidence.Single().IsNearExact, Is.False);
        }

        private static IEnumerable<TestCaseData> TitleEvidenceRegressionCases()
        {
            yield return new TestCaseData("exact-title", "Fantastic Beasts and Where to Find Them", "Fantastic Beasts and Where to Find Them", false, true, false);
            yield return new TestCaseData("single-trailing-char", "Fantastic Beasts and Where to Find Them", "Fantastic Beast and Where to Find Them", false, true, false);
            yield return new TestCaseData("short-title-no-token-count-magic", "It Takes Two", "It Take Two", false, true, false);
            yield return new TestCaseData("missing-candidate-token", "Fifty Shades Darker", "Fifty Shades", false, false, false);
            yield return new TestCaseData("numeric-mismatch", "Dungeon Crawler Carl 6", "Dungeon Crawler Carl 5", false, false, false);
            yield return new TestCaseData("multi-char-suffix-expansion", "Fifty Shades of Grayer", "Fifty Shades of Gray", false, false, false);
            yield return new TestCaseData("two-near-tokens", "The Rings and Towers", "The Ring and Tower", false, false, false);
            yield return new TestCaseData("cat-act-default-reject", "The Cat in the Hat", "The Act in the Hat", false, false, false);
            yield return new TestCaseData("from-form-default-reject", "A Letter From Home", "A Letter Form Home", false, false, false);
            yield return new TestCaseData("salt-slat-default-reject", "Salt to the Sea", "Slat to the Sea", false, false, false);
            yield return new TestCaseData("salt-slat-duration-gated", "Salt to the Sea", "Slat to the Sea", true, true, true);
        }

        [TestCaseSource(nameof(TitleEvidenceRegressionCases))]
        public void should_preserve_title_evidence_regression_matrix(
            string name,
            string editionTitle,
            string tagTitle,
            bool includeDurationGatedNearExact,
            bool shouldMatch,
            bool shouldRequireDuration)
        {
            var evidence = CreateValidator().GetEditionTitleEvidence(
                editionTitle,
                new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { tagTitle } }
                },
                includeDurationGatedNearExact);

            Assert.That(evidence.Count > 0, Is.EqualTo(shouldMatch), name);
            if (shouldMatch)
            {
                Assert.That(evidence.Single().RequiresAudiobookDurationCorroboration, Is.EqualTo(shouldRequireDuration), name);
            }
        }
    }
}
