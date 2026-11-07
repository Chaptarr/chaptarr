using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class BookImportUnitGroupingFixture
    {
        [Test]
        public void standalone_ebooks_in_one_folder_should_each_be_their_own_unit()
        {
            var files = new[]
            {
                CreateFile(1, @"C:\audiobooks\audiobooks\audiobooks\Freida McFadden\Freida McFadden - The Housemaid Is Watching.epub".AsOsAgnostic(), "ebook"),
                CreateFile(2, @"C:\audiobooks\audiobooks\audiobooks\Freida McFadden\Freida McFadden - The Boyfriend - A Psychological Thriller.epub".AsOsAgnostic(), "ebook"),
                CreateFile(3, @"C:\audiobooks\audiobooks\Jim Murphy\Inner Excellence - Julian Mehne\Inner Excellence.m4b".AsOsAgnostic(), "audiobook")
            };

            var units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\audiobooks".AsOsAgnostic());

            Assert.That(units, Has.Count.EqualTo(3));
            Assert.That(units.SelectMany(unit => unit.Files).Count(), Is.EqualTo(3));
            Assert.That(units.All(unit => unit.Files.Count == 1), Is.True);
            Assert.That(units.Select(unit => unit.Key).Distinct().Count(), Is.EqualTo(3));
        }

        [Test]
        public void case_only_duplicate_paths_should_not_break_unit_grouping()
        {
            // A folder renamed only by casing (case-insensitive mounts, sync tools)
            // can leave the same file catalogued twice with paths that differ only
            // by case. Grouping must degrade to keeping one of them, not throw and
            // collapse the entire unmapped page to per-file units.
            var folder = @"C:\library\Author\Vision in Silver".AsOsAgnostic();
            var duplicateCasing = @"C:\library\Author\Vision In Silver".AsOsAgnostic();
            var files = new[]
            {
                CreateFile(1, $@"{folder}{Sep}Vision in Silver.m4b", "audiobook",
                    ("ARTIST", "Author"),
                    ("TITLE", "Vision in Silver")),
                CreateFile(2, $@"{duplicateCasing}{Sep}Vision in Silver.m4b", "audiobook",
                    ("ARTIST", "Author"),
                    ("TITLE", "Vision in Silver"))
            };

            IReadOnlyList<BookImportUnit> units = null;

            Assert.DoesNotThrow(() => units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\library".AsOsAgnostic()));
            Assert.That(units, Is.Not.Empty);
            Assert.That(units.SelectMany(unit => unit.Files).Any(file => file.Id == 1 || file.Id == 2), Is.True);
        }

        [Test]
        public void homogeneous_audio_tracks_should_share_one_unit()
        {
            var folder = @"C:\library\Michael Connelly\Schwarzes Echo".AsOsAgnostic();
            var files = new[]
            {
                CreateFile(1, $@"{folder}{Sep}Schwarzes Echo (5).mp3", "audiobook",
                    ("ARTIST", "Michael Connelly"),
                    ("ALBUM", "BOSCH Schwarzes Echo"),
                    ("TITLE", "Kapitel 05 BOSCH Schwarzes Echo"),
                    ("TRACKNUMBER", "005")),
                CreateFile(2, $@"{folder}{Sep}Schwarzes Echo (8).mp3", "audiobook",
                    ("ARTIST", "Michael Connelly"),
                    ("ALBUM", "BOSCH Schwarzes Echo"),
                    ("TITLE", "Kapitel 08 BOSCH Schwarzes Echo"),
                    ("TRACKNUMBER", "008"))
            };

            var units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\library".AsOsAgnostic());

            Assert.That(units, Has.Count.EqualTo(1));
            Assert.That(units[0].Files.Select(file => file.Id), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(units[0].RootPath, Is.EqualTo(folder));
        }

        [Test]
        public void repeated_author_name_alone_should_not_merge_direct_author_files()
        {
            var files = new[]
            {
                CreateFile(1, @"C:\library\Author\First Book.mp3".AsOsAgnostic(), "audiobook",
                    ("ARTIST", "Author"),
                    ("TITLE", "First Book")),
                CreateFile(2, @"C:\library\Author\Second Book.mp3".AsOsAgnostic(), "audiobook",
                    ("ARTIST", "Author"),
                    ("TITLE", "Second Book"))
            };

            var units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\library".AsOsAgnostic());

            Assert.That(units, Has.Count.EqualTo(2));
            Assert.That(units.All(unit => unit.Files.Count == 1), Is.True);
        }

        [Test]
        public void disc_only_sibling_folders_with_shared_evidence_should_form_one_unit()
        {
            var root = @"C:\library\Author\Multipart Book".AsOsAgnostic();
            var files = new[]
            {
                CreateFile(1, $@"{root}{Sep}CD1{Sep}01.mp3", "audiobook",
                    ("ARTIST", "Author"),
                    ("ALBUM", "Multipart Book"),
                    ("TITLE", "Chapter 1")),
                CreateFile(2, $@"{root}{Sep}Disc 2{Sep}02.mp3", "audiobook",
                    ("ARTIST", "Author"),
                    ("ALBUM", "Multipart Book"),
                    ("TITLE", "Chapter 2"))
            };

            var units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\library".AsOsAgnostic());

            Assert.That(units, Has.Count.EqualTo(1));
            Assert.That(units[0].Files.Select(file => file.Id), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(units[0].RootPath, Is.EqualTo(root));
        }

        [Test]
        public void arbitrary_sibling_folders_should_not_merge_even_when_tags_match()
        {
            var files = new[]
            {
                CreateFile(1, @"C:\library\Author\Book\Section A\01.mp3".AsOsAgnostic(), "audiobook",
                    ("ARTIST", "Author"),
                    ("ALBUM", "Book")),
                CreateFile(2, @"C:\library\Author\Book\Section B\02.mp3".AsOsAgnostic(), "audiobook",
                    ("ARTIST", "Author"),
                    ("ALBUM", "Book"))
            };

            var units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\library".AsOsAgnostic());

            Assert.That(units, Has.Count.EqualTo(2));
        }

        [Test]
        public void tagless_audio_files_in_one_folder_should_use_the_folder_fallback()
        {
            var folder = @"C:\library\Author\Tagless Book".AsOsAgnostic();
            var files = new[]
            {
                CreateFile(1, $@"{folder}{Sep}01.mp3", "audiobook"),
                CreateFile(2, $@"{folder}{Sep}02.mp3", "audiobook")
            };

            var units = BookImportUnitGroupingService.BuildUnmappedUnits(files, _ => @"C:\library".AsOsAgnostic());

            Assert.That(units, Has.Count.EqualTo(1));
            Assert.That(units[0].Files.Select(file => file.Id), Is.EquivalentTo(new[] { 1, 2 }));
        }

        private static char Sep => System.IO.Path.DirectorySeparatorChar;

        private static BookFile CreateFile(
            int id,
            string path,
            string mediaType,
            params (string Key, string Value)[] tags)
        {
            return new BookFile
            {
                Id = id,
                EditionId = 0,
                Path = path,
                MediaType = mediaType,
                AllTags = tags
                    .GroupBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(tag => tag.Value).ToList(),
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
