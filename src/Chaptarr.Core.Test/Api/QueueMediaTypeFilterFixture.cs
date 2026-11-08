using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Queue;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using CoreQueue = NzbDrone.Core.Queue.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class QueueMediaTypeFilterFixture
    {
        [Test]
        public void should_keep_unknown_rows_when_filtering_by_media_type()
        {
            var queue = new List<CoreQueue>
            {
                new CoreQueue { Id = 1, Author = null, Book = null },
                new CoreQueue { Id = 2, Author = new Author(), Book = null },
                new CoreQueue { Id = 3, Author = new Author(), Book = new Book { MediaType = BookMediaType.Ebook } },
                new CoreQueue { Id = 4, Author = new Author(), Book = new Book { MediaType = BookMediaType.Audiobook } }
            };

            var filtered = QueueMediaTypeFilter.FilterByMediaType(queue, "ebook").ToList();

            Assert.That(filtered.Select(q => q.Id), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [TestCase("ebook", new[] { 3, 4, 5 })]
        [TestCase("audiobook", new[] { 1, 2, 5 })]
        [TestCase(null, new[] { 1, 2, 3, 4, 5 })]
        public void unresolved_rows_should_use_known_quality_and_keep_unknown_quality_visible(string mediaType, int[] expectedIds)
        {
            var queue = new List<CoreQueue>
            {
                new() { Id = 1, Quality = new QualityModel(Quality.M4B) },
                new() { Id = 2, Quality = new QualityModel(Quality.UnknownAudio) },
                new() { Id = 3, Quality = new QualityModel(Quality.EPUB) },
                new() { Id = 4, Quality = new QualityModel(Quality.PDF) },
                new() { Id = 5, Quality = new QualityModel(Quality.Unknown), Title = "The Audible Ebook Handbook" }
            };

            Assert.That(QueueMediaTypeFilter.FilterByMediaType(queue, mediaType).Select(q => q.Id), Is.EqualTo(expectedIds));
        }

        [TestCase("ebook", "M4B")]
        [TestCase("audiobook", "EPUB")]
        public void unresolved_tracked_downloads_should_follow_their_known_quality_side(string mediaType, string qualityName)
        {
            var row = new CoreQueue { Id = 17, DownloadId = "tracked-download", Quality = new QualityModel(qualityName == "M4B" ? Quality.M4B : Quality.EPUB) };
            Assert.That(QueueMediaTypeFilter.FilterByMediaType(new[] { row }, mediaType), Is.Empty);
        }

        [TestCase(BookMediaType.Audiobook, "audiobook")]
        [TestCase(BookMediaType.Ebook, "ebook")]
        public void proven_book_media_should_take_precedence_over_quality(BookMediaType bookMedia, string mediaType)
        {
            var queue = new CoreQueue
            {
                Id = 1, Book = new Book { MediaType = bookMedia },
                Quality = new QualityModel(bookMedia == BookMediaType.Ebook ? Quality.M4B : Quality.EPUB)
            };

            Assert.That(QueueMediaTypeFilter.FilterByMediaType(new[] { queue }, mediaType), Is.EqualTo(new[] { queue }));
        }

        [TestCase("audiobook", "M4B", 1, true)]
        [TestCase("ebook", "M4B", 0, false)]
        [TestCase("ebook", "EPUB", 1, true)]
        [TestCase("audiobook", "EPUB", 0, false)]
        [TestCase("ebook", "Unknown", 1, true)]
        [TestCase("audiobook", "Unknown", 1, true)]
        [TestCase("ebook", null, 1, true)]
        [TestCase(null, "M4B", 1, true)]
        public void status_should_count_visible_unresolved_pending_rows_as_warnings(string mediaType, string qualityName, int expectedCount, bool expectedWarnings)
        {
            var quality = qualityName switch
            {
                "M4B" => Quality.M4B,
                "EPUB" => Quality.EPUB,
                "Unknown" => Quality.Unknown,
                _ => null
            };
            var pending = new List<CoreQueue>
            {
                new() { Id = 1, Author = new Author { Id = 38 }, Book = null, Status = "warning", Quality = quality == null ? null : new QualityModel(quality) }
            };
            var queueService = Proxy<IQueueService>((method, args) => new List<CoreQueue>());
            var pendingService = Proxy<IPendingReleaseService>((method, args) => pending);
            var controller = new QueueStatusController(null, queueService, pendingService);

            var status = controller.GetQueueStatus(mediaType);

            Assert.Multiple(() =>
            {
                Assert.That(status.Count, Is.EqualTo(expectedCount));
                Assert.That(status.TotalCount, Is.EqualTo(expectedCount));
                Assert.That(status.Warnings, Is.EqualTo(expectedWarnings));
                Assert.That(status.UnknownWarnings, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void status_should_preserve_identified_pending_and_tracked_warning_behavior(bool trackedWarning)
        {
            var author = new Author { Id = 38 };
            var queue = trackedWarning ? new List<CoreQueue> { new() { Author = author, TrackedDownloadStatus = TrackedDownloadStatus.Warning } } : new List<CoreQueue>();
            var pending = new List<CoreQueue> { new() { Author = author, Book = new Book { Id = 501, MediaType = BookMediaType.Ebook }, Quality = new QualityModel(Quality.EPUB), Status = "delay" } };
            var controller = new QueueStatusController(null, Proxy<IQueueService>((method, args) => queue), Proxy<IPendingReleaseService>((method, args) => pending));

            var status = controller.GetQueueStatus("ebook");

            Assert.That(status.Warnings, Is.EqualTo(trackedWarning));
        }


        [TestCase("ebook")]
        [TestCase("audiobook")]
        public void unresolved_tracked_downloads_with_unknown_quality_should_remain_visible_on_both_sides(string mediaType)
        {
            var row = new CoreQueue { Id = 17, DownloadId = "tracked-download", Quality = new QualityModel(Quality.Unknown) };
            Assert.That(QueueMediaTypeFilter.FilterByMediaType(new[] { row }, mediaType), Is.EqualTo(new[] { row }));
        }
    }
}
