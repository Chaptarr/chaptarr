using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceActiveEditionSelectionFixture
    {
        private sealed class StubBookService : IBookService
        {
            public List<Book> InsertedBooks { get; } = new();

            public void InsertMany(List<Book> books)
            {
                if (books != null)
                {
                    InsertedBooks.AddRange(books);
                }
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
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
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

        private sealed class StubEditionService : IEditionService
        {
            public Edition MonitoredEdition { get; private set; }

            public void InsertMany(List<Edition> editions)
            {
            }

            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
            {
                MonitoredEdition = edition;
                return new List<Edition> { edition };
            }

            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions)
            {
                MonitoredEdition = editions?.FirstOrDefault(edition => edition.Monitored);
            }
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
        }

        private sealed class TestableRefreshAuthorService : RefreshAuthorService
        {
            public TestableRefreshAuthorService(IBookService bookService, IEditionService editionService, Logger logger)
                : base(authorInfo: null,
                    authorService: null,
                    bookService: bookService,
                    editionService: editionService,
                    metadataProfileService: null,
                    refreshBookService: null,
                    refreshSeriesService: null,
                    eventAggregator: null,
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
                    logger: logger,
                    editionSelector: new EditionSelector(logger))
            {
            }

            public void AddChildrenPublic(List<Book> books)
            {
                AddChildren(books);
            }
        }

        [Test]
        public void add_children_should_select_best_edition_when_no_monitored_edition_exists()
        {
            var book = new Book
            {
                Id = 70,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 20, Title = "Second", ForeignEditionId = "second", ReadingFormatId = 2 },
                    new Edition { Id = 10, Title = "First", ForeignEditionId = "first", ReadingFormatId = 2 }
                }
            };

            var bookService = new StubBookService();
            var editionService = new StubEditionService();
            var service = new TestableRefreshAuthorService(bookService, editionService, LogManager.GetCurrentClassLogger());

            service.AddChildrenPublic(new List<Book> { book });

            Assert.That(editionService.MonitoredEdition?.Id, Is.EqualTo(10));
            Assert.That(book.ForeignEditionId, Is.EqualTo("first"));
        }
    }
}
