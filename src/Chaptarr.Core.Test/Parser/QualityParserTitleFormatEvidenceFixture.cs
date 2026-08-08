using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Parser
{
    /// <summary>
    /// Release titles assert formats with very different strength. These pin the tiering:
    /// terminal filename extension > delimited/adjacent format list > loose word, with only the
    /// strongest tier present contributing to DetectedQualities.
    /// </summary>
    [TestFixture]
    public class QualityParserTitleFormatEvidenceFixture
    {
        // The release behind the 2026-07-25 report: a Usenet request post asking for an epub, filled
        // with a mobi. Before tiering, the leftmost word won and Chaptarr grabbed it as an EPUB.
        private const string RequestPostWithMobiPayload =
            "Captain's Fury (Codex Alera 2), epub, by Jim Butcher please...thanks - Jim Butcher - Codex Alera 04 - Captain's Fury/Jim Butcher - Codex Alera 04 - Captain's Fury.mobi";

        [Test]
        public void should_prefer_terminal_payload_extension_over_an_earlier_format_word()
        {
            var evidence = QualityParser.DetectTitleFormats(RequestPostWithMobiPayload);

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.TerminalExtension));
            Assert.That(evidence.Qualities, Is.EquivalentTo(new[] { Quality.MOBI }));
        }

        [Test]
        public void should_not_expose_a_weaker_tier_format_for_promotion()
        {
            var quality = QualityParser.ParseQuality(RequestPostWithMobiPayload);

            Assert.That(quality.Quality, Is.EqualTo(Quality.MOBI));
            Assert.That(quality.QualityDetectionSource, Is.EqualTo(QualityDetectionSource.Extension));

            // The multi-format promotion picks any allowed member of DetectedQualities, so the
            // weaker "epub" must never appear here or an EPUB-only profile would grab this again.
            Assert.That(quality.DetectedQualities, Does.Not.Contain(Quality.EPUB));
        }

        [Test]
        public void should_detect_every_format_in_a_delimited_list()
        {
            var evidence = QualityParser.DetectTitleFormats("Brad Thor-[Scot Harvath 24-25] [azw3 epub mobi]");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.FormatGroup));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.AZW3, Quality.EPUB, Quality.MOBI }));
        }

        [Test]
        public void should_detect_adjacent_format_tokens_without_delimiters()
        {
            var evidence = QualityParser.DetectTitleFormats("Jim Butcher - Captain's Fury epub mobi pdf");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.FormatGroup));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.EPUB, Quality.MOBI, Quality.PDF }));
        }

        [Test]
        public void should_expose_a_multi_format_list_for_promotion()
        {
            var quality = QualityParser.ParseQuality("Brad Thor-[Scot Harvath 24-25] [azw3 epub mobi]");

            Assert.That(quality.Quality, Is.EqualTo(Quality.AZW3));
            Assert.That(quality.IsMultiFormat, Is.True);
            Assert.That(quality.DetectedQualities, Does.Contain(Quality.EPUB));
        }

        [Test]
        public void should_fall_back_to_a_single_loose_format_word()
        {
            var evidence = QualityParser.DetectTitleFormats("Captain's Fury (Codex Alera 4), epub, by Jim Butcher please...thanks");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.LooseToken));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.EPUB }));
        }

        [Test]
        public void should_ignore_a_terminal_extension_that_is_not_a_book_format()
        {
            var evidence = QualityParser.DetectTitleFormats("Jim Butcher - Captain's Fury [epub].rar");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.FormatGroup));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.EPUB }));
        }

        [Test]
        public void should_not_mistake_a_volume_number_for_an_extension()
        {
            var evidence = QualityParser.DetectTitleFormats("Some Series Vol.4 - Captain's Fury epub");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.LooseToken));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.EPUB }));
        }

        [Test]
        public void should_report_no_evidence_when_the_title_names_no_format()
        {
            var evidence = QualityParser.DetectTitleFormats("Jim Butcher - Captain's Fury");

            Assert.That(evidence.Any, Is.False);
            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.None));
        }

        [TestCase("Jim Butcher - Captain's Fury [M4B]", nameof(Quality.M4B))]
        [TestCase("Jim Butcher - Captain's Fury 2019 MP3", nameof(Quality.MP3))]
        [TestCase("Jim Butcher - Captain's Fury FLAC", nameof(Quality.FLAC))]
        [TestCase("Jim Butcher - Captain's Fury.m4b", nameof(Quality.M4B))]
        public void should_keep_classifying_audiobook_titles(string title, string expectedQualityName)
        {
            var quality = QualityParser.ParseQuality(title);

            Assert.That(quality.Quality.Name, Is.EqualTo(expectedQualityName));
        }

        [Test]
        public void should_be_language_neutral_for_non_english_titles()
        {
            // Structure carries the evidence, not the words around it.
            var german = QualityParser.DetectTitleFormats("Der Herr der Ringe - Ungekürzte Lesung [epub mobi]");
            Assert.That(german.Tier, Is.EqualTo(FormatEvidenceTier.FormatGroup));
            Assert.That(german.Qualities, Is.EqualTo(new[] { Quality.EPUB, Quality.MOBI }));

            var korean = QualityParser.DetectTitleFormats("김 작가 - 소설 제목/소설 제목.epub");
            Assert.That(korean.Tier, Is.EqualTo(FormatEvidenceTier.TerminalExtension));
            Assert.That(korean.Qualities, Is.EqualTo(new[] { Quality.EPUB }));
        }

        [Test]
        public void should_ignore_case_and_underscores_around_format_tokens()
        {
            var evidence = QualityParser.DetectTitleFormats("Jim_Butcher_-_Captains_Fury_[EPUB_MOBI]");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.FormatGroup));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.EPUB, Quality.MOBI }));
        }

        [Test]
        public void should_keep_detected_qualities_distinct()
        {
            var evidence = QualityParser.DetectTitleFormats("Captain's Fury [epub epub mobi]");

            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.EPUB, Quality.MOBI }));
        }

        [Test]
        public void should_combine_a_package_list_with_the_member_the_title_names()
        {
            // A package that lists its contents and then names one member: the extension is the
            // concrete file, the list is what else is in there. The profile picks between them.
            var evidence = QualityParser.DetectTitleFormats("Captain's Fury [epub azw3]/Captain's Fury.mobi");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.FormatGroup));
            Assert.That(evidence.PrimaryFromExtension, Is.True);
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.MOBI, Quality.EPUB, Quality.AZW3 }));
        }

        [Test]
        public void should_let_the_extension_stand_alone_when_the_claim_is_about_the_same_file()
        {
            // One file, whose extension we can already read — a bracketed claim beside it does not
            // get to override the bytes.
            var evidence = QualityParser.DetectTitleFormats("Captain's Fury [EPUB].mobi");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.TerminalExtension));
            Assert.That(evidence.Qualities.Single(), Is.EqualTo(Quality.MOBI));
        }

        [Test]
        public void should_not_treat_scattered_prose_mentions_as_a_bundle()
        {
            // Two unrelated words in a sentence are not a format list. Only ONE quality may come
            // out of the loose tier, or an incidental mention could be promoted over what the
            // release actually is.
            var evidence = QualityParser.DetectTitleFormats("Jim Butcher - Captain's Fury MP3 transcoded from the FLAC master");

            Assert.That(evidence.Tier, Is.EqualTo(FormatEvidenceTier.LooseToken));
            Assert.That(evidence.Qualities, Is.EqualTo(new[] { Quality.MP3 }));
        }

        [Test]
        public void should_not_expose_a_second_format_from_prose_for_promotion()
        {
            var quality = QualityParser.ParseQuality("Jim Butcher - Captain's Fury MP3 transcoded from the FLAC master");

            Assert.That(quality.Quality, Is.EqualTo(Quality.MP3));
            Assert.That(quality.IsMultiFormat, Is.False);
        }
    }
}
