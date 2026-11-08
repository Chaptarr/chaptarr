using Chaptarr.Core.Test.Common;
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
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            public List<(int bookId, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats)> DeleteBookCalls { get; } = new();

            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false)
            {
                DeleteBookCalls.Add((bookId, deleteFiles, addImportListExclusion, applyToBothFormats));
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public List<Book> FindBySlug(string titleSlug) => throw new NotImplementedException();
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
            public Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated = null) => throw new System.NotSupportedException();
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
            public void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<NzbDrone.Core.MediaFiles.BookFile> deletedBookFiles = null) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public void ValidateReplacementFiles(System.Collections.Generic.List<BookFile> replacedFiles,
                System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles)
                => throw new System.NotSupportedException("This test adapter does not support replacement validation.");

            public void ReplaceMany(System.Collections.Generic.List<BookFile> bookFiles, System.Collections.Generic.List<BookFile> replacedFiles,
                DeleteMediaFileReason reason, System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles,
                System.Action<System.Action<System.Data.IDbConnection, System.Data.IDbTransaction>> persist)
                => throw new System.NotSupportedException("This test adapter does not support transactional replacement.");

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
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

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
            public BookTitleSlugIndex TitleSlugs { get; private set; }

            public bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags)
            {
                RefreshCallsBooks.Add(book);
                return false;
            }

            public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, BookTitleSlugIndex titleSlugs = null)
            {
                TitleSlugs = titleSlugs;
                RefreshCallsBooks.AddRange(books);
                return false;
            }

            public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, BookTitleSlugIndex titleSlugs, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds) =>
                RefreshBookInfo(books, remoteBooks, remoteData, forceBookRefresh, forceUpdateFileTags, lastUpdate, titleSlugs);
        }

        private sealed class TestableRefreshAuthorService : RefreshAuthorService
        {
            public TestableRefreshAuthorService(IBookService bookService, IMediaFileService mediaFileService, IRefreshBookService refreshBookService, Logger logger, IMainDatabase database = null, IEditionService editions = null)
                : base(MetadataHealthGateStub.Ready(), authorInfo: null,
                    authorService: null,
                    bookService: bookService,
                    editionService: editions,
                    metadataProfileService: null,
                    refreshBookService: refreshBookService,
                    refreshSeriesService: null,
                    eventAggregator: null,
                    commandQueueManager: null,
                    mediaFileService: mediaFileService,
                    historyRepository: database == null ? ImportLifecycleStubs.NoHistoryRepositoryCalls() : new global::NzbDrone.Core.History.HistoryRepository(database, null),
                    rootFolderService: null,
                    checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: null,
                    configService: null,
                    importListExclusionService: null,
                    syncMetadataService: null,
                    syncQueueService: null,
                    rootFolderSettingsResolver: null,
                    logger: logger, mainDatabase: database, bookRepository: database == null ? ImportLifecycleStubs.NoBookRepositoryCalls() : new BookRepository(database, null), authorRepository: database == null ? ImportLifecycleStubs.NoPendingAuthorSetup() : new AuthorRepository(database, null, logger))
            {
            }

            private RefreshContext _context;

            public Tuple<Book, List<Book>> MatchExisting(List<Book> existingChildren, Book remote)
            {
                // This fixture bypasses GetLocalChildren; the getter requires its complete snapshot.
                _context = CreateContext();
                InitializeBookRefreshMatchingIndex(existingChildren, new List<Book> { remote }, _context);
                return GetMatchingExistingChildren(existingChildren, remote, _context);
            }

            public bool RefreshBooks(RefreshEntityServiceBase<Author, Book>.SortedChildren localChildren, List<Book> remoteChildren)
            {
                return RefreshChildren(localChildren, remoteChildren, remoteData: null, forceChildRefresh: false, forceUpdateFileTags: false, lastUpdate: null, context: _context ?? CreateContext());
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

        [TestCase(false)]
        [TestCase(true)]
        public void should_not_delete_manual_edition_clone_that_shares_work_provider_ids(bool wantedNarration)
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
                AnyEditionOk = !wantedNarration,
                TitleSlug = wantedNarration ? "book_wanted_20" : "book",
                AddOptions = wantedNarration ? new AddBookOptions { AddType = BookAddType.Manual } : null,
                Editions = new List<Edition>
                {
                    new() { Id = 20, BookId = 2, ManualAdd = true, Monitored = true }
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
        public void refresh_children_transfers_history_before_removing_fileless_duplicate([Values(false, true)] bool postgres)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                var bookService = new StubBookService();
                var refreshBookService = new StubRefreshBookService();
                var author = new AuthorRepository(database, null, LogManager.GetCurrentClassLogger()).Insert(new Author { Name = "C. S. Lewis", CleanName = "cslewis", TitleSlug = "cs-lewis", Path = "/library/lewis", Added = DateTime.UtcNow });
                var repository = new BookRepository(database, null);
                var target = repository.Insert(new Book { AuthorId = author.Id, Title = "Mere Christianity", CleanTitle = "merechristianity", TitleSlug = "mere-christianity", MediaType = BookMediaType.Audiobook, AnyEditionOk = true, Added = DateTime.UtcNow });
                var duplicate = repository.Insert(new Book { AuthorId = author.Id, Title = "Mere Christianity", CleanTitle = "merechristianity", TitleSlug = "old-duplicate", MediaType = BookMediaType.Audiobook, AnyEditionOk = true, Added = DateTime.UtcNow });
                var history = new NzbDrone.Core.History.HistoryRepository(database, null);
                history.Insert(new NzbDrone.Core.History.EntityHistory { AuthorId = author.Id, BookId = duplicate.Id, SourceTitle = "Mere Christianity", EventType = NzbDrone.Core.History.EntityHistoryEventType.Grabbed, Date = DateTime.UtcNow, Quality = new() });
                var editions = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IEditionService>((method, args) => new List<Edition>());
                var service = new TestableRefreshAuthorService(bookService, new StubMediaFileService(), refreshBookService, LogManager.GetCurrentClassLogger(), database, editions);
                var children = new RefreshEntityServiceBase<Author, Book>.SortedChildren();
                children.UpToDate.Add(target);
                children.Merged.Add(Tuple.Create(duplicate, target));
                service.RefreshBooks(children, new List<Book>());
                Assert.That(repository.Find(duplicate.Id), Is.Null);
                Assert.That(history.All().Single().BookId, Is.EqualTo(target.Id));
                Assert.That(bookService.DeleteBookCalls, Is.Empty, "Ordinary deletion would discard the acquisition history");
                Assert.That(refreshBookService.RefreshCallsBooks.Select(book => book.Id), Does.Not.Contain(duplicate.Id));
                Assert.That(refreshBookService.TitleSlugs.Contains(target.MediaType, target.TitleSlug), Is.True);
                Assert.That(refreshBookService.TitleSlugs.Contains(duplicate.MediaType, duplicate.TitleSlug), Is.False);
            });
        }
    }
}
