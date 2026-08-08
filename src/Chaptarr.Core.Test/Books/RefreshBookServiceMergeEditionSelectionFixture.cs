using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceMergeEditionSelectionFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly List<BookFile> _files;

            public StubMediaFileService(params BookFile[] files)
            {
                _files = new List<BookFile>(files ?? Array.Empty<BookFile>());
            }

            public List<BookFile> UpdatedFiles { get; private set; }

            public List<BookFile> GetFilesByBook(int bookId) => _files;

            public void Update(List<BookFile> bookFiles)
            {
                UpdatedFiles = bookFiles;
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class StubHistoryService : IHistoryService
        {
            public List<EntityHistory> GetByBook(int bookId, EntityHistoryEventType? eventType) => new List<EntityHistory>();
            public void UpdateMany(IList<EntityHistory> items) { }

            public NzbDrone.Core.Datastore.PagingSpec<EntityHistory> Paged(NzbDrone.Core.Datastore.PagingSpec<EntityHistory> pagingSpec) => throw new NotImplementedException();
            public NzbDrone.Core.Datastore.PagingSpec<EntityHistory> Paged(NzbDrone.Core.Datastore.PagingSpec<EntityHistory> pagingSpec, BookMediaType? mediaType) => throw new NotImplementedException();
            public EntityHistory MostRecentForBook(int bookId) => throw new NotImplementedException();
            public EntityHistory MostRecentForDownloadId(string downloadId) => throw new NotImplementedException();
            public EntityHistory Get(int historyId) => throw new NotImplementedException();
            public List<EntityHistory> GetByAuthor(int authorId, EntityHistoryEventType? eventType) => throw new NotImplementedException();
            public List<EntityHistory> Find(string downloadId, EntityHistoryEventType eventType) => throw new NotImplementedException();
            public List<EntityHistory> FindByDownloadId(string downloadId) => throw new NotImplementedException();
            public List<EntityHistory> FindByDownloadIds(IEnumerable<string> downloadIds, EntityHistoryEventType eventType) => throw new NotImplementedException();
            public string FindDownloadId(NzbDrone.Core.MediaFiles.Events.TrackImportedEvent trackedDownload) => throw new NotImplementedException();
            public List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType) => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            public void DeleteMany(List<Book> books) { }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
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
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
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

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService(IMediaFileService mediaFileService, IHistoryService historyService, Logger logger)
                : base(bookService: new StubBookService(),
                    authorService: null,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: null,
                    bookInfo: null,
                    refreshEditionService: null,
                    mediaFileService: mediaFileService,
                    historyService: historyService,
                    eventAggregator: null,
                    checkIfBookShouldBeRefreshed: null,
                    editionSelector: new EditionSelector(logger),
                    editionMetadataProfileFilter: new NzbDrone.Core.Books.Services.EditionMetadataProfileFilter(new TestTermMatcherService()),
                    mediaCoverService: null,
                    logger: logger)
            {
            }

            public void InvokeMerge(Book local, Book target, Book remote)
            {
                MergeEntity(local, target, remote);
            }
        }

        [Test]
        public void should_reassign_duplicate_foreign_edition_ids_using_monitored_edition()
        {
            var mediaFileService = new StubMediaFileService(
                new BookFile
                {
                    Id = 1,
                    EditionId = 999,
                    Edition = new Edition { ForeignEditionId = "dup-edition" }
                });

            var service = new TestableRefreshBookService(mediaFileService, new StubHistoryService(), LogManager.GetCurrentClassLogger());

            var local = new Book { Id = 1, Title = "Local" };
            var target = new Book
            {
                Id = 2,
                Title = "Target",
                Editions = new List<Edition>
                {
                    new Edition { Id = 10, ForeignEditionId = "dup-edition", Monitored = true, ManualAdd = false },
                    new Edition { Id = 11, ForeignEditionId = "dup-edition", Monitored = false, ManualAdd = true }
                }
            };

            service.InvokeMerge(local, target, new Book { Id = 3, Title = "Remote" });

            Assert.That(mediaFileService.UpdatedFiles, Is.Not.Null);
            Assert.That(mediaFileService.UpdatedFiles[0].EditionId, Is.EqualTo(10));
        }

        [Test]
        public void should_use_monitored_edition_as_merge_fallback_when_foreign_id_does_not_match()
        {
            var mediaFileService = new StubMediaFileService(
                new BookFile
                {
                    Id = 2,
                    EditionId = 998,
                    Edition = new Edition { ForeignEditionId = "missing-edition" }
                });

            var service = new TestableRefreshBookService(mediaFileService, new StubHistoryService(), LogManager.GetCurrentClassLogger());

            var local = new Book { Id = 4, Title = "Local" };
            var target = new Book
            {
                Id = 5,
                Title = "Target",
                Editions = new List<Edition>
                {
                    new Edition { Id = 20, ForeignEditionId = "monitored", Monitored = true, ManualAdd = false },
                    new Edition { Id = 21, ForeignEditionId = "manual", Monitored = false, ManualAdd = true }
                }
            };

            service.InvokeMerge(local, target, new Book { Id = 6, Title = "Remote" });

            Assert.That(mediaFileService.UpdatedFiles, Is.Not.Null);
            Assert.That(mediaFileService.UpdatedFiles[0].EditionId, Is.EqualTo(20));
        }
    }
}
