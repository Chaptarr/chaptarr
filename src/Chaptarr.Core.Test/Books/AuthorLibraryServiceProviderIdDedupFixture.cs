using Chaptarr.Core.Test.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServiceProviderIdDedupFixture
    {
        // Authored Windows-style and adapted per-OS: the production path builder joins with
        // Path.Combine, so a forward-slash literal seeded as an "existing" path would never match
        // the backslash-joined path generated on Windows — and the collision that drives
        // disambiguation would silently go undetected. Derive every child path with Path.Combine
        // for the same reason.
        private static readonly string AudiobookRoot = @"C:\audio".AsOsAgnostic();
        private static readonly string EbookRoot = @"C:\ebooks".AsOsAgnostic();
        private static readonly string DiscoveredFolder = @"C:\incoming\David Mitchell".AsOsAgnostic();

        private sealed class StubEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class StubAuthorInfo : IProvideAuthorInfo
        {
            private readonly Author _author;

            public StubAuthorInfo(Author author)
            {
                _author = author;
            }

            public Author GetAuthorInfo(string chaptarrId, bool useCache = true)
            {
                return _author;
            }

            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            public bool AddAuthorCalled { get; private set; }
            public int FindByNameCalls { get; private set; }
            public int AuthorPathExistsCalls { get; private set; }
            public Author AddedAuthor { get; private set; }
            public Author UpdatedAuthor { get; private set; }
            private readonly Func<string, string, Author> _findByProviderId;
            private Author _foundAuthor;
            private readonly Author _findByName;
            private readonly HashSet<string> _existingPaths;
            private readonly bool _allowAdd;

            public StubAuthorService(
                Func<string, string, Author> findByProviderId,
                Author findByName = null,
                IEnumerable<string> existingPaths = null,
                bool allowAdd = false)
            {
                _findByProviderId = findByProviderId;
                _findByName = findByName;
                _existingPaths = new HashSet<string>(existingPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                _allowAdd = allowAdd;
            }

            public Author FindByProviderId(string provider, string providerId) => _foundAuthor = _findByProviderId?.Invoke(provider, providerId);

            public Author AddAuthor(Author newAuthor, bool doRefresh)
            {
                AddAuthorCalled = true;
                if (!_allowAdd)
                {
                    throw new InvalidOperationException("AddAuthor should not be called when an existing author is found by provider IDs");
                }

                newAuthor.Id = 84;
                AddedAuthor = newAuthor;
                return newAuthor;
            }

            public Author GetAuthor(int authorId) => UpdatedAuthor?.Id == authorId ? UpdatedAuthor :
                AddedAuthor?.Id == authorId ? AddedAuthor : _foundAuthor?.Id == authorId ? _foundAuthor : null;
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByName(string title)
            {
                FindByNameCalls++;
                return _findByName;
            }
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
	            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
	            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
	            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
	            public Author UpdateAuthor(Author author)
                {
                    UpdatedAuthor = author;
                    return author;
                }
	            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
	            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
	            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder)
            {
                AuthorPathExistsCalls++;
                return _existingPaths.Contains(folder);
            }
            public void SetAddOptions(Author author)
            {
                var stored = GetAuthor(author.Id);
                if (stored != null) stored.AddOptions = author.AddOptions;
            }
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubAuthorPathBuilder : IBuildAuthorPaths
        {
            public string BuildPath(Author author, bool useExistingRelativeFolder)
            {
                return author.AudiobookPath ?? author.EbookPath;
            }

            public string BuildPathForQuality(Author author, NzbDrone.Core.Qualities.Quality quality, bool useExistingRelativeFolder)
            {
                var root = quality == NzbDrone.Core.Qualities.Quality.EPUB
                    ? author.EbookRootFolderPath
                    : author.AudiobookRootFolderPath;
                return Path.Combine(root, author.Name);
            }

            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder, Author previous = null)
            {
            }
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly Dictionary<string, RootFolder> _roots;

            public StubRootFolderService(params RootFolder[] roots)
            {
                _roots = new Dictionary<string, RootFolder>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    _roots[root.Path] = root;
                }
            }

            public List<RootFolder> All() => new List<RootFolder>(_roots.Values);
            public List<RootFolder> AllWithSpaceStats() => All();
            public RootFolder GetBestRootFolder(string path) => _roots.TryGetValue(path, out var root) ? root : null;
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path);
            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolderPath(path);
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
        }

        private static RootFolder BuildRoot(string path, FolderType type)
        {
            var root = new RootFolder
            {
                Path = path,
                FolderType = type
            };

            var settings = new MediaTypeSettings
            {
                QualityProfileId = 1,
                MetadataProfileId = 1,
                MonitorExistingBooks = false,
                MonitorNewItems = NzbDrone.Core.Books.NewItemMonitorTypes.New
            };

            if (type == FolderType.Audiobook)
            {
                root.SetAudiobookSettings(settings);
            }
            else
            {
                root.SetEbookSettings(settings);
            }

            return root;
        }

        private sealed class StubBookService : IBookService
        {
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            private readonly Dictionary<int, List<Book>> _booksByAuthorId = new();

            public void SetBooks(int authorId, params Book[] books)
            {
                _booksByAuthorId[authorId] = new List<Book>(books ?? Array.Empty<Book>());
            }

            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => _booksByAuthorId.TryGetValue(authorId, out var v) ? v : new List<Book>();
            public IReadOnlySet<BookMediaType> GetMediaTypesByAuthor(int authorId) => GetBooksByAuthor(authorId).Select(book => book.MediaType).ToHashSet();

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
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
            public List<Book> FindAllByWorkProviderId(string provider, string providerId, BookMediaType mediaType) =>
                _booksByAuthorId.Values.SelectMany(books => books).Where(book => book.MediaType == mediaType &&
                    book.GoodreadsWorkId == provider + ":" + providerId).ToList();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
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

        [Test]
        public void existing_author_add_keeps_identity_on_metadata_outage_or_unique_race(
            [Values(false, true)] bool postgres, [Values(false, true)] bool uniqueRace)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var rows = new AuthorRepository(database, null, logger);
                var local = rows.Insert(new Author { Name = "Frank Herbert", CleanName = "frankherbert", Path = TestContext.CurrentContext.TestDirectory,
                    TitleSlug = "frank-herbert", HardcoverAuthorId = "hc:100", GoodreadsAuthorId = "gr:58", Added = DateTime.UtcNow });
                if (uniqueRace) rows.Insert(new Author { Name = "Other author", CleanName = "otherauthor", Path = TestContext.CurrentContext.TestDirectory,
                    TitleSlug = "other-author", HardcoverAuthorId = "hc:200", Added = DateTime.UtcNow });
                var remote = new Author { Name = local.Name, CleanName = local.CleanName, TitleSlug = local.TitleSlug, HardcoverAuthorId = uniqueRace ? "hc:200" : "hc:100",
                    GoodreadsAuthorId = uniqueRace ? "gr:58" : null, Books = new() };
                var authors = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.FindByProviderId) or nameof(IAuthorService.GetAuthor) => rows.Get(local.Id),
                    nameof(IAuthorService.UpdateAuthor) => rows.Update((Author)args[0]),
                    _ => throw new AssertionException(method.Name)
                });
                var books = new StubBookService();
                books.SetBooks(local.Id, new Book { MediaType = BookMediaType.Audiobook }, new Book { MediaType = BookMediaType.Ebook });
                var refresh = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IRefreshAuthorService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IRefreshAuthorService.ReconcilePublishedAuthorIdentity)));
                    if (!uniqueRace) throw new NzbDrone.Core.Exceptions.AuthorIdentityUnverifiedException("Cannot verify ownership of gr:58; api2 unavailable");
                    return args[0];
                });
                var service = new AuthorLibraryService(authors, new StubAuthorInfo(remote), books, null, null, null, null,
                    new TestQualityProfileService(), null, null, null, new StubEventAggregator(), null, null, null,
                    new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, refreshAuthorService: refresh);
                var before = rows.Get(local.Id).ProviderIds;
                var result = service.AddAuthorAsync("gr:58", config: null).GetAwaiter().GetResult();
                Assert.Multiple(() =>
                {
                    Assert.That(result.Id, Is.EqualTo(local.Id));
                    Assert.That(rows.Get(local.Id).ProviderIds, Is.EqualTo(before));
                    Assert.That(rows.Get(local.Id).HardcoverAuthorId, Is.EqualTo("hc:100"));
                    Assert.That(new ProviderAliasService(new ProviderAliasRepository(database, null)).FindAuthorIds(new[] { "hc:100", "gr:58" }), Is.EqualTo(new[] { local.Id }));
                });
            });
        }


        [Test]
        public void unverifiable_existing_identity_keeps_setup_and_requested_work_but_programming_errors_escape(
            [Values(false, true)] bool postgres, [Values(false, true)] bool replay, [Values(false, true)] bool programmingError,
            [Values(false, true)] bool initiallyPending, [Values("missing", "existing", "setup")] string demand,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                var audio = mediaType == BookMediaType.Audiobook;
                var root = audio ? AudiobookRoot : EbookRoot;
                using var factory = new NLog.LogFactory();
                var sink = new NLog.Targets.MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRuleForAllLevels(sink);
                factory.Configuration = logging;
                var logger = factory.GetLogger("identity-add");
                var rows = new AuthorRepository(database, null, logger);
                var local = rows.Insert(new Author { Name = "Frank Herbert", CleanName = "frankherbert", Path = AudiobookRoot,
                    TitleSlug = "frank-herbert", HardcoverAuthorId = "hc:100", GoodreadsAuthorId = "gr:58", Added = DateTime.UtcNow,
                    AudiobookRootFolderPath = audio && initiallyPending ? root : null,
                    EbookRootFolderPath = !audio && initiallyPending ? root : null,
                    AudiobookQualityProfileId = audio && demand == "setup" && !initiallyPending ? 0 : 1,
                    AudiobookMetadataProfileId = audio && demand == "setup" && !initiallyPending ? 0 : 1,
                    EbookQualityProfileId = !audio && demand == "setup" && !initiallyPending ? 0 : 1,
                    EbookMetadataProfileId = !audio && demand == "setup" && !initiallyPending ? 0 : 1,
                    AddOptions = initiallyPending ? new AddAuthorOptions { AwaitingMetadata = true,
                        AudiobookAwaitingMetadata = audio, EbookAwaitingMetadata = !audio } : null });
                var remote = new Author { Name = local.Name, CleanName = local.CleanName, TitleSlug = local.TitleSlug,
                    HardcoverAuthorId = "hc:100", AudiobookRootFolderPath = AudiobookRoot, Books = new() };
                var authors = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.FindByProviderId) or nameof(IAuthorService.GetAuthor) => rows.Get(local.Id),
                    nameof(IAuthorService.UpdateAuthor) => rows.Update((Author)args[0]),
                    nameof(IAuthorService.SetAddOptions) => SaveOptions((Author)args[0]),
                    nameof(IAuthorService.UpdateAuthorProgressiveSettings) => args[0],
                    _ => throw new AssertionException(method.Name)
                });
                object SaveOptions(Author row) { rows.SetFields(row, a => a.AddOptions); return null; }
                var events = new StubEventAggregator();
                var requests = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(requests, authors, events, logger,
                    new ProviderAliasService(new ProviderAliasRepository(database, events)));
                var config = new MonitoringConfig { CreateAudiobook = audio, CreateEbook = !audio,
                    AudiobookBooksToAdd = audio && demand != "setup" ? new() { "gr:3634639" } : null,
                    EbookBooksToAdd = !audio && demand != "setup" ? new() { "gr:3634639" } : null,
                    IsAuthorSettingsRequest = demand == "setup", QueueIfUnavailable = false, AuthorName = local.Name,
                    AudiobookQualityProfileId = audio ? 1 : null, AudiobookMetadataProfileId = audio ? 1 : null,
                    AudiobookRootFolderPath = audio ? root : null, EbookQualityProfileId = audio ? null : 1,
                    EbookMetadataProfileId = audio ? null : 1, EbookRootFolderPath = audio ? null : root };
                if (replay)
                {
                    config.PendingImportId = pending.EnqueueAsync("hc:100", config, "UserInterface").GetAwaiter().GetResult();
                    config.PendingImportVersion = requests.Get(config.PendingImportId.Value).Version;
                    config.IsFromQueue = true;
                }
                var info = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideAuthorInfo.RefreshAuthorInfo)));
                    if (programmingError) throw new InvalidOperationException("planted non-outage bug");
                    return RefreshResult.Error("api2 unavailable", System.Net.HttpStatusCode.BadGateway);
                });
                var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), info, authors, null, null, null, null, null, events, null, null,
                    new global::NzbDrone.Core.History.HistoryRepository(database, events), null, null, null, null, null, null, null, null, logger, bookRepository: new BookRepository(database, events), authorRepository: new AuthorRepository(database, events, logger));
                var books = new StubBookService();
                books.SetBooks(local.Id, demand == "setup"
                    ? new[] { new Book { AuthorId = local.Id, MediaType = audio ? BookMediaType.Ebook : BookMediaType.Audiobook } }
                    : new[] { new Book { Id = 7, AuthorId = local.Id, MediaType = mediaType,
                        GoodreadsWorkId = demand == "existing" ? "gr:3634639" : "gr:999" }, new Book { AuthorId = local.Id, MediaType = audio ? BookMediaType.Ebook : BookMediaType.Audiobook } });
                var service = new AuthorLibraryService(authors, new StubAuthorInfo(remote), books, null, null, null,
                    new TestMetadataProfileService(),
                    new TestQualityProfileService(), new StubAuthorPathBuilder(), new StubRootFolderService(BuildRoot(root, audio ? FolderType.Audiobook : FolderType.Ebook)),
                    null, events, pending, null, null, new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, refreshAuthorService: refresh);
                if (programmingError)
                    Assert.That(() => service.AddAuthorAsync("hc:100", config).GetAwaiter().GetResult(), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("planted non-outage bug"));
                else
                {
                    if (!replay)
                    {
                        var result = service.AddAuthorAsync("hc:100", config).GetAwaiter().GetResult();
                        Assert.That(result.Id, Is.LessThan(0));
                    }
                    Assert.That(requests.All(), Has.Count.EqualTo(1));
                    var request = requests.All().Single();
                    config.IsFromQueue = true;
                    config.PendingImportId = request.Id;
                    config.PendingImportVersion = request.Version;
                    Assert.Throws<AuthorIdentityUnverifiedException>(() => service.AddAuthorAsync("hc:100", config).GetAwaiter().GetResult(),
                        "unverified replay must fail with the identity hold before attempting downstream work");
                    var handler = new NzbDrone.Core.Books.Commands.ProcessPendingImportsCommandHandler(pending, service, books,
                        null, null, events, logger);
                    var processing = (Task<PendingAuthorImport>)handler.GetType().GetMethod("ProcessPendingImportAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(handler, new object[] { request });
                    processing.GetAwaiter().GetResult();
                    var retained = requests.Get(request.Id);
                    Assert.That(retained.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(retained.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow).And.LessThan(DateTime.UtcNow.AddMinutes(2)));
                    Assert.That(retained.LastError, Does.Contain("identity"));
                    if (demand != "setup") Assert.That(audio ? retained.AudiobookBooksToAdd : retained.EbookBooksToAdd, Does.Contain("gr:3634639"));
                    Assert.That(events.Events.OfType<NzbDrone.Core.Books.Events.PendingAuthorImportSucceededEvent>(), Is.Empty);
                    Assert.That(string.Join("\n", sink.Logs), Does.Contain("[AUTHOR-IDENTITY] Keeping author").And.Contain("Cannot verify ownership of gr:58"));
                }
                Assert.That(rows.Get(local.Id).AddOptions?.AwaitingMetadata == true, Is.EqualTo(initiallyPending || !programmingError));
                Assert.That(events.Events.OfType<NzbDrone.Core.Books.Events.AuthorRefreshCompleteEvent>(), Is.Empty, "unverified identity must not announce readiness to ingest");
                Assert.That(rows.Get(local.Id).GoodreadsAuthorId, Is.EqualTo("gr:58"));
            });
        }
        [Test]
        public async Task should_match_existing_author_by_secondary_provider_id_from_remote_payload()
        {
            var existing = new Author { Id = 42, Name = "Existing Author", GoodreadsAuthorId = "gr:123" };

            var authorInfo = new StubAuthorInfo(new Author
            {
                Name = "Remote Author",
                HardcoverAuthorId = "hc:80626",
                GoodreadsAuthorId = "gr:123"
            });

            var authorService = new StubAuthorService((provider, providerId) =>
                provider == "gr" && providerId == "123" ? existing : null);

            var bookService = new StubBookService();
            bookService.SetBooks(existing.Id,
                new Book { MediaType = BookMediaType.Audiobook },
                new Book { MediaType = BookMediaType.Ebook });

            var reconciled = false;
            var refresh = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IRefreshAuthorService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IRefreshAuthorService.ReconcilePublishedAuthorIdentity)));
                Assert.That(authorService.UpdatedAuthor, Is.Null, "ownership must be resolved before snapshot replacement");
                Assert.That(args[0], Is.SameAs(existing));
                Assert.That(existing.HardcoverAuthorId, Is.Null);
                reconciled = true;
                return existing;
            });
            var svc = new AuthorLibraryService(
                authorService: authorService,
                authorInfo: authorInfo,
                bookService: bookService,
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(), refreshAuthorService: refresh);

            var result = await svc.AddAuthorAsync("hc:80626", config: null);

            Assert.That(reconciled, Is.True);
            Assert.That(result.Id, Is.EqualTo(existing.Id));
            Assert.That(authorService.AddAuthorCalled, Is.False);
        }

        [Test]
        public async Task should_apply_fresh_life_dates_when_the_author_already_exists()
        {
            var existing = new Author
            {
                Id = 42,
                Name = "Existing Author",
                HardcoverAuthorId = "hc:80626",
                Status = AuthorStatusType.Continuing
            };
            var born = new DateTime(1928, 2, 15);
            var died = new DateTime(1996, 7, 9);
            var authorInfo = new StubAuthorInfo(new Author
            {
                Name = existing.Name,
                HardcoverAuthorId = existing.HardcoverAuthorId,
                Born = born,
                Died = died,
                Status = AuthorStatusType.Ended
            });
            var authorService = new StubAuthorService((provider, providerId) =>
                provider == "hc" && providerId == "80626" ? existing : null);
            var bookService = new StubBookService();
            bookService.SetBooks(existing.Id,
                new Book { MediaType = BookMediaType.Audiobook },
                new Book { MediaType = BookMediaType.Ebook });
            var service = new AuthorLibraryService(
                authorService: authorService,
                authorInfo: authorInfo,
                bookService: bookService,
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = await service.AddAuthorAsync(existing.HardcoverAuthorId);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.SameAs(existing));
                Assert.That(authorService.UpdatedAuthor.Born, Is.EqualTo(born));
                Assert.That(authorService.UpdatedAuthor.Died, Is.EqualTo(died));
                Assert.That(authorService.UpdatedAuthor.Status, Is.EqualTo(AuthorStatusType.Ended));
                Assert.That(authorService.UpdatedAuthor.HardcoverAuthorId, Is.EqualTo("hc:80626"));
            });
        }

        [Test]
        public async Task should_not_merge_same_name_authors_and_should_disambiguate_all_generated_paths()
        {
            var first = new Author
            {
                Id = 42,
                Name = "David Mitchell",
                HardcoverAuthorId = "hc:111"
            };

            var authorInfo = new StubAuthorInfo(new Author
            {
                Name = "David Mitchell",
                HardcoverAuthorId = "hc:222",
                Books = new List<Book>()
            });

            var authorService = new StubAuthorService(
                findByProviderId: (_, _) => null,
                findByName: first,
                existingPaths: new[]
                {
                    Path.Combine(AudiobookRoot, "David Mitchell"),
                    Path.Combine(EbookRoot, "David Mitchell")
                },
                allowAdd: true);
            var rootFolderService = new StubRootFolderService(
                BuildRoot(AudiobookRoot, FolderType.Audiobook),
                BuildRoot(EbookRoot, FolderType.Ebook));

            var service = new AuthorLibraryService(
                authorService: authorService,
                authorInfo: authorInfo,
                bookService: new StubBookService(),
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: new TestMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: rootFolderService,
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = await service.AddAuthorAsync(
                "hc:222",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = true,
                    AudiobookRootFolderPath = AudiobookRoot,
                    EbookRootFolderPath = EbookRoot
                });

            Assert.That(result.Id, Is.EqualTo(84));
            Assert.That(authorService.FindByNameCalls, Is.Zero);
            Assert.That(authorService.AddAuthorCalled, Is.True);
            Assert.That(authorService.AddedAuthor.HardcoverAuthorId, Is.EqualTo("hc:222"));
            Assert.That(authorService.AddedAuthor.Path, Is.EqualTo(Path.Combine(AudiobookRoot, "David Mitchell") + " (1)"));
            Assert.That(authorService.AddedAuthor.AudiobookPath, Is.EqualTo(Path.Combine(AudiobookRoot, "David Mitchell") + " (1)"));
            Assert.That(authorService.AddedAuthor.EbookPath, Is.EqualTo(Path.Combine(EbookRoot, "David Mitchell") + " (1)"));
        }

        [Test]
        public async Task should_not_disambiguate_a_discovered_author_folder()
        {
            var authorInfo = new StubAuthorInfo(new Author
            {
                Name = "David Mitchell",
                HardcoverAuthorId = "hc:222",
                Books = new List<Book>()
            });
            var authorService = new StubAuthorService(
                findByProviderId: (_, _) => null,
                existingPaths: new[] { DiscoveredFolder },
                allowAdd: true);
            var service = new AuthorLibraryService(
                authorService: authorService,
                authorInfo: authorInfo,
                bookService: new StubBookService(),
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: new TestMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildRoot(AudiobookRoot, FolderType.Audiobook)),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = await service.AddAuthorAsync(
                "hc:222",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    AudiobookRootFolderPath = AudiobookRoot,
                    DiscoveredAuthorFolderPath = DiscoveredFolder
                });

            Assert.That(result.Path, Is.EqualTo(DiscoveredFolder));
            Assert.That(result.AudiobookPath, Is.EqualTo(DiscoveredFolder));
            Assert.That(authorService.AuthorPathExistsCalls, Is.Zero);
        }
    }
}
