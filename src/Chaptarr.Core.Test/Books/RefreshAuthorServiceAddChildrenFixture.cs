using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceAddChildrenFixture
    {
        private sealed class RecordingBookService : IBookService
        {
            private int _nextId = 100;

            public List<Book> InsertedBooks { get; } = new List<Book>();
            public int InsertManyCalls { get; private set; }

            public void InsertMany(List<Book> books)
            {
                InsertManyCalls++;

                foreach (var book in books)
                {
                    book.Id = _nextId++;
                }

                InsertedBooks.AddRange(books);
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh) => throw new NotImplementedException();
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
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded) => throw new NotImplementedException();
        }

        private sealed class RecordingEditionService : IEditionService
        {
            public List<Edition> InsertedEditions { get; } = new List<Edition>();
            public List<Edition> MonitoredEditions { get; } = new List<Edition>();
            public List<Edition> UpdatedEditions { get; } = new List<Edition>();
            public int InsertManyCalls { get; private set; }
            public int UpdateManyCalls { get; private set; }

            public void InsertMany(List<Edition> editions)
            {
                InsertManyCalls++;
                InsertedEditions.AddRange(editions);
            }

            public void UpdateMany(List<Edition> editions)
            {
                UpdateManyCalls++;
                UpdatedEditions.AddRange(editions);
            }

            public List<Edition> SetMonitored(Edition edition, bool isManualSelection)
            {
                MonitoredEditions.Add(edition);
                return new List<Edition>();
            }

            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
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
                       logger: logger)
            {
            }

            public void InvokeAddChildren(List<Book> children) => AddChildren(children);
        }

        private RecordingBookService _bookService;
        private RecordingEditionService _editionService;
        private TestableRefreshAuthorService _service;

        [SetUp]
        public void Setup()
        {
            _bookService = new RecordingBookService();
            _editionService = new RecordingEditionService();
            _service = new TestableRefreshAuthorService(_bookService, _editionService, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_skip_zero_edition_book_and_still_insert_siblings_with_their_editions()
        {
            // Shell first on purpose: the pre-0.9.743 code inserted every book row and then threw
            // on this child before the edition bulk-insert ran, leaving the whole batch edition-less.
            var shell = new Book
            {
                Title = "Dramatized Adaptation",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>()
            };

            var goodAudiobook = new Book
            {
                Title = "Good Audiobook",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc:edition:1", Title = "Good Audiobook Audio", ReadingFormatId = 2, Monitored = true },
                    new Edition { ForeignEditionId = "hc:edition:2", Title = "Good Audiobook Ebook Companion", ReadingFormatId = 3 }
                }
            };

            var goodEbook = new Book
            {
                Title = "Good Ebook",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "hc:edition:3", Title = "Good Ebook", ReadingFormatId = 3, Monitored = true }
                }
            };

            _service.InvokeAddChildren(new List<Book> { shell, goodAudiobook, goodEbook });

            Assert.That(_bookService.InsertedBooks, Has.Count.EqualTo(2));
            Assert.That(_bookService.InsertedBooks.Any(b => ReferenceEquals(b, goodAudiobook)), Is.True);
            Assert.That(_bookService.InsertedBooks.Any(b => ReferenceEquals(b, goodEbook)), Is.True);
            Assert.That(_bookService.InsertedBooks.Any(b => ReferenceEquals(b, shell)), Is.False);

            Assert.That(_editionService.InsertedEditions, Has.Count.EqualTo(3));
            Assert.That(_editionService.InsertedEditions.Count(e => e.BookId == goodAudiobook.Id), Is.EqualTo(2));
            Assert.That(_editionService.InsertedEditions.Count(e => e.BookId == goodEbook.Id), Is.EqualTo(1));
            Assert.That(_editionService.InsertedEditions.All(e => e.BookId > 0), Is.True);

            Assert.That(_editionService.MonitoredEditions, Is.Empty);
            Assert.That(_editionService.UpdateManyCalls, Is.EqualTo(1));
            Assert.That(_editionService.UpdatedEditions, Has.Count.EqualTo(3));
            Assert.That(_editionService.UpdatedEditions.Where(e => e.Monitored), Is.EquivalentTo(new[] { goodAudiobook.Editions[0], goodEbook.Editions[0] }));

            Assert.That(goodAudiobook.ForeignEditionId, Is.EqualTo("hc:edition:1"));
            Assert.That(goodEbook.ForeignEditionId, Is.EqualTo("hc:edition:3"));
        }

        [Test]
        public void should_insert_nothing_when_no_children_have_retained_editions()
        {
            var emptyEditions = new Book
            {
                Title = "Empty Editions",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>()
            };

            var nullEditions = new Book
            {
                Title = "Null Editions",
                MediaType = BookMediaType.Ebook,
                Editions = null
            };

            _service.InvokeAddChildren(new List<Book> { emptyEditions, nullEditions });

            Assert.That(_bookService.InsertManyCalls, Is.EqualTo(0));
            Assert.That(_editionService.InsertManyCalls, Is.EqualTo(0));
            Assert.That(_editionService.UpdateManyCalls, Is.EqualTo(0));
            Assert.That(_bookService.InsertedBooks, Is.Empty);
            Assert.That(_editionService.InsertedEditions, Is.Empty);
        }
    }
}
