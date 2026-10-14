using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ReleaseSplitPartPolicyFixture
    {
        private static LocalBook Incoming(int part, int count) => new LocalBook { Path = "/downloads/new.m4b", ReleasePart = part, ReleasePartCount = count };

        [Test]
        public void should_keep_files_of_another_part_of_the_split()
        {
            var part1 = new BookFile { Id = 1, Path = "/library/Iron Flame (Part 1 of 2) (01).mp3", ReleasePart = 1, ReleasePartCount = 2 };

            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(part1, Incoming(2, 2)), Is.True);
        }

        [Test]
        public void should_still_replace_the_same_part()
        {
            var oldPart2 = new BookFile { Id = 1, Path = "/library/old part 2.m4b", ReleasePart = 2, ReleasePartCount = 2 };

            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(oldPart2, Incoming(2, 2)), Is.False);
        }

        [Test]
        public void should_still_replace_a_whole_book_file()
        {
            var narrated = new BookFile { Id = 1, Path = "/library/Iron Flame.m4b" };

            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(narrated, Incoming(1, 2)), Is.False);
        }

        [Test]
        public void should_not_change_behaviour_for_whole_book_imports()
        {
            var part1 = new BookFile { Id = 1, Path = "/library/part1.m4b", ReleasePart = 1, ReleasePartCount = 2 };

            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(part1, new LocalBook { Path = "/downloads/whole.m4b" }), Is.False);
        }

        [Test]
        public void should_read_part_of_files_imported_before_it_was_recorded_from_scene_name()
        {
            var legacy = new BookFile { Id = 1, Path = "/library/Fourth Wing - GraphicAudio.m4b", SceneName = "Fourth Wing (2 of 2)" };

            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(legacy, Incoming(1, 2)), Is.True);
            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(legacy, Incoming(2, 2)), Is.False);
        }

        [Test]
        public void should_read_part_of_legacy_files_from_original_download_path()
        {
            var fromFolder = new BookFile { Id = 1, Path = "/library/a.mp3", OriginalFilePath = "Iron Flame (Part 1 of 2)/Chapter 1 - Iron Flame.mp3" };
            var fromFile = new BookFile { Id = 2, Path = "/library/b.m4b", OriginalFilePath = "Onyx Storm Part 1 of 2.m4b" };

            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(fromFolder, Incoming(2, 2)), Is.True);
            Assert.That(ReleaseSplitPartPolicy.IsSiblingPart(fromFile, Incoming(2, 2)), Is.True);
        }

        [Test]
        public void should_exclude_only_sibling_parts_from_replacement()
        {
            var existing = new List<BookFile>
            {
                new BookFile { Id = 1, Path = "/library/narrated.m4b" },
                new BookFile { Id = 2, Path = "/library/p1-01.mp3", ReleasePart = 1, ReleasePartCount = 2 },
                new BookFile { Id = 3, Path = "/library/p1-02.mp3", ReleasePart = 1, ReleasePartCount = 2 },
                new BookFile { Id = 4, Path = "/library/old-p2.m4b", ReleasePart = 2, ReleasePartCount = 2 }
            };

            var replaceable = ReleaseSplitPartPolicy.ExcludeSiblingParts(existing, new[] { new LocalBook(), Incoming(2, 2) });

            Assert.That(replaceable.Select(f => f.Id), Is.EquivalentTo(new[] { 1, 4 }));
        }

        [Test]
        public void should_return_all_files_when_import_is_not_a_split_part()
        {
            var existing = new List<BookFile>
            {
                new BookFile { Id = 1, Path = "/library/p1.m4b", ReleasePart = 1, ReleasePartCount = 2 }
            };

            Assert.That(ReleaseSplitPartPolicy.ExcludeSiblingParts(existing, new[] { new LocalBook() }), Has.Count.EqualTo(1));
        }

        private static RemoteBook Release(string title) => new RemoteBook
        {
            Release = new ReleaseInfo { Title = title },
            PackDetection = ReleasePackDetector.Detect(title, null, null)
        };

        [Test]
        public void should_not_let_part_one_on_disk_block_grabbing_part_two()
        {
            var existing = new List<BookFile>
            {
                new BookFile { Id = 1, Path = "/library/narrated.m4b" },
                new BookFile { Id = 2, Path = "/library/p1.m4b", ReleasePart = 1, ReleasePartCount = 2 }
            };

            var competing = ReleaseSplitPartPolicy.ExcludeSiblingParts(existing, Release("Rebecca Yarros - Onyx Storm (Part 2 of 2) [GraphicAudio]"));

            Assert.That(competing.Select(f => f.Id), Is.EquivalentTo(new[] { 1 }));
        }

        [Test]
        public void should_compare_a_whole_book_release_against_every_file()
        {
            var existing = new List<BookFile>
            {
                new BookFile { Id = 1, Path = "/library/p1.m4b", ReleasePart = 1, ReleasePartCount = 2 }
            };

            Assert.That(ReleaseSplitPartPolicy.ExcludeSiblingParts(existing, Release("Rebecca Yarros - Onyx Storm")).Count(), Is.EqualTo(1));
            Assert.That(ReleaseSplitPartPolicy.ExcludeSiblingParts(existing, new RemoteBook()).Count(), Is.EqualTo(1));
        }

        [Test]
        public void should_take_split_part_of_a_tracked_download_from_its_grabbed_title()
        {
            // As grabbed from MyAnonaMouse; the download's files are 001_ACOTAR0101P01.mp3 etc.
            var trackedFromHistory = new RemoteBook { Release = new ReleaseInfo { Title = "A Court of Thorns and Roses (1 of 2)" } };

            Assert.That(ReleaseSplitPartPolicy.GetSplitPartOfGrab(trackedFromHistory, "A Court of Thorns and Roses (1 of 2)"),
                Is.EqualTo(new ReleaseSplitPart(1, 2)));
        }

        [Test]
        public void should_prefer_pack_detection_of_the_grab_when_present()
        {
            Assert.That(ReleaseSplitPartPolicy.GetSplitPartOfGrab(Release("A Court of Mist and Fury (2 of 2)"), "ACOMAF"),
                Is.EqualTo(new ReleaseSplitPart(2, 2)));
        }

        [Test]
        public void should_find_no_split_part_for_a_whole_book_grab()
        {
            Assert.That(ReleaseSplitPartPolicy.GetSplitPartOfGrab(Release("A Court of Thorns and Roses"), "A Court of Thorns and Roses"), Is.Null);
            Assert.That(ReleaseSplitPartPolicy.GetSplitPartOfGrab(null, null), Is.Null);
        }

        [Test]
        public void should_mark_manually_imported_files_with_the_part_they_were_grabbed_as()
        {
            var files = new List<LocalBook> { new LocalBook { Path = "/seeds/ACOTAR 1, 2of 2.m4b" }, null };

            ReleaseSplitPartPolicy.ApplySplitPartOfGrab(files, null, "A Court of Thorns and Roses (2 of 2)");

            Assert.That(files[0].ReleasePart, Is.EqualTo(2));
            Assert.That(files[0].ReleasePartCount, Is.EqualTo(2));
        }

        [Test]
        public void should_leave_files_of_a_whole_book_grab_unmarked()
        {
            var file = new LocalBook { Path = "/downloads/book.m4b" };

            ReleaseSplitPartPolicy.ApplySplitPartOfGrab(new[] { file }, null, "A Court of Thorns and Roses");

            Assert.That(file.ReleasePart, Is.EqualTo(0));
        }
    }
}
