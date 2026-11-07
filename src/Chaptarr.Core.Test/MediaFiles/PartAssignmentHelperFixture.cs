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
        public void should_use_embedded_disc_and_track_numbers_before_natural_path_order()
        {
            var edition = new Edition { Id = 102 };
            var localBooks = new List<LocalBook>
            {
                new LocalBook
                {
                    Edition = edition,
                    Path = "/audiobooks/Dune/01-disc-2-track-1.mp3",
                    Quality = new QualityModel(Quality.MP3),
                    RawTags = new RawFileTags { AllTags = Tags(("TRACKNUMBER", "1/10"), ("DISCNUMBER", "2/2")) }
                },
                new LocalBook
                {
                    Edition = edition,
                    Path = "/audiobooks/Dune/02-disc-1-track-2.mp3",
                    Quality = new QualityModel(Quality.MP3),
                    RawTags = new RawFileTags { AllTags = Tags(("TRACKNUMBER", "2"), ("DISCNUMBER", "1")) }
                },
                new LocalBook
                {
                    Edition = edition,
                    Path = "/audiobooks/Dune/03-disc-1-track-1.mp3",
                    Quality = new QualityModel(Quality.MP3),
                    RawTags = new RawFileTags { AllTags = Tags(("TRACKNUMBER", "1"), ("DISCNUMBER", "1/2")) }
                }
            };

            PartAssignmentHelper.NormalizeLocalBooksByEdition(localBooks);

            Assert.That(localBooks.Single(book => book.Path.EndsWith("03-disc-1-track-1.mp3")).Part, Is.EqualTo(1));
            Assert.That(localBooks.Single(book => book.Path.EndsWith("02-disc-1-track-2.mp3")).Part, Is.EqualTo(2));
            Assert.That(localBooks.Single(book => book.Path.EndsWith("01-disc-2-track-1.mp3")).Part, Is.EqualTo(3));
            Assert.That(localBooks.All(book => book.PartCount == 3), Is.True);
        }

        [Test]
        public void should_restore_missing_parts_for_existing_book_files_by_disc_and_track_tags()
        {
            var files = new List<BookFile>
            {
                new BookFile
                {
                    EditionId = 103,
                    Path = "/audiobooks/Dune/01-disc-2-track-1.mp3",
                    MediaType = "audiobook",
                    Quality = new QualityModel(Quality.MP3),
                    AllTags = Tags(("TRACKNUMBER", "1"), ("DISCNUMBER", "2"))
                },
                new BookFile
                {
                    EditionId = 103,
                    Path = "/audiobooks/Dune/02-disc-1-track-2.mp3",
                    MediaType = "audiobook",
                    Quality = new QualityModel(Quality.MP3),
                    AllTags = Tags(("TRACKNUMBER", "2"), ("DISCNUMBER", "1"))
                },
                new BookFile
                {
                    EditionId = 103,
                    Path = "/audiobooks/Dune/03-disc-1-track-1.mp3",
                    MediaType = "audiobook",
                    Quality = new QualityModel(Quality.MP3),
                    AllTags = Tags(("TRACKNUMBER", "1"), ("DISCNUMBER", "1"))
                }
            };

            PartAssignmentHelper.NormalizeBookFilesByEdition(files);

            Assert.That(files.Single(file => file.Path.EndsWith("03-disc-1-track-1.mp3")).Part, Is.EqualTo(1));
            Assert.That(files.Single(file => file.Path.EndsWith("02-disc-1-track-2.mp3")).Part, Is.EqualTo(2));
            Assert.That(files.Single(file => file.Path.EndsWith("01-disc-2-track-1.mp3")).Part, Is.EqualTo(3));
            Assert.That(files.All(file => file.PartCount == 3), Is.True);
        }

        [Test]
        public void should_use_embedded_disc_and_track_numbers_for_download_assignments()
        {
            var assignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                new[]
                {
                    ("/audiobooks/Dune/01-disc-2-track-1.mp3", (int?)88, Tags(("TRACKNUMBER", "1"), ("DISCNUMBER", "2"))),
                    ("/audiobooks/Dune/02-disc-1-track-2.mp3", (int?)88, Tags(("TRACKNUMBER", "2"), ("DISCNUMBER", "1"))),
                    ("/audiobooks/Dune/03-disc-1-track-1.mp3", (int?)88, Tags(("TRACKNUMBER", "1"), ("DISCNUMBER", "1")))
                },
                defaultEditionId: 0);

            Assert.That(assignments["/audiobooks/Dune/03-disc-1-track-1.mp3"], Is.EqualTo((1, 3)));
            Assert.That(assignments["/audiobooks/Dune/02-disc-1-track-2.mp3"], Is.EqualTo((2, 3)));
            Assert.That(assignments["/audiobooks/Dune/01-disc-2-track-1.mp3"], Is.EqualTo((3, 3)));
        }

        [Test]
        public void should_fall_back_to_natural_path_order_when_track_numbers_are_missing_or_ambiguous()
        {
            var assignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                new[]
                {
                    ("/audiobooks/Dune/02-track.mp3", (int?)88, Tags(("TRACKNUMBER", "1"))),
                    ("/audiobooks/Dune/01-track.mp3", (int?)88, new Dictionary<string, List<string>>())
                },
                defaultEditionId: 0);

            Assert.That(assignments["/audiobooks/Dune/01-track.mp3"], Is.EqualTo((1, 2)));
            Assert.That(assignments["/audiobooks/Dune/02-track.mp3"], Is.EqualTo((2, 2)));
        }

        [Test]
        public void should_fall_back_to_natural_path_order_when_disc_tags_are_incomplete()
        {
            var assignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                new[]
                {
                    ("/audiobooks/Dune/01-track.mp3", (int?)88, Tags(("TRACKNUMBER", "2"))),
                    ("/audiobooks/Dune/02-track.mp3", (int?)88, Tags(("TRACKNUMBER", "1"), ("DISCNUMBER", "1")))
                },
                defaultEditionId: 0);

            Assert.That(assignments["/audiobooks/Dune/01-track.mp3"], Is.EqualTo((1, 2)));
            Assert.That(assignments["/audiobooks/Dune/02-track.mp3"], Is.EqualTo((2, 2)));
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

        private static Dictionary<string, List<string>> Tags(params (string Key, string Value)[] values)
        {
            return values.ToDictionary(value => value.Key, value => new List<string> { value.Value });
        }
    }
}
