using NzbDrone.Core.Books;
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

        [Test]
        public void bookshelf_compatibility_should_set_zero_legacy_statistics_when_no_books_exist()
        {
            var resources = new List<AuthorResource>
            {
                new AuthorResource { Id = 1, Statistics = new AuthorStatisticsResource { BookCount = 4, TotalBookCount = 4 } },
                new AuthorResource { Id = 2, Statistics = new AuthorStatisticsResource { BookCount = 9, TotalBookCount = 9 } }
            };
            var legacyStatistics = new Dictionary<int, AuthorStatistics>
            {
                [1] = Statistics(authorId: 1, books: 3, files: 1, size: 100)
            };

            AuthorController.LinkBookshelfAuthorStatistics(resources, legacyStatistics);

            Assert.Multiple(() =>
            {
                Assert.That(resources[0].Statistics.BookCount, Is.EqualTo(3));
                Assert.That(resources[1].Statistics.BookCount, Is.Zero,
                    "BookshelfNG returns zero statistics for authors that have no statistics row");
                Assert.That(resources[1].Statistics.TotalBookCount, Is.Zero);
            });
        }

        [Test]
        public void legacy_embedded_work_should_combine_format_monitoring_without_using_the_ignored_raw_flag()
        {
            var books = new List<Book>
            {
                new() { Id = 30, MediaType = BookMediaType.Audiobook, AudiobookMonitored = true },
                new() { Id = 31, MediaType = BookMediaType.Ebook, EbookMonitored = false },
                new() { Id = 32, MediaType = BookMediaType.Ebook, EbookMonitored = false }
            };
            var mappings = new List<BookshelfLegacyIdentityMapEntry>
            {
                new() { SourceId = 30, TargetId = 30 },
                new() { SourceId = 30, TargetId = 31 },
                new() { SourceId = 32, TargetId = 32 }
            };

            var flags = AuthorController.BuildBookshelfWorkMonitoring(books, mappings);
            Assert.That(flags[30], Is.True);
            Assert.That(flags[31], Is.True);
            Assert.That(flags[32], Is.False);
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
