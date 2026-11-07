using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.Clients;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class BookDownloadCategorySettingsFixture
    {
        [Test]
        public void should_fall_back_to_legacy_category_when_a_format_category_is_blank()
        {
            var settings = new TestSettings
            {
                MusicCategory = "books",
                AudiobookCategory = "audio",
                EbookCategory = ""
            };

            Assert.That(BookDownloadCategorySettings.GetCategory(settings, BookMediaType.Ebook), Is.EqualTo("books"));
            Assert.That(BookDownloadCategorySettings.GetCategory(settings, BookMediaType.Audiobook), Is.EqualTo("audio"));
            Assert.That(BookDownloadCategorySettings.GetConfiguredCategories(settings), Is.EqualTo(new[] { "books", "audio" }));
        }

        [Test]
        public void should_trim_and_deduplicate_categories_case_insensitively()
        {
            var settings = new TestSettings
            {
                MusicCategory = "legacy",
                AudiobookCategory = "shared",
                EbookCategory = " ebooks "
            };

            Assert.That(BookDownloadCategorySettings.GetConfiguredCategories(settings), Is.EqualTo(new[] { "ebooks", "shared" }));
        }

        [Test]
        public void should_match_legacy_fallback_and_default_categories()
        {
            var settings = new TestSettings
            {
                MusicCategory = "books",
                AudiobookCategory = "audio",
                EbookCategory = ""
            };

            Assert.That(BookDownloadCategorySettings.MatchesConfiguredCategory(settings, "BOOKS"), Is.True);
            Assert.That(BookDownloadCategorySettings.MatchesConfiguredCategory(settings, "other"), Is.False);
            Assert.That(BookDownloadCategorySettings.MatchesConfiguredCategory(new TestSettings(), "*"), Is.True);
        }

        [Test]
        public void should_find_fallback_category_in_path_segments()
        {
            var settings = new TestSettings
            {
                MusicCategory = "books",
                AudiobookCategory = "audio",
                EbookCategory = ""
            };

            Assert.That(BookDownloadCategorySettings.FindCategoryInPath(settings, "/downloads/books/item.epub"), Is.EqualTo("books"));
            Assert.That(BookDownloadCategorySettings.FindCategoryInPath(settings, "/downloads/notbooks/item.epub"), Is.Null);
        }

        [Test]
        public void should_report_the_format_field_for_a_legacy_fallback_validation_error()
        {
            var settings = new TestSettings
            {
                MusicCategory = "books",
                AudiobookCategory = "audio",
                EbookCategory = ""
            };

            Assert.That(BookDownloadCategorySettings.GetCategoryFieldName(settings, "books"), Is.EqualTo("MusicCategory"));
            Assert.That(BookDownloadCategorySettings.GetCategoryFieldName(settings, "audio"), Is.EqualTo("AudiobookCategory"));
        }

        private sealed class TestSettings : IBookDownloadCategorySettings
        {
            public string MusicCategory { get; set; }
            public string EbookCategory { get; set; }
            public string AudiobookCategory { get; set; }
        }
    }
}
