using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class CastLabelFixture
    {
        [TestCase("Full Cast", "Penelope Hardcastle")]
        [TestCase("A Full Cast", "Fuller")]
        [TestCase("Ensemble Cast", "Inés del Castillo")]
        [TestCase("An Ensemble Cast", "Geoff Castellucci")]
        [TestCase("Various", "P. C. Cast")]
        [TestCase("Cast Album", "Castillo")]
        [TestCase("Full Various Cast - GraphicAudio", "GraphicAudiofan")]
        [TestCase("Full Cast Full Cast", "Full Castaneda")]
        [TestCase("Diverse", "Dietmar Wunder")]
        [TestCase("Diverse Sprecher", "Sprecher")]
        [TestCase("Varios narradores", "Asier Etxeandia")]
        [TestCase("Narratori Vari", "Simone Bedetti")]
        public void known_group_credit_is_a_label_but_a_person_is_not(string label, string person)
        {
            Assert.That(AudioProductionDetector.IsCastLabel(label), Is.True, label);
            Assert.That(AudioProductionDetector.IsCastLabel(person), Is.False, person);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("山寺宏一")]
        [TestCase("未知の集団名")]
        [TestCase("Museum Audiobooks cast")]
        [TestCase("Peter Pan Ensemble")]
        [TestCase("Full Castaneda")]
        [TestCase("multicasting")]
        public void unknown_credits_are_not_hidden_as_cast_labels(string name)
        {
            Assert.That(AudioProductionDetector.IsCastLabel(name), Is.False);
        }

        [TestCase("  ＦＵＬＬ　ＣＡＳＴ  ")]
        [TestCase("full-cast")]
        [TestCase("THE FULL CAST")]
        public void normalization_reuses_unicode_folding_and_article_catalog(string label)
        {
            Assert.That(AudioProductionDetector.IsCastLabel(label), Is.True);
        }

        [Test]
        public void a_group_credit_does_not_make_a_recording_dramatized()
        {
            Assert.That(AudioProductionDetector.IsCastLabel("Various"), Is.True);
            Assert.That(AudioProductionDetector.IsDramatizedOrFullCast(false, null, "Various"), Is.False);
        }

        [Test]
        public void book_and_edition_resources_share_classification_without_changing_legacy_names()
        {
            var names = new List<string> { "A Full Cast", "Inés del Castillo" };
            var book = new BookResource { NarratorNames = names, AvailableNarrators = names };
            var edition = new Edition { NarratorNames = names, Narrator = "A Full Cast, Inés del Castillo" }.ToResource();
            foreach (var credits in new[] { book.Narrators, book.AvailableNarratorCredits, edition.Narrators })
            {
                Assert.That(credits.Select(n => n.Name), Is.EqualTo(names));
                Assert.That(credits.Select(n => n.IsCastLabel), Is.EqualTo(new[] { true, false }));
            }
            Assert.That(edition.NarratorNames, Is.EqualTo(names));
            Assert.That(edition.Narrator, Is.EqualTo("A Full Cast, Inés del Castillo"));
            Assert.That(new Edition { Narrator = "Diverse Sprecher" }.ToResource().Narrators.Single().IsCastLabel, Is.True);
            Assert.That(new BookResource().Narrators, Is.Empty);
        }
    }
}
