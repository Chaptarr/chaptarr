using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionRetentionSelectorFixture
    {
        private static EditionSelector CreateSut()
        {
            return new EditionSelector(LogManager.GetCurrentClassLogger());
        }

        private static Edition MakeEdition(int id, string foreignEditionId, int readingFormatId, string language, string title, string hardcoverEditionId = null)
        {
            return new Edition
            {
                Id = id,
                ForeignEditionId = foreignEditionId,
                ReadingFormatId = readingFormatId,
                Language = language,
                Title = title,
                HardcoverEditionId = hardcoverEditionId
            };
        }

        [Test]
        public void audiobook_should_keep_all_audio_plus_one_ebook_companion_per_language()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "eng-audio-1", 2, "eng", "Dune Audio A"),
                    MakeEdition(2, "eng-audio-2", 2, "eng", "Dune Audio B"),
                    MakeEdition(3, "eng-ebook", 3, "eng", "Dune"),
                    MakeEdition(4, "eng-print", 1, "eng", "Dune Print"),
                    MakeEdition(5, "fra-audio", 2, "fra", "Dune French Audio")
                });

            // English: keep both audios + best ebook companion (RF=3 outranks RF=1).
            // French: audio only — no ebook/print available to retain.
            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-audio-1", "eng-audio-2", "eng-ebook", "fra-audio" }));
        }

        [Test]
        public void audiobook_should_keep_audio_alone_when_language_has_no_non_audio_candidates()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "null-audio", 2, null, "Dune Audio")
                });

            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "null-audio" }));
        }

        [Test]
        public void audiobook_should_fall_back_to_print_companion_when_no_ebook_in_language()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "eng-audio", 2, "eng", "Dune Audio"),
                    MakeEdition(2, "eng-print", 1, "eng", "Dune Print")
                });

            // No RF=3 in eng, so the audiobook book row keeps the audio plus the print companion.
            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-audio", "eng-print" }));
        }

        [Test]
        public void audiobook_should_keep_top_rated_ebook_companion()
        {
            var sut = CreateSut();
            var lowerRatedEbook = MakeEdition(2, "eng-ebook-low", 3, "eng", "Dune");
            lowerRatedEbook.Ratings = new Ratings { Votes = 10, Value = 4.9m };
            var higherRatedEbook = MakeEdition(3, "eng-ebook-high", 3, "eng", "Dune");
            higherRatedEbook.Ratings = new Ratings { Votes = 500, Value = 4.2m };

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "eng-audio", 2, "eng", "Dune Audio"),
                    lowerRatedEbook,
                    higherRatedEbook,
                    MakeEdition(4, "eng-print", 1, "eng", "Dune Print")
                });

            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-audio", "eng-ebook-high" }));
        }

        [Test]
        public void audiobook_should_keep_one_ebook_representative_when_no_audio_exists_for_language()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "eng-audio", 2, "eng", "Dune Audio"),
                    MakeEdition(2, "spa-ebook", 3, "spa", "Dune Spanish"),
                    MakeEdition(3, "spa-print", 1, "spa", "Dune Spanish Print")
                });

            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-audio", "spa-ebook" }));
        }

        [Test]
        public void audiobook_should_keep_one_print_representative_when_no_audio_or_ebook_exists_for_language()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "eng-audio", 2, "eng", "Dune Audio"),
                    MakeEdition(2, "spa-print", 1, "spa", "Dune Spanish Print")
                });

            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-audio", "spa-print" }));
        }

        [Test]
        public void should_treat_null_language_as_a_real_retention_bucket()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(1, "null-audio", 2, null, "Dune Audio"),
                    MakeEdition(2, "null-ebook", 3, null, "Dune"),
                    MakeEdition(3, "eng-ebook", 3, "eng", "Dune")
                });

            // null bucket: audio + ebook companion. eng bucket: no audio, ebook fallback fires.
            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "null-audio", "null-ebook", "eng-ebook" }));
        }

        [Test]
        public void should_not_dedupe_distinct_audio_editions_that_lack_foreign_ids()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Audiobook,
                new List<Edition>
                {
                    MakeEdition(0, null, 2, "eng", "Dune Audio A", hardcoverEditionId: "hc:audio-a"),
                    MakeEdition(0, null, 2, "eng", "Dune Audio B", hardcoverEditionId: "hc:audio-b"),
                    MakeEdition(0, "eng-ebook", 3, "eng", "Dune"),
                });

            Assert.That(selection.RetainedEditions.Count(e => e.HardcoverEditionId == "hc:audio-a"), Is.EqualTo(1));
            Assert.That(selection.RetainedEditions.Count(e => e.HardcoverEditionId == "hc:audio-b"), Is.EqualTo(1));
            // Companion ebook is also retained per the always-include-ebook safety net.
            Assert.That(selection.RetainedEditions.Count(e => e.ForeignEditionId == "eng-ebook"), Is.EqualTo(1));
        }

        [Test]
        public void ebook_should_not_retain_print_companion_when_ebooks_exist_for_language()
        {
            var sut = CreateSut();

            var selection = sut.SelectRetainedEditions(
                BookMediaType.Ebook,
                new List<Edition>
                {
                    MakeEdition(1, "eng-ebook", 3, "eng", "Dune"),
                    MakeEdition(2, "eng-print", 1, "eng", "Dune Print")
                });

            // The audiobook safety-net rule does not apply to ebook book rows; print is dropped when ebooks exist.
            Assert.That(selection.RetainedEditions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-ebook" }));
        }
    }
}
