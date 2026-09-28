using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorApplyChangesPathPreservationFixture
    {
        private static Author StoredAuthor()
        {
            return new Author
            {
                Path = "/library/authors/Stored Author",
                AudiobookRootFolderPath = "/library/audiobooks",
                EbookRootFolderPath = "/library/ebooks"
            };
        }

        [Test]
        public void should_preserve_stored_paths_when_incoming_author_omits_them()
        {
            var stored = StoredAuthor();

            // An update that does not mention paths - e.g. a PUT that only changes
            // monitoring, or an Author built from remote metadata during a refresh.
            stored.ApplyChanges(new Author());

            Assert.Multiple(() =>
            {
                Assert.That(stored.Path, Is.EqualTo("/library/authors/Stored Author"));
                Assert.That(stored.AudiobookRootFolderPath, Is.EqualTo("/library/audiobooks"));
                Assert.That(stored.EbookRootFolderPath, Is.EqualTo("/library/ebooks"));
            });
        }

        [Test]
        public void should_apply_paths_when_incoming_author_supplies_them()
        {
            var stored = StoredAuthor();

            stored.ApplyChanges(new Author
            {
                Path = "/library/authors/Moved Author",
                AudiobookRootFolderPath = "/library/audiobooks-2",
                EbookRootFolderPath = "/library/ebooks-2"
            });

            Assert.Multiple(() =>
            {
                Assert.That(stored.Path, Is.EqualTo("/library/authors/Moved Author"));
                Assert.That(stored.AudiobookRootFolderPath, Is.EqualTo("/library/audiobooks-2"));
                Assert.That(stored.EbookRootFolderPath, Is.EqualTo("/library/ebooks-2"));
            });
        }

        [Test]
        public void should_preserve_each_path_independently()
        {
            var stored = StoredAuthor();

            // Only the audiobook root is being changed; the other two must survive.
            stored.ApplyChanges(new Author { AudiobookRootFolderPath = "/library/audiobooks-2" });

            Assert.Multiple(() =>
            {
                Assert.That(stored.AudiobookRootFolderPath, Is.EqualTo("/library/audiobooks-2"));
                Assert.That(stored.Path, Is.EqualTo("/library/authors/Stored Author"));
                Assert.That(stored.EbookRootFolderPath, Is.EqualTo("/library/ebooks"));
            });
        }
    }
}
