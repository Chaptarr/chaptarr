using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PostImportCategoryResolverFixture
    {
        [Test]
        public void should_prefer_configured_post_import_category_when_base_categories_are_the_same()
        {
            var item = new DownloadClientItem
            {
                Category = "chaptarr",
                MediaType = null
            };

            var resolved = PostImportCategoryResolver.Resolve(
                item,
                audiobookCategory: "chaptarr",
                ebookCategory: "chaptarr",
                audiobookImportedCategory: "chaptarr-imported",
                ebookImportedCategory: "");

            Assert.That(resolved, Is.EqualTo("chaptarr-imported"));
        }

        [Test]
        public void should_not_fallback_to_other_post_import_category_when_categories_are_distinct()
        {
            var item = new DownloadClientItem
            {
                Category = "ebooks",
                MediaType = null
            };

            var resolved = PostImportCategoryResolver.Resolve(
                item,
                audiobookCategory: "audiobooks",
                ebookCategory: "ebooks",
                audiobookImportedCategory: "audiobooks-imported",
                ebookImportedCategory: "");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void should_resolve_explicit_ebook_media_type_to_ebook_post_import_category()
        {
            var item = new DownloadClientItem
            {
                Category = "audiobooks",
                MediaType = BookMediaType.Ebook
            };

            var resolved = PostImportCategoryResolver.Resolve(
                item,
                audiobookCategory: "audiobooks",
                ebookCategory: "ebooks",
                audiobookImportedCategory: "",
                ebookImportedCategory: "ebooks-imported");

            Assert.That(resolved, Is.EqualTo("ebooks-imported"));
        }

        [Test]
        public void should_not_preserve_explicit_audiobook_when_only_ebook_post_import_category_exists()
        {
            var item = new DownloadClientItem
            {
                Category = "audiobooks",
                MediaType = BookMediaType.Audiobook
            };

            var resolved = PostImportCategoryResolver.Resolve(
                item,
                audiobookCategory: "audiobooks",
                ebookCategory: "ebooks",
                audiobookImportedCategory: "",
                ebookImportedCategory: "ebooks-imported");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void should_not_consider_item_preserved_until_category_matches_post_import_category()
        {
            var item = new DownloadClientItem
            {
                Category = "audiobooks",
                MediaType = BookMediaType.Audiobook
            };

            var preserved = PostImportCategoryResolver.IsInResolvedPostImportCategory(
                item,
                audiobookCategory: "audiobooks",
                ebookCategory: "ebooks",
                audiobookImportedCategory: "audiobooks-imported",
                ebookImportedCategory: "ebooks-imported");

            Assert.That(preserved, Is.False);
        }

        [Test]
        public void should_consider_item_preserved_after_category_matches_post_import_category()
        {
            var item = new DownloadClientItem
            {
                Category = "audiobooks-imported",
                MediaType = BookMediaType.Audiobook
            };

            var preserved = PostImportCategoryResolver.IsInResolvedPostImportCategory(
                item,
                audiobookCategory: "audiobooks",
                ebookCategory: "ebooks",
                audiobookImportedCategory: "audiobooks-imported",
                ebookImportedCategory: "ebooks-imported");

            Assert.That(preserved, Is.True);
        }
    }
}
