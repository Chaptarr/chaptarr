using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Wanted;
using Chaptarr.Http;
using NUnit.Framework;
using MediaCoverModel = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Api.Wanted
{
    [TestFixture]
    public class WantedMonitoringFilterFixture
    {
        private sealed class CapturingBookService : IBookService
        {
            public PagingSpec<Book> CapturedPagingSpec { get; private set; }

            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec)
            {
                CapturedPagingSpec = pagingSpec;
                pagingSpec.Records = new List<Book>();
                pagingSpec.TotalRecords = 0;
                return pagingSpec;
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class CapturingBookCutoffService : IBookCutoffService
        {
            public PagingSpec<Book> CapturedPagingSpec { get; private set; }

            public PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec)
            {
                CapturedPagingSpec = pagingSpec;
                pagingSpec.Records = new List<Book>();
                pagingSpec.TotalRecords = 0;
                return pagingSpec;
            }
        }

        private sealed class StubSeriesBookLinkService : ISeriesBookLinkService
        {
            public List<SeriesBookLink> GetLinksBySeries(int seriesId) => new();
            public List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId) => new();
            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds) => new();
            public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId) => new();
            public void InsertMany(List<SeriesBookLink> model) => throw new NotImplementedException();
            public void InsertMany(List<SeriesBookLink> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<SeriesBookLink> model) => throw new NotImplementedException();
            public void DeleteMany(List<SeriesBookLink> model) => throw new NotImplementedException();
            public void AddLink(SeriesBookLink link) => throw new NotImplementedException();
        }

        private sealed class StubAuthorStatisticsService : IAuthorStatisticsService
        {
            public List<AuthorStatistics> AuthorStatistics() => new();
            public AuthorStatistics AuthorStatistics(int authorId) => new();
            public List<AuthorStatistics> AuthorStatistics(string mediaType) => new();
            public AuthorStatistics AuthorStatistics(int authorId, string mediaType) => new();
            public BookAggregate GetAggregateStatistics(List<int> authorIds, string mediaType) => new();
            public void InvalidateAuthorCache(int authorId) { }
        }

        private sealed class StubCoverMapper : IMapCoversToLocal
        {
            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<MediaCoverModel> covers, string selectedAuthorImageHash = null) { }
            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null) => null;
            public void EnsureAuthorCovers(Author author) { }
            public void EnsureBookCovers(Book book) { }
            public Task<EnsureImageResult> EnsureAuthorImage(Author author, MediaCoverModel cover) => Task.FromResult<EnsureImageResult>(null);
        }

        private sealed class StubUpgradableSpecification : IUpgradableSpecification
        {
            public bool IsUpgradable(QualityProfile profile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats) => false;
            public bool QualityCutoffNotMet(QualityProfile profile, QualityModel currentQuality, QualityModel newQuality = null) => false;
            public bool CutoffNotMet(QualityProfile profile, List<QualityModel> currentQualities, List<CustomFormat> currentFormats, QualityModel newQuality = null) => false;
            public bool IsRevisionUpgrade(QualityModel currentQuality, QualityModel newQuality) => false;
            public bool IsUpgradeAllowed(QualityProfile qualityProfile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats) => false;
        }

        private sealed class StubSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;
            public Task BroadcastMessage(SignalRMessage message) => Task.CompletedTask;
        }

        [Test]
        public void missing_monitored_filter_should_be_media_type_aware()
        {
            var bookService = new CapturingBookService();

            var controller = new MissingController(
                bookService,
                new StubSeriesBookLinkService(),
                new StubAuthorStatisticsService(),
                new StubCoverMapper(),
                new StubUpgradableSpecification(),
                new StubSignalRBroadcaster());

            controller.GetMissingBooks(new PagingRequestResource(), includeAuthor: false, monitored: true, mediaType: null);

            var predicate = bookService.CapturedPagingSpec.FilterExpressions[0].Compile();
            var monitoredAuthor = new Author
            {
                Monitored = true,
                AudiobookMonitorExisting = 2,
                EbookMonitorExisting = 2
            };

            // "Wrong-side" flag should not make the record count as monitored.
            Assert.That(predicate(new Book
            {
                Author = monitoredAuthor,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = false,
                EbookMonitored = true
            }), Is.False);

            Assert.That(predicate(new Book
            {
                Author = monitoredAuthor,
                MediaType = BookMediaType.Ebook,
                AudiobookMonitored = true,
                EbookMonitored = false
            }), Is.False);

            Assert.That(predicate(new Book
            {
                Author = monitoredAuthor,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                EbookMonitored = false
            }), Is.True);

            Assert.That(predicate(new Book
            {
                Author = new Author
                {
                    Monitored = true,
                    AudiobookMonitorExisting = 0,
                    AudiobookMonitorFuture = false,
                    EbookMonitorExisting = 2
                },
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true
            }), Is.False, "the enabled ebook side must not keep a paused audiobook visible as monitored");

            Assert.That(predicate(new Book
            {
                Author = new Author
                {
                    AudiobookMonitorExisting = 0,
                    AudiobookMonitorFuture = true
                },
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true
            }), Is.True, "future monitoring keeps the audiobook side enabled");
        }

        [Test]
        public void missing_unmonitored_filter_should_include_a_paused_media_side()
        {
            var bookService = new CapturingBookService();
            var controller = new MissingController(
                bookService,
                new StubSeriesBookLinkService(),
                new StubAuthorStatisticsService(),
                new StubCoverMapper(),
                new StubUpgradableSpecification(),
                new StubSignalRBroadcaster());

            controller.GetMissingBooks(new PagingRequestResource(), includeAuthor: false, monitored: false, mediaType: "audiobook");

            var predicate = bookService.CapturedPagingSpec.FilterExpressions[0].Compile();
            Assert.That(predicate(new Book
            {
                Author = new Author
                {
                    Monitored = true,
                    AudiobookMonitorExisting = 0,
                    AudiobookMonitorFuture = false,
                    EbookMonitorExisting = 2
                },
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true
            }), Is.True);

            Assert.That(predicate(new Book
            {
                Author = new Author { AudiobookMonitorExisting = 2 },
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true
            }), Is.False);
        }

        [Test]
        public void cutoff_unmet_monitored_filter_should_be_media_type_aware()
        {
            var cutoffService = new CapturingBookCutoffService();

            var controller = new CutoffController(
                cutoffService,
                new CapturingBookService(),
                new StubSeriesBookLinkService(),
                new StubAuthorStatisticsService(),
                new StubCoverMapper(),
                new StubUpgradableSpecification(),
                new StubSignalRBroadcaster());

            controller.GetCutoffUnmetBooks(new PagingRequestResource(), includeAuthor: false, monitored: true);

            var predicate = cutoffService.CapturedPagingSpec.FilterExpressions[0].Compile();
            var monitoredAuthor = new Author
            {
                Monitored = true,
                AudiobookMonitorExisting = 2
            };

            Assert.That(predicate(new Book
            {
                Author = monitoredAuthor,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = false,
                EbookMonitored = true
            }), Is.False);

            Assert.That(predicate(new Book
            {
                Author = new Author
                {
                    Monitored = true,
                    AudiobookMonitorExisting = 0,
                    EbookMonitorExisting = 2
                },
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true
            }), Is.False);
        }
    }
}
