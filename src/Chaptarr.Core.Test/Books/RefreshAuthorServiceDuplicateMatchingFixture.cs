using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceDuplicateMatchingFixture
    {
        private sealed class StubBookService : IBookService
        {
            public List<(int bookId, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats)> DeleteBookCalls { get; } = new();

            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false)
            {
                DeleteBookCalls.Add((bookId, deleteFiles, addImportListExclusion, applyToBothFormats));
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
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
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

        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly Dictionary<int, List<BookFile>> _filesByBookId = new();

            public void SetFilesByBook(int bookId, params BookFile[] files)
            {
                _filesByBookId[bookId] = files?.ToList() ?? new List<BookFile>();
            }

            public List<BookFile> GetFilesByBook(int bookId)
            {
                return _filesByBookId.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
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

        private sealed class StubRefreshBookService : IRefreshBookService
        {
            public List<Book> RefreshCallsBooks { get; } = new();

            public bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags)
            {
                RefreshCallsBooks.Add(book);
                return false;
            }

            public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
            {
                RefreshCallsBooks.AddRange(books);
                return false;
            }
        }

        private sealed class TestableRefreshAuthorService : RefreshAuthorService
        {
            public TestableRefreshAuthorService(IBookService bookService, IMediaFileService mediaFileService, IRefreshBookService refreshBookService, Logger logger)
                : base(authorInfo: null,
                    authorService: null,
                    bookService: bookService,
                    editionService: null,
                    metadataProfileService: null,
                    refreshBookService: refreshBookService,
                    refreshSeriesService: null,
                    eventAggregator: null,
                    commandQueueManager: null,
                    mediaFileService: mediaFileService,
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

            public Tuple<Book, List<Book>> MatchExisting(List<Book> existingChildren, Book remote)
            {
                return GetMatchingExistingChildren(existingChildren, remote);
            }

            public bool RefreshBooks(RefreshEntityServiceBase<Author, Book>.SortedChildren localChildren, List<Book> remoteChildren)
            {
                return RefreshChildren(localChildren, remoteChildren, remoteData: null, forceChildRefresh: false, forceUpdateFileTags: false, lastUpdate: null);
            }
        }

        [Test]
        public void should_prefer_book_with_files_and_mark_fileless_duplicates_for_deletion()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var duplicateWithoutFiles = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true
            };

            var duplicateWithFiles = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true
            };

            mediaFileService.SetFilesByBook(duplicateWithFiles.Id, new BookFile { Id = 10 });

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001"
            };

            var match = service.MatchExisting(new List<Book> { duplicateWithoutFiles, duplicateWithFiles }, remote);

            Assert.That(match.Item1.Id, Is.EqualTo(duplicateWithFiles.Id));
            Assert.That(match.Item2.Select(b => b.Id).ToList(), Is.EqualTo(new List<int> { duplicateWithoutFiles.Id }));
        }

        [Test]
        public void should_prefer_hardcover_id_when_multiple_provider_matches_exist_without_files()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var goodreadsOnly = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:77157",
                AnyEditionOk = true
            };

            var hardcoverOnly = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:342994",
                AnyEditionOk = true
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:77157",
                HardcoverBookId = "hc:342994"
            };

            var match = service.MatchExisting(new List<Book> { goodreadsOnly, hardcoverOnly }, remote);

            Assert.That(match.Item1.Id, Is.EqualTo(hardcoverOnly.Id));
            Assert.That(match.Item2.Select(b => b.Id).ToList(), Is.EqualTo(new List<int> { goodreadsOnly.Id }));
        }

        [Test]
        public void should_not_consolidate_duplicates_that_only_share_base_book_id()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var staleDuplicate = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Ebook,
                BaseBookId = "hc:2135032",
                Title = "Chris Ryan Extreme: Night Strike",
                AnyEditionOk = true
            };

            var survivor = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Ebook,
                BaseBookId = "hc:2135032",
                Title = "Chris Ryan Extreme: Night Strike",
                AnyEditionOk = true
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Ebook,
                BaseBookId = "hc:2135032",
                HardcoverBookId = "hc:2135032",
                GoodreadsWorkId = "gr:21943909",
                Title = "Chris Ryan Extreme: Night Strike"
            };

            var match = service.MatchExisting(new List<Book> { staleDuplicate, survivor }, remote);

            Assert.That(match.Item1, Is.Null);
            Assert.That(match.Item2, Is.Empty);
        }

        [Test]
        public void should_not_treat_non_provider_base_book_id_as_work_identity()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var legacyLocalKey = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Ebook,
                BaseBookId = "2135032",
                Title = "Chris Ryan Extreme: Night Strike"
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Ebook,
                BaseBookId = "hc:2135032",
                HardcoverBookId = "hc:2135032",
                Title = "Chris Ryan Extreme: Night Strike"
            };

            var match = service.MatchExisting(new List<Book> { legacyLocalKey }, remote);

            Assert.That(match.Item1, Is.Null);
            Assert.That(match.Item2, Is.Empty);
        }

        [Test]
        public void should_not_delete_manual_clone_that_shares_work_provider_ids()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var canonical = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true
            };

            var manualClone = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Manual }
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001"
            };

            var match = service.MatchExisting(new List<Book> { canonical, manualClone }, remote);

            Assert.That(match.Item1.Id, Is.EqualTo(canonical.Id));
            Assert.That(match.Item2.Select(b => b.Id), Does.Not.Contain(manualClone.Id));
        }

        [Test]
        public void should_not_delete_strict_edition_clone_that_shares_work_provider_ids()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var canonical = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true
            };

            var strictClone = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = false
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001"
            };

            var match = service.MatchExisting(new List<Book> { canonical, strictClone }, remote);

            Assert.That(match.Item1.Id, Is.EqualTo(canonical.Id));
            Assert.That(match.Item2.Select(b => b.Id), Does.Not.Contain(strictClone.Id));
        }

        [Test]
        public void should_not_delete_manual_edition_clone_that_shares_work_provider_ids()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var canonical = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true
            };

            var manualEditionClone = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001",
                AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new() { Id = 20, BookId = 2, ManualAdd = true }
                }
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:142593",
                GoodreadsWorkId = "gr:178409001"
            };

            var match = service.MatchExisting(new List<Book> { canonical, manualEditionClone }, remote);

            Assert.That(match.Item1.Id, Is.EqualTo(canonical.Id));
            Assert.That(match.Item2.Select(b => b.Id), Does.Not.Contain(manualEditionClone.Id));
        }

        [Test]
        public void should_match_by_asin_when_other_provider_ids_missing()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

	            var local = new Book
	            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                ASIN = "B002V1A14G",
                AnyEditionOk = true
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                ASIN = "B002V1A14G"
            };

            var match = service.MatchExisting(new List<Book> { local }, remote);

            Assert.That(match.Item1, Is.Not.Null);
            Assert.That(match.Item1.Id, Is.EqualTo(local.Id));
        }

        [Test]
        public void should_adopt_same_pocket_edition_only_copies_without_minting_another_refresh_row()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            Book Local(int id, string unitKeyHash = null) => new()
            {
                Id = id,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:B09FBT3T7G",
                UnitKeyHash = unitKeyHash,
                AnyEditionOk = true,
                Editions = new List<Edition> { new() { Asin = "B09FC52TN9" } }
            };

            var original = Local(1);
            var explicitCopy = Local(2, "copy-unit");
            var refreshManufactured = Local(3);
            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:B09FBT3T7G",
                Editions = new List<Edition> { new() { AudibleASIN = "B09FC52TN9" } }
            };

            var match = service.MatchExisting(new List<Book> { original, explicitCopy, refreshManufactured }, remote);

            Assert.That(match.Item1, Is.SameAs(original));
            Assert.That(match.Item2, Is.EqualTo(new[] { refreshManufactured }));
            Assert.That(match.Item2, Does.Not.Contain(explicitCopy));
        }

        [Test]
        public void should_not_adopt_ambiguous_edition_only_rows_from_different_server_pockets()
        {
            var service = new TestableRefreshAuthorService(
                new StubBookService(),
                new StubMediaFileService(),
                new StubRefreshBookService(),
                LogManager.GetCurrentClassLogger());
            var first = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:POCKET1",
                Editions = new List<Edition> { new() { Asin = "B000SHARED" } }
            };
            var second = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:POCKET2",
                Editions = new List<Edition> { new() { Asin = "B000SHARED" } }
            };
            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:POCKET1",
                Editions = new List<Edition> { new() { Asin = "B000SHARED" } }
            };

            var match = service.MatchExisting(new List<Book> { first, second }, remote);

            Assert.That(match.Item1, Is.Null);
            Assert.That(match.Item2, Is.Empty);
        }

        [Test]
        public void should_not_match_asin_only_remote_to_existing_work_id_row()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:69152788",
                ASIN = "B084M88L38",
                AnyEditionOk = true
            };

            var remote = new Book
            {
                MediaType = BookMediaType.Audiobook,
                ASIN = "B084M88L38"
            };

            var match = service.MatchExisting(new List<Book> { local }, remote);

            Assert.That(match.Item1, Is.Null);
            Assert.That(match.Item2, Is.Empty);
        }

        [Test]
        public void refresh_children_should_delete_merged_duplicates_and_not_send_them_to_refresh_book_service()
        {
            var bookService = new StubBookService();
            var mediaFileService = new StubMediaFileService();
            var refreshBookService = new StubRefreshBookService();
            var service = new TestableRefreshAuthorService(bookService, mediaFileService, refreshBookService, LogManager.GetCurrentClassLogger());

	            var target = new Book { Id = 2, Title = "Mere Christianity", MediaType = BookMediaType.Audiobook, AnyEditionOk = true };
	            var duplicate = new Book { Id = 1, Title = "Mere Christianity", MediaType = BookMediaType.Audiobook, AnyEditionOk = true };

            var children = new RefreshEntityServiceBase<Author, Book>.SortedChildren();
            children.UpToDate.Add(target);
            children.Merged.Add(Tuple.Create(duplicate, target));

            service.RefreshBooks(children, new List<Book>());

            Assert.That(bookService.DeleteBookCalls.Select(c => c.bookId).ToList(), Is.EqualTo(new List<int> { duplicate.Id }));
            Assert.That(refreshBookService.RefreshCallsBooks.Select(b => b.Id).Contains(duplicate.Id), Is.False);
            Assert.That(refreshBookService.RefreshCallsBooks.Select(b => b.Id).Contains(target.Id), Is.True);
        }
    }
}
