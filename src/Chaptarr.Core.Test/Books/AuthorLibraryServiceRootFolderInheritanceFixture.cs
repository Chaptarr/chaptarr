using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using FluentValidation;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServiceRootFolderInheritanceFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubAuthorInfo : IProvideAuthorInfo
        {
            private readonly Author _author;

            public StubAuthorInfo(Author author)
            {
                _author = author;
            }

            public Author GetAuthorInfo(string chaptarrId, bool useCache = true) => _author;
            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class NotReadyAuthorInfo : IProvideAuthorInfo
        {
            public Author GetAuthorInfo(string chaptarrId, bool useCache = true) => throw new AuthorNotFoundException(chaptarrId);
            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class RecordingPendingImportService : IPendingAuthorImportService
        {
            public MonitoringConfig Config { get; private set; }

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                Config = config;
                return Task.FromResult(77);
            }

            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();
            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void CleanupOldCompleted() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<(string provider, string providerId), Author> _authorsByProvider = new();
            private readonly Dictionary<int, List<Book>> _booksByAuthorId = new();
            private int _nextId = 100;

            public Author AddedAuthor { get; private set; }
            public (int? audiobookQualityProfileId, int? audiobookMetadataProfileId, bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems,
                    int? ebookQualityProfileId, int? ebookMetadataProfileId, bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems, string rootFolderPath)? LastProgressiveUpdate { get; private set; }

            public void AddExisting(string provider, string providerId, Author author, IEnumerable<Book> books = null)
            {
                _authorsByProvider[(provider, providerId)] = author;
                if (author != null)
                {
                    _booksByAuthorId[author.Id] = books?.ToList() ?? new List<Book>();
                }
            }

            public Author FindByProviderId(string provider, string providerId)
            {
                _authorsByProvider.TryGetValue((provider, providerId), out var author);
                return author;
            }

            public Author AddAuthor(Author newAuthor, bool doRefresh)
            {
                newAuthor.Id = _nextId++;
                AddedAuthor = newAuthor;
                return newAuthor;
            }

            public Author FindByName(string title) => null;

            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems, int? ebookQualityProfileId, int? ebookMetadataProfileId, bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems, string rootFolderPath)
            {
                LastProgressiveUpdate = (audiobookQualityProfileId, audiobookMetadataProfileId, audiobookMonitored, audiobookMonitorNewItems,
                    ebookQualityProfileId, ebookMetadataProfileId, ebookMonitored, ebookMonitorNewItems, rootFolderPath);

                author.AudiobookQualityProfileId = audiobookQualityProfileId ?? author.AudiobookQualityProfileId;
                author.AudiobookMetadataProfileId = audiobookMetadataProfileId ?? author.AudiobookMetadataProfileId;
                author.AudiobookMonitored = audiobookMonitored ?? author.AudiobookMonitored;
                author.AudiobookMonitorNewItems = audiobookMonitorNewItems ?? author.AudiobookMonitorNewItems;
                author.EbookQualityProfileId = ebookQualityProfileId ?? author.EbookQualityProfileId;
                author.EbookMetadataProfileId = ebookMetadataProfileId ?? author.EbookMetadataProfileId;
                author.EbookMonitored = ebookMonitored ?? author.EbookMonitored;
                author.EbookMonitorNewItems = ebookMonitorNewItems ?? author.EbookMonitorNewItems;
                return author;
            }

            public Author UpdateAuthor(Author author) => author;
            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => false;
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            private readonly Func<int, List<Book>> _getBooksByAuthor;

            public StubBookService(Func<int, List<Book>> getBooksByAuthor = null)
            {
                _getBooksByAuthor = getBooksByAuthor ?? (_ => new List<Book>());
            }

            public List<Book> GetBooksByAuthor(int authorId) => _getBooksByAuthor(authorId);
            public void UpdateMany(List<Book> books) { }
            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
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
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(params RootFolder[] rootFolders)
            {
                _rootFolders = rootFolders.ToList();
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => _rootFolders;
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();

            public RootFolder GetBestRootFolder(string path)
            {
                return GetBestRootFolder(path, _rootFolders);
            }

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return (allRootFolders ?? new List<RootFolder>())
                    .Where(r => !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(r.Path) &&
                                (path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) ||
                                 path.StartsWith(r.Path.TrimEnd('/', '\\') + "/", StringComparison.OrdinalIgnoreCase) ||
                                 path.StartsWith(r.Path.TrimEnd('/', '\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();
            }

            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path, allRootFolders)?.Path;
        }

        private sealed class StubAuthorPathBuilder : IBuildAuthorPaths
        {
            public string BuildPath(Author author, bool useExistingRelativeFolder)
            {
                return author.AudiobookPath ?? author.EbookPath ?? "/authors/unknown";
            }

            public string BuildPathForQuality(Author author, Quality quality, bool useExistingRelativeFolder)
            {
                var root = quality == Quality.EPUB ? author.EbookRootFolderPath : author.AudiobookRootFolderPath;
                return $"{root?.TrimEnd('/', '\\')}/{author.Name}";
            }

            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder)
            {
            }
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public List<object> PublishedEvents { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, NzbDrone.Common.Messaging.IEvent
            {
                PublishedEvents.Add(@event);
            }
        }

        private sealed class StubAuthorSyncMetadataService : IAuthorSyncMetadataService
        {
            public int? LastAuthorId { get; private set; }
            public string LastExternalAuthorId { get; private set; }
            public string LastETag { get; private set; }
            public bool UpdateSyncResultCalled { get; private set; }

            public AuthorSyncMetadata CreateOrUpdateSyncMetadata(int authorId, string externalAuthorId, string etag = null)
            {
                LastAuthorId = authorId;
                LastExternalAuthorId = externalAuthorId;
                LastETag = etag;

                return new AuthorSyncMetadata
                {
                    Id = 1,
                    AuthorId = authorId,
                    ExternalAuthorId = externalAuthorId,
                    ETag = etag
                };
            }

            public void UpdateSyncResult(int authorId, bool success, string etag = null, string error = null, int httpStatus = 0, TimeSpan? duration = null)
            {
                UpdateSyncResultCalled = true;
                LastAuthorId = authorId;
                LastETag = etag;
            }

            public AuthorSyncMetadata GetSyncMetadata(int authorId) => throw new NotImplementedException();
            public AuthorSyncMetadata GetSyncMetadataByExternalId(string externalAuthorId) => throw new NotImplementedException();
            public List<AuthorSyncMetadata> GetSyncMetadataForAuthors(List<int> authorIds) => throw new NotImplementedException();
            public void UpdateSyncMetadata(AuthorSyncMetadata syncMetadata) => throw new NotImplementedException();
            public List<AuthorSyncMetadata> GetDueForSync(int limit = 100) => throw new NotImplementedException();
            public void BulkUpdateSyncMetadata(List<AuthorSyncMetadata> syncMetadata) => throw new NotImplementedException();
        }

        private static RootFolder BuildAudiobookRoot(
            string path,
            int qualityProfileId = 2,
            int metadataProfileId = 1,
            MonitorTypes initialBookMonitoring = MonitorTypes.None,
            bool monitored = true,
            NewItemMonitorTypes monitorNewItems = NewItemMonitorTypes.New,
            IEnumerable<int> tags = null)
        {
            var root = new RootFolder
            {
                Path = path,
                FolderType = FolderType.Audiobook
            };

            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = qualityProfileId,
                MetadataProfileId = metadataProfileId,
                Monitored = monitored,
                MonitorExistingMode = initialBookMonitoring,
                MonitorNewItems = monitorNewItems,
                Tags = tags?.ToList()
            });

            return root;
        }

        private static RootFolder BuildEbookRoot(
            string path,
            int qualityProfileId = 4,
            int metadataProfileId = 3,
            MonitorTypes initialBookMonitoring = MonitorTypes.All,
            bool monitored = true,
            NewItemMonitorTypes monitorNewItems = NewItemMonitorTypes.All,
            IEnumerable<int> tags = null)
        {
            var root = new RootFolder
            {
                Path = path,
                FolderType = FolderType.Ebook
            };

            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = qualityProfileId,
                MetadataProfileId = metadataProfileId,
                Monitored = monitored,
                MonitorExistingMode = initialBookMonitoring,
                MonitorNewItems = monitorNewItems,
                Tags = tags?.ToList()
            });

            return root;
        }

        private static Author BuildRemoteAuthor(string name = "Test Author")
        {
            return new Author
            {
                Name = name,
                Books = new List<Book>(),
                Series = new List<Series>()
            };
        }

        private static AuthorLibraryService BuildService(StubAuthorService authorService, IProvideAuthorInfo authorInfo, IRootFolderService rootFolderService, IBookService bookService = null, IEventAggregator eventAggregator = null, IAuthorSyncMetadataService syncMetadataService = null, IPendingAuthorImportService pendingImportService = null)
        {
            return new AuthorLibraryService(
                authorService: authorService,
                authorInfo: authorInfo,
                bookService: bookService ?? new StubBookService(),
                refreshSeriesService: DispatchProxy.Create<IRefreshSeriesService, ThrowingProxy<IRefreshSeriesService>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                narratorLinkService: DispatchProxy.Create<INarratorLinkService, ThrowingProxy<INarratorLinkService>>(),
                metadataProfileService: new TestMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: rootFolderService,
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                eventAggregator: eventAggregator ?? new StubEventAggregator(),
                pendingImportService: pendingImportService,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: syncMetadataService,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: null);
        }

        [Test]
        public async Task add_author_should_seed_sync_metadata_from_import_etag()
        {
            var remoteAuthor = BuildRemoteAuthor();
            remoteAuthor.HardcoverAuthorId = "hc:123";
            remoteAuthor.RemoteMetadataETag = "W/\"v123\"";

            var authorService = new StubAuthorService();
            var syncMetadataService = new StubAuthorSyncMetadataService();
            var service = BuildService(
                authorService,
                new StubAuthorInfo(remoteAuthor),
                rootFolderService: null,
                syncMetadataService: syncMetadataService);

            var author = await service.AddAuthorAsync("hc:123");

            Assert.That(syncMetadataService.LastAuthorId, Is.EqualTo(author.Id));
            Assert.That(syncMetadataService.LastExternalAuthorId, Is.EqualTo("hc:123"));
            Assert.That(syncMetadataService.LastETag, Is.EqualTo("W/\"v123\""));
            Assert.That(syncMetadataService.UpdateSyncResultCalled, Is.True);
        }

        [Test]
        public async Task add_author_should_inherit_missing_audiobook_settings_from_explicit_root_folder()
        {
            var root = BuildAudiobookRoot(
                "/audiobooks",
                qualityProfileId: 12,
                metadataProfileId: 34,
                initialBookMonitoring: MonitorTypes.Missing,
                monitored: false,
                monitorNewItems: NewItemMonitorTypes.New,
                tags: new[] { 7, 8 });
            var authorService = new StubAuthorService();
            var service = BuildService(authorService, new StubAuthorInfo(BuildRemoteAuthor()), new StubRootFolderService(root));

            var author = await service.AddAuthorAsync("hc:123", new MonitoringConfig
            {
                AuthorName = "Test Author",
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookRootFolderPath = root.Path,
                AudiobookQualityProfileId = 0,
                AudiobookMetadataProfileId = 0
            });

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo(root.Path));
            Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(12));
            Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(34));
            Assert.That(author.AudiobookMonitored, Is.False);
            Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(author.AudiobookTags, Is.EquivalentTo(new[] { 7, 8 }));
            Assert.That(author.EbookTags, Is.Null);
            Assert.That(author.Tags, Is.EquivalentTo(new[] { 7, 8 }));
            Assert.That(author.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.Missing));
            Assert.That(author.AddOptions.EbookMonitor, Is.Null);
            Assert.That(author.EbookQualityProfileId, Is.Null);
            Assert.That(authorService.AddedAuthor, Is.SameAs(author));
        }

        [Test]
        public async Task not_ready_author_should_queue_the_same_root_tags_as_an_immediate_add()
        {
            var root = BuildAudiobookRoot("/audiobooks", tags: new[] { 7, 8 });
            var pendingImportService = new RecordingPendingImportService();
            var service = BuildService(
                new StubAuthorService(),
                new NotReadyAuthorInfo(),
                new StubRootFolderService(root),
                pendingImportService: pendingImportService);

            var result = await service.AddAuthorAsync("hc:not-ready", new MonitoringConfig
            {
                AuthorName = "Not Ready Author",
                CreateAudiobook = true,
                AudiobookRootFolderPath = root.Path,
                QueueIfUnavailable = true
            });

            Assert.That(result.Id, Is.EqualTo(-77));
            Assert.That(pendingImportService.Config.AudiobookTags, Is.EquivalentTo(new[] { 7, 8 }));
            Assert.That(pendingImportService.Config.EbookTags, Is.Null);
        }

        [Test]
        public async Task add_author_should_preserve_explicit_profile_overrides()
        {
            var root = BuildAudiobookRoot(
                "/audiobooks",
                qualityProfileId: 12,
                metadataProfileId: 34,
                initialBookMonitoring: MonitorTypes.All,
                monitored: true,
                monitorNewItems: NewItemMonitorTypes.All);
            var authorService = new StubAuthorService();
            var service = BuildService(authorService, new StubAuthorInfo(BuildRemoteAuthor()), new StubRootFolderService(root));

            var author = await service.AddAuthorAsync("hc:456", new MonitoringConfig
            {
                AuthorName = "Override Author",
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookRootFolderPath = root.Path,
                AudiobookQualityProfileId = 99,
                AudiobookMetadataProfileId = 0
            });

            Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(99));
            Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(34));
            Assert.That(author.AudiobookMonitored, Is.True);
            Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
        }

        [Test]
        public void add_author_should_reject_incompatible_explicit_root_folder()
        {
            var ebookRoot = BuildEbookRoot("/ebooks");
            var authorService = new StubAuthorService();
            var service = BuildService(authorService, new StubAuthorInfo(BuildRemoteAuthor()), new StubRootFolderService(ebookRoot));

            var ex = Assert.ThrowsAsync<ValidationException>(async () => await service.AddAuthorAsync("hc:789", new MonitoringConfig
            {
                AuthorName = "Wrong Root",
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookRootFolderPath = ebookRoot.Path,
                AudiobookQualityProfileId = 0,
                AudiobookMetadataProfileId = 0
            }));

            Assert.That(ex.Errors.Single().PropertyName, Is.EqualTo(nameof(MonitoringConfig.AudiobookRootFolderPath)));
            Assert.That(authorService.AddedAuthor, Is.Null);
        }

        [Test]
        public async Task existing_author_progressive_update_should_inherit_missing_settings_from_explicit_root_folder()
        {
            var root = BuildAudiobookRoot(
                "/audiobooks",
                qualityProfileId: 7,
                metadataProfileId: 8,
                initialBookMonitoring: MonitorTypes.All,
                monitored: true,
                monitorNewItems: NewItemMonitorTypes.All,
                tags: new[] { 7 });
            var existingAuthor = new Author
            {
                Id = 42,
                Name = "Existing Author",
                AudiobookTags = new HashSet<int>(),
                EbookTags = new HashSet<int> { 20 },
                Tags = new HashSet<int> { 20 }
            };

            var authorService = new StubAuthorService();
            authorService.AddExisting("hc", "existing", existingAuthor, books: Array.Empty<Book>());

            var remoteAuthor = BuildRemoteAuthor(existingAuthor.Name);
            remoteAuthor.Born = new DateTime(1928, 2, 15);
            remoteAuthor.Died = new DateTime(1996, 7, 9);
            remoteAuthor.Status = AuthorStatusType.Ended;

            var service = BuildService(
                authorService,
                new StubAuthorInfo(remoteAuthor),
                new StubRootFolderService(root),
                new StubBookService(_ => new List<Book>()),
                new StubEventAggregator());

            var result = await service.AddAuthorAsync("hc:existing", new MonitoringConfig
            {
                AuthorName = existingAuthor.Name,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookRootFolderPath = root.Path,
                AudiobookQualityProfileId = 0,
                AudiobookMetadataProfileId = 0
            });

            Assert.That(result, Is.SameAs(existingAuthor));
            Assert.That(authorService.LastProgressiveUpdate.HasValue, Is.True);
            Assert.That(authorService.LastProgressiveUpdate.Value.audiobookQualityProfileId, Is.EqualTo(7));
            Assert.That(authorService.LastProgressiveUpdate.Value.audiobookMetadataProfileId, Is.EqualTo(8));
            Assert.That(authorService.LastProgressiveUpdate.Value.audiobookMonitored, Is.True);
            Assert.That(authorService.LastProgressiveUpdate.Value.audiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(authorService.LastProgressiveUpdate.Value.rootFolderPath, Is.EqualTo(root.Path));
            Assert.That(result.Born, Is.EqualTo(remoteAuthor.Born));
            Assert.That(result.Died, Is.EqualTo(remoteAuthor.Died));
            Assert.That(result.Status, Is.EqualTo(AuthorStatusType.Ended));
            Assert.That(result.AudiobookTags, Is.Empty, "an explicit empty set must not inherit root tags");
            Assert.That(result.EbookTags, Is.EquivalentTo(new[] { 20 }));
            Assert.That(authorService.AddedAuthor, Is.Null);
        }

        [Test]
        public async Task existing_author_should_inherit_root_tags_when_the_media_side_is_still_unset()
        {
            var root = BuildAudiobookRoot("/audiobooks", tags: new[] { 7, 8 });
            var existingAuthor = new Author
            {
                Id = 43,
                Name = "First Touch Author",
                AudiobookTags = null,
                EbookTags = new HashSet<int> { 20 },
                Tags = new HashSet<int> { 20 }
            };
            var authorService = new StubAuthorService();
            authorService.AddExisting("hc", "first-touch", existingAuthor, books: Array.Empty<Book>());
            var service = BuildService(
                authorService,
                new StubAuthorInfo(BuildRemoteAuthor(existingAuthor.Name)),
                new StubRootFolderService(root));

            var result = await service.AddAuthorAsync("hc:first-touch", new MonitoringConfig
            {
                CreateAudiobook = true,
                AudiobookRootFolderPath = root.Path
            });

            Assert.That(result.AudiobookTags, Is.EquivalentTo(new[] { 7, 8 }));
            Assert.That(result.EbookTags, Is.EquivalentTo(new[] { 20 }));
            Assert.That(result.Tags, Is.EquivalentTo(new[] { 7, 8, 20 }));
        }
    }
}
