using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;

namespace Chaptarr.Core.Test.ApiV1
{
    [TestFixture]
    public class BookStatisticsResourceFixture
    {
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
    }
}
