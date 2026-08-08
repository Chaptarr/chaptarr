using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class PartAssignmentHelperFixture
    {
        [Test]
        public void should_keep_alternate_ebook_formats_as_single_part_files()
        {
            var files = new List<BookFile>
            {
                new BookFile
                {
                    EditionId = 42,
                    Path = "/books/Dune.epub",
                    MediaType = "ebook",
                    Quality = new QualityModel(Quality.EPUB),
                    Part = 1,
                    PartCount = 2
                },
                new BookFile
                {
                    EditionId = 42,
                    Path = "/books/Dune.mobi",
                    MediaType = "ebook",
                    Quality = new QualityModel(Quality.MOBI),
                    Part = 2,
                    PartCount = 2
                }
            };

            PartAssignmentHelper.NormalizeBookFilesByEdition(files);

            Assert.That(files.All(file => file.Part == 1), Is.True);
            Assert.That(files.All(file => file.PartCount == 1), Is.True);
        }

        [Test]
        public void should_assign_sequential_parts_for_multi_file_audiobook_editions()
        {
            var edition = new Edition { Id = 101 };
            var localBooks = new List<LocalBook>
            {
                new LocalBook
                {
                    Edition = edition,
                    Path = "/audiobooks/Dune/02-track.mp3",
                    Quality = new QualityModel(Quality.MP3),
                    Part = 0,
                    PartCount = 0
                },
                new LocalBook
                {
                    Edition = edition,
                    Path = "/audiobooks/Dune/01-track.mp3",
                    Quality = new QualityModel(Quality.MP3),
                    Part = 0,
                    PartCount = 0
                }
            };

            PartAssignmentHelper.NormalizeLocalBooksByEdition(localBooks);

            var ordered = localBooks.OrderBy(book => book.Path).ToList();
            Assert.That(ordered[0].Part, Is.EqualTo(1));
            Assert.That(ordered[1].Part, Is.EqualTo(2));
            Assert.That(localBooks.All(book => book.PartCount == 2), Is.True);
        }

        [Test]
        public void should_build_single_part_assignments_for_same_edition_ebook_formats()
        {
            var assignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                new[]
                {
                    ("/books/Dune.epub", (int?)77),
                    ("/books/Dune.kepub", (int?)77),
                    ("/books/Dune.mobi", (int?)77)
                },
                defaultEditionId: 0);

            Assert.That(assignments.Values.All(value => value.Part == 1 && value.PartCount == 1), Is.True);
        }

        [Test]
        public void should_build_multipart_assignments_for_same_edition_audio_files()
        {
            var assignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                new[]
                {
                    ("/audiobooks/Dune/02-track.mp3", (int?)88),
                    ("/audiobooks/Dune/01-track.mp3", (int?)88)
                },
                defaultEditionId: 0);

            Assert.That(assignments["/audiobooks/Dune/01-track.mp3"], Is.EqualTo((1, 2)));
            Assert.That(assignments["/audiobooks/Dune/02-track.mp3"], Is.EqualTo((2, 2)));
        }

        [Test]
        public void should_build_multipart_assignments_for_same_edition_matroska_audio_files()
        {
            var assignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                new[]
                {
                    ("/audiobooks/Dune/02-track.mka", (int?)88),
                    ("/audiobooks/Dune/01-track.mka", (int?)88)
                },
                defaultEditionId: 0);

            Assert.That(assignments["/audiobooks/Dune/01-track.mka"], Is.EqualTo((1, 2)));
            Assert.That(assignments["/audiobooks/Dune/02-track.mka"], Is.EqualTo((2, 2)));
        }
    }
}
