using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Series;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class EditionCatalogFixture
    {
        private static Edition Audio(int id, string title, params string[] narrators) => new Edition
        {
            Id = id,
            Title = title,
            IsEbook = false,
            NarratorNames = narrators.ToList(),
            ForeignEditionId = "hc:" + id
        };

        [Test]
        public void should_report_a_single_narrator_as_solo()
        {
            var card = Audio(1, "Book", "Jim Dale").ToCatalogResource();

            Assert.That(card.Production, Is.EqualTo("solo"));
            Assert.That(card.Narrators, Is.EqualTo(new[] { "Jim Dale" }));
        }

        [Test]
        public void should_report_graphic_audio_as_full_cast()
        {
            var edition = Audio(2, "Book (GraphicAudio)", "A", "B");
            edition.IsGraphicAudio = true;

            Assert.That(edition.ToCatalogResource().Production, Is.EqualTo("full-cast"));
        }

        [Test]
        public void should_leave_unstated_details_unknown()
        {
            var card = Audio(3, "Book").ToCatalogResource();

            Assert.That(card.Production, Is.EqualTo("unknown"));
            Assert.That(card.Abridged, Is.EqualTo("unknown"));
            Assert.That(card.Narrators, Is.Empty);
        }

        [TestCase("Book (Unabridged)", "unabridged")]
        [TestCase("Book (Abridged)", "abridged")]
        public void should_read_abridgement_only_from_the_edition_text(string title, string expected)
        {
            Assert.That(Audio(4, title, "N").ToCatalogResource().Abridged, Is.EqualTo(expected));
        }

        [Test]
        public void should_split_a_legacy_narrator_string()
        {
            var edition = new Edition { Id = 5, Title = "Book", Narrator = "Ann Smith, Bob Jones" };

            Assert.That(EditionCatalogMapper.NarratorsOf(edition), Is.EqualTo(new[] { "Ann Smith", "Bob Jones" }));
        }

        [Test]
        public void should_compare_narrators_across_all_volumes()
        {
            var owned = Audio(10, "Vol 1", "Jim Dale");
            owned.BookFiles = new List<BookFile> { new BookFile() };
            var volumes = new List<SeriesNarratorMatrixBuilder.Volume>
            {
                new SeriesNarratorMatrixBuilder.Volume(1, "Vol 1", 1, new List<Edition> { owned, Audio(11, "Vol 1", "Stephen Fry") }),
                new SeriesNarratorMatrixBuilder.Volume(2, "Vol 2", 2, new List<Edition> { Audio(12, "Vol 2", "Jim Dale") }),
                new SeriesNarratorMatrixBuilder.Volume(3, "Vol 3", 3, new List<Edition> { Audio(13, "Vol 3", "Stephen Fry") })
            };

            var matrix = SeriesNarratorMatrixBuilder.Build(7, "Series", volumes);

            Assert.That(matrix.TotalBooks, Is.EqualTo(3));
            var dale = matrix.Narrators.Single(n => n.Name == "Jim Dale");
            Assert.That(dale.CoveredBookIds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(dale.MissingBookIds, Is.EqualTo(new[] { 3 }));
            Assert.That(dale.OwnedBookIds, Is.EqualTo(new[] { 1 }));
            var fry = matrix.Narrators.Single(n => n.Name == "Stephen Fry");
            Assert.That(fry.MissingBookIds, Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void should_match_series_titles_ignoring_case_and_punctuation()
        {
            Assert.That(SeriesNarratorMatrixBuilder.Normalize("Harry Potter!"), Is.EqualTo(SeriesNarratorMatrixBuilder.Normalize("harry  potter")));
        }
    }
}
