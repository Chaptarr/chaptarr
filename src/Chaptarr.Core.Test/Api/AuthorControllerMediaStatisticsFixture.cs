using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerMediaStatisticsFixture
    {
        [Test]
        public void unscoped_author_list_should_expose_each_media_side_and_their_combined_totals()
        {
            var resources = new List<AuthorResource>
            {
                new AuthorResource { Id = 1 },
                new AuthorResource { Id = 2 }
            };
            var audiobookStatistics = new Dictionary<int, AuthorStatistics>
            {
                [1] = Statistics(authorId: 1, books: 720, files: 0, size: 0)
            };
            var ebookStatistics = new Dictionary<int, AuthorStatistics>
            {
                [1] = Statistics(authorId: 1, books: 566, files: 3, size: 5061609),
                [2] = Statistics(authorId: 2, books: 5, files: 1, size: 100)
            };

            AuthorController.LinkMediaTypeAuthorStatistics(resources, audiobookStatistics, ebookStatistics);

            Assert.Multiple(() =>
            {
                Assert.That(resources[0].AudiobookStatistics.BookCount, Is.EqualTo(720));
                Assert.That(resources[0].AudiobookStatistics.BookFileCount, Is.Zero);
                Assert.That(resources[0].EbookStatistics.BookCount, Is.EqualTo(566));
                Assert.That(resources[0].EbookStatistics.BookFileCount, Is.EqualTo(3));
                Assert.That(resources[0].Statistics.BookCount, Is.EqualTo(1286));
                Assert.That(resources[0].Statistics.BookFileCount, Is.EqualTo(3));
                Assert.That(resources[0].Statistics.SizeOnDisk, Is.EqualTo(5061609));

                Assert.That(resources[1].AudiobookStatistics, Is.Not.Null);
                Assert.That(resources[1].AudiobookStatistics.BookCount, Is.Zero,
                    "A missing media side must be explicit zeroes instead of falling back to combined statistics");
                Assert.That(resources[1].Statistics.BookCount, Is.EqualTo(5));
            });
        }

        private static AuthorStatistics Statistics(int authorId, int books, int files, long size)
        {
            return new AuthorStatistics
            {
                AuthorId = authorId,
                BookCount = books,
                AvailableBookCount = files > 0 ? 1 : 0,
                TotalBookCount = books,
                BookFileCount = files,
                SizeOnDisk = size
            };
        }
    }
}
