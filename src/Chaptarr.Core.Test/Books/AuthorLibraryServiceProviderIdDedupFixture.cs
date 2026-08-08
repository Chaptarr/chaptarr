using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
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
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
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
            private readonly Func<string, string, Author> _findByProviderId;
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

            public Author FindByProviderId(string provider, string providerId) => _findByProviderId?.Invoke(provider, providerId);

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

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByName(string title)
            {
                FindByNameCalls++;
                return _findByName;
            }
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
	            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
	            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
	            public Author UpdateAuthor(Author author) => author;
	            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
	            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
	            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder)
            {
                AuthorPathExistsCalls++;
                return _existingPaths.Contains(folder);
            }
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
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

            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder)
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
                MonitorExisting = 2,
                MonitorFuture = true
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
            private readonly Dictionary<int, List<Book>> _booksByAuthorId = new();

            public void SetBooks(int authorId, params Book[] books)
            {
                _booksByAuthorId[authorId] = new List<Book>(books ?? Array.Empty<Book>());
            }

            public List<Book> GetBooksByAuthor(int authorId) => _booksByAuthorId.TryGetValue(authorId, out var v) ? v : new List<Book>();

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
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
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
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        [Test]
        public async Task should_match_existing_author_by_secondary_provider_id_from_remote_payload()
        {
            var existing = new Author { Id = 42, Name = "Existing Author" };

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
                logger: LogManager.GetCurrentClassLogger());

            var result = await svc.AddAuthorAsync("hc:80626", config: null);

            Assert.That(result.Id, Is.EqualTo(existing.Id));
            Assert.That(authorService.AddAuthorCalled, Is.False);
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
