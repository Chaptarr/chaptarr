using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class WorkIdMatcherFixture
    {
        [Test]
        public void work_id_matches_should_not_promote_shared_asin_over_work_ids()
        {
            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:work-1",
                ASIN = "B00SHAREDASIN"
            };

            var ebook = new Book
            {
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = "hc:work-2",
                ASIN = "B00SHAREDASIN"
            };

            Assert.That(WorkIdMatcher.WorkIdMatches(audiobook, ebook), Is.False);
            Assert.That(WorkIdMatcher.WorkProviderIdMatches(audiobook, ebook), Is.False);
            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(audiobook, ebook), Is.False);
        }

        [Test]
        public void cross_format_safe_matches_should_still_allow_same_format_edition_matches()
        {
            var firstAudiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                ASIN = "B00SHAREDASIN"
            };

            var secondAudiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                ASIN = "B00SHAREDASIN"
            };

            Assert.That(WorkIdMatcher.WorkIdMatches(firstAudiobook, secondAudiobook), Is.True);
            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(firstAudiobook, secondAudiobook), Is.True);
        }

        [Test]
        public void work_id_matches_should_not_bridge_asin_only_row_to_work_id_row()
        {
            var workBacked = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:work-1",
                ASIN = "B00SHAREDASIN"
            };

            var asinOnly = new Book
            {
                MediaType = BookMediaType.Audiobook,
                ASIN = "B00SHAREDASIN"
            };

            Assert.That(WorkIdMatcher.WorkIdMatches(workBacked, asinOnly), Is.False);
            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(workBacked, asinOnly), Is.False);
        }

        [Test]
        public void work_id_matches_should_not_use_edition_ids_across_media_types()
        {
            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                ASIN = "B00SHAREDASIN"
            };

            var ebook = new Book
            {
                MediaType = BookMediaType.Ebook,
                ASIN = "B00SHAREDASIN"
            };

            Assert.That(WorkIdMatcher.WorkIdMatches(audiobook, ebook), Is.False);
            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(audiobook, ebook), Is.False);
        }

        [Test]
        public void work_provider_matches_should_ignore_base_book_id()
        {
            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "hc:work-1"
            };

            var second = new Book
            {
                MediaType = BookMediaType.Ebook,
                BaseBookId = "hc:work-1"
            };

            Assert.That(WorkIdMatcher.WorkProviderIdMatches(first, second), Is.False);
            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(first, second), Is.False);
        }
    }
}
