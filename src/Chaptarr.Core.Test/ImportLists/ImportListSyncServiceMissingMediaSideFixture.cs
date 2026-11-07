using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListSyncServiceMissingMediaSideFixture
    {
        private static GoodreadsBookshelfImportListSettings Settings(bool audiobooks, bool ebooks)
        {
            return new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = audiobooks, MonitorEbooks = ebooks };
        }

        private static Author AudiobookOnlyAuthor()
        {
            return new Author
            {
                Name = "Audio Author",
                AudiobookRootFolderPath = "/library/audiobooks",
                AudiobookQualityProfileId = 1
            };
        }

        [Test]
        public void should_detect_an_audiobook_only_author_when_the_list_monitors_ebooks()
        {
            Assert.That(ImportListSyncService.AuthorLacksEnabledMediaSide(AudiobookOnlyAuthor(), Settings(true, true)), Is.True);
            Assert.That(ImportListSyncService.AuthorLacksEnabledMediaSide(AudiobookOnlyAuthor(), Settings(false, true)), Is.True);
        }

        [Test]
        public void should_not_flag_an_author_when_every_enabled_side_is_configured()
        {
            Assert.That(ImportListSyncService.AuthorLacksEnabledMediaSide(AudiobookOnlyAuthor(), Settings(true, false)), Is.False);

            var both = AudiobookOnlyAuthor();
            both.EbookRootFolderPath = "/library/ebooks";
            both.EbookQualityProfileId = 2;

            Assert.That(ImportListSyncService.AuthorLacksEnabledMediaSide(both, Settings(true, true)), Is.False);
        }

        [Test]
        public void should_treat_a_root_folder_without_a_profile_as_missing()
        {
            var author = AudiobookOnlyAuthor();
            author.EbookRootFolderPath = "/library/ebooks";

            Assert.That(ImportListSyncService.AuthorLacksEnabledMediaSide(author, Settings(false, true)), Is.True);
        }
    }
}
