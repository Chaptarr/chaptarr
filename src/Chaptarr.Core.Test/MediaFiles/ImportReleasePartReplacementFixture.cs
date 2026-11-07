using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportReleasePartReplacementFixture
    {
        private const int EditionId = 7;
        private const string Incoming = "/downloads/part2/book.m4b";

        private static BookFile Existing(int releasePart, string path = "/library/book.m4b")
        {
            return new BookFile { Id = 1, Path = path, EditionId = EditionId, ReleasePart = releasePart };
        }

        [Test]
        public void should_keep_sibling_part_when_importing_another_part()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(1), Incoming, EditionId, false, null, 2), Is.False);
        }

        [Test]
        public void should_keep_sibling_part_when_import_is_forced()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(1), Incoming, EditionId, true, null, 2), Is.False);
        }

        [Test]
        public void should_keep_a_complete_file_when_importing_one_part()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(0), Incoming, EditionId, false, null, 2), Is.False);
        }

        [Test]
        public void should_replace_the_same_part_from_an_earlier_grab()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(2), Incoming, EditionId, false, null, 2), Is.True);
        }

        [Test]
        public void should_let_a_complete_file_replace_parts()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(1), Incoming, EditionId, false, null, 0), Is.True);
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(0), Incoming, EditionId, false, null), Is.True);
        }
    }
}
