using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportProductionTypeReplacementFixture
    {
        private const int EditionId = 7;
        private const string Incoming = "/downloads/book.m4b";

        private static BookFile Existing(bool graphicAudio, string mediaType = "audiobook")
        {
            return new BookFile { Id = 1, Path = "/library/book.m4b", EditionId = EditionId, IsGraphicAudio = graphicAudio, MediaType = mediaType };
        }

        [Test]
        public void should_not_replace_dramatized_recording_with_straight_read()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(true), Incoming, EditionId, false, null, 0, false), Is.False);
        }

        [Test]
        public void should_not_replace_straight_read_with_dramatized_recording()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(false), Incoming, EditionId, false, null, 0, true), Is.False);
        }

        [Test]
        public void should_replace_same_production_type()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(true), Incoming, EditionId, false, null, 0, true), Is.True);
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(false), Incoming, EditionId, false, null, 0, false), Is.True);
        }

        [Test]
        public void should_allow_explicit_replace_across_production_types()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(true), Incoming, EditionId, true, null, 0, false), Is.True);
        }

        [Test]
        public void should_ignore_production_type_for_ebooks_and_unknown_incoming()
        {
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(true, "ebook"), Incoming, EditionId, false, null, 0, false), Is.True);
            Assert.That(ImportApprovedBooks.IsReplaceableExistingFile(Existing(true), Incoming, EditionId, false, null, 0, null), Is.True);
        }
    }
}
