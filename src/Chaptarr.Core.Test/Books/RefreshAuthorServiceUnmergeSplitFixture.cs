using Chaptarr.Core.Test.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceUnmergeSplitFixture
    {
        private sealed class StubBookService : IBookService
        {
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            public List<Book> BooksByAuthorId { get; set; } = new();
            public List<(int bookId, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats)> DeleteBookCalls { get; } = new();

            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false)
            {
                DeleteBookCalls.Add((bookId, deleteFiles, addImportListExclusion, applyToBothFormats));
            }

            public List<Book> GetBooksByAuthorId(int authorId)
            {
                return BooksByAuthorId.Where(b => b.AuthorId == authorId).ToList();
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => GetBooksByAuthorId(authorId);
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

            public List<BookFile> Files { get; } = new();
            public List<BookFile> UpdatedFiles { get; } = new();

            public void SeedFile(int bookId, BookFile file)
            {
                file.Edition ??= new Edition { BookId = bookId };
                file.Edition.BookId = bookId;
                Files.Add(file);
            }

            public List<BookFile> GetFilesByBooks(List<int> bookIds)
            {
                return Files.Where(f => f.Edition != null && bookIds.Contains(f.Edition.BookId)).ToList();
            }
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

            public void Update(List<BookFile> bookFiles)
            {
                UpdatedFiles.AddRange(bookFiles);
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
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

        private sealed class StubEditionService : IEditionService
        {
            public List<Edition> Editions { get; set; } = new();
            public List<Edition> UpdatedEditions { get; } = new();
            public List<Edition> DeletedEditions { get; } = new();

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                var set = new HashSet<int>(bookIds ?? Enumerable.Empty<int>());
                return Editions.Where(e => e != null && set.Contains(e.BookId)).ToList();
            }

            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions)
            {
                UpdatedEditions.AddRange(editions ?? new List<Edition>());
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
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions)
            {
                DeletedEditions.AddRange(editions ?? new List<Edition>());
            }
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();

            public void RefreshProviderAliases(List<Edition> editions) { }
            public void UpdateSelection(List<Edition> editions) => UpdateMany(editions);
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            public bool Exists(int id, MetadataProfileType? profileType = null) => true;

            public List<Book> FilterBooks(Author input, int profileId)
            {
                return input?.Books ?? new List<Book>();
            }

            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => throw new NotImplementedException();
            public MetadataProfile Get(int id) => throw new NotImplementedException();

            public List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds) =>
                FilterBooks(input, profileId, requestedNarrationIds, null);

            public List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds) =>
                FilterBooks(input, profileId);
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds) => new();

            public void ValidateAuthorExclusions(IEnumerable<Author> authors) => throw new NotImplementedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType = null) => throw new NotImplementedException();
            public ImportListExclusion Add(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public ImportListExclusion Update(ImportListExclusion importListExclusion) => throw new NotImplementedException();
        }

        private sealed class StubCommandQueueManager : IManageCommandQueue
        {
            public List<(Command command, CommandPriority priority, CommandTrigger trigger)> PushCalls { get; } = new();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                PushCalls.Add((command, priority, trigger));
                return new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger
                };
            }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private sealed class StubRefreshBookService : IRefreshBookService
        {
            public bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags) => false;
            public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, BookTitleSlugIndex titleSlugs = null) => false;

            public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, BookTitleSlugIndex titleSlugs, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds) =>
                RefreshBookInfo(books, remoteBooks, remoteData, forceBookRefresh, forceUpdateFileTags, lastUpdate, titleSlugs);
        }

        private sealed class TestableRefreshAuthorService : RefreshAuthorService
        {
            // These tests verify the provider-evidence plan. Persistence and rollback are
            // exercised against both engines by RefreshBookServiceMergeEditionSelectionFixture.
            private StubBookService _books;
            public readonly System.Collections.Concurrent.ConcurrentBag<LibraryIdentityWriter.EditionMove> OwnershipMoves = new();
            public readonly System.Collections.Concurrent.ConcurrentBag<Book> IdentityUpdates = new();
            public readonly System.Collections.Concurrent.ConcurrentBag<(int Source, int Target)> EmptyMerges = new();
            internal override List<int> ApplyEditionOwnershipMoves(int authorId, IReadOnlyCollection<LibraryIdentityWriter.EditionMove> moves, IReadOnlyCollection<int> sourceBookIds, IReadOnlyCollection<Book> identityUpdates, IReadOnlyDictionary<int, int> filelessMerges)
            {
                foreach (var move in moves) OwnershipMoves.Add(move);
                foreach (var book in identityUpdates) IdentityUpdates.Add(book);
                foreach (var pair in filelessMerges) EmptyMerges.Add((pair.Key, pair.Value));
                _books.BooksByAuthorId.RemoveAll(book => filelessMerges.ContainsKey(book.Id));
                return filelessMerges.Keys.ToList();
            }

            public TestableRefreshAuthorService(
                IBookService bookService,
                IEditionService editionService,
                IMediaFileService mediaFileService,
                IMetadataProfileService metadataProfileService,
                IImportListExclusionService importListExclusionService,
                IManageCommandQueue commandQueueManager,
                Logger logger)
                : base(MetadataHealthGateStub.Ready(), authorInfo: null,
                    authorService: null,
                    bookService: bookService,
                    editionService: editionService,
                    metadataProfileService: metadataProfileService,
                    refreshBookService: new StubRefreshBookService(),
                    refreshSeriesService: null,
                    eventAggregator: null,
                    commandQueueManager: commandQueueManager,
                    mediaFileService: mediaFileService,
                    historyRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoHistoryRepositoryCalls(),
                    rootFolderService: null,
                    checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: null,
                    configService: null,
                    importListExclusionService: importListExclusionService,
                    syncMetadataService: null,
                    syncQueueService: null,
                    rootFolderSettingsResolver: null,
                    logger: logger, bookRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoBookRepositoryCalls(), authorRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoPendingAuthorSetup())
            {
                _books = (StubBookService)bookService;
            }

            public void RefreshOverlapping(Author author, Author remote, List<Book> localBooks, Barrier ready, ManualResetEventSlim firstFinished, bool first)
            {
                var context = CreateContext();
                var remoteBooks = GetRemoteChildren(author, remote, context);
                InitializeBookRefreshMatchingIndex(localBooks, remoteBooks, context);
                Assert.That(ready.SignalAndWait(TimeSpan.FromSeconds(10)), Is.True);
                if (!first) Assert.That(firstFinished.Wait(TimeSpan.FromSeconds(10)), Is.True);
                try
                {
                    var matched = GetMatchingExistingChildren(localBooks, remoteBooks.Single(), context);
                    Assert.That(matched.Item1.Id, Is.EqualTo(localBooks[1].Id), "This run must retain its own pocket mapping.");
                    // Simulate native-format pruning losing the identity used by the filed edition.
                    // Repair must still consume THIS run's complete pre-prune blueprint.
                    var filtered = RefreshEntityCopy.CloneBook(remoteBooks.Single(), includeEditions: false);
                    filtered.Editions = new List<Edition> { new Edition { ForeignEditionId = $"hc:edition:{author.Id}-audiobook", ReadingFormatId = 2 } };
                    var children = new SortedChildren { UpToDate = localBooks };
                    PrepareChildrenForRefresh(author, children, new List<Book> { filtered }, context);
                    RefreshChildren(children, new List<Book> { filtered }, remote, false, false, null, context);
                }
                finally
                {
                    if (first) firstFinished.Set();
                }
            }

            private RefreshContext _context;

            public List<Book> GetRemoteChildrenPublic(Author local, Author remote)
            {
                _context = CreateContext();
                return GetRemoteChildren(local, remote, _context);
            }

            public bool PrepareAndRefreshChildrenPublic(Author author, SortedChildren localChildren, List<Book> remoteChildren, Author remoteData)
            {
                var context = _context ?? CreateContext();
                PrepareChildrenForRefresh(author, localChildren, remoteChildren, context);
                return RefreshChildren(localChildren, remoteChildren, remoteData, false, false, null, context);
            }
        }

        [Test]
        public void should_reparent_editions_when_remote_split_detected()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

	            var localBook = new Book
	            {
	                Id = 10,
	                Title = "Over-merged",
	                AuthorId = author.Id,
	                Author = author,
	                MediaType = BookMediaType.Audiobook,
	                HardcoverBookId = "hc:1",
	                GoodreadsWorkId = "gr:2",
                    AnyEditionOk = true
	            };

            var targetBook = new Book
            {
                Id = 11,
                Title = "Work B",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:2"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { localBook, targetBook }
            };

            var editionA = new Edition { Id = 1000, BookId = localBook.Id, ForeignEditionId = "hc:edition:1000-audiobook" };
            var editionB = new Edition { Id = 1001, BookId = localBook.Id, ForeignEditionId = "gr:1001-audiobook" };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(localBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Over-merged/part01.m4b",
                EditionId = editionA.Id
            });
            mediaFileService.SeedFile(localBook.Id, new BookFile
            {
                Id = 101,
                Path = "/audiobooks/Test Author/Over-merged/part02.m4b",
                EditionId = editionA.Id
            });
            mediaFileService.SeedFile(localBook.Id, new BookFile
            {
                Id = 102,
                Path = "/audiobooks/Test Author/Over-merged/bonus.m4b",
                EditionId = editionB.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { editionA, editionB }
            };

            var metadataProfileService = new StubMetadataProfileService();
            var exclusionService = new StubImportListExclusionService();
            var commandQueueManager = new StubCommandQueueManager();

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                metadataProfileService,
                exclusionService,
                commandQueueManager,
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Work A",
                    BaseBookId = "hc:1",
                    HardcoverBookId = "hc:1",
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "hc:edition:1000-audiobook" }
                    }
                },
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Work B",
                    BaseBookId = "gr:2",
                    GoodreadsWorkId = "gr:2",
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "gr:1001-audiobook" }
                    }
                }
            };

            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Has.Count.EqualTo(1));
            Assert.That(service.OwnershipMoves.Single().EditionId, Is.EqualTo(editionB.Id));
            Assert.That(service.OwnershipMoves.Single().TargetBookId, Is.EqualTo(targetBook.Id));
            Assert.That(bookService.DeleteBookCalls, Is.Empty);
        }

        [Test]
        public void should_not_move_editions_when_all_files_map_to_one_remote_work()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

	            var localBook = new Book
	            {
	                Id = 10,
	                Title = "Single Work",
	                AuthorId = author.Id,
	                Author = author,
	                MediaType = BookMediaType.Audiobook,
	                HardcoverBookId = "hc:1",
	                GoodreadsWorkId = "gr:2"
	            };

            var targetBook = new Book
            {
                Id = 11,
                Title = "Other Work",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:2"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { localBook, targetBook }
            };

            var editionA = new Edition { Id = 1000, BookId = localBook.Id, ForeignEditionId = "hc:edition:1000-audiobook" };
            var editionB = new Edition { Id = 1001, BookId = localBook.Id, ForeignEditionId = "gr:1001-audiobook" };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(localBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Single Work/part01.m4b",
                EditionId = editionA.Id
            });
            mediaFileService.SeedFile(localBook.Id, new BookFile
            {
                Id = 101,
                Path = "/audiobooks/Test Author/Single Work/part02.m4b",
                EditionId = editionB.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { editionA, editionB }
            };

            var metadataProfileService = new StubMetadataProfileService();
            var exclusionService = new StubImportListExclusionService();
            var commandQueueManager = new StubCommandQueueManager();

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                metadataProfileService,
                exclusionService,
                commandQueueManager,
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Work A",
                    BaseBookId = "hc:1",
                    HardcoverBookId = "hc:1",
                    GoodreadsWorkId = "gr:2",
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "hc:edition:1000-audiobook" },
                        new Edition { ForeignEditionId = "gr:1001-audiobook" }
                    }
                }
            };

            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty);
        }

        [Test]
        public void plans_identity_adoption_bare_amazon_edition_when_foreign_edition_id_rotates()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

            var bareAmazonBook = new Book
            {
                Id = 10,
                Title = "Masada's Gate",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook
            };

            var resolvedWorkBook = new Book
            {
                Id = 11,
                Title = "Masada's Gate",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:69152788"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { bareAmazonBook, resolvedWorkBook }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = bareAmazonBook.Id,
                ForeignEditionId = "az:B084M8M993-audiobook",
                Asin = "B084M8M993",
                Asins = new List<string> { "B084M88L38" }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(bareAmazonBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Masada/part01.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Masada's Gate",
                    GoodreadsWorkId = "gr:69152788",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "az:B084M8J83N-audiobook",
                            Asin = "B084M8J83N",
                            Asins = new List<string> { "B084M88L38" }
                        }
                    }
                }
            };

            resolvedWorkBook.AnyEditionOk = true; // Empty, unpinned duplicate may be absorbed.
            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty, "the populated row keeps its edition");
            Assert.That(service.IdentityUpdates.Select(book => book.Id), Is.EqualTo(new[] { bareAmazonBook.Id }));
            Assert.That(service.EmptyMerges, Is.EqualTo(new[] { (resolvedWorkBook.Id, bareAmazonBook.Id) }));
            Assert.That(localEdition.BookId, Is.EqualTo(bareAmazonBook.Id));
        }

        [Test]
        public void plans_media_scoped_identity_adoption_to_source_media_type()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1,
                EbookMetadataProfileId = 2
            };

            var bareAmazonAudiobook = new Book
            {
                Id = 10,
                Title = "A Dance With Dragons",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook
            };

            var resolvedAudiobook = new Book
            {
                Id = 11,
                Title = "A Dance with Dragons",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:120973222"
            };

            var resolvedEbook = new Book
            {
                Id = 12,
                Title = "A Dance with Dragons",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:120973222"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { bareAmazonAudiobook, resolvedAudiobook, resolvedEbook }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = bareAmazonAudiobook.Id,
                ForeignEditionId = "az:B005BHCSUM-audiobook",
                Asin = "0007454570",
                AudibleASIN = "B005BHCSUM",
                Asins = new List<string>
                {
                    "0007454570",
                    "B005BHA2MS",
                    "B005BHBLYG",
                    "B005BHCSUM",
                    "B00927AXRC",
                    "B00FGHOJ6S",
                    "B07HFFDGCF",
                    "B0CHQSRG8M"
                }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(bareAmazonAudiobook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/A Dance with Dragons/part01.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "A Dance with Dragons",
                    GoodreadsWorkId = "gr:120973222",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "gr:26127501-audiobook",
                            GoodreadsEditionId = 26127501,
                            Asin = "B005BHCSUM",
                            Asins = new List<string>
                            {
                                "0007454570",
                                "B005BHA2MS",
                                "B005BHBLYG",
                                "B005BHCSUM",
                                "B00927AXRC",
                                "B00FGHOJ6S",
                                "B07HFFDGCF",
                                "B0CHQSRG8M"
                            }
                        }
                    }
                },
                new Book
                {
                    MediaType = BookMediaType.Ebook,
                    Title = "A Dance with Dragons",
                    GoodreadsWorkId = "gr:120973222",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "gr:26127501-ebook",
                            GoodreadsEditionId = 26127501,
                            Asin = "B005BHCSUM",
                            Asins = new List<string> { "B005BHCSUM" }
                        }
                    }
                }
            };

            resolvedAudiobook.AnyEditionOk = true; // Empty, unpinned duplicate may be absorbed.
            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty, "the populated row keeps its edition");
            Assert.That(service.IdentityUpdates.Select(book => book.Id), Is.EqualTo(new[] { bareAmazonAudiobook.Id }));
            Assert.That(service.EmptyMerges, Is.EqualTo(new[] { (resolvedAudiobook.Id, bareAmazonAudiobook.Id) }));
            Assert.That(localEdition.BookId, Is.EqualTo(bareAmazonAudiobook.Id));
        }

        [Test]
        public void plans_identity_adoption_bare_amazon_edition_when_remote_edition_gains_stable_provider_id()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

            var bareAmazonBook = new Book
            {
                Id = 10,
                Title = "Resolved Later",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook
            };

            var resolvedWorkBook = new Book
            {
                Id = 11,
                Title = "Resolved Later",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:12345"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { bareAmazonBook, resolvedWorkBook }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = bareAmazonBook.Id,
                ForeignEditionId = "az:B000BARE-audiobook",
                Asins = new List<string> { "B000BARE" }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(bareAmazonBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Resolved Later/part01.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Resolved Later",
                    GoodreadsWorkId = "gr:12345",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "gr:54321-audiobook",
                            Asins = new List<string> { "B000BARE" }
                        }
                    }
                }
            };

            resolvedWorkBook.AnyEditionOk = true; // Empty, unpinned duplicate may be absorbed.
            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty, "the populated row keeps its edition");
            Assert.That(service.IdentityUpdates.Select(book => book.Id), Is.EqualTo(new[] { bareAmazonBook.Id }));
            Assert.That(service.EmptyMerges, Is.EqualTo(new[] { (resolvedWorkBook.Id, bareAmazonBook.Id) }));
            Assert.That(localEdition.BookId, Is.EqualTo(bareAmazonBook.Id));
        }

        [Test]
        public void should_not_reparent_bare_amazon_edition_when_asin_cluster_points_to_multiple_remote_works()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

            var sourceBook = new Book
            {
                Id = 10,
                Title = "Shared ASIN",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook
            };

            var workOne = new Book
            {
                Id = 11,
                Title = "Work One",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:111"
            };

            var workTwo = new Book
            {
                Id = 12,
                Title = "Work Two",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:222"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { sourceBook, workOne, workTwo }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = sourceBook.Id,
                ForeignEditionId = "az:B000OLD-audiobook",
                Asins = new List<string> { "B000ONE", "B000TWO" }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(sourceBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Shared/part01.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Work One",
                    GoodreadsWorkId = "gr:111",
                    Editions = new List<Edition>
                    {
                        new() { Asins = new List<string> { "B000ONE" } }
                    }
                },
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Work Two",
                    GoodreadsWorkId = "gr:222",
                    Editions = new List<Edition>
                    {
                        new() { Asins = new List<string> { "B000TWO" } }
                    }
                }
            };

            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty);
            Assert.That(localEdition.BookId, Is.EqualTo(sourceBook.Id));
        }

        [Test]
        public void should_prefer_stable_edition_id_over_shared_asin_cluster_when_adopting_identity()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

            var sourceBook = new Book
            {
                Id = 10,
                Title = "Stable Edition",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook
            };

            var intendedWork = new Book
            {
                Id = 11,
                Title = "Stable Edition",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:111"
            };

            var sharedAsinWork = new Book
            {
                Id = 12,
                Title = "Different Work Sharing ASIN",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:222"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { sourceBook, intendedWork, sharedAsinWork }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = sourceBook.Id,
                ForeignEditionId = "gr:21479617-audiobook",
                Asins = new List<string> { "B008R9EM3M" }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(sourceBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Stable/part01.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Stable Edition",
                    GoodreadsWorkId = "gr:111",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "gr:21479617-audiobook",
                            Asins = new List<string> { "B008R9EM3M" }
                        }
                    }
                },
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Different Work Sharing ASIN",
                    GoodreadsWorkId = "gr:222",
                    Editions = new List<Edition>
                    {
                        new() { Asins = new List<string> { "B008R9EM3M" } }
                    }
                }
            };

            intendedWork.AnyEditionOk = true; // Empty, unpinned duplicate may be absorbed.
            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty, "the populated row keeps its edition");
            Assert.That(service.IdentityUpdates.Select(book => book.Id), Is.EqualTo(new[] { sourceBook.Id }));
            Assert.That(service.EmptyMerges, Is.EqualTo(new[] { (intendedWork.Id, sourceBook.Id) }));
            Assert.That(localEdition.BookId, Is.EqualTo(sourceBook.Id));
        }

        [Test]
        public void overlapping_authors_keep_their_own_pocket_mapping_and_pre_prune_blueprint()
        {
            var books = new StubBookService();
            var editions = new StubEditionService();
            var files = new StubMediaFileService();
            var authors = new List<Author>();
            var snapshots = new List<Author>();
            foreach (var id in new[] { 1, 2 })
            {
                var author = new Author { Id = id, Name = $"Author {id}", AudiobookMetadataProfileId = 1 };
                var asin = $"B00000010{id}";
                var source = new Book { Id = id * 10, AuthorId = id, Author = author, Title = "Overmerged", BaseBookId = $"az:{asin}", MediaType = BookMediaType.Audiobook,
                    Editions = new List<Edition>(), BookFiles = new List<BookFile>() };
                var target = new Book { Id = id * 10 + 1, AnyEditionOk = true, AuthorId = id, Author = author, Title = "Target", GoodreadsWorkId = $"gr:{id}", MediaType = BookMediaType.Audiobook,
                    Editions = new List<Edition>(), BookFiles = new List<BookFile>() };
                books.BooksByAuthorId.AddRange(new[] { source, target });
                var edition = new Edition { Id = id * 100, BookId = source.Id, ForeignEditionId = $"az:{asin}-audiobook", Asin = asin, ReadingFormatId = 2 };
                editions.Editions.Add(edition);
                files.SeedFile(source.Id, new BookFile { Id = id, EditionId = edition.Id, Path = $"/audiobooks/{id}/recording.m4b" });
                authors.Add(author);
                snapshots.Add(new Author { Id = id, Name = author.Name, Books = new List<Book>
                {
                    new Book { Title = "Target", GoodreadsWorkId = target.GoodreadsWorkId, MediaType = BookMediaType.Audiobook,
                        Editions = new List<Edition> { new Edition { ForeignEditionId = $"gr:{id}-audiobook", GoodreadsEditionId = id, Asin = asin, ReadingFormatId = 2 } } }
                } });
            }

            var service = new TestableRefreshAuthorService(books, editions, files, new StubMetadataProfileService(),
                new StubImportListExclusionService(), new StubCommandQueueManager(), LogManager.GetCurrentClassLogger());
            using var ready = new Barrier(2);
            using var firstFinished = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() => service.RefreshOverlapping(authors[index], snapshots[index],
                books.GetBooksByAuthorId(authors[index].Id), ready, firstFinished, index == 0))).ToArray();
            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(20)), Is.True);
            Assert.That(service.OwnershipMoves, Is.Empty);
            Assert.That(service.IdentityUpdates.Select(book => (book.Id, book.GoodreadsWorkId)), Is.EquivalentTo(new[] { (10, "gr:1"), (20, "gr:2") }));
            Assert.That(service.EmptyMerges, Is.EquivalentTo(new[] { (11, 10), (21, 20) }));
        }

        [Test]
        public void plans_identity_adoption_from_full_author_blueprint_when_filtered_remote_children_lack_asin_token()
        {
            var author = new Author
            {
                Id = 1,
                Name = "George R.R. Martin",
                AudiobookMetadataProfileId = 1
            };

            var sourceBook = new Book
            {
                Id = 10,
                Title = "A Dance With Dragons",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:0007454570"
            };

            var targetBook = new Book
            {
                Id = 11,
                Title = "A Dance with Dragons",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:120973222",
                HardcoverBookId = "hc:144636"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { sourceBook, targetBook }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = sourceBook.Id,
                ForeignEditionId = "az:B005BHCSUM-audiobook",
                Asin = "0007454570",
                AudibleASIN = "B005BHCSUM",
                Asins = new List<string>
                {
                    "0007454570",
                    "B005BHA2MS",
                    "B005BHBLYG",
                    "B005BHCSUM"
                }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(sourceBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/George R. R. Martin/A Dance with Dragons.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var fullRemoteBook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "A Dance with Dragons",
                GoodreadsWorkId = "gr:120973222",
                HardcoverBookId = "hc:144636",
                Editions = new List<Edition>
                {
                    new()
                    {
                        ForeignEditionId = "gr:26127501-audiobook",
                        GoodreadsEditionId = 26127501,
                        Asin = "B005BHCSUM",
                        Asins = new List<string>
                        {
                            "0007454570",
                            "B005BHA2MS",
                            "B005BHBLYG",
                            "B005BHCSUM"
                        },
                        ReadingFormatId = 2
                    }
                }
            };

            var remoteData = new Author
            {
                Id = author.Id,
                Name = author.Name,
                Books = new List<Book> { fullRemoteBook }
            };

            targetBook.AnyEditionOk = true; // Empty, unpinned duplicate may be absorbed.
            service.GetRemoteChildrenPublic(author, remoteData);

            var filteredRemoteChildren = new List<Book>
            {
                new()
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "A Dance with Dragons",
                    GoodreadsWorkId = "gr:120973222",
                    HardcoverBookId = "hc:144636",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "hc:edition:31545605-audiobook",
                            HardcoverEditionId = "31545605",
                            ReadingFormatId = 2
                        }
                    }
                }
            };

            var sortedChildren = new RefreshEntityServiceBase<Author, Book>.SortedChildren
            {
                UpToDate = new List<Book> { sourceBook, targetBook }
            };

            service.PrepareAndRefreshChildrenPublic(author, sortedChildren, filteredRemoteChildren, remoteData);

            Assert.That(service.OwnershipMoves, Is.Empty, "the populated row keeps its edition");
            Assert.That(service.IdentityUpdates.Select(book => book.Id), Is.EqualTo(new[] { sourceBook.Id }));
            Assert.That(service.EmptyMerges, Is.EqualTo(new[] { (targetBook.Id, sourceBook.Id) }));
            Assert.That(localEdition.BookId, Is.EqualTo(sourceBook.Id));
        }

        [Test]
        public void plans_absorbing_the_empty_equivalent_pocket_without_retargeting_the_file()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Jim Butcher",
                AudiobookMetadataProfileId = 1
            };

            var bareBook = new Book
            {
                Id = 10,
                Title = "Brief Cases",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "az:B07B3DKSNT",
                AnyEditionOk = true
            };

            var targetBook = new Book
            {
                Id = 11,
                Title = "Brief Cases",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:17155691",
                HardcoverBookId = "hc:461427"
            };

            var sourceEdition = new Edition
            {
                Id = 5166,
                BookId = bareBook.Id,
                ForeignEditionId = "az:B07B3WPXMJ-audiobook",
                Asin = "B07B3DKSNT",
                AudibleASIN = "B07B3WPXMJ",
                Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" }
            };

            var targetEdition = new Edition
            {
                Id = 9001,
                BookId = targetBook.Id,
                ForeignEditionId = "az:B07B3ROTATED-audiobook",
                Asin = "B07B3WPXMJ",
                Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" }
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { bareBook, targetBook }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(bareBook.Id, new BookFile
            {
                Id = 415,
                Path = "/audiobooks/Jim Butcher/Brief Cases/Brief Cases.m4b",
                EditionId = sourceEdition.Id,
                Edition = sourceEdition
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { sourceEdition, targetEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Brief Cases",
                    GoodreadsWorkId = "gr:17155691",
                    HardcoverBookId = "hc:461427",
                    Editions = new List<Edition>
                    {
                        new()
                        {
                            ForeignEditionId = "az:B07B3ROTATED-audiobook",
                            Asin = "B07B3WPXMJ",
                            Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" }
                        }
                    }
                }
            };

            targetBook.AnyEditionOk = true; // Empty, unpinned duplicate may be absorbed.
            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty);
            Assert.That(service.IdentityUpdates.Select(book => book.Id), Is.EqualTo(new[] { bareBook.Id }));
            Assert.That(service.EmptyMerges, Is.EqualTo(new[] { (targetBook.Id, bareBook.Id) }));
            Assert.That(sourceEdition.BookId, Is.EqualTo(bareBook.Id));
        }

        [Test]
        public void should_not_reparent_when_source_already_matches_remote_work_by_alternate_provider_id()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfileId = 1
            };

            var sourceBook = new Book
            {
                Id = 10,
                Title = "Same Work",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:111"
            };

            var duplicateBook = new Book
            {
                Id = 11,
                Title = "Same Work",
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:222"
            };

            var bookService = new StubBookService
            {
                BooksByAuthorId = new List<Book> { sourceBook, duplicateBook }
            };

            var localEdition = new Edition
            {
                Id = 1000,
                BookId = sourceBook.Id,
                ForeignEditionId = "hc:edition:1000-audiobook"
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.SeedFile(sourceBook.Id, new BookFile
            {
                Id = 100,
                Path = "/audiobooks/Test Author/Same Work/part01.m4b",
                EditionId = localEdition.Id
            });

            var editionService = new StubEditionService
            {
                Editions = new List<Edition> { localEdition }
            };

            var service = new TestableRefreshAuthorService(
                bookService,
                editionService,
                mediaFileService,
                new StubMetadataProfileService(),
                new StubImportListExclusionService(),
                new StubCommandQueueManager(),
                LogManager.GetCurrentClassLogger());

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    MediaType = BookMediaType.Audiobook,
                    Title = "Same Work",
                    HardcoverBookId = "hc:111",
                    GoodreadsWorkId = "gr:222",
                    Editions = new List<Edition>
                    {
                        new() { ForeignEditionId = "hc:edition:1000-audiobook" }
                    }
                }
            };

            service.RepairOverMergedBookEditions(author.Id, remoteBooks);

            Assert.That(service.OwnershipMoves, Is.Empty);
            Assert.That(localEdition.BookId, Is.EqualTo(sourceBook.Id));
        }
    }
}
