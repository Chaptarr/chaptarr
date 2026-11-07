using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;

namespace Chaptarr.Core.Test.ApiV1
{
    [TestFixture]
    public class BookStatisticsResourceFixture
    {
        [TestCase(0, 1, 0)]
        [TestCase(6, 1, 600)]
        [TestCase(6, 0, 0)]
        public void legacy_clients_should_receive_bookshelf_file_based_percentage(int files, int books, decimal expected)
        {
            var resource = new BookStatistics
            {
                BookFileCount = files,
                BookCount = books,
                AvailableBookCount = files > 0 ? 1 : 0
            }.ToResource(bookshelfCompatibility: true);

            Assert.That(resource.PercentOfBooks, Is.EqualTo(expected));
        }

        [Test]
        public void complete_multi_file_audiobook_should_report_100_percent()
        {
            var model = new BookStatistics
            {
                BookFileCount = 24,
                BookCount = 1,
                AvailableBookCount = 1,
                TotalBookCount = 1,
                SizeOnDisk = 1234
            };

            var resource = model.ToResource();

            Assert.That(resource.PercentOfBooks, Is.EqualTo(100));
        }

        [Test]
        public void missing_book_should_report_0_percent()
        {
            var model = new BookStatistics
            {
                BookFileCount = 0,
                BookCount = 0,
                AvailableBookCount = 0,
                TotalBookCount = 1
            };

            var resource = model.ToResource();

            Assert.That(resource.PercentOfBooks, Is.EqualTo(0));
        }

        [Test]
        public void should_map_format_specific_file_counts_and_sizes()
        {
            var model = new BookStatistics
            {
                BookFileCount = 3,
                EbookFileCount = 1,
                AudiobookFileCount = 2,
                SizeOnDisk = 450,
                EbookSizeOnDisk = 100,
                AudiobookSizeOnDisk = 350
            };

            var resource = model.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.BookFileCount, Is.EqualTo(3));
                Assert.That(resource.EbookFileCount, Is.EqualTo(1));
                Assert.That(resource.AudiobookFileCount, Is.EqualTo(2));
                Assert.That(resource.SizeOnDisk, Is.EqualTo(450));
                Assert.That(resource.EbookSizeOnDisk, Is.EqualTo(100));
                Assert.That(resource.AudiobookSizeOnDisk, Is.EqualTo(350));
            });
        }
    }
}
