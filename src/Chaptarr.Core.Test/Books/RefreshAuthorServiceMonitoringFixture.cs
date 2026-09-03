using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceMonitoringFixture
    {
        private sealed class StubBookService : IBookService
        {
            private readonly List<Book> _books;

            public List<List<Book>> UpdateManyCalls { get; } = new();

            public StubBookService(List<Book> books)
            {
                _books = books ?? new List<Book>();
            }

            public Book GetBook(int bookId) => _books.SingleOrDefault(b => b.Id == bookId);

            public List<Book> GetBooks(IEnumerable<int> bookIds)
            {
                if (bookIds == null)
                {
                    return new List<Book>();
                }

                var idSet = bookIds.ToHashSet();
                return _books.Where(b => idSet.Contains(b.Id)).ToList();
            }

            public List<Book> GetBooksByAuthor(int authorId) => _books;

            public void UpdateMany(List<Book> books)
            {
                UpdateManyCalls.Add(books);
            }

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
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
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
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public List<IEvent> PublishedEvents { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                PublishedEvents.Add(@event);
            }
        }

        private sealed class TestableRefreshAuthorService : RefreshAuthorService
        {
            public TestableRefreshAuthorService(IBookService bookService, IEventAggregator eventAggregator, Logger logger)
                : base(authorInfo: null,
                    authorService: null,
                    bookService: bookService,
                    editionService: null,
                    metadataProfileService: null,
                    refreshBookService: null,
                    refreshSeriesService: null,
                    eventAggregator: eventAggregator,
                    commandQueueManager: null,
                    mediaFileService: null,
                    historyService: null,
                    rootFolderService: null,
                    checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: null,
                    configService: null,
                    importListExclusionService: null,
                    syncMetadataService: null,
                    syncQueueService: null,
                    rootFolderSettingsResolver: null,
                    logger: logger)
            {
            }

            public void PublishRefreshComplete(Author author)
            {
                base.PublishRefreshCompleteEvent(author);
            }
        }

        [Test]
        public void refresh_complete_should_not_auto_monitor_visible_unmonitored_books()
        {
            var book = new Book
            {
                Id = 1,
                Title = "Test Book",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = false,
                EbookMonitored = false
            };

            var bookService = new StubBookService(new List<Book> { book });
            var eventAggregator = new StubEventAggregator();
            var service = new TestableRefreshAuthorService(bookService, eventAggregator, LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 10,
                Name = "Test Author",
                Monitored = true,
                AudiobookMonitorFuture = true,
                EbookMonitorFuture = true
            };

            service.PublishRefreshComplete(author);

            Assert.That(book.AudiobookMonitored, Is.False);
            Assert.That(book.EbookMonitored, Is.False);
            Assert.That(bookService.UpdateManyCalls, Is.Empty);
        }
    }
}
