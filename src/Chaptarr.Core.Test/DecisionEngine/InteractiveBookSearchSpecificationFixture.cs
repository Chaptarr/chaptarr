using System.Collections.Generic;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class InteractiveBookSearchSpecificationFixture
    {
        private sealed class NoOpCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        [Test]
        public void monitored_book_should_accept_for_resolved_interactive_book_search()
        {
            var author = BuildAuthor(monitored: false);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_book_should_still_reject_outside_interactive_book_search()
        {
            var author = BuildAuthor(monitored: false);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: false);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.False);
        }

        [Test]
        public void monitored_book_should_accept_explicit_unmonitored_book_search()
        {
            var author = BuildAuthor(monitored: false);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: false);
            criteria.MonitoredBooksOnly = false;

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_book_should_use_media_settings_instead_of_the_legacy_author_rollup()
        {
            var author = BuildAuthor(monitored: false);
            author.AudiobookMonitored = true;
            var book = BuildBook(author, audiobookMonitored: true);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, searchCriteria: null);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_book_should_use_the_remote_author_without_loading_the_book_author()
        {
            var author = BuildAuthor(monitored: false);
            author.AudiobookMonitored = true;
            var book = BuildBook(author, audiobookMonitored: true);
            book.LazyAuthor = null;
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, searchCriteria: null);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_book_should_report_when_the_author_media_side_is_not_monitored()
        {
            var author = BuildAuthor(monitored: true);
            author.AudiobookMonitored = false;
            author.EbookMonitored = true;
            var book = BuildBook(author, audiobookMonitored: true);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, searchCriteria: null);

            Assert.Multiple(() =>
            {
                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Reason, Is.EqualTo("Author is not monitored"));
            });
        }

        [Test]
        public void monitored_book_should_report_when_only_the_book_is_not_monitored()
        {
            var author = BuildAuthor(monitored: true);
            author.AudiobookMonitored = true;
            var book = BuildBook(author, audiobookMonitored: false);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, searchCriteria: null);

            Assert.Multiple(() =>
            {
                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Reason, Is.EqualTo("Book is not monitored"));
            });
        }

        [Test]
        public void monitored_book_should_accept_explicit_interactive_book_search_even_when_match_is_soft_rejected()
        {
            var author = BuildAuthor(monitored: false);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B, isMatch: false);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_book_should_reject_for_author_search_criteria()
        {
            var author = BuildAuthor(monitored: false);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);
            var criteria = BuildAuthorSearchCriteria(author, book);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.False);
        }

        [Test]
        public void monitored_book_should_reject_when_matched_book_does_not_overlap_requested_book()
        {
            var author = BuildAuthor(monitored: false);
            var requestedBook = BuildBook(author);
            var differentBook = BuildBook(author, id: 9991, title: "The Boyfriend");
            var subject = BuildRemoteBook(author, differentBook, Quality.M4B, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, requestedBook, interactiveSearch: true);

            var result = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.False);
        }

        [Test]
        public void monitored_media_type_should_accept_for_resolved_interactive_book_search()
        {
            var author = BuildAuthor(monitored: true);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new MonitoredMediaTypeSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_media_type_should_accept_explicit_unmonitored_book_search()
        {
            var author = BuildAuthor(monitored: false);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: false);
            criteria.MonitoredBooksOnly = false;

            var result = new MonitoredMediaTypeSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void monitored_media_type_should_accept_explicit_interactive_book_search_even_when_match_is_soft_rejected()
        {
            var author = BuildAuthor(monitored: true);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.M4B, Quality.M4B, isMatch: false);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new MonitoredMediaTypeSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [TestCase("audiobook", 0, false, false)]
        [TestCase("ebook", 0, false, false)]
        [TestCase("audiobook", 2, false, true)]
        [TestCase("ebook", 2, false, true)]
        [TestCase("audiobook", 0, true, false)]
        [TestCase("ebook", 0, true, false)]
        public void monitored_media_type_should_respect_requested_author_side(
            string mediaType,
            int monitorExisting,
            bool monitorFuture,
            bool expectedAccepted)
        {
            var isAudiobook = mediaType == "audiobook";
            var author = BuildAuthor(monitored: true);
            author.AudiobookMonitored = isAudiobook ? monitorExisting != 0 : true;
            author.AudiobookMonitorNewItems = isAudiobook && monitorFuture
                ? NewItemMonitorTypes.New
                : NewItemMonitorTypes.None;
            author.EbookMonitored = isAudiobook ? true : monitorExisting != 0;
            author.EbookMonitorNewItems = !isAudiobook && monitorFuture
                ? NewItemMonitorTypes.New
                : NewItemMonitorTypes.None;

            var book = BuildBook(author, audiobookMonitored: isAudiobook, ebookMonitored: !isAudiobook);
            var quality = isAudiobook ? Quality.M4B : Quality.EPUB;
            var subject = BuildRemoteBook(author, book, quality, quality);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: false);

            var result = new MonitoredMediaTypeSpecification(LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.EqualTo(expectedAccepted));
        }

        [Test]
        public void cutoff_should_accept_for_resolved_interactive_book_search()
        {
            var author = BuildAuthor(monitored: true, upgradeAllowed: true, cutoffQuality: Quality.M4B);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.MP3, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new CutoffSpecification(
                    new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()),
                    new NoOpCustomFormatCalculationService(),
                    LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void upgrade_allowed_should_accept_for_resolved_interactive_book_search()
        {
            var author = BuildAuthor(monitored: true, upgradeAllowed: false, cutoffQuality: Quality.M4B);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.MP3, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new UpgradeAllowedSpecification(
                    new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()),
                    LogManager.GetCurrentClassLogger(),
                    new NoOpCustomFormatCalculationService())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void upgrade_disk_should_accept_for_resolved_interactive_book_search()
        {
            var author = BuildAuthor(monitored: true, upgradeAllowed: true, cutoffQuality: Quality.M4B);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.MP3, Quality.M4B);
            var criteria = BuildBookSearchCriteria(author, book, interactiveSearch: true);

            var result = new UpgradeDiskSpecification(
                    new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()),
                    null,
                    new NoOpCustomFormatCalculationService(),
                    LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, criteria);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void upgrade_disk_should_accept_source_quality_that_will_convert_to_an_upgrade()
        {
            var author = BuildAuthor(monitored: true, upgradeAllowed: true, cutoffQuality: Quality.M4B, convertToQuality: Quality.M4B);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.MP3, Quality.MP3);

            var result = new UpgradeDiskSpecification(
                    new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()),
                    null,
                    new NoOpCustomFormatCalculationService(),
                    LogManager.GetCurrentClassLogger())
                .IsSatisfiedBy(subject, null);

            Assert.That(result.Accepted, Is.True);
        }

        [Test]
        public void upgrade_allowed_should_reject_source_quality_that_will_convert_to_an_upgrade_when_upgrades_are_disabled()
        {
            var author = BuildAuthor(monitored: true, upgradeAllowed: false, cutoffQuality: Quality.M4B, convertToQuality: Quality.M4B);
            var book = BuildBook(author);
            var subject = BuildRemoteBook(author, book, Quality.MP3, Quality.MP3);

            var result = new UpgradeAllowedSpecification(
                    new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()),
                    LogManager.GetCurrentClassLogger(),
                    new NoOpCustomFormatCalculationService())
                .IsSatisfiedBy(subject, null);

            Assert.That(result.Accepted, Is.False);
        }

        private static Author BuildAuthor(bool monitored, bool upgradeAllowed = true, Quality cutoffQuality = null, Quality convertToQuality = null)
        {
            var profile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                UpgradeAllowed = upgradeAllowed,
                ConvertToQualityId = convertToQuality?.Id,
                Cutoff = (cutoffQuality ?? Quality.M4B).Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };

            return new Author
            {
                Id = 42,
                Name = "Freida McFadden",
                Monitored = monitored,
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile
            };
        }

        private static Book BuildBook(Author author, bool audiobookMonitored = false, bool ebookMonitored = false, int id = 1493, string title = "Dead Med")
        {
            return new Book
            {
                Id = id,
                Title = title,
                Author = author,
                AudiobookMonitored = audiobookMonitored,
                EbookMonitored = ebookMonitored
            };
        }

        private static RemoteBook BuildRemoteBook(Author author, Book book, Quality incomingQuality, Quality existingFileQuality, bool isMatch = true)
        {
            book.BookFiles = new List<BookFile>
            {
                new BookFile
                {
                    Quality = new QualityModel(existingFileQuality)
                }
            };

            return new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                Release = new ReleaseInfo { Title = "Dead Med" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    Quality = new QualityModel(incomingQuality)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = isMatch,
                    Book = book
                }
            };
        }

        private static BookSearchCriteria BuildBookSearchCriteria(Author author, Book book, bool interactiveSearch)
        {
            return new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                MonitoredBooksOnly = true,
                InteractiveSearch = interactiveSearch,
                UserInvokedSearch = true
            };
        }

        private static AuthorSearchCriteria BuildAuthorSearchCriteria(Author author, Book book)
        {
            return new AuthorSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true,
                UserInvokedSearch = true
            };
        }
    }
}
