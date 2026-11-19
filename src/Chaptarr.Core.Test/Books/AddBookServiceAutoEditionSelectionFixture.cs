using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentValidation;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public partial class AddBookServiceAutoEditionSelectionFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class AuthorLibraryProxy : DispatchProxy
        {
            public Author AddedAuthor { get; set; }
            public string ProviderId { get; private set; }
            public MonitoringConfig Config { get; private set; }
            public Action BeforeAdd { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    BeforeAdd?.Invoke();
                    ProviderId = (string)args[0];
                    Config = (MonitoringConfig)args[1];
                    return Task.FromResult(AddedAuthor);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorLibraryService.{targetMethod?.Name}");
            }
        }

        private class CommandRepositoryProxy : DispatchProxy
        {
            public List<Command> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ICommandRepository.Insert))
                {
                    var model = (CommandModel)args[0];
                    model.Id = Commands.Count + 1;
                    Commands.Add(model.Body);
                    return model;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private sealed class RecordingBookMonitoredService : IBookMonitoredService
        {
            public Author Author { get; private set; }
            public MonitoringOptions Options { get; private set; }

            public void SetBookMonitoredStatus(Author author, MonitoringOptions monitoringOptions, bool initialSetup = false)
            {
                Author = author;
                Options = monitoringOptions;
            }
        }

        private class ImportListExclusionServiceProxy : DispatchProxy
        {
            public List<ImportListExclusion> Exclusions { get; set; } = new();
            public List<int> DeletedIds { get; } = new();
            public List<int> UpdatedIds { get; } = new();
            public List<List<string>> LookupIdLists { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportListExclusionService.FindByForeignId))
                {
                    var ids = (List<string>)args[0];
                    LookupIdLists.Add(ids.ToList());
                    return Exclusions
                        .Where(exclusion => ids.Contains(exclusion.ForeignId, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                }

                if (targetMethod?.Name == nameof(IImportListExclusionService.Delete))
                {
                    var id = (int)args[0];
                    DeletedIds.Add(id);
                    Exclusions.RemoveAll(exclusion => exclusion.Id == id);

                    return null;
                }

                if (targetMethod?.Name == nameof(IImportListExclusionService.Update))
                {
                    UpdatedIds.Add(((ImportListExclusion)args[0]).Id);
                    return args[0];
                }

                throw new NotImplementedException($"Test proxy does not implement IImportListExclusionService.{targetMethod?.Name}");
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;
            private readonly Func<string, string, Author> _findByProviderId;

            public StubAuthorService(Author author, Func<string, string, Author> findByProviderId = null)
            {
                _author = author;
                _findByProviderId = findByProviderId;
            }

            public Author GetAuthor(int authorId) => _author != null && _author.Id == authorId ? _author : null;
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => _author != null && authorIds.Contains(_author.Id) ? new List<Author> { _author } : new List<Author>();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => _findByProviderId != null ? _findByProviderId(provider, providerId) : throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
            public Author UpdateAuthor(Author author) => author;
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void SetAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();

            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            public List<Book> AuthorBooks { get; set; } = new();
            public List<Book> AddOptionsBooks { get; private set; }
            public Book AddedBook { get; private set; }
            public Book UpdatedBook { get; private set; }
            public List<(int[] Ids, string MediaType)> RequestedMonitoringCalls { get; } = new();
            public bool FindExistingBook { get; set; }
            public Book WantedResult { get; set; }
            public (int BookId, int EditionId)? WantedRequest { get; private set; }
            public List<List<(string Scope, string Provider, string Id)>> ProviderLookupBatches { get; } = new();
            public List<int[]> LookupBookBatches { get; } = new();

            public Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated = null) => throw new System.NotSupportedException();
            public Book UpdateBook(Book book)
            {
                UpdatedBook = book;
                return book;
            }

            public Book GetBook(int bookId) => AuthorBooks.Single(book => book.Id == bookId);
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksForLookup(IEnumerable<int> bookIds)
            {
                var ids = bookIds.ToHashSet();
                LookupBookBatches.Add(ids.ToArray());
                return AuthorBooks.Where(book => ids.Contains(book.Id)).ToList();
            }
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => AuthorBooks;
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true)
            {
                if (newBook.Id <= 0)
                {
                    newBook.Id = 101;
                }

                AddedBook = newBook;
                var existingIndex = AuthorBooks.FindIndex(book => book.Id == newBook.Id);
                if (existingIndex >= 0)
                {
                    AuthorBooks[existingIndex] = newBook;
                }
                else
                {
                    AuthorBooks.Add(newBook);
                }

                return newBook;
            }
            public List<Book> FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => FindExistingBook
                ? FindAllByWorkProviderId(provider, providerId, mediaType).FirstOrDefault() : null;
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public Dictionary<(string Scope, string Provider, string Id), List<int>> FindBookIdsByProviderIds(
                IEnumerable<(string Scope, string Provider, string Id)> lookups, Action<(string Scope, string Provider, string Id)> onIncomplete = null)
            {
                var keys = lookups.ToList();
                ProviderLookupBatches.Add(keys);
                return keys.ToDictionary(key => key, key => key.Scope == "work"
                    ? AuthorBooks.Where(book => BookIdentity.GetStableWorkProviderIdentityTokens(book)
                        .Contains(ProviderIdHelper.Canonicalize(key.Id, key.Provider))).Select(book => book.Id).ToList()
                    : throw new NotImplementedException());
            }
            public List<Book> FindAllByWorkProviderId(string provider, string providerId, BookMediaType mediaType)
            {
                var normalized = ProviderIdHelper.Canonicalize(providerId, provider);
                return AuthorBooks.Where(book => book.MediaType == mediaType &&
                                                  BookEditionIdentity.GetCanonicalWorkProviderIds(book)
                                                      .Concat(book.RemoteProviderIds ?? Enumerable.Empty<string>())
                                                      .Any(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored)
            {
                var selected = ids.ToHashSet();
                AuthorBooks.Where(book => selected.Contains(book.Id)).ToList().ForEach(book => book.SetMonitored(monitored));
            }
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored)
            {
                foreach (var book in AuthorBooks.Where(book => ids.Contains(book.Id)))
                {
                    book.SetMonitoredForMediaType(mediaType, monitored);
                }
            }
            public void MonitorRequestedBooks(IEnumerable<int> ids, string mediaType)
            {
                var bookIds = ids.ToArray();
                RequestedMonitoringCalls.Add((bookIds, mediaType));
                SetMonitoredForMediaType(bookIds, mediaType, true);
            }
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<NzbDrone.Core.MediaFiles.BookFile> deletedBookFiles = null) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => AddOptionsBooks = books.ToList();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId) => throw new NotImplementedException();
            public Dictionary<int, Book> AddWantedEditions(List<Edition> requestedEditions, Action<Edition, Exception> onFailure = null)
            {
                var edition = requestedEditions.Single();
                WantedRequest = (edition.BookId, edition.Id);
                return new Dictionary<int, Book> { [edition.Id] = WantedResult ?? throw new NotImplementedException() };
            }
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly List<Edition> _editions;
            public Edition MonitoredEdition { get; private set; }

            public StubEditionService(IEnumerable<Edition> editions)
            {
                _editions = editions?.ToList() ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByBook(int bookId) => _editions.Where(e => e.BookId == bookId).ToList();

            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
            {
                MonitoredEdition = edition;
                foreach (var item in _editions.Where(e => e.BookId == edition.BookId))
                {
                    item.Monitored = item.Id == edition.Id;
                }

                return _editions.Where(e => e.BookId == edition.BookId).ToList();
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
            public Dictionary<string, List<Edition>> GetEditionsByProviderIds(string provider, IEnumerable<string> ids) => ids
                .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id,
                    id => _editions.Where(edition => BookEditionIdentity.EditionMatchesProviderId(edition, provider + ":" + id)).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();

            public void RefreshProviderAliases(List<Edition> editions) { }
            public void UpdateSelection(List<Edition> editions) => UpdateMany(editions);
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            private readonly Dictionary<int, MetadataProfile> _profiles;

            public StubMetadataProfileService(params MetadataProfile[] profiles)
            {
                _profiles = (profiles ?? Array.Empty<MetadataProfile>()).ToDictionary(p => p.Id);
            }

            public bool Exists(int id, MetadataProfileType? profileType = null) => _profiles.ContainsKey(id);
            public MetadataProfile Get(int id) => _profiles.TryGetValue(id, out var profile) ? profile : null;

            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public List<Book> FilterBooks(Author input, int profileId) => throw new NotImplementedException();

            public List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds) =>
                FilterBooks(input, profileId, requestedNarrationIds, null);

            public List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds) =>
                FilterBooks(input, profileId);
        }

        [TestCase("gr-ed:gr:8888")]
        [TestCase("hc-ed:hc:8888")]
        [TestCase("hc:edition:hc:8888")]
        public void explicit_edition_payload_rejects_malformed_identity_before_variant_expansion(string editionId)
        {
            var service = BuildService(new StubAuthorService(null), new StubBookService(), new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService());
            var method = typeof(AddBookService).GetMethod("GetRequestedEditionProviderIdsFromPayload", BindingFlags.NonPublic | BindingFlags.Instance);
            var ids = (IReadOnlyCollection<string>)method.Invoke(service, new object[] { new List<Edition> { new() { ForeignEditionId = editionId, Monitored = true } } });
            Assert.That(ids, Is.Empty);
        }

        [TestCase("gr:8888")]
        [TestCase("hc:edition:8888")]
        [TestCase("az:B000ABC123")]
        [TestCase("ol:OL8888M")]
        [TestCase("gb:volume8888")]
        public void explicit_edition_payload_keeps_valid_provider_identity(string editionId)
        {
            var service = BuildService(new StubAuthorService(null), new StubBookService(), new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService());
            var method = typeof(AddBookService).GetMethod("GetRequestedEditionProviderIdsFromPayload", BindingFlags.NonPublic | BindingFlags.Instance);
            var ids = (IReadOnlyCollection<string>)method.Invoke(service, new object[] { new List<Edition> { new() { ForeignEditionId = editionId, Monitored = true } } });
            Assert.That(ids, Does.Contain(editionId));
        }

        private static AddBookService BuildService(
            IAuthorService authorService,
            IBookService bookService,
            IEditionService editionService,
            IMetadataProfileService metadataProfileService,
            IAuthorLibraryService authorLibraryService = null,
            IImportListExclusionService importListExclusionService = null,
            IProvideBookInfo bookInfo = null,
            IBookMonitoredService bookMonitoredService = null,
            IManageCommandQueue commandQueue = null)
        {
            commandQueue ??= new CommandQueueManager(DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>(),
                null, null, LogManager.GetCurrentClassLogger());
            return new AddBookService(
                authorService,
                authorLibraryService ?? DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>(),
                bookService,
                bookMonitoredService ?? new RecordingBookMonitoredService(),
                bookInfo ?? DispatchProxy.Create<IProvideBookInfo, RoutingBookInfoProxy>(),
                importListExclusionService ?? DispatchProxy.Create<IImportListExclusionService, ThrowingProxy<IImportListExclusionService>>(),
                DispatchProxy.Create<ISeriesBookLinkService, ThrowingProxy<ISeriesBookLinkService>>(),
                DispatchProxy.Create<ISeriesService, ThrowingProxy<ISeriesService>>(),
                DispatchProxy.Create<IProvideAuthorInfo, ThrowingProxy<IProvideAuthorInfo>>(),
                DispatchProxy.Create<IBuildFileNames, ThrowingProxy<IBuildFileNames>>(),
                DispatchProxy.Create<IMonitoringService, ThrowingProxy<IMonitoringService>>(),
                editionService,
                new EditionSelector(LogManager.GetCurrentClassLogger()),
                new EditionMetadataProfileFilter(new TestTermMatcherService()),
                metadataProfileService,
                LogManager.GetCurrentClassLogger(),
                commandQueue);
        }

        [Test]
        public async Task root_scoped_request_should_preserve_settings_and_search_only_its_target(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(-1, 0, 1)] int profileId,
            [Values(MonitorTypes.None, MonitorTypes.All)] MonitorTypes initial,
            [Values(false, true)] bool searchNow)
        {
            var audio = mediaType == BookMediaType.Audiobook;
            var configured = profileId > 0;
            var author = new Author
            {
                Id = 42,
                HardcoverAuthorId = "hc:777",
                AudiobookQualityProfileId = !audio ? 1 : profileId < 0 ? null : profileId,
                AudiobookRootFolderPath = !audio || profileId >= 0 ? @"C:\Audio" : null,
                EbookQualityProfileId = audio ? 2 : profileId < 0 ? null : profileId,
                EbookRootFolderPath = audio || profileId >= 0 ? @"C:\Books" : null,
                AudiobookMonitored = false,
                EbookMonitored = false,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitorNewItems = NewItemMonitorTypes.None,
                AddOptions = configured ? null : new AddAuthorOptions
                {
                    AudiobookMonitor = audio ? initial : null,
                    EbookMonitor = audio ? null : initial
                }
            };
            var target = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = mediaType };
            var sibling = new Book { Id = 102, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1002", MediaType = mediaType };
            var selectedSibling = new Book { Id = 104, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1003", MediaType = mediaType };
            selectedSibling.SetMonitored(true);
            var opposite = new Book { Id = 103, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = audio ? BookMediaType.Ebook : BookMediaType.Audiobook };
            var books = new StubBookService { AuthorBooks = new List<Book> { target, sibling, selectedSibling, opposite }, FindExistingBook = true };
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = author;
            var commandRepository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var queue = new CommandQueueManager(commandRepository, null, null, LogManager.GetCurrentClassLogger());
            var info = DispatchProxy.Create<IProvideBookInfo, RoutingBookInfoProxy>();
            var service = BuildService(new StubAuthorService(author, (_, _) => author), books,
                new StubEditionService(Array.Empty<Edition>()), new StubMetadataProfileService(), library,
                DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), bookInfo: info, commandQueue: queue);
            var result = await service.AddBook(new Book
            {
                HardcoverBookId = "hc:1001",
                MediaType = mediaType,
                Author = new Author
                {
                    HardcoverAuthorId = "hc:777",
                    AudiobookMonitored = false,
                    EbookMonitored = false,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                    EbookMonitorNewItems = NewItemMonitorTypes.None,
                    AddOptions = new AddAuthorOptions
                    {
                        Monitor = MonitorTypes.SpecificBook,
                        AudiobookMonitor = audio ? initial : null,
                        EbookMonitor = audio ? null : initial
                    }
                },
                AddOptions = new AddBookOptions { SearchForNewBook = searchNow }
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsMonitored(), Is.True);
                Assert.That(sibling.IsMonitored(), Is.False);
                Assert.That(selectedSibling.IsMonitored(), Is.True);
                Assert.That(opposite.IsMonitored(), Is.False);
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(books.UpdatedBook, Is.Null);
                Assert.That(((RoutingBookInfoProxy)info).WorkReads, Is.EqualTo(configured ? 0 : 1));
                Assert.That(target.AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                var commands = ((CommandRepositoryProxy)commandRepository).Commands.OfType<BookSearchCommand>().ToList();
                Assert.That(commands.Count, Is.EqualTo(searchNow ? 1 : 0));
                if (searchNow)
                {
                    Assert.That(commands.Single().BookIds, Is.EqualTo(new[] { target.Id }));
                    Assert.That(commands.Single().Trigger, Is.EqualTo(CommandTrigger.Manual));
                    var repeated = queue.Push(new BookSearchCommand(new List<int> { target.Id })
                    {
                        Trigger = CommandTrigger.Manual
                    }, trigger: CommandTrigger.Manual);
                    Assert.That(repeated.Body, Is.SameAs(commands.Single()));
                    Assert.That(repeated.Trigger, Is.EqualTo(CommandTrigger.Manual));
                }
                var config = ((AuthorLibraryProxy)library).Config;
                if (configured)
                {
                    Assert.That(config, Is.Null);
                }
                else
                {
                    Assert.That(audio ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(initial));
                    Assert.That(audio ? config.AudiobookMonitored : config.EbookMonitored, Is.False);
                    Assert.That(audio ? config.AudiobookBooksToSearch : config.EbookBooksToSearch,
                        Is.EqualTo(searchNow ? new[] { "hc:1001" } : null));
                    Assert.That(target.AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                }
            });
        }

        private static void InvokeEnsureAutoSelectedEdition(AddBookService service, Book book)
        {
            var method = typeof(AddBookService).GetMethod("EnsureAutoSelectedEdition", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.EnsureAutoSelectedEdition via reflection");
            }

            method.Invoke(service, new object[] { book });
        }

        private static Edition InvokeSelectPreferredEditionForAdd(AddBookService service, Book book, IEnumerable<Edition> editions, string requestedEditionId)
        {
            var requestedIds = requestedEditionId == null
                ? Array.Empty<string>()
                : new[] { requestedEditionId };

            return InvokeSelectPreferredEditionForAdd(service, book, editions, requestedIds);
        }

        private static Edition InvokeSelectPreferredEditionForAdd(AddBookService service, Book book, IEnumerable<Edition> editions, IReadOnlyCollection<string> requestedEditionProviderIds)
        {
            var method = typeof(AddBookService).GetMethod("SelectPreferredEditionForAdd", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.SelectPreferredEditionForAdd via reflection");
            }

            return (Edition)method.Invoke(service, new object[] { book, editions, requestedEditionProviderIds ?? Array.Empty<string>() });
        }

        private static void InvokeApplyEditionRetentionForAdd(AddBookService service, Book book, string requestedEditionId)
        {
            var method = typeof(AddBookService).GetMethod("ApplyEditionRetentionForAdd", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.ApplyEditionRetentionForAdd via reflection");
            }

            var requestedIds = requestedEditionId == null
                ? Array.Empty<string>()
                : new[] { requestedEditionId };

            method.Invoke(service, new object[] { book, requestedIds });
        }

        private static IReadOnlyCollection<string> InvokeGetRequestedEditionProviderIdsFromPayload(AddBookService service, IEnumerable<Edition> editions)
        {
            var method = typeof(AddBookService).GetMethod("GetRequestedEditionProviderIdsFromPayload", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.GetRequestedEditionProviderIdsFromPayload via reflection");
            }

            return (IReadOnlyCollection<string>)method.Invoke(service, new object[] { editions });
        }

        private static void AssertSingleMonitoredEdition(IEnumerable<Edition> editions, string expectedForeignEditionId)
        {
            var monitored = editions.Where(e => e.Monitored).ToList();
            Assert.That(monitored.Select(e => e.ForeignEditionId), Is.EqualTo(new[] { expectedForeignEditionId }));
        }

        [Test]
        public void should_select_allowed_audio_over_escape_hatch_and_update_book_foreign_edition_id()
        {
            var profile = new MetadataProfile { Id = 7, AllowedLanguages = "eng" };
            var author = new Author { Id = 3, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 44,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
            };

            var bookService = new StubBookService();
            var editionService = new StubEditionService(editions);
            var service = BuildService(
                new StubAuthorService(author),
                bookService,
                editionService,
                new StubMetadataProfileService(profile));

            InvokeEnsureAutoSelectedEdition(service, book);

            Assert.That(editionService.MonitoredEdition?.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(bookService.UpdatedBook, Is.SameAs(book));
        }

        [Test]
        public void should_use_allowed_ebook_representative_when_no_audio_candidate_survives_filters()
        {
            var profile = new MetadataProfile { Id = 8, AllowedLanguages = "eng" };
            var author = new Author { Id = 4, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 45,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
            };

            var editionService = new StubEditionService(editions);
            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                editionService,
                new StubMetadataProfileService(profile));

            InvokeEnsureAutoSelectedEdition(service, book);

            Assert.That(editionService.MonitoredEdition?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_fall_back_to_print_when_skip_missing_isbn_removes_ebook_candidates()
        {
            var profile = new MetadataProfile { Id = 9, AllowedLanguages = "eng", SkipMissingIsbn = true };
            var author = new Author { Id = 5, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 46,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-ebook-missing", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1, Isbn13 = "9780441013593" }
            };

            var editionService = new StubEditionService(editions);
            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                editionService,
                new StubMetadataProfileService(profile));

            InvokeEnsureAutoSelectedEdition(service, book);

            Assert.That(editionService.MonitoredEdition?.ForeignEditionId, Is.EqualTo("eng-print"));
        }

        [Test]
        public void should_throw_validation_when_no_candidate_survives_filters()
        {
            var profile = new MetadataProfile { Id = 10, AllowedLanguages = "eng", SkipMissingIsbn = true };
            var author = new Author { Id = 6, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 47,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-ebook-missing", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(editions),
                new StubMetadataProfileService(profile));

            var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureAutoSelectedEdition(service, book));
            Assert.That(ex?.InnerException, Is.TypeOf<ValidationException>());
        }

        [Test]
        public void should_use_selector_instead_of_first_remote_edition_for_ids_only_hydration()
        {
            var profile = new MetadataProfile { Id = 11, AllowedLanguages = "eng" };
            var author = new Author { Id = 7, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 },
                new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: null);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_select_highest_rated_allowed_audiobook_when_multiple_audio_editions_exist()
        {
            var profile = new MetadataProfile { Id = 13, AllowedLanguages = "eng" };
            var author = new Author { Id = 9, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    ForeignEditionId = "eng-audio-rich",
                    Title = "Dune Audio Rich",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Overview = "Detailed description",
                    Narrator = "Narrator One",
                    DurationSeconds = 36000,
                    Ratings = new Ratings { Votes = 25, Value = 4.9m }
                },
                new Edition
                {
                    Id = 2,
                    ForeignEditionId = "eng-audio-popular",
                    Title = "Dune Audio Popular",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Ratings = new Ratings { Votes = 5000, Value = 4.2m }
                },
                new Edition
                {
                    Id = 3,
                    ForeignEditionId = "eng-ebook",
                    Title = "Dune Ebook",
                    Language = "eng",
                    ReadingFormatId = 3,
                    Ratings = new Ratings { Votes = 50000, Value = 4.8m }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: null);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio-popular"));
        }

        [Test]
        public void should_select_highest_rated_allowed_ebook_when_multiple_ebook_editions_exist()
        {
            var profile = new MetadataProfile { Id = 14, AllowedLanguages = "eng" };
            var author = new Author { Id = 10, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    ForeignEditionId = "eng-ebook-rich",
                    Title = "Dune Ebook Rich",
                    Language = "eng",
                    ReadingFormatId = 3,
                    Overview = "Detailed description",
                    Ratings = new Ratings { Votes = 25, Value = 4.9m }
                },
                new Edition
                {
                    Id = 2,
                    ForeignEditionId = "eng-ebook-popular",
                    Title = "Dune Ebook Popular",
                    Language = "eng",
                    ReadingFormatId = 3,
                    Ratings = new Ratings { Votes = 5000, Value = 4.2m }
                },
                new Edition
                {
                    Id = 3,
                    ForeignEditionId = "eng-print",
                    Title = "Dune Hardcover",
                    Language = "eng",
                    ReadingFormatId = 1,
                    Ratings = new Ratings { Votes = 50000, Value = 4.8m }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: null);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook-popular"));
        }

        [Test]
        public void should_fall_back_to_best_edition_when_requested_edition_is_missing()
        {
            var profile = new MetadataProfile { Id = 12, AllowedLanguages = "eng" };
            var author = new Author { Id = 8, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "fra-ebook", Title = "Dune French", Language = "fra", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "missing-edition");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_select_requested_native_edition_by_goodreads_alias_when_foreign_edition_shape_changes()
        {
            var profile = new MetadataProfile { Id = 27, AllowedLanguages = "eng" };
            var author = new Author { Id = 27, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    ForeignEditionId = "hc:edition:22222-audiobook",
                    GoodreadsEditionId = 61304420,
                    Title = "Dune Requested Audio",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Ratings = new Ratings { Votes = 1, Value = 3.0m }
                },
                new Edition
                {
                    Id = 2,
                    ForeignEditionId = "hc:edition:33333-audiobook",
                    GoodreadsEditionId = 99999999,
                    Title = "Dune Popular Audio",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Ratings = new Ratings { Votes = 5000, Value = 4.8m }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "gr:61304420");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("hc:edition:22222-audiobook"));
        }

        [Test]
        public void should_select_requested_edition_from_payload_alias_when_foreign_id_is_missing()
        {
            var profile = new MetadataProfile { Id = 28, AllowedLanguages = "eng" };
            var author = new Author { Id = 28, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var payloadEditions = new List<Edition>
            {
                new Edition { HardcoverEditionId = "hc:edition:44444", Monitored = true }
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "hc:edition:44444-audiobook", HardcoverEditionId = "44444", Title = "Dune Requested Audio", Language = "eng", ReadingFormatId = 2 },
                new Edition { Id = 2, ForeignEditionId = "hc:edition:55555-audiobook", HardcoverEditionId = "55555", Title = "Dune Other Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var requestedIds = InvokeGetRequestedEditionProviderIdsFromPayload(service, payloadEditions);
            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedIds);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("hc:edition:44444-audiobook"));
        }

        [Test]
        public void should_match_requested_edition_against_plural_asins()
        {
            var profile = new MetadataProfile { Id = 29, AllowedLanguages = "eng" };
            var author = new Author { Id = 29, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-audio-requested", Title = "Dune Requested Audio", Language = "eng", ReadingFormatId = 2, Asins = new List<string> { "B012345678" } },
                new Edition { Id = 2, ForeignEditionId = "eng-audio-other", Title = "Dune Other Audio", Language = "eng", ReadingFormatId = 2, Asin = "B087654321" }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "az:B012345678");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio-requested"));
        }

        [Test]
        public void should_not_match_goodreads_provider_id_to_hardcover_numeric_collision()
        {
            var edition = new Edition
            {
                ForeignEditionId = "hc:edition:12345-audiobook",
                HardcoverEditionId = "12345",
                Title = "Dune Audio",
                Language = "eng",
                ReadingFormatId = 2
            };

            Assert.That(BookEditionIdentity.EditionMatchesProviderId(edition, "gr:12345"), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void service_scoped_request_should_choose_one_copy_in_the_requested_format(bool monitoredCopy)
        {
            var author = new Author { Id = 42 };
            var low = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var high = new Book { Id = 102, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            high.SetMonitored(monitoredCopy);
            var otherSide = new Book { Id = 99, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Ebook };
            otherSide.SetMonitored(true);
            var service = BuildService(new StubAuthorService(author),
                new StubBookService { AuthorBooks = new List<Book> { otherSide, high, low } },
                new StubEditionService(Array.Empty<Edition>()), new StubMetadataProfileService());
            var method = typeof(AddBookService).GetMethod("ResolveLocalRequestedBook", BindingFlags.NonPublic | BindingFlags.Instance);
            var result = (Book)method.Invoke(service, new object[] { author.Id, "hc:1001", BookMediaType.Audiobook });
            Assert.That(result.Id, Is.EqualTo(monitoredCopy ? high.Id : low.Id));
        }

        [Test]
        public void add_retention_should_preserve_the_pin_over_a_different_requested_edition()
        {
            var profile = new MetadataProfile { Id = 31, AllowedLanguages = "eng" };
            var author = new Author { Id = 31, AudiobookMetadataProfileId = profile.Id };
            var pinned = new Edition { Id = 102, ForeignEditionId = "hc:edition:102", ReadingFormatId = 2,
                Title = "Pinned recording", Language = "eng", Monitored = true, ManualAdd = true };
            var requested = new Edition { Id = 101, ForeignEditionId = "hc:edition:101", ReadingFormatId = 2,
                Title = "Other recording", Language = "eng" };
            var book = new Book { Id = 10, AuthorId = author.Id, Author = author, Title = "Requested work",
                MediaType = BookMediaType.Audiobook, AnyEditionOk = false, Editions = new List<Edition> { requested, pinned } };
            var service = BuildService(new StubAuthorService(author), new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()), new StubMetadataProfileService(profile));
            InvokeApplyEditionRetentionForAdd(service, book, requested.ForeignEditionId);
            Assert.That(book.Editions.Single(edition => edition.Monitored).Id, Is.EqualTo(pinned.Id));
            Assert.That(book.Editions.Single(edition => edition.ManualAdd).Id, Is.EqualTo(pinned.Id));
        }

        [Test]
        public void should_monitor_only_one_saved_edition_when_requested_alias_matches_multiple_retained_editions()
        {
            var profile = new MetadataProfile { Id = 31, AllowedLanguages = "eng" };
            var author = new Author { Id = 31, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 101, ForeignEditionId = "eng-audio-one", Title = "Dune Audio One", Language = "eng", ReadingFormatId = 2, Asin = "B012345678" },
                    new Edition { Id = 102, ForeignEditionId = "eng-audio-two", Title = "Dune Audio Two", Language = "eng", ReadingFormatId = 2, AudibleASIN = "B012345678" }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "az:B012345678");

            AssertSingleMonitoredEdition(book.Editions, "eng-audio-one");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio-one"));
        }

        [Test]
        public void should_monitor_only_one_unsaved_edition_when_requested_alias_matches_multiple_retained_editions()
        {
            var profile = new MetadataProfile { Id = 32, AllowedLanguages = "eng" };
            var author = new Author { Id = 32, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-audio-one", Title = "Dune Audio One", Language = "eng", ReadingFormatId = 2, Asin = "B012345678" },
                    new Edition { ForeignEditionId = "eng-audio-two", Title = "Dune Audio Two", Language = "eng", ReadingFormatId = 2, AudibleASIN = "B012345678" }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "az:B012345678");

            AssertSingleMonitoredEdition(book.Editions, "eng-audio-one");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio-one"));
        }

        [Test]
        public void should_not_let_requested_non_native_alias_steal_monitoring_when_native_survives()
        {
            var profile = new MetadataProfile { Id = 30, AllowedLanguages = "eng" };
            var author = new Author { Id = 30, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "hc:edition:44444-ebook", GoodreadsEditionId = 61304420, Title = "Dune Ebook", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "hc:edition:55555-audiobook", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "gr:61304420");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("hc:edition:55555-audiobook"));
        }

        [Test]
        public void should_accept_requested_representative_edition_when_no_native_edition_survives_filters()
        {
            var profile = new MetadataProfile { Id = 15, AllowedLanguages = "eng" };
            var author = new Author { Id = 15, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                new Edition { Id = 3, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "eng-ebook");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_prefer_native_edition_when_requested_non_native_is_only_a_hint()
        {
            var profile = new MetadataProfile { Id = 18, AllowedLanguages = "eng" };
            var author = new Author { Id = 18, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "eng-ebook");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_honor_requested_manual_representative_even_when_native_survives()
        {
            var profile = new MetadataProfile { Id = 20, AllowedLanguages = "eng" };
            var author = new Author { Id = 20, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3, ManualAdd = true },
                new Edition { Id = 2, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "eng-ebook");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_prune_editions_before_direct_book_insert()
        {
            var profile = new MetadataProfile { Id = 16, AllowedLanguages = "eng" };
            var author = new Author { Id = 16, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: null);

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-audio", "eng-ebook" }));
            AssertSingleMonitoredEdition(book.Editions, "eng-audio");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_drop_non_allowed_languages_before_audiobook_companion_rule_fires()
        {
            var profile = new MetadataProfile { Id = 26, AllowedLanguages = "eng" };
            var author = new Author { Id = 26, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "spa-audio", Title = "Dune Spanish Audio", Language = "spa", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "spa-ebook", Title = "Dune Spanish", Language = "spa", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "spa-print", Title = "Dune Spanish Print", Language = "spa", ReadingFormatId = 1 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: null);

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-audio", "eng-ebook" }));
            Assert.That(book.Editions.All(e => e.Language == "eng"), Is.True);
        }

        [Test]
        public void should_protect_requested_representative_when_pruning_before_direct_book_insert_if_no_native_survives()
        {
            var profile = new MetadataProfile { Id = 17, AllowedLanguages = "eng" };
            var author = new Author { Id = 17, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                    new Edition { ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "eng-ebook");

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-ebook" }));
            AssertSingleMonitoredEdition(book.Editions, "eng-ebook");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_not_protect_requested_representative_before_direct_book_insert_when_native_survives()
        {
            var profile = new MetadataProfile { Id = 19, AllowedLanguages = "eng" };
            var author = new Author { Id = 19, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "eng-ebook");

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-audio", "eng-ebook" }));
            AssertSingleMonitoredEdition(book.Editions, "eng-audio");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_use_monitored_payload_edition_for_requested_edition_id()
        {
            var service = BuildService(
                new StubAuthorService(null),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService());

            var payloadEditions = new List<Edition>
            {
                new Edition { Id = 20, ForeignEditionId = "second" },
                new Edition { Id = 10, ForeignEditionId = "first", Monitored = true }
            };

            var requestedEditionIds = InvokeGetRequestedEditionProviderIdsFromPayload(service, payloadEditions);

            Assert.That(requestedEditionIds, Does.Contain("first"));
        }

        [Test]
        public async Task explicit_narration_on_readded_book_must_use_the_wanted_writer(
            [Values(false, true)] bool compatiblePin, [Values(false, true)] bool monitor)
        {
            var author = new Author { Id = 42, Name = "Requested Author", HardcoverAuthorId = "hc:777" };
            var current = new Edition { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:301", Title = "Requested Work",
                Narrator = "Jim Dale", ReadingFormatId = 2, Monitored = true, ManualAdd = true };
            var narration = new Edition { Id = 302, BookId = 101, ForeignEditionId = "hc:edition:302", Title = current.Title,
                Narrator = compatiblePin ? "Jim dal" : "Stephen Fry", ReadingFormatId = 2 };
            var stored = new Book { Id = 101, AuthorId = author.Id, Author = author, Title = current.Title,
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                Editions = new List<Edition> { current, narration } };
            stored.SetMonitored(true);
            var wanted = compatiblePin ? stored : new Book { Id = 102, AuthorId = author.Id, Author = author, Title = current.Title,
                HardcoverBookId = stored.HardcoverBookId, MediaType = stored.MediaType, AnyEditionOk = false,
                Editions = new List<Edition> { new() { Id = 303, BookId = 102, ForeignEditionId = narration.ForeignEditionId,
                    Title = narration.Title, Narrator = narration.Narrator, ReadingFormatId = 2, ManualAdd = true, Monitored = true } } };
            var books = new StubBookService { AuthorBooks = new List<Book> { stored }, FindExistingBook = true, WantedResult = wanted };
            if (!compatiblePin) books.AuthorBooks.Add(wanted);
            var queueRepository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var queue = new CommandQueueManager(queueRepository, null, null, LogManager.GetCurrentClassLogger());
            var service = BuildService(new StubAuthorService(author, (_, _) => author), books,
                new StubEditionService(stored.Editions), new StubMetadataProfileService(),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), commandQueue: queue);
            var requestEdition = RefreshEntityCopy.CloneEdition(narration);
            requestEdition.Monitored = requestEdition.ManualAdd = true;
            var request = new Book { HardcoverBookId = stored.HardcoverBookId, MediaType = stored.MediaType,
                Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId,
                    AddOptions = new AddAuthorOptions { Monitor = monitor ? MonitorTypes.SpecificBook : MonitorTypes.None } },
                Editions = new List<Edition> { requestEdition },
                AddOptions = new AddBookOptions { SearchForNewBook = true, MonitorBook = monitor } };
            var result = await service.AddBook(request);
            Assert.That(result.Id, Is.EqualTo(wanted.Id));
            Assert.That(result.IsMonitored(), Is.EqualTo(monitor));
            Assert.That(books.WantedRequest, Is.EqualTo((wanted.Id, compatiblePin ? current.Id : wanted.Editions.Single().Id)));
            Assert.That(current.Monitored && current.ManualAdd, Is.True);
            Assert.That(books.AddedBook, Is.Null);
            Assert.That(((CommandRepositoryProxy)queueRepository).Commands.OfType<BookSearchCommand>().Single().BookIds,
                Is.EqualTo(new[] { wanted.Id }));
        }

        [TestCase(false, false, false, false)]
        [TestCase(false, true, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(false, true, false, true)]
        public async Task native_narration_request_should_reuse_the_complete_cast_across_authoritative_pockets(
            bool exactEditionPresent, bool compatibleInSource, bool workAliasOnly, bool sourceAutomatic)
        {
            var author = new Author { Id = 42, Name = "Requested Author", HardcoverAuthorId = "hc:777" };
            var requested = new Edition { ForeignEditionId = "hc:edition:999", ReadingFormatId = 2,
                Title = "Requested Work", NarratorNames = new List<string> { "Jim Dale", "David Tennant" },
                DurationSeconds = 600, Monitored = true, ManualAdd = true };
            var current = new Edition { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:301", ReadingFormatId = 2,
                Title = requested.Title, NarratorNames = compatibleInSource ? new List<string> { "David Tennant", "Jim dal" }
                    : new List<string> { "Stephen Fry" }, DurationSeconds = 7200, Monitored = true, ManualAdd = !sourceAutomatic };
            var file = new NzbDrone.Core.MediaFiles.BookFile { Id = 401, EditionId = current.Id, Edition = current };
            var stored = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001",
                Title = requested.Title, MediaType = BookMediaType.Audiobook, AnyEditionOk = sourceAutomatic,
                Editions = new List<Edition> { current }, BookFiles = new List<NzbDrone.Core.MediaFiles.BookFile> { file } };
            var compatible = new Edition { Id = 302, BookId = 102, ForeignEditionId = "hc:edition:302", ReadingFormatId = 2,
                Title = requested.Title, NarratorNames = new List<string> { "David Tennant", "Jim dal" },
                DurationSeconds = null, Monitored = true, ManualAdd = true };
            var sibling = new Book { Id = 102, AuthorId = author.Id, Author = author,
                HardcoverBookId = workAliasOnly ? "hc:1002" : stored.HardcoverBookId,
                RemoteProviderIds = workAliasOnly ? new HashSet<string> { stored.HardcoverBookId } : null,
                Title = requested.Title, MediaType = stored.MediaType, AnyEditionOk = false,
                Editions = new List<Edition> { compatible } };
            if (exactEditionPresent)
            {
                var exact = RefreshEntityCopy.CloneEdition(requested);
                exact.Id = 303;
                exact.BookId = stored.Id;
                exact.Monitored = exact.ManualAdd = false;
                stored.Editions.Add(exact);
            }

            var wanted = compatibleInSource && !sourceAutomatic ? stored : sibling;
            var books = new StubBookService { AuthorBooks = new List<Book> { stored, sibling }, FindExistingBook = true, WantedResult = wanted };
            books.AuthorBooks.AddRange(Enumerable.Range(200, 64).Select(id => new Book { Id = id, Author = author,
                HardcoverBookId = "hc:" + (2000 + id), MediaType = stored.MediaType, Editions = new List<Edition>() }));
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = author;
            var queueRepository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var service = BuildService(new StubAuthorService(author, (_, _) => author), books,
                new StubEditionService(books.AuthorBooks.SelectMany(book => book.Editions)), new StubMetadataProfileService(), library,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(),
                commandQueue: new CommandQueueManager(queueRepository, null, null, LogManager.GetCurrentClassLogger()));
            var request = new Book { HardcoverBookId = stored.HardcoverBookId, MediaType = stored.MediaType,
                Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId }, Editions = new List<Edition> { requested },
                AddOptions = new AddBookOptions { SearchForNewBook = true } };

            var result = await service.AddBook(request);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.SameAs(wanted));
                Assert.That(books.WantedRequest, Is.EqualTo((wanted.Id, wanted == stored ? current.Id : compatible.Id)));
                Assert.That(books.ProviderLookupBatches.Count, Is.EqualTo(1));
                Assert.That(books.ProviderLookupBatches.Single(), Is.EqualTo(new[] { ("work", "hc", "hc:1001") }));
                Assert.That(books.LookupBookBatches.Single(), Is.EquivalentTo(new[] { stored.Id, sibling.Id }));
                Assert.That(((AuthorLibraryProxy)library).Config, Is.Null);
                Assert.That(current.Monitored, Is.True);
                Assert.That(current.ManualAdd, Is.EqualTo(!sourceAutomatic));
                Assert.That(stored.AnyEditionOk, Is.EqualTo(sourceAutomatic));
                Assert.That(stored.BookFiles.Single(), Is.SameAs(file));
                Assert.That(file.EditionId, Is.EqualTo(current.Id));
                Assert.That(books.AddedBook, Is.Null);
                Assert.That(((CommandRepositoryProxy)queueRepository).Commands.OfType<BookSearchCommand>().Single().BookIds,
                    Is.EqualTo(new[] { wanted.Id }));
            });
        }

        [TestCase("work")]
        [TestCase("media")]
        [TestCase("author")]
        [TestCase("cast")]
        [TestCase("format")]
        [TestCase("exact-format")]
        [TestCase("exact-work")]
        [TestCase("narrator")]
        [TestCase("asin")]
        public void native_narration_request_should_reject_incompatible_catalog_evidence(string difference)
        {
            var author = new Author { Id = 42, Name = "Requested Author", HardcoverAuthorId = "hc:777" };
            var requested = new Edition { ForeignEditionId = "hc:edition:999", ReadingFormatId = 2,
                Title = "Requested Work", NarratorNames = new List<string> { "Jim Dale", "David Tennant" },
                Asin = "B012345678", Monitored = true, ManualAdd = true };
            var current = new Edition { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:301", ReadingFormatId = 2,
                Title = requested.Title, Narrator = "Stephen Fry", Monitored = true, ManualAdd = true };
            var stored = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001",
                Title = requested.Title, MediaType = BookMediaType.Audiobook, AnyEditionOk = false, Editions = new List<Edition> { current } };
            var candidate = new Edition { Id = 302, BookId = 102,
                ForeignEditionId = difference is "exact-format" or "exact-work" ? requested.ForeignEditionId : "hc:edition:302",
                ReadingFormatId = difference is "format" or "exact-format" ? 3 : 2, Title = requested.Title,
                NarratorNames = difference == "cast" ? new List<string> { "Jim Dale" }
                    : difference == "narrator" ? new List<string> { "Stephen Fry", "David Tennant" }
                    : new List<string> { "David Tennant", "Jim Dale" }, Asin = difference == "asin" ? requested.Asin : null };
            var sibling = new Book { Id = 102, Author = difference == "author" ? new Author { Id = 43 } : author,
                HardcoverBookId = difference is "work" or "asin" or "exact-work" ? "hc:1002" : stored.HardcoverBookId,
                Title = requested.Title, MediaType = difference == "media" ? BookMediaType.Ebook : stored.MediaType,
                Editions = new List<Edition> { candidate } };
            var books = new StubBookService { AuthorBooks = new List<Book> { stored, sibling }, FindExistingBook = true };
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = author;
            var service = BuildService(new StubAuthorService(author, (_, _) => author), books,
                new StubEditionService(books.AuthorBooks.SelectMany(book => book.Editions)), new StubMetadataProfileService(), library,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var request = new Book { HardcoverBookId = stored.HardcoverBookId, MediaType = stored.MediaType,
                Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId }, Editions = new List<Edition> { requested } };

            Assert.ThrowsAsync<ValidationException>(async () => await service.AddBook(request));
            Assert.That(books.WantedRequest, Is.Null);
            Assert.That(current.Monitored && current.ManualAdd && !stored.AnyEditionOk, Is.True);
            Assert.That(books.AddedBook, Is.Null);
        }

        [Test]
        public void queued_native_narration_request_should_retain_the_selected_complete_cast()
        {
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = new Author { Id = -123 };
            var service = BuildService(new StubAuthorService(null), new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()), new StubMetadataProfileService(), library,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var request = new Book { HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Author = new Author { HardcoverAuthorId = "hc:777" }, AddOptions = new AddBookOptions { SearchForNewBook = true },
                Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:999", ReadingFormatId = 2,
                    NarratorNames = new List<string> { "Jim Dale", "David Tennant" }, Monitored = true, ManualAdd = true } } };

            Assert.ThrowsAsync<PendingBookRequestException>(async () => await service.AddBook(request));

            var narration = ((AuthorLibraryProxy)library).Config.AudiobookNarrations.Single();
            Assert.Multiple(() =>
            {
                Assert.That(narration.WorkProviderId, Is.EqualTo("hc:1001"));
                Assert.That(narration.EditionProviderId, Is.EqualTo("hc:edition:999"));
                Assert.That(narration.NarratorNames, Is.EqualTo(request.Editions.Single().NarratorNames));
                Assert.That(narration.RequiresEdition && narration.Search, Is.True);
            });
        }

        [Test]
        public void native_narration_request_should_reject_conflicting_casts_for_the_same_edition_identity()
        {
            var author = new Author { Id = 42, HardcoverAuthorId = "hc:777" };
            var requested = new Edition { ForeignEditionId = "hc:edition:999", ReadingFormatId = 2,
                NarratorNames = new List<string> { "Jim Dale", "David Tennant" }, Monitored = true, ManualAdd = true };
            var matching = RefreshEntityCopy.CloneEdition(requested);
            matching.Id = 301;
            matching.BookId = 101;
            var conflicting = RefreshEntityCopy.CloneEdition(requested);
            conflicting.Id = 302;
            conflicting.BookId = matching.BookId;
            conflicting.NarratorNames = new List<string> { "Stephen Fry", "David Tennant" };
            conflicting.Monitored = conflicting.ManualAdd = false;
            var stored = new Book { Id = matching.BookId, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Audiobook, AnyEditionOk = false, Editions = new List<Edition> { matching, conflicting } };
            var books = new StubBookService { AuthorBooks = new List<Book> { stored }, FindExistingBook = true };
            var service = BuildService(new StubAuthorService(author), books, new StubEditionService(stored.Editions), new StubMetadataProfileService());
            var request = new Book { HardcoverBookId = stored.HardcoverBookId, MediaType = stored.MediaType,
                Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId }, Editions = new List<Edition> { requested } };

            Assert.ThrowsAsync<ValidationException>(async () => await service.AddBook(request));
            Assert.That(books.WantedRequest, Is.Null);
            Assert.That(matching.Monitored && matching.ManualAdd && !stored.AnyEditionOk, Is.True);
        }

        [Test]
        public void native_narration_completion_should_preserve_exact_selection_within_an_edition_only_source()
        {
            var author = new Author { Id = 42, AddOptions = null };
            var edition = new Edition { Id = 301, BookId = 101, ForeignEditionId = "gr:999", ReadingFormatId = 2,
                Narrator = "Jim Dale", Monitored = true, ManualAdd = true };
            var source = new Book { Id = edition.BookId, Author = author, MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false, Editions = new List<Edition> { edition } };
            var books = new StubBookService { AuthorBooks = new List<Book> { source }, WantedResult = source };
            var service = BuildService(new StubAuthorService(author), books, new StubEditionService(source.Editions),
                new StubMetadataProfileService(),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var request = new Book { MediaType = source.MediaType, Editions = new List<Edition> { RefreshEntityCopy.CloneEdition(edition) } };
            var complete = typeof(AddBookService).GetMethod("CompleteRequestedNarration", BindingFlags.Instance | BindingFlags.NonPublic);

            var result = (Book)complete.Invoke(service, new object[] { request, source });

            Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(source), Is.Empty);
            Assert.That(result, Is.SameAs(source));
            Assert.That(books.WantedRequest, Is.EqualTo((source.Id, edition.Id)));
        }

        [TestCase(BookMediaType.Audiobook, false, false, true)]
        [TestCase(BookMediaType.Audiobook, true, false, true)]
        [TestCase(BookMediaType.Ebook, false, false, true)]
        [TestCase(BookMediaType.Ebook, true, false, true)]
        [TestCase(BookMediaType.Audiobook, false, true, false)]
        [TestCase(BookMediaType.Ebook, false, true, false)]
        [TestCase(BookMediaType.Audiobook, false, false, false)]
        [TestCase(BookMediaType.Ebook, false, false, false)]
        [TestCase(BookMediaType.Audiobook, true, false, false)]
        [TestCase(BookMediaType.Ebook, true, false, false)]
        [TestCase(BookMediaType.Audiobook, true, true, false, true)]
        public async Task existing_native_book_request_should_preserve_explicit_intent_without_resuming_author(
            BookMediaType mediaType, bool alreadySelected, bool monitorRequested, bool searchRequested, bool pinned = false)
        {
            var author = new Author
            {
                Id = 42, Name = "Requested Author", HardcoverAuthorId = "hc:777",
                AudiobookMetadataProfileId = 1, EbookMetadataProfileId = 2,
                AudiobookMonitored = false, EbookMonitored = false
            };
            var edition = new Edition
            {
                Id = 301, BookId = 101, ForeignEditionId = "hc:301", Title = "Requested Book",
                Language = "eng", ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3,
                Monitored = true, ManualAdd = pinned
            };
            var stored = new Book
            {
                Id = 101, AuthorId = author.Id, Author = author, Title = edition.Title, AnyEditionOk = !pinned,
                HardcoverBookId = "hc:1001", MediaType = mediaType, Editions = new List<Edition> { edition }
            };
            stored.SetMonitored(alreadySelected);
            var books = new StubBookService { AuthorBooks = new List<Book> { stored }, FindExistingBook = true };
            var repository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var queue = new CommandQueueManager(repository, null, null, LogManager.GetCurrentClassLogger());
            var bookAdded = new BookAddedService(new NzbDrone.Common.Cache.CacheManager(), queue, books, LogManager.GetCurrentClassLogger());
            var service = BuildService(new StubAuthorService(author, (_, _) => author), books,
                new StubEditionService(new[] { edition }), new StubMetadataProfileService(
                    new MetadataProfile { Id = 1, AllowedLanguages = "eng" },
                    new MetadataProfile { Id = 2, AllowedLanguages = "eng" }),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(),
                commandQueue: queue);

            var request = new Book
            {
                HardcoverBookId = stored.HardcoverBookId, MediaType = mediaType,
                Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId, AudiobookMonitored = true, EbookMonitored = true },
                Editions = new List<Edition> { pinned ? new Edition { Id = 302, BookId = 101, ForeignEditionId = "hc:302", Title = edition.Title, Language = "eng", ReadingFormatId = 2, Monitored = true } : edition },
                AddOptions = new AddBookOptions { SearchForNewBook = searchRequested }
            };
            request.SetMonitored(monitorRequested);
            var result = await service.AddBook(request);
            bookAdded.SearchForRecentlyAdded(author.Id);

            Assert.Multiple(() =>
            {
                Assert.That(books.AddedBook, Is.SameAs(result), "Exercise the existing-row native add path.");
                Assert.That(result.Id, Is.EqualTo(stored.Id));
                if (pinned)
                {
                    Assert.That(result.AnyEditionOk, Is.False);
                    Assert.That(result.Editions.Single(item => item.Monitored).Id, Is.EqualTo(301));
                    Assert.That(result.Editions.Single(item => item.Monitored).ManualAdd, Is.True);
                }
                Assert.That(result.IsMonitored(), Is.EqualTo(alreadySelected || monitorRequested || searchRequested));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                var searches = ((CommandRepositoryProxy)repository).Commands.OfType<BookSearchCommand>().ToList();
                Assert.That(searches, Has.Count.EqualTo(searchRequested ? 1 : 0));
                if (searchRequested)
                {
                    Assert.That(searches.Single().BookIds, Is.EqualTo(new[] { stored.Id }));
                    Assert.That(searches.Single().Trigger, Is.EqualTo(CommandTrigger.Manual));
                }
                Assert.That(result.AddOptions.SearchForNewBook, Is.False);
            });
        }

        [TestCase(BookMediaType.Audiobook, 2, false)]
        [TestCase(BookMediaType.Audiobook, 2, true)]
        [TestCase(BookMediaType.Ebook, 3, false)]
        [TestCase(BookMediaType.Ebook, 3, true)]
        public async Task existing_author_search_request_should_monitor_the_book_without_resuming_its_author_side(
            BookMediaType mediaType,
            int readingFormatId,
            bool alreadySelected)
        {
            var existingAuthor = new Author
            {
                Id = 42,
                Name = "Existing Author",
                HardcoverAuthorId = "hc:777",
                AudiobookMetadataProfileId = 1,
                EbookMetadataProfileId = 2,
                AudiobookMonitored = false,
                EbookMonitored = false
            };
            var requestedAuthor = new Author
            {
                Name = existingAuthor.Name,
                HardcoverAuthorId = existingAuthor.HardcoverAuthorId,
                AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.SpecificBook }
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                requestedAuthor.AudiobookMonitored = true;
            }
            else
            {
                requestedAuthor.EbookMonitored = true;
            }

            var requestedEdition = new Edition
            {
                Id = 301,
                Title = "Requested Edition",
                ReadingFormatId = readingFormatId,
                Language = "eng",
                Monitored = true
            };
            var requestedBook = new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = mediaType,
                Author = requestedAuthor,
                AddOptions = new AddBookOptions
                {
                    SearchForNewBook = true
                },
                Editions = new List<Edition> { requestedEdition }
            };

            var authoritativeBook = new Book
            {
                Id = 101,
                AuthorId = existingAuthor.Id,
                Author = existingAuthor,
                Title = requestedBook.Title,
                HardcoverBookId = requestedBook.HardcoverBookId,
                MediaType = mediaType
            };
            authoritativeBook.SetMonitored(alreadySelected);
            var bookService = new StubBookService { AuthorBooks = new List<Book> { authoritativeBook } };
            var authorService = new StubAuthorService(existingAuthor, (_, _) => existingAuthor);
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = existingAuthor;
            var commandRepository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var queue = new CommandQueueManager(commandRepository, null, null, LogManager.GetCurrentClassLogger());
            var storedEdition = new Edition
            {
                Id = requestedEdition.Id,
                BookId = 101,
                Title = requestedEdition.Title,
                ReadingFormatId = readingFormatId,
                Language = requestedEdition.Language,
                Monitored = true
            };
            var service = BuildService(
                authorService,
                bookService,
                new StubEditionService(new[] { storedEdition }),
                new StubMetadataProfileService(
                    new MetadataProfile { Id = 1, AllowedLanguages = "eng" },
                    new MetadataProfile { Id = 2, AllowedLanguages = "eng" }),
                authorLibraryService,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), commandQueue: queue);

            var result = await service.AddBook(requestedBook);

            Assert.That(result, Is.SameAs(authoritativeBook));
            Assert.That(bookService.RequestedMonitoringCalls.Single().Ids, Is.EqualTo(new[] { authoritativeBook.Id }));
            Assert.That(bookService.RequestedMonitoringCalls.Single().MediaType,
                Is.EqualTo(mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook"));
            Assert.That(bookService.AddedBook, Is.Null, "A direct book add must never create a row outside the authoritative author catalog.");
            Assert.That(result.AddOptions.SearchForNewBook, Is.False);
            Assert.That(result.IsMonitored(), Is.True);
            Assert.That(result.IsMonitoredWithAuthor(), Is.False);
            Assert.That(
                mediaType == BookMediaType.Audiobook
                    ? existingAuthor.AudiobookMonitored
                    : existingAuthor.EbookMonitored,
                Is.False);
            Assert.That(
                mediaType == BookMediaType.Audiobook
                    ? existingAuthor.EbookMonitored
                    : existingAuthor.AudiobookMonitored,
                Is.False);
            var config = ((AuthorLibraryProxy)authorLibraryService).Config;
            Assert.That(mediaType == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor,
                Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(mediaType == BookMediaType.Audiobook ? config.AudiobookBooksToSearch : config.EbookBooksToSearch,
                Is.EqualTo(new[] { "hc:1001" }));
            var search = ((CommandRepositoryProxy)commandRepository).Commands.OfType<BookSearchCommand>().Single();
            Assert.That(search.BookIds, Is.EqualTo(new[] { result.Id }));
            Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Manual));
        }

        [Test]
        public async Task newly_imported_author_should_keep_search_for_new_book_intent()
        {
            var importedAuthor = new Author
            {
                Id = 42,
                Name = "New Author",
                HardcoverAuthorId = "hc:777",
                AudiobookMonitored = false
            };
            var importedBook = new Book
            {
                Id = 101,
                AuthorId = importedAuthor.Id,
                Author = importedAuthor,
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Audiobook
            };
            var requestedBook = new Book
            {
                Title = importedBook.Title,
                HardcoverBookId = importedBook.HardcoverBookId,
                MediaType = BookMediaType.Audiobook,
                Author = new Author
                {
                    Name = importedAuthor.Name,
                    HardcoverAuthorId = importedAuthor.HardcoverAuthorId,
                    Monitored = true,
                    AudiobookMonitored = false,
                    AddOptions = new AddAuthorOptions()
                },
                AddOptions = new AddBookOptions
                {
                    AddType = BookAddType.Manual,
                    SearchForNewBook = true
                },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = "Requested Book",
                        ReadingFormatId = 2,
                        Language = "eng"
                    }
                }
            };

            var bookService = new StubBookService
            {
                AuthorBooks = new List<Book> { importedBook }
            };
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = importedAuthor;
            var commandRepository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var queue = new CommandQueueManager(commandRepository, null, null, LogManager.GetCurrentClassLogger());
            var importListExclusionService = DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>();
            var service = BuildService(
                new StubAuthorService(importedAuthor, (_, _) => null),
                bookService,
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                authorLibraryService,
                importListExclusionService, commandQueue: queue);

            var result = await service.AddBook(requestedBook);

            Assert.That(result, Is.SameAs(importedBook));
            Assert.That(bookService.RequestedMonitoringCalls.Single().Ids, Is.EqualTo(new[] { importedBook.Id }));
            Assert.That(bookService.RequestedMonitoringCalls.Single().MediaType, Is.EqualTo("audiobook"));
            Assert.That(((AuthorLibraryProxy)authorLibraryService).Config.AudiobookBooksToSearch, Does.Contain("hc:1001"));
            Assert.That(((AuthorLibraryProxy)authorLibraryService).Config.EbookBooksToSearch, Is.Null);
            Assert.That(bookService.AddOptionsBooks, Is.EqualTo(new[] { importedBook }));
            Assert.That(importedBook.AddOptions.SearchForNewBook, Is.False);
            Assert.That(importedBook.IsMonitored(), Is.True);
            Assert.That(importedAuthor.AudiobookMonitored, Is.False);
            Assert.That(((AuthorLibraryProxy)authorLibraryService).Config.AudiobookMonitored, Is.False);
            var search = ((CommandRepositoryProxy)commandRepository).Commands.OfType<BookSearchCommand>().Single();
            Assert.That(search.BookIds, Is.EqualTo(new[] { result.Id }));
            Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Manual));
        }

        [Test]
        public void unavailable_requested_book_should_queue_exact_work_monitoring_and_search_without_direct_insert()
        {
            var pendingAuthor = new Author { Id = -77, Name = "Pending Import" };
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = pendingAuthor;
            var bookService = new StubBookService();
            var service = BuildService(
                new StubAuthorService(null, (_, _) => null),
                bookService,
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                authorLibraryService,
                DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var request = new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Ebook,
                Author = new Author
                {
                    Name = "Pending Author",
                    HardcoverAuthorId = " ",
                    GoodreadsAuthorId = "777",
                    EbookMonitored = true,
                    EbookQualityProfileId = 1,
                    EbookMetadataProfileId = 2,
                    EbookRootFolderPath = "/ebooks",
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.SpecificBook }
                },
                AddOptions = new AddBookOptions { SearchForNewBook = true }
            };

            var exception = Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(request));

            Assert.That(exception.PendingId, Is.EqualTo(77));
            Assert.That(exception.Message, Is.EqualTo(PendingBookRequestException.UserMessage));
            Assert.That(bookService.AddedBook, Is.Null);
            var authorLibraryProxy = (AuthorLibraryProxy)authorLibraryService;
            Assert.That(authorLibraryProxy.ProviderId, Is.EqualTo("gr:777"));
            var config = authorLibraryProxy.Config;
            Assert.That(config.EbookBooksToMonitor, Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(config.EbookBooksToSearch, Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(config.AudiobookBooksToMonitor, Is.Null);
            Assert.That(config.QueueIfUnavailable, Is.True);
        }

        [Test]
        public void omitted_book_request_policies_queue_only_the_requested_work(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool explicitSettings,
            [Values(null, MonitorTypes.None, MonitorTypes.All)] MonitorTypes? initialPolicy)
        {
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = new Author { Id = -77 };
            var service = BuildService(new StubAuthorService(null, (_, _) => null), new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()), new StubMetadataProfileService(), library,
                DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var audio = mediaType == BookMediaType.Audiobook;
            var author = new Author { GoodreadsAuthorId = "777", AddOptions = initialPolicy.HasValue ? new AddAuthorOptions { Monitor = initialPolicy } : null };
            if (explicitSettings)
            {
                author.AudiobookMonitored = false;
                author.EbookMonitored = false;
                author.AudiobookMonitorNewItems = NewItemMonitorTypes.New;
                author.EbookMonitorNewItems = NewItemMonitorTypes.New;
            }

            Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(new Book
            {
                Title = "Dune", HardcoverBookId = "hc:1001", MediaType = mediaType, Author = author
            }));

            var config = ((AuthorLibraryProxy)library).Config;
            Assert.Multiple(() =>
            {
                Assert.That(audio ? config.AudiobookMonitored : config.EbookMonitored, Is.EqualTo(!explicitSettings));
                Assert.That(audio ? config.AudiobookMonitorNewItems : config.EbookMonitorNewItems,
                    Is.EqualTo(explicitSettings ? NewItemMonitorTypes.New : NewItemMonitorTypes.None));
                Assert.That(audio ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(initialPolicy ?? MonitorTypes.SpecificBook));
                Assert.That(config.MonitorMode, Is.EqualTo(initialPolicy ?? MonitorTypes.SpecificBook));
                Assert.That(audio ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor,
                    Is.EqualTo(new[] { "hc:1001" }));
                Assert.That(audio ? config.AudiobookBooksToAdd : config.EbookBooksToAdd,
                    Is.Null);
                Assert.That(audio ? config.EbookBooksToMonitor : config.AudiobookBooksToMonitor, Is.Null);
            });
        }

        [Test]
        public void unavailable_all_books_request_should_preserve_all_mode_and_the_requested_work_for_rescue()
        {
            var pendingAuthor = new Author { Id = -77, Name = "Pending Import" };
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = pendingAuthor;
            var service = BuildService(
                new StubAuthorService(null, (_, _) => null),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                authorLibraryService,
                DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var request = new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Ebook,
                Author = new Author
                {
                    Name = "Pending Author",
                    GoodreadsAuthorId = "777",
                    EbookMonitored = true,
                    EbookQualityProfileId = 1,
                    EbookMetadataProfileId = 2,
                    EbookRootFolderPath = "/ebooks",
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.All }
                }
            };

            Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(request));

            var config = ((AuthorLibraryProxy)authorLibraryService).Config;
            Assert.That(config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(config.EbookBooksToMonitor, Is.EqualTo(new[] { "hc:1001" }));
        }

        [Test]
        public void exact_book_request_should_remove_matching_exclusion_before_importing_the_author_blob()
        {
            var pendingAuthor = new Author { Id = -77, Name = "Pending Import" };
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = pendingAuthor;
            var exclusionService = DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>();
            var exclusionProxy = (ImportListExclusionServiceProxy)exclusionService;
            exclusionProxy.Exclusions.Add(new ImportListExclusion
            {
                Id = 9,
                ForeignId = "hc:1001",
                MediaType = BookMediaType.Ebook
            });
            ((AuthorLibraryProxy)authorLibraryService).BeforeAdd = () =>
                Assert.That(exclusionProxy.DeletedIds, Does.Contain(9), "the requested work must survive author-blob filtering");

            var service = BuildService(
                new StubAuthorService(null, (_, _) => null),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                authorLibraryService,
                exclusionService);
            var request = new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Ebook,
                Author = new Author
                {
                    Name = "Pending Author",
                    HardcoverAuthorId = "hc:777",
                    EbookMonitored = true,
                    EbookQualityProfileId = 1,
                    EbookMetadataProfileId = 2,
                    EbookRootFolderPath = "/ebooks",
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.SpecificBook }
                }
            };

            Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(request));
            Assert.That(exclusionProxy.DeletedIds, Is.EqualTo(new[] { 9 }));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void author_add_should_update_only_the_requested_exclusion_scope_for_all_provider_ids(BookMediaType mediaType)
        {
            var oppositeMediaType = ImportListExclusionBookMatcher.GetOppositeMediaType(mediaType);
            var retainedHardcoverExclusionId = mediaType == BookMediaType.Audiobook ? 2 : 1;
            var author = new Author
            {
                HardcoverAuthorId = "hc:777",
                GoodreadsAuthorId = "gr:888",
                OpenLibraryAuthorId = "ol:999"
            };
            var exclusionService = DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>();
            var exclusionProxy = (ImportListExclusionServiceProxy)exclusionService;
            exclusionProxy.Exclusions.AddRange(new[]
            {
                new ImportListExclusion { Id = 1, ForeignId = "hc:777", MediaType = BookMediaType.Audiobook },
                new ImportListExclusion { Id = 2, ForeignId = "hc:777", MediaType = BookMediaType.Ebook },
                new ImportListExclusion { Id = 3, ForeignId = "gr:888" },
                new ImportListExclusion { Id = 4, ForeignId = "ol:999", MediaType = mediaType },
                new ImportListExclusion { Id = 5, ForeignId = "ol:999", MediaType = oppositeMediaType }
            });
            var service = BuildService(
                new StubAuthorService(null),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                importListExclusionService: exclusionService);

            InvokeRemoveMatchingImportListExclusions(service, new Book { MediaType = mediaType, Author = author });

            var authorIds = AuthorIdentity.GetProviderIdentityTokenList(author);
            Assert.That(exclusionProxy.LookupIdLists.Any(ids => ids.SequenceEqual(authorIds)), Is.True);
            Assert.That(exclusionProxy.DeletedIds, Is.EquivalentTo(new[]
            {
                mediaType == BookMediaType.Audiobook ? 1 : 2,
                4
            }));
            Assert.That(exclusionProxy.UpdatedIds, Is.EqualTo(new[] { 3 }));
            Assert.That(exclusionProxy.Exclusions.Select(exclusion => exclusion.Id), Is.EquivalentTo(new[]
            {
                retainedHardcoverExclusionId,
                3,
                5
            }));
            Assert.That(exclusionProxy.Exclusions.Single(exclusion => exclusion.Id == retainedHardcoverExclusionId).MediaType,
                Is.EqualTo(oppositeMediaType));
            Assert.That(exclusionProxy.Exclusions.Single(exclusion => exclusion.Id == 3).MediaType, Is.EqualTo(oppositeMediaType));
            Assert.That(exclusionProxy.Exclusions.Single(exclusion => exclusion.Id == 5).MediaType, Is.EqualTo(oppositeMediaType));
        }

        private static void InvokeRemoveMatchingImportListExclusions(AddBookService service, Book book)
        {
            var method = typeof(AddBookService).GetMethod("RemoveMatchingImportListExclusions", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.RemoveMatchingImportListExclusions via reflection");
            }

            method.Invoke(service, new object[] { book, null });
        }
    }
}
