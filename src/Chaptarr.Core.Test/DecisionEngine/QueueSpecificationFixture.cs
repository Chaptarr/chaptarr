using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using QueueItem = NzbDrone.Core.Queue.Queue;
using IQueueService = NzbDrone.Core.Queue.IQueueService;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class QueueSpecificationFixture
    {
        [Test]
        public void should_reject_when_same_book_already_has_queued_release_that_meets_cutoff()
        {
            var author = BuildAuthor();
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.MP3);
            var queueItem = BuildQueueItem(author, book, Quality.M4B, TrackedDownloadState.Downloading);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("already meets cutoff"));
        }

        [Test]
        public void should_accept_true_cutoff_upgrade_for_same_book()
        {
            var author = BuildAuthor();
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.M4B);
            var queueItem = BuildQueueItem(author, book, Quality.MP3, TrackedDownloadState.Downloading);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_when_queued_release_will_convert_to_cutoff_quality()
        {
            var author = BuildAuthor(convertToQuality: Quality.M4B);
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.M4B);
            var queueItem = BuildQueueItem(author, book, Quality.MP3, TrackedDownloadState.Downloading);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("already meets cutoff"));
        }

        [Test]
        public void should_reject_equal_release_when_same_book_is_stuck_on_import_blocked()
        {
            var author = BuildAuthor();
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.MP3);
            var queueItem = BuildQueueItem(author, book, Quality.MP3, TrackedDownloadState.ImportBlocked);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("equal or higher preference"));
        }

        [Test]
        public void should_reject_when_queue_row_only_has_book_and_quality()
        {
            var author = BuildAuthor();
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.MP3);
            var queueItem = BuildQueueItem(author, book, Quality.M4B, TrackedDownloadState.Downloading, includeRemoteBook: false);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("already meets cutoff"));
        }

        [Test]
        public void should_reject_when_queue_row_only_has_grab_target_book_ids()
        {
            var author = BuildAuthor();
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.MP3);
            var queueItem = BuildQueueItem(author, null, Quality.M4B, TrackedDownloadState.ImportBlocked, includeRemoteBook: false);
            queueItem.TargetBookIds = new List<int> { book.Id };

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("already meets cutoff"));
        }

        [Test]
        public void should_not_block_sibling_media_type_book()
        {
            var author = BuildAuthor(includeEbookProfile: true);
            var audiobook = BuildBook(author, 100, BookMediaType.Audiobook);
            var ebook = BuildBook(author, 200, BookMediaType.Ebook);
            var subject = BuildRemoteBook(author, ebook, Quality.EPUB);
            var queueItem = BuildQueueItem(author, audiobook, Quality.M4B, TrackedDownloadState.Downloading);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_ignore_download_failed_pending_queue_item()
        {
            var author = BuildAuthor();
            var book = BuildBook(author, 100, BookMediaType.Audiobook);
            var subject = BuildRemoteBook(author, book, Quality.MP3);
            var queueItem = BuildQueueItem(author, book, Quality.M4B, TrackedDownloadState.DownloadFailedPending);

            var decision = BuildSubject(queueItem).IsSatisfiedBy(subject, null);

            Assert.That(decision.Accepted, Is.True);
        }

        private static QueueSpecification BuildSubject(params QueueItem[] queue)
        {
            IConfigService configService = ConfigServiceTestProxy.Create();
            var logger = LogManager.GetCurrentClassLogger();

            return new QueueSpecification(
                new StubQueueService(new List<QueueItem>(queue)),
                new UpgradableSpecification(configService, logger),
                new NoOpCustomFormatCalculationService(),
                configService,
                logger);
        }

        private static Author BuildAuthor(bool includeEbookProfile = false, Quality convertToQuality = null)
        {
            var audiobookProfile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                UpgradeAllowed = true,
                ConvertToQualityId = convertToQuality?.Id,
                Cutoff = Quality.M4B.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };

            var ebookProfile = new QualityProfile
            {
                Id = 3,
                Name = "Ebook",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Cutoff = Quality.EPUB.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.PDF, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true }
                }
            };

            var author = new Author
            {
                Id = 10,
                Name = "Martha Wells",
                AudiobookQualityProfileId = audiobookProfile.Id,
                AudiobookQualityProfile = audiobookProfile
            };

            if (includeEbookProfile)
            {
                author.EbookQualityProfileId = ebookProfile.Id;
                author.EbookQualityProfile = ebookProfile;
            }

            return author;
        }

        private static Book BuildBook(Author author, int id, BookMediaType mediaType)
        {
            return new Book
            {
                Id = id,
                AuthorId = author.Id,
                Author = author,
                Title = mediaType == BookMediaType.Ebook ? "All Systems Red ebook" : "All Systems Red audio",
                MediaType = mediaType
            };
        }

        private static RemoteBook BuildRemoteBook(Author author, Book book, Quality quality)
        {
            return new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                Release = new ReleaseInfo { Title = $"{author.Name} - {book.Title} [{quality.Name}]" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    Quality = new QualityModel(quality)
                }
            };
        }

        private static QueueItem BuildQueueItem(Author author, Book book, Quality quality, TrackedDownloadState state, bool includeRemoteBook = true)
        {
            return new QueueItem
            {
                Author = author,
                Book = book,
                Quality = new QualityModel(quality),
                Title = $"{author.Name} - {book?.Title ?? "Queued Book"} [{quality.Name}]",
                Size = 100,
                TrackedDownloadState = state,
                RemoteBook = includeRemoteBook ? BuildRemoteBook(author, book, quality) : null
            };
        }

        private sealed class StubQueueService : IQueueService
        {
            private readonly List<QueueItem> _queue;

            public StubQueueService(List<QueueItem> queue)
            {
                _queue = queue;
            }

            public List<QueueItem> GetQueue()
            {
                return _queue;
            }

            public QueueItem Find(int id)
            {
                return null;
            }

            public void Remove(int id)
            {
            }
        }

        private sealed class NoOpCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }
    }
}
