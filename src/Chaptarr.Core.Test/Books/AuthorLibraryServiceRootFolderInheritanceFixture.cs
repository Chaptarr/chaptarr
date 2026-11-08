using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Series;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Core.Test;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Core.Validation;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

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

            public PendingAuthorImport Get(int id) => GetAll().Find(item => item.Id == id);
            public NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> GetPaged(NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> pagingSpec) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();
            public bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null) => throw new NotImplementedException();
            public bool SaveProgress(PendingAuthorImport item, PendingAuthorImport previous) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public bool IsActiveRequest(int id, long version) => true;
            public void Cancel(int id) => throw new NotImplementedException();
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
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

            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();

            public Author UpdateAuthor(Author author) => author;
            public Author GetAuthor(int authorId) => _authorsByProvider.Values.FirstOrDefault(author => author?.Id == authorId)
                ?? (AddedAuthor?.Id == authorId ? AddedAuthor : null);
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => false;
            public void SetAddOptions(Author author) { }
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            private readonly Func<int, List<Book>> _getBooksByAuthor;

            public StubBookService(Func<int, List<Book>> getBooksByAuthor = null)
            {
                _getBooksByAuthor = getBooksByAuthor ?? (_ => new List<Book>());
            }

            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => _getBooksByAuthor(authorId);
            public void UpdateMany(List<Book> books) { }
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
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
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
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<NzbDrone.Core.MediaFiles.BookFile> deletedBookFiles = null) => throw new NotImplementedException();
        }

        internal sealed class StubRootFolderService : IRootFolderService
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

        internal sealed class StubAuthorPathBuilder : IBuildAuthorPaths
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

            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder, Author previous = null)
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

            public void UpdateSyncResult(int authorId, bool success, string etag = null, string error = null, int httpStatus = 0, TimeSpan? duration = null, bool retryAllowed = true, bool localFailure = false)
            {
                UpdateSyncResultCalled = true;
                LastAuthorId = authorId;
                LastETag = etag;
            }

            public AuthorSyncMetadata GetSyncMetadata(int authorId) => throw new NotImplementedException();
            public AuthorSyncMetadata GetSyncMetadataByExternalId(string externalAuthorId) => throw new NotImplementedException();
            public List<AuthorSyncMetadata> GetSyncMetadataForAuthors(List<int> authorIds) => throw new NotImplementedException();
            public void UpdateSyncMetadata(AuthorSyncMetadata syncMetadata) => throw new NotImplementedException();
            public void DeferRetry(AuthorSyncMetadata metadata) => throw new NotImplementedException();
            public List<AuthorSyncMetadata> GetDueForSync(int limit = 100) => throw new NotImplementedException();
            public void BulkUpdateSyncMetadata(List<AuthorSyncMetadata> syncMetadata) => throw new NotImplementedException();
        }

        internal static RootFolder BuildAudiobookRoot(
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

        internal static RootFolder BuildEbookRoot(
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

        internal static AuthorLibraryService BuildService(IAuthorService authorService, IProvideAuthorInfo authorInfo, IRootFolderService rootFolderService, IBookService bookService = null, IEventAggregator eventAggregator = null, IAuthorSyncMetadataService syncMetadataService = null, IPendingAuthorImportService pendingImportService = null, IManageCommandQueue commandQueue = null, IMainDatabase database = null)
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
                commandQueueManager: commandQueue ?? DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                eventAggregator: eventAggregator ?? new StubEventAggregator(),
                pendingImportService: pendingImportService,
                mainDatabase: database,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: syncMetadataService,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: null);
        }

        private sealed class ModalAuthorInfo : IProvideAuthorInfo
        {
            public bool Available { get; set; }
            public int Fetches { get; private set; }
            public Author GetAuthorInfo(string providerId, bool useCache = true)
            {
                Fetches++;
                if (!Available) throw new AuthorNotFoundException(providerId);
                var author = BuildRemoteAuthor("Selected Search Writer");
                author.HardcoverAuthorId = providerId;
                author.TitleSlug = Guid.NewGuid().ToString("N");
                author.SortName = "selected search writer";
                author.SortNameLastFirst = "Writer, Selected Search";
                return author;
            }
            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        internal static AuthorController BuildModalController(IAuthorService authors, IAuthorLibraryService library, IRootFolderService roots, IManageCommandQueue queue, IEventAggregator events, IBookService books)
        {
            var controller = new AuthorController(Proxy<NzbDrone.SignalR.IBroadcastSignalRMessage>((method, args) =>
                method.Name == "get_IsConnected" ? false : Task.CompletedTask), authors, books, null, library, null, null, queue, roots, events, null, null,
                LogManager.GetCurrentClassLogger(), new RecycleBinValidator(null), new RootFolderValidator(roots),
                new MappedNetworkDriveValidator(null, null), new AuthorPathValidator(authors), new AuthorExistsValidator(authors),
                new AuthorAncestorValidator(authors), new SystemFolderValidator(),
                new QualityProfileExistsValidator(new TestQualityProfileService()),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()), new AuthorFolderAsRootFolderValidator(null));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        private static JsonArray CapturedModalWire(string media, bool overrides)
        {
            var json = (media, overrides) switch
            {
                ("audiobook", false) => """[{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"audiobook","rootFolder":"/fixture/audio","qualityProfileId":11,"metadataProfileId":21,"monitor":"all","audiobookMonitorExistingMode":"all","audiobookMonitored":false,"audiobookMonitorNewItems":"new","tags":[41],"manualFlag":true,"searchForMissingBooks":false}]""",
                ("audiobook", true) => """[{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"audiobook","rootFolder":"/fixture/audio-alt","qualityProfileId":12,"metadataProfileId":22,"monitor":"existing","audiobookMonitorExistingMode":"existing","audiobookMonitored":true,"audiobookMonitorNewItems":"none","tags":[43],"manualFlag":true,"searchForMissingBooks":true}]""",
                ("ebook", false) => """[{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"ebook","rootFolder":"/fixture/ebook","qualityProfileId":13,"metadataProfileId":23,"monitor":"missing","ebookMonitorExistingMode":"missing","ebookMonitored":true,"ebookMonitorNewItems":"none","tags":[42],"manualFlag":true,"searchForMissingBooks":false}]""",
                ("ebook", true) => """[{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"ebook","rootFolder":"/fixture/ebook-alt","qualityProfileId":14,"metadataProfileId":24,"monitor":"none","ebookMonitorExistingMode":"none","ebookMonitored":false,"ebookMonitorNewItems":"all","tags":[],"manualFlag":true,"searchForMissingBooks":true}]""",
                ("both", false) => """[{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"audiobook","rootFolder":"/fixture/audio","qualityProfileId":11,"metadataProfileId":21,"monitor":"all","audiobookMonitorExistingMode":"all","audiobookMonitored":false,"audiobookMonitorNewItems":"new","tags":[41],"manualFlag":true,"searchForMissingBooks":false},{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"ebook","rootFolder":"/fixture/ebook","qualityProfileId":13,"metadataProfileId":23,"monitor":"missing","ebookMonitorExistingMode":"missing","ebookMonitored":true,"ebookMonitorNewItems":"none","tags":[42],"manualFlag":true,"searchForMissingBooks":false}]""",
                ("both", true) => """[{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"audiobook","rootFolder":"/fixture/audio-alt","qualityProfileId":12,"metadataProfileId":22,"monitor":"existing","audiobookMonitorExistingMode":"existing","audiobookMonitored":true,"audiobookMonitorNewItems":"none","tags":[43],"manualFlag":true,"searchForMissingBooks":false},{"foreignAuthorId":"gr:fixture-selected-author","authorName":"Selected Search Writer","mediaType":"ebook","rootFolder":"/fixture/ebook-alt","qualityProfileId":14,"metadataProfileId":24,"monitor":"none","ebookMonitorExistingMode":"none","ebookMonitored":false,"ebookMonitorNewItems":"all","tags":[],"manualFlag":true,"searchForMissingBooks":true}]""",
                _ => throw new ArgumentOutOfRangeException(nameof(media))
            };
            return JsonNode.Parse(json).AsArray();
        }

        private static IEnumerable<TestCaseData> CapturedProviderNameCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var provider in new[] { "goodreads", "hardcover", "audible" })
            foreach (var mode in new[] { "audiobook", "ebook", "both" })
            foreach (var route in new[] { "author-import", "author", "book", "book-import" })
            {
                yield return new TestCaseData(postgres, provider, mode, route)
                    .SetName($"captured_provider_name_{provider}_{route}_{mode}_{(postgres ? "postgres" : "sqlite")}");
            }
            foreach (var postgres in new[] { false, true })
            foreach (var mode in new[] { "audiobook", "ebook", "both" })
                yield return new TestCaseData(postgres, "grouped", mode, "series")
                    .SetName($"supplied_series_names_stay_with_author_ids_{mode}_{(postgres ? "postgres" : "sqlite")}");
        }

        [TestCaseSource(nameof(CapturedProviderNameCases))]
        public void supplied_provider_name_json_survives_real_pending_persistence(bool postgres, string provider, string mode, string route)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new StubEventAggregator();
                var logger = LogManager.GetCurrentClassLogger();
                var queue = Proxy<IManageCommandQueue>((method, args) => throw new AssertionException("Queued requests must not start a search"));
                var roots = new StubRootFolderService(
                    BuildAudiobookRoot("/fixture/audio", 2, 1, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 91 }),
                    BuildAudiobookRoot("/fixture/audio-alt", 2, 1, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 91 }),
                    BuildEbookRoot("/fixture/ebook", 4, 3, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 92 }),
                    BuildEbookRoot("/fixture/ebook-alt", 4, 3, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 92 }));
                var authorRepository = new AuthorRepository(database, events, logger);
                var bookRepository = new BookRepository(database, events);
                var authors = new AuthorService(authorRepository, events, new StubAuthorPathBuilder(), roots, queue,
                    new CacheManager(), bookRepository, null, logger, null, null, null, database);
                var repository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(repository, authors, events, logger);
                var editions = new EditionService(new EditionRepository(database, events), events, logger);
                var books = new BookService(bookRepository, editions, events, authors,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var library = BuildService(authors, new NotReadyAuthorInfo(), roots, books, events,
                    pendingImportService: pending, commandQueue: queue, database: database);
                var authorController = BuildModalController(authors, library, roots, queue, events, books);
                var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "ProviderSearch");
                var captured = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "captured-name-modal-posts.json")));
                var publishedWork = File.ReadAllText(Path.Combine(fixtures, "published-earthsea-work.json"));
                var metadataRequests = new List<string>();
                var mapped = new List<object>();
                var metadata = Chaptarr.Core.Test.MetadataSource.BookInfo.BookInfoProxyCanonicalLookupFixture.CreateProxy(
                    Proxy<NzbDrone.Common.Http.IHttpClient>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo("Get"));
                        var request = (NzbDrone.Common.Http.HttpRequest)args[0];
                        metadataRequests.Add(request.Url.ToString());
                        return new NzbDrone.Common.Http.HttpResponse(request, new NzbDrone.Common.Http.HttpHeader { ContentType = "application/json" },
                            publishedWork, System.Net.HttpStatusCode.OK);
                    }));
                var bookInfo = Proxy<IProvideBookInfo>((method, args) =>
                {
                    if (route == "book-import" && provider != "audible") throw new BookNotFoundException((string)args[0]);
                    var media = (BookMediaType)args[1];
                    var result = method.Name == nameof(IProvideBookInfo.GetEditionInfo)
                        ? metadata.GetEditionInfo((string)args[0], media)
                        : metadata.GetWorkInfo((string)args[0], media, args.Length > 2 ? (string)args[2] : null);
                    Assert.Multiple(() =>
                    {
                        Assert.That(result.Item2.MediaType, Is.EqualTo(media));
                        Assert.That(result.Item2.Editions.Any(edition => edition.ReadingFormatId == (media == BookMediaType.Audiobook ? 2 : 3)), Is.True);
                        Assert.That(result.Item1, Is.EqualTo("hc:213830"));
                    });
                    mapped.Add(new { requested = (string)args[0], media, primary = result.Item1, result.Item2.Title,
                        editionCount = result.Item2.Editions.Count, formats = result.Item2.Editions.GroupBy(edition => edition.ReadingFormatId).Select(group => new { format = group.Key, count = group.Count() }) });
                    return result;
                });
                var add = new AddBookService(authors, library, books,
                    Proxy<IBookMonitoredService>((method, args) => throw new AssertionException(method.Name)), bookInfo,
                    Proxy<NzbDrone.Core.ImportLists.Exclusions.IImportListExclusionService>((method, args) =>
                        method.Name == "FindByForeignId" ? new List<NzbDrone.Core.ImportLists.Exclusions.ImportListExclusion>() : throw new AssertionException(method.Name)),
                    null, null, null, null, null, editions, new EditionSelector(logger),
                    new EditionMetadataProfileFilter(new TestTermMatcherService()), new TestMetadataProfileService(), logger, queue);
                var bookController = new BookController(authors, books, add, editions, new EditionSelector(logger),
                    null, null, null, null, null, null, queue, events, roots,
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()), logger);
                bookController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                STJson.ApplySerializerSettings(options);
                var authorId = provider == "goodreads" ? "gr:874602" : provider == "audible" ? "az:B0GZZ57KFW" : "hc:213830";
                var workId = provider == "goodreads" ? "gr:113603" : provider == "audible" ? "az:B002VA3CDO" : "hc:427401";
                var expected = new Dictionary<string, JsonObject>();
                var wire = new List<string>();
                var responses = new List<string>();
                var mediaTypes = mode == "both" ? new[] { "audiobook", "ebook" } : new[] { mode };
                foreach (var media in mediaTypes)
                {
                    var audio = media == "audiobook";
                    var side = audio ? "audiobook" : "ebook";
                    var importRows = captured["importRequests"].AsArray().Select(row => row["body"].AsObject())
                        .Where(row => row["foreignAuthorId"].ToString() == (route == "series" ? "gr:874602" : authorId)).ToList();
                    Assert.That(importRows, Has.Count.EqualTo(4));
                    var payload = importRows[mode == "both" ? audio ? 2 : 3 : audio ? 0 : 1].DeepClone().AsObject();
                    Assert.That(payload["mediaType"].ToString(), Is.EqualTo(media));
                    object response;
                    if (route == "author-import")
                    {
                        wire.Add(payload.ToJsonString());
                        response = authorController.ImportAuthor(JsonSerializer.Deserialize<AuthorImportResource>(wire.Last(), options)).GetAwaiter().GetResult().Result;
                    }
                    else if (route == "author")
                    {
                        var request = new JsonObject { ["foreignAuthorId"] = authorId, ["authorName"] = payload["authorName"].DeepClone(),
                            [side + "RootFolderPath"] = payload["rootFolder"].DeepClone(), [side + "QualityProfileId"] = payload["qualityProfileId"].DeepClone(),
                            [side + "MetadataProfileId"] = payload["metadataProfileId"].DeepClone(), [side + "Monitored"] = payload[side + "Monitored"].DeepClone(),
                            [side + "MonitorNewItems"] = payload[side + "MonitorNewItems"].DeepClone(), [side + "MonitorExistingMode"] = payload[side + "MonitorExistingMode"].DeepClone(),
                            [side + "Tags"] = payload["tags"].DeepClone(), ["tags"] = payload["tags"].DeepClone(),
                            ["addOptions"] = new JsonObject { ["monitor"] = payload["monitor"].DeepClone(), ["searchForMissingBooks"] = payload["searchForMissingBooks"].DeepClone() } };
                        wire.Add(request.ToJsonString());
                        response = authorController.AddAuthor(JsonSerializer.Deserialize<Chaptarr.Api.V1.Author.AuthorResource>(wire.Last(), options)).GetAwaiter().GetResult().Result;
                    }
                    else if (route == "book-import")
                    {
                        var request = new JsonObject { ["foreignBookId"] = workId, ["foreignAuthorId"] = authorId,
                            ["foreignEditionId"] = provider == "goodreads" ? "gr:13642" : "0", ["authorName"] = payload["authorName"].DeepClone(),
                            ["mediaType"] = media, ["rootFolder"] = payload["rootFolder"].DeepClone(),
                            ["qualityProfileId"] = payload["qualityProfileId"].DeepClone(), ["metadataProfileId"] = payload["metadataProfileId"].DeepClone(),
                            ["authorMonitoring"] = new JsonObject { ["monitor"] = payload["monitor"].DeepClone(), [side + "Monitored"] = payload[side + "Monitored"].DeepClone(),
                                [side + "MonitorNewItems"] = payload[side + "MonitorNewItems"].DeepClone(), ["searchForMissing"] = true } };
                        wire.Add(request.ToJsonString());
                        var failure = Assert.ThrowsAsync<ValidationException>(() => bookController.ImportBook(JsonSerializer.Deserialize<BookImportResource>(wire.Last(), options)));
                        Assert.That(failure.Errors.Any(error => error.ErrorMessage.Contains(PendingBookRequestException.UserMessage)), Is.True);
                        responses.Add(failure.Message);
                        payload.Remove("tags");
                        expected[media] = payload;
                        continue;
                    }
                    else
                    {
                        var bookRows = captured["bookRequests"].AsArray().Select(row => row["body"].AsObject())
                            .Where(row => row["author"]["foreignAuthorId"].ToString() == authorId).ToList();
                        var request = (route == "series"
                            ? captured["seriesRequests"][mode == "both" ? audio ? 6 : 7 : audio ? 1 : 3]["body"]
                            : bookRows[mode == "both" ? audio ? 2 : 3 : audio ? 0 : 1]).DeepClone().AsObject();
                        Assert.That(request[route == "series" ? "selectedMediaType" : "mediaType"].ToString(), Is.EqualTo(media));
                        var settings = route == "series" ? request : request["author"].AsObject();
                        payload = new JsonObject { ["rootFolder"] = settings[side + "RootFolderPath"].DeepClone(),
                            ["qualityProfileId"] = settings[side + "QualityProfileId"].DeepClone(), ["metadataProfileId"] = settings[side + "MetadataProfileId"].DeepClone(),
                            [side + "Monitored"] = settings[side + "Monitored"].DeepClone(), [side + "MonitorNewItems"] = settings[side + "MonitorNewItems"].DeepClone(),
                            ["monitor"] = (route == "series" ? request[side + "MonitorExistingMode"] ?? request["monitor"] : settings["addOptions"]["monitor"]).DeepClone(),
                            ["tags"] = (route == "series" ? request["tags"] : settings[side + "Tags"]).DeepClone() };
                        wire.Add(request.ToJsonString());
                        if (route == "series")
                            response = new AddSeriesController(library, authors, books, null, queue, logger).AddSeries(JsonSerializer.Deserialize<AddSeriesRequest>(wire.Last(), options)).GetAwaiter().GetResult().Result;
                        else
                            response = bookController.AddBook(null, JsonSerializer.Deserialize<Chaptarr.Api.V1.Books.BookResource>(wire.Last(), options)).GetAwaiter().GetResult().Result;
                    }
                    expected[media] = payload;
                    Assert.That(response, route == "series" ? Is.TypeOf<OkObjectResult>() : Is.TypeOf<AcceptedResult>());
                    responses.Add(response.GetType().Name);
                }
                var rows = repository.All();
                var expectedId = route == "series" ? "gr:874602" : route == "book" || provider == "audible" && route == "book-import" ? "hc:213830" : authorId;
                Assert.That(rows.Count, Is.EqualTo(route == "series" ? 3 : 1));
                var stored = rows.Single(row => row.ProviderId == expectedId);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.AuthorName, Is.EqualTo("Ursula K. Le Guin"));
                    Assert.That(stored.HasAudiobook(), Is.EqualTo(mode != "ebook"));
                    Assert.That(stored.HasEbook(), Is.EqualTo(mode != "audiobook"));
                    foreach (var media in mediaTypes)
                    {
                        var audio = media == "audiobook";
                        var side = audio ? "audiobook" : "ebook";
                        var raw = expected[media];
                        Assert.That(audio ? stored.AudiobookRootFolderPath : stored.EbookRootFolderPath, Is.EqualTo(raw["rootFolder"].ToString()));
                        Assert.That(audio ? stored.AudiobookQualityProfileId : stored.EbookQualityProfileId, Is.EqualTo(raw["qualityProfileId"].Deserialize<int>(options)));
                        Assert.That(audio ? stored.AudiobookMetadataProfileId : stored.EbookMetadataProfileId, Is.EqualTo(raw["metadataProfileId"].Deserialize<int>(options)));
                        Assert.That(audio ? stored.AudiobookMonitored : stored.EbookMonitored, Is.EqualTo(raw[side + "Monitored"].GetValue<bool>()));
                        Assert.That(audio ? stored.AudiobookMonitorNewItems : stored.EbookMonitorNewItems, Is.EqualTo(raw[side + "MonitorNewItems"].Deserialize<NewItemMonitorTypes>(options)));
                        Assert.That(audio ? stored.AudiobookMonitorExistingMode : stored.EbookMonitorExistingMode, Is.EqualTo(raw["monitor"].Deserialize<MonitorTypes>(options)));
                        if (raw["tags"] != null) Assert.That(JsonSerializer.Deserialize<int[]>(audio ? stored.AudiobookTags : stored.EbookTags, options), Is.EqualTo(raw["tags"].Deserialize<int[]>(options)));
                        if (route is "book" or "book-import")
                        {
                            var requestedWork = provider == "audible" ? "hc:427401" : workId;
                            Assert.That(JsonSerializer.Deserialize<string[]>(audio ? stored.AudiobookBooksToSearch : stored.EbookBooksToSearch, options), Is.EqualTo(new[] { requestedWork }));
                            var monitorRequested = route == "book-import" || raw["monitor"].Deserialize<MonitorTypes>(options) != MonitorTypes.None;
                            var monitorIds = audio ? stored.AudiobookBooksToMonitor : stored.EbookBooksToMonitor;
                            var addIds = audio ? stored.AudiobookBooksToAdd : stored.EbookBooksToAdd;
                            Assert.That(JsonSerializer.Deserialize<string[]>(monitorRequested ? monitorIds : addIds, options), Is.EqualTo(new[] { requestedWork }));
                            Assert.That(monitorRequested ? addIds : monitorIds, Is.Null.Or.EqualTo("[]"));
                        }
                    }
                    Assert.That(stored.SearchForMissingBooks, Is.EqualTo(route is "author" or "author-import"));
                    if (route == "series")
                    {
                        var selected = JsonNode.Parse(wire[0])["selectedBooks"].AsArray();
                        foreach (var row in selected)
                            Assert.That(rows.Single(item => item.ProviderId == row["foreignAuthorId"].ToString()).AuthorName, Is.EqualTo(row["authorName"]?.ToString()));
                    }
                    Assert.That(authorRepository.All(), Is.Empty);
                    Assert.That(bookRepository.All(), Is.Empty);
                    Assert.That(metadataRequests.Count, Is.EqualTo(route == "book" || provider == "audible" && route == "book-import" ? mediaTypes.Length : 0));
                });
                TestContext.WriteLine("PROVIDER_NAME_PENDING " + JsonSerializer.Serialize(new { postgres, provider, mode, route, wire, responses,
                    rows = rows.Select(row => new { row.Id, row.ProviderId, row.AuthorName, row.AudiobookQualityProfileId, row.AudiobookMetadataProfileId,
                        row.EbookQualityProfileId, row.EbookMetadataProfileId, row.AudiobookRootFolderPath, row.EbookRootFolderPath,
                        row.AudiobookMonitored, row.EbookMonitored, row.AudiobookMonitorNewItems, row.EbookMonitorNewItems,
                        row.AudiobookTags, row.EbookTags, row.SearchForMissingBooks, row.AudiobookBooksToSearch, row.EbookBooksToSearch,
                        row.AudiobookBooksToAdd, row.EbookBooksToAdd, row.AudiobookBooksToMonitor, row.EbookBooksToMonitor }), metadataRequests, mapped }, options));
            });
        }

        [Test]
        public void modal_settings_json_round_trips_pending_and_actual_queued_author(
            [Values(false, true)] bool postgres,
            [Values("audiobook", "ebook", "both", "audio-defaults", "ebook-defaults", "both-defaults", "active-repeat")] string scenario,
            [Values(false, true)] bool overrides)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new StubEventAggregator();
                var logger = LogManager.GetCurrentClassLogger();
                var commands = new List<Command>();
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    var command = (Command)args[0];
                    commands.Add(command);
                    return new CommandModel { Id = commands.Count, Body = command };
                });
                var roots = new StubRootFolderService(
                    BuildAudiobookRoot("/audit/audio-override", 2, 1, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 7, 8 }),
                    BuildAudiobookRoot("/audit/audio-default", 2, 1, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 7, 8 }),
                    BuildEbookRoot("/audit/ebook-override", 4, 3, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 17, 18 }),
                    BuildAudiobookRoot("/fixture/audio", 2, 1, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 7, 8 }),
                    BuildAudiobookRoot("/fixture/audio-alt", 2, 1, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 7, 8 }),
                    BuildEbookRoot("/fixture/ebook", 4, 3, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 17, 18 }),
                    BuildEbookRoot("/fixture/ebook-alt", 4, 3, MonitorTypes.All, true, NewItemMonitorTypes.All, new[] { 17, 18 }));
                var authorRepository = new AuthorRepository(database, events, logger);
                var bookRepository = new BookRepository(database, events);
                var authors = new AuthorService(authorRepository, events, new StubAuthorPathBuilder(), roots, queue,
                    new CacheManager(), bookRepository, null, logger, null, null, null, database);
                var repository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(repository, authors, events, logger);
                var books = new BookService(bookRepository, new EditionService(new EditionRepository(database, events), events, logger),
                    events, authors, Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var info = new ModalAuthorInfo();
                var library = BuildService(authors, info, roots, books, events, pendingImportService: pending, commandQueue: queue, database: database);
                var controller = BuildModalController(authors, library, roots, queue, events, books);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                STJson.ApplySerializerSettings(options);
                var audio = JsonNode.Parse("""{"foreignAuthorId":"hc:880001","mediaType":"audiobook","rootFolder":"/audit/audio-override","qualityProfileId":12,"metadataProfileId":34,"monitor":"none","audiobookMonitorExistingMode":"none","audiobookMonitored":false,"audiobookMonitorNewItems":"none","tags":[],"manualFlag":true,"searchForMissingBooks":false,"authorName":"Selected Search Writer"}""").AsObject();
                var ebook = JsonNode.Parse("""{"foreignAuthorId":"hc:880001","mediaType":"ebook","rootFolder":"/audit/ebook-override","qualityProfileId":56,"metadataProfileId":78,"monitor":"existing","ebookMonitorExistingMode":"existing","ebookMonitored":false,"ebookMonitorNewItems":"new","tags":[],"manualFlag":true,"searchForMissingBooks":true,"authorName":"Selected Search Writer"}""").AsObject();
                var replayActualModal = scenario == "audiobook" || scenario == "ebook" || scenario == "both";
                if (replayActualModal)
                {
                    var captured = CapturedModalWire(scenario, overrides);
                    foreach (var payload in captured.OfType<JsonObject>())
                    {
                        payload["foreignAuthorId"] = "hc:880001";
                        if (payload["mediaType"].ToString() == "audiobook") audio = payload;
                        else ebook = payload;
                    }
                }
                var defaults = scenario.EndsWith("defaults", StringComparison.Ordinal);
                var repeat = scenario == "active-repeat";
                var hasAudio = scenario != "ebook" && scenario != "ebook-defaults";
                var hasEbook = scenario == "ebook" || scenario == "both" || scenario == "ebook-defaults" || scenario == "both-defaults";
                var audioTags = defaults ? new[] { 7, 8 } : repeat || overrides ? new[] { 9, 10 } : Array.Empty<int>();
                var ebookTags = defaults ? new[] { 17, 18 } : overrides ? new[] { 11, 12 } : Array.Empty<int>();
                var commonTags = (hasAudio ? audioTags : Array.Empty<int>()).Concat(hasEbook ? ebookTags : Array.Empty<int>()).Distinct().ToArray();
                foreach (var payload in new[] { audio, ebook })
                {
                    if (overrides && !replayActualModal)
                    {
                        payload["qualityProfileId"] = payload["qualityProfileId"].ToString();
                        payload["metadataProfileId"] = payload["metadataProfileId"].ToString();
                        payload["tags"] = JsonSerializer.SerializeToNode(payload == audio ? audioTags : ebookTags);
                    }
                    if (defaults)
                    {
                        payload["qualityProfileId"] = overrides ? JsonValue.Create("0") : JsonValue.Create(0);
                        payload["metadataProfileId"] = overrides ? JsonValue.Create("0") : JsonValue.Create(0);
                        foreach (var field in new[] { "monitor", "audiobookMonitored", "ebookMonitored", "audiobookMonitorNewItems", "ebookMonitorNewItems", "audiobookMonitorExistingMode", "ebookMonitorExistingMode", "tags" }) payload.Remove(field);
                        payload["searchForMissingBooks"] = false;
                    }
                }
                if (repeat)
                {
                    audio["audiobookMonitored"] = true;
                    audio["audiobookMonitorNewItems"] = "all";
                    audio["audiobookMonitorExistingMode"] = "all";
                    audio["monitor"] = "all";
                    audio["searchForMissingBooks"] = true;
                    audio["tags"] = JsonSerializer.SerializeToNode(new[] { 9 });
                }
                var wire = new List<string>();
                var bound = new List<AuthorImportResource>();
                var rawSearchWanted = false;
                int Import(JsonObject payload)
                {
                    var json = payload.ToJsonString();
                    var dto = JsonSerializer.Deserialize<AuthorImportResource>(json, options);
                    wire.Add(json);
                    bound.Add(dto);
                    rawSearchWanted |= payload["searchForMissingBooks"].GetValue<bool>();
                    Assert.Multiple(() =>
                    {
                        Assert.That(dto.MediaType, Is.EqualTo(payload["mediaType"].GetValue<string>()));
                        Assert.That(dto.RootFolder, Is.EqualTo(payload["rootFolder"].GetValue<string>()));
                        Assert.That(dto.QualityProfileId, Is.EqualTo(payload["qualityProfileId"].Deserialize<int>(options)));
                        Assert.That(dto.MetadataProfileId, Is.EqualTo(payload["metadataProfileId"].Deserialize<int>(options)));
                        Assert.That(dto.Monitor, Is.EqualTo(payload["monitor"]?.GetValue<string>()));
                        Assert.That(dto.AudiobookMonitored, Is.EqualTo(payload["audiobookMonitored"]?.Deserialize<bool?>(options)));
                        Assert.That(dto.EbookMonitored, Is.EqualTo(payload["ebookMonitored"]?.Deserialize<bool?>(options)));
                        Assert.That(dto.AudiobookMonitorNewItems, Is.EqualTo(payload["audiobookMonitorNewItems"]?.Deserialize<NewItemMonitorTypes?>(options)));
                        Assert.That(dto.EbookMonitorNewItems, Is.EqualTo(payload["ebookMonitorNewItems"]?.Deserialize<NewItemMonitorTypes?>(options)));
                        Assert.That(dto.AudiobookMonitorExistingMode, Is.EqualTo(payload["audiobookMonitorExistingMode"]?.Deserialize<MonitorTypes?>(options)));
                        Assert.That(dto.EbookMonitorExistingMode, Is.EqualTo(payload["ebookMonitorExistingMode"]?.Deserialize<MonitorTypes?>(options)));
                        var rawTags = payload["tags"]?.Deserialize<HashSet<int>>(options);
                        Assert.That(dto.Tags, rawTags == null ? Is.Null : Is.EquivalentTo(rawTags));
                        Assert.That(dto.SearchForMissingBooks, Is.EqualTo(payload["searchForMissingBooks"].GetValue<bool>()));
                        Assert.That(dto.ForeignAuthorId, Is.EqualTo(payload["foreignAuthorId"].GetValue<string>()));
                        Assert.That(dto.AuthorName, Is.EqualTo(payload["authorName"].GetValue<string>()));
                        Assert.That(dto.ManualFlag, Is.EqualTo(payload["manualFlag"].GetValue<bool>()));
                        Assert.That(dto.MonitorExisting, Is.EqualTo(payload["monitorExisting"]?.GetValue<string>()));
                        Assert.That(dto.MonitorFuture, Is.EqualTo(payload["monitorFuture"]?.Deserialize<bool?>(options)));
                    });
                    var result = controller.ImportAuthor(dto).GetAwaiter().GetResult();
                    Assert.That(result.Result, Is.TypeOf<AcceptedResult>());
                    return (int)((AcceptedResult)result.Result).Value.GetType().GetProperty("pendingId").GetValue(((AcceptedResult)result.Result).Value);
                }
                var id = Import(hasAudio ? audio : ebook);
                if (scenario == "both" || scenario == "both-defaults") Assert.That(Import(ebook), Is.EqualTo(id));
                if (repeat)
                {
                    var second = JsonNode.Parse("""{"foreignAuthorId":"hc:880001","mediaType":"audiobook","rootFolder":"/audit/audio-override","qualityProfileId":12,"metadataProfileId":34,"monitor":"none","audiobookMonitorExistingMode":"none","audiobookMonitored":false,"audiobookMonitorNewItems":"none","tags":[],"manualFlag":true,"searchForMissingBooks":false,"authorName":"Selected Search Writer"}""").AsObject();
                    second["rootFolder"] = "/audit/audio-default";
                    second["qualityProfileId"] = "56";
                    second["metadataProfileId"] = "78";
                    second["tags"] = JsonSerializer.SerializeToNode(new[] { 10 });
                    Assert.That(Import(second), Is.EqualTo(id));
                }
                var stored = repository.Get(id);
                var resource = stored.ToResource();
                var audioDto = bound.LastOrDefault(dto => dto.MediaType == "audiobook");
                var ebookDto = bound.LastOrDefault(dto => dto.MediaType == "ebook");
                var audioRoot = replayActualModal && hasAudio ? audioDto.RootFolder : "/audit/audio-override";
                var ebookRoot = replayActualModal && hasEbook ? ebookDto.RootFolder : "/audit/ebook-override";
                var audioQuality = replayActualModal && hasAudio ? audioDto.QualityProfileId : defaults ? 2 : 12;
                var audioMetadata = replayActualModal && hasAudio ? audioDto.MetadataProfileId : defaults ? 1 : 34;
                var ebookQuality = replayActualModal && hasEbook ? ebookDto.QualityProfileId : defaults ? 4 : 56;
                var ebookMetadata = replayActualModal && hasEbook ? ebookDto.MetadataProfileId : defaults ? 3 : 78;
                var audioGate = replayActualModal && hasAudio ? audioDto.AudiobookMonitored.Value : defaults || repeat;
                var ebookGate = replayActualModal && hasEbook ? ebookDto.EbookMonitored.Value : defaults;
                var audioMode = replayActualModal && hasAudio ? audioDto.AudiobookMonitorExistingMode.Value : defaults || repeat ? MonitorTypes.All : MonitorTypes.None;
                var ebookMode = replayActualModal && hasEbook ? ebookDto.EbookMonitorExistingMode.Value : defaults ? MonitorTypes.All : MonitorTypes.Existing;
                var audioNew = replayActualModal && hasAudio ? audioDto.AudiobookMonitorNewItems.Value : defaults || repeat ? NewItemMonitorTypes.All : NewItemMonitorTypes.None;
                var ebookNew = replayActualModal && hasEbook ? ebookDto.EbookMonitorNewItems.Value : defaults ? NewItemMonitorTypes.All : NewItemMonitorTypes.New;
                var search = rawSearchWanted;
                if (replayActualModal)
                {
                    audioTags = audioDto?.Tags.ToArray() ?? Array.Empty<int>();
                    ebookTags = ebookDto?.Tags.ToArray() ?? Array.Empty<int>();
                    commonTags = audioTags.Concat(ebookTags).Distinct().ToArray();
                }
                Assert.Multiple(() =>
                {
                    Assert.That(repository.All().Count, Is.EqualTo(1));
                    Assert.That(stored.ProviderId, Is.EqualTo("hc:880001"));
                    Assert.That(resource.AuthorName, Is.EqualTo("Selected Search Writer"));
                    Assert.That(stored.RequestedBy, Is.EqualTo("UserInterface"));
                    Assert.That(resource.LastSelectedMediaType, Is.EqualTo(hasEbook ? "ebook" : "audiobook"));
                    Assert.That(resource.SearchForMissingBooks, Is.EqualTo(search));
                    Assert.That(stored.HasAudiobook(), Is.EqualTo(hasAudio));
                    Assert.That(stored.HasEbook(), Is.EqualTo(hasEbook));
                    if (hasAudio)
                    {
                        Assert.That(resource.AudiobookRootFolderPath, Is.EqualTo(audioRoot));
                        Assert.That(resource.AudiobookQualityProfileId, Is.EqualTo(audioQuality));
                        Assert.That(resource.AudiobookMetadataProfileId, Is.EqualTo(audioMetadata));
                        Assert.That(resource.AudiobookMonitored, Is.EqualTo(audioGate));
                        Assert.That(resource.AudiobookMonitorExistingMode, Is.EqualTo(audioMode));
                        Assert.That(resource.AudiobookMonitorNewItems, Is.EqualTo(audioNew));
                        Assert.That(resource.AudiobookTags, Is.EquivalentTo(audioTags));
                    }
                    if (hasEbook)
                    {
                        Assert.That(resource.EbookRootFolderPath, Is.EqualTo(ebookRoot));
                        Assert.That(resource.EbookQualityProfileId, Is.EqualTo(ebookQuality));
                        Assert.That(resource.EbookMetadataProfileId, Is.EqualTo(ebookMetadata));
                        Assert.That(resource.EbookMonitored, Is.EqualTo(ebookGate));
                        Assert.That(resource.EbookMonitorExistingMode, Is.EqualTo(ebookMode));
                        Assert.That(resource.EbookMonitorNewItems, Is.EqualTo(ebookNew));
                        Assert.That(resource.EbookTags, Is.EquivalentTo(ebookTags));
                    }
                });
                info.Available = true;
                MonitoringConfig workerConfig = null;
                var workerLibrary = Proxy<IAuthorLibraryService>((method, args) =>
                {
                    workerConfig = (MonitoringConfig)args[1];
                    return method.Invoke(library, args);
                });
                new ProcessPendingImportsCommandHandler(pending, workerLibrary, books,
                    Proxy<IProvideBookInfo>((method, args) => throw new AssertionException("Author-wide modal must not request work rescue")),
                    queue, events, logger).Execute(new ProcessPendingImportsCommand());
                Assert.That(repository.Find(id), Is.Null, "The actual worker must complete the stored author request");
                var author = authorRepository.All().Single();
                Assert.Multiple(() =>
                {
                    Assert.That(author.HardcoverAuthorId, Is.EqualTo("hc:880001"));
                    Assert.That(author.LastSelectedMediaType, Is.EqualTo(hasEbook ? "ebook" : "audiobook"));
                    Assert.That(author.AudiobookRootFolderPath, Is.EqualTo(hasAudio ? audioRoot : null));
                    Assert.That(author.EbookRootFolderPath, Is.EqualTo(hasEbook ? ebookRoot : null));
                    Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(hasAudio ? audioQuality : null));
                    Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(hasAudio ? audioMetadata : null));
                    Assert.That(author.EbookQualityProfileId, Is.EqualTo(hasEbook ? ebookQuality : null));
                    Assert.That(author.EbookMetadataProfileId, Is.EqualTo(hasEbook ? ebookMetadata : null));
                    Assert.That(author.AudiobookMonitored, Is.EqualTo(hasAudio ? audioGate : null));
                    Assert.That(author.EbookMonitored, Is.EqualTo(hasEbook ? ebookGate : null));
                    Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(hasAudio ? audioNew : null));
                    Assert.That(author.EbookMonitorNewItems, Is.EqualTo(hasEbook ? ebookNew : null));
                    Assert.That(author.AudiobookTags, hasAudio ? Is.EquivalentTo(audioTags) : Is.Empty);
                    Assert.That(author.EbookTags, hasEbook ? Is.EquivalentTo(ebookTags) : Is.Empty);
                    Assert.That(author.Tags, Is.EquivalentTo(commonTags));
                    Assert.That(author.AddOptions?.AudiobookMonitor, Is.EqualTo(hasAudio ? audioMode : null));
                    Assert.That(author.AddOptions?.EbookMonitor, Is.EqualTo(hasEbook ? ebookMode : null));
                    Assert.That(author.AddOptions?.SearchForMissingBooks ?? false, Is.EqualTo(search));
                    Assert.That(workerConfig.AudiobookTags, hasAudio ? Is.EquivalentTo(audioTags) : Is.Null);
                    Assert.That(workerConfig.EbookTags, hasEbook ? Is.EquivalentTo(ebookTags) : Is.Null);
                    Assert.That(info.Fetches, Is.EqualTo(bound.Count + 1));
                    Assert.That(commands.OfType<NzbDrone.Core.IndexerSearch.MissingBookSearchCommand>(), Is.Empty);
                });
                var scan = new AuthorScannedHandler(new BookMonitoredService(authors, books, logger), authors, queue,
                    Proxy<IBookAddedService>((_, _) => null), events, logger, books);
                scan.Handle(new NzbDrone.Core.MediaFiles.Events.AuthorScannedEvent(author));
                scan.Handle(new NzbDrone.Core.MediaFiles.Events.AuthorScannedEvent(author));
                Assert.That(commands.OfType<NzbDrone.Core.IndexerSearch.MissingBookSearchCommand>().Count(), Is.EqualTo(search ? 1 : 0));
                var outputOptions = new JsonSerializerOptions(options) { WriteIndented = false };
                TestContext.WriteLine("MODAL_SETTINGS_RESULT " + JsonSerializer.Serialize(new
                {
                    postgres, scenario, overrides, replayActualModal, wire, bound, pending = resource,
                    workerConfig, author = new { author.Id, author.Name, author.HardcoverAuthorId, author.Path,
                        author.AudiobookRootFolderPath, author.EbookRootFolderPath, author.AudiobookQualityProfileId, author.EbookQualityProfileId,
                        author.AudiobookMetadataProfileId, author.EbookMetadataProfileId, author.AudiobookMonitored, author.EbookMonitored,
                        author.AudiobookMonitorNewItems, author.EbookMonitorNewItems, author.AudiobookTags, author.EbookTags, author.Tags,
                        author.AddOptions, author.LastSelectedMediaType }, manualFlag = new { submitted = bound.All(dto => dto.ManualFlag), queuedConfig = workerConfig.IsManualAddition,
                        author.AudiobookSettingsManuallyOverridden, author.EbookSettingsManuallyOverridden },
                    metadataFetches = info.Fetches, commandTypes = commands.Select(command => command.GetType().Name).ToArray()
                }, outputOptions));
            });
        }

        [Test]
        public void modal_existing_author_changes_only_selected_side_and_retains_settings_when_hydration_is_unavailable(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string media,
            [Values(false, true)] bool selectedCatalogPresent)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new StubEventAggregator();
                var logger = LogManager.GetCurrentClassLogger();
                var commands = new List<Command>();
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    var command = (Command)args[0];
                    commands.Add(command);
                    return new CommandModel { Id = commands.Count, Body = command };
                });
                var roots = new StubRootFolderService(BuildAudiobookRoot("/audit/audio-override"), BuildEbookRoot("/audit/ebook-override"));
                var authorRepository = new AuthorRepository(database, events, logger);
                var bookRepository = new BookRepository(database, events);
                var authors = new AuthorService(authorRepository, events, new StubAuthorPathBuilder(), roots, queue,
                    new CacheManager(), bookRepository, null, logger, null, null, null, database);
                var books = new BookService(bookRepository, new EditionService(new EditionRepository(database, events), events, logger),
                    events, authors, Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var original = BuildRemoteAuthor("Selected Search Writer");
                original.HardcoverAuthorId = "hc:880001";
                original.TitleSlug = Guid.NewGuid().ToString("N");
                original.Path = "/audit/audio-override/Selected Search Writer";
                original.AudiobookRootFolderPath = "/audit/audio-override";
                original.EbookRootFolderPath = "/audit/ebook-override";
                original.AudiobookQualityProfileId = 2;
                original.AudiobookMetadataProfileId = 1;
                original.EbookQualityProfileId = 4;
                original.EbookMetadataProfileId = 3;
                original.AudiobookMonitored = original.EbookMonitored = true;
                original.AudiobookMonitorNewItems = original.EbookMonitorNewItems = NewItemMonitorTypes.All;
                original.AudiobookTags = new HashSet<int> { 21 };
                original.EbookTags = new HashSet<int> { 31 };
                original.Tags = new HashSet<int> { 21, 31 };
                var existing = authors.AddAuthor(original, false);
                if (selectedCatalogPresent)
                {
                    bookRepository.Insert(new Book { AuthorId = existing.Id, Title = "Existing Catalog", CleanTitle = "existingcatalog",
                        TitleSlug = Guid.NewGuid().ToString("N"), MediaType = media == "audiobook" ? BookMediaType.Audiobook : BookMediaType.Ebook,
                        Added = DateTime.UtcNow });
                }
                var repository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(repository, authors, events, logger);
                var info = new ModalAuthorInfo();
                var library = BuildService(authors, info, roots, books, events, pendingImportService: pending, commandQueue: queue, database: database);
                var controller = BuildModalController(authors, library, roots, queue, events, books);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                STJson.ApplySerializerSettings(options);
                var payload = JsonNode.Parse(media == "audiobook" ? """{"foreignAuthorId":"hc:880001","mediaType":"audiobook","rootFolder":"/audit/audio-override","qualityProfileId":12,"metadataProfileId":34,"monitor":"none","audiobookMonitorExistingMode":"none","audiobookMonitored":false,"audiobookMonitorNewItems":"none","tags":[],"manualFlag":true,"searchForMissingBooks":false,"authorName":"Selected Search Writer"}""" : """{"foreignAuthorId":"hc:880001","mediaType":"ebook","rootFolder":"/audit/ebook-override","qualityProfileId":56,"metadataProfileId":78,"monitor":"existing","ebookMonitorExistingMode":"existing","ebookMonitored":false,"ebookMonitorNewItems":"new","tags":[],"manualFlag":true,"searchForMissingBooks":true,"authorName":"Selected Search Writer"}""").AsObject();
                var result = controller.ImportAuthor(JsonSerializer.Deserialize<AuthorImportResource>(payload.ToJsonString(), options)).GetAwaiter().GetResult();
                Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
                var response = (Chaptarr.Api.V1.Author.AuthorResource)((OkObjectResult)result.Result).Value;
                var stored = authorRepository.Get(existing.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(repository.All(), Is.Empty);
                    Assert.That(authorRepository.All().Count, Is.EqualTo(1));
                    Assert.That(stored.AudiobookQualityProfileId, Is.EqualTo(media == "audiobook" ? 12 : 2));
                    Assert.That(stored.AudiobookMetadataProfileId, Is.EqualTo(media == "audiobook" ? 34 : 1));
                    Assert.That(stored.EbookQualityProfileId, Is.EqualTo(media == "ebook" ? 56 : 4));
                    Assert.That(stored.EbookMetadataProfileId, Is.EqualTo(media == "ebook" ? 78 : 3));
                    Assert.That(stored.AudiobookMonitored, Is.EqualTo(media != "audiobook"));
                    Assert.That(stored.EbookMonitored, Is.EqualTo(media != "ebook"));
                    Assert.That(stored.AudiobookMonitorNewItems, Is.EqualTo(media == "audiobook" ? NewItemMonitorTypes.None : NewItemMonitorTypes.All));
                    Assert.That(stored.EbookMonitorNewItems, Is.EqualTo(media == "ebook" ? NewItemMonitorTypes.New : NewItemMonitorTypes.All));
                    Assert.That(stored.AudiobookTags, Is.EquivalentTo(media == "audiobook" ? Array.Empty<int>() : new[] { 21 }));
                    Assert.That(stored.EbookTags, Is.EquivalentTo(media == "ebook" ? Array.Empty<int>() : new[] { 31 }));
                    Assert.That(stored.Tags, Is.EquivalentTo(media == "audiobook" ? new[] { 31 } : new[] { 21 }));
                    Assert.That(stored.LastSelectedMediaType, Is.EqualTo(media));
                    Assert.That(response.AudiobookQualityProfileId, Is.EqualTo(stored.AudiobookQualityProfileId));
                    Assert.That(response.EbookQualityProfileId, Is.EqualTo(stored.EbookQualityProfileId));
                    Assert.That(response.AudiobookTags, Is.EquivalentTo(stored.AudiobookTags));
                    Assert.That(response.EbookTags, Is.EquivalentTo(stored.EbookTags));
                    Assert.That(response.HydrationWarning, selectedCatalogPresent ? Is.Null : Is.Not.Null);
                    Assert.That(info.Fetches, Is.EqualTo(selectedCatalogPresent ? 0 : 1));
                });
                var outputOptions = new JsonSerializerOptions(options) { WriteIndented = false };
                TestContext.WriteLine("MODAL_EXISTING_RESULT " + JsonSerializer.Serialize(new
                {
                    postgres, media, selectedCatalogPresent, wire = payload.ToJsonString(), response,
                    metadataFetches = info.Fetches, commandTypes = commands.Select(command => command.GetType().Name).ToArray()
                }, outputOptions));
            });
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
        public async Task add_author_should_persist_requested_initial_media_type()
        {
            var root = BuildEbookRoot("/ebooks");
            var authorService = new StubAuthorService();
            var service = BuildService(authorService, new StubAuthorInfo(BuildRemoteAuthor()), new StubRootFolderService(root));

            var author = await service.AddAuthorAsync("hc:123", new MonitoringConfig
            {
                CreateAudiobook = false,
                CreateEbook = true,
                EbookRootFolderPath = root.Path,
                LastSelectedMediaType = "ebook"
            });

            Assert.That(author.LastSelectedMediaType, Is.EqualTo("ebook"));
            Assert.That(authorService.AddedAuthor.LastSelectedMediaType, Is.EqualTo("ebook"));
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
        public async Task new_author_should_inherit_opt_in_book_policies_without_configuring_the_other_side(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values] bool selectAll)
        {
            var initial = selectAll ? MonitorTypes.All : MonitorTypes.None;
            var future = selectAll ? NewItemMonitorTypes.All : NewItemMonitorTypes.None;
            var audiobook = mediaType == BookMediaType.Audiobook;
            var root = audiobook
                ? BuildAudiobookRoot(@"C:\Audiobooks".AsOsAgnostic(), initialBookMonitoring: initial, monitorNewItems: future)
                : BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic(), initialBookMonitoring: initial, monitorNewItems: future);
            var authorService = new StubAuthorService();
            var service = BuildService(authorService, new StubAuthorInfo(BuildRemoteAuthor()), new StubRootFolderService(root));

            var author = await service.AddAuthorAsync("hc:123", new MonitoringConfig
            {
                AuthorName = "Test Author",
                CreateAudiobook = audiobook,
                CreateEbook = !audiobook,
                AudiobookRootFolderPath = audiobook ? root.Path : null,
                EbookRootFolderPath = audiobook ? null : root.Path
            });

            Assert.Multiple(() =>
            {
                Assert.That(audiobook ? author.AudiobookMonitored : author.EbookMonitored, Is.True);
                Assert.That(audiobook ? author.AudiobookMonitorNewItems : author.EbookMonitorNewItems, Is.EqualTo(future));
                Assert.That(audiobook ? author.AddOptions.AudiobookMonitor : author.AddOptions.EbookMonitor, Is.EqualTo(initial));
                Assert.That(audiobook ? author.EbookMonitored : author.AudiobookMonitored, Is.Null);
                Assert.That(audiobook ? author.EbookMonitorNewItems : author.AudiobookMonitorNewItems, Is.Null);
                Assert.That(audiobook ? author.AddOptions.EbookMonitor : author.AddOptions.AudiobookMonitor, Is.Null);
            });
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
            Assert.That(result.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
            Assert.That(result.AddOptions.EbookMonitor, Is.Null);
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
