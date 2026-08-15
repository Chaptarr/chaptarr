using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServiceSpecificBookMonitoringFixture
    {
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

        private sealed class StubAuthorService : IAuthorService
        {
            private int _nextId = 500;

            public Author ExistingAuthor { get; set; }

            public Author FindByProviderId(string provider, string providerId) => ExistingAuthor;

            public Author AddAuthor(Author newAuthor, bool doRefresh)
            {
                newAuthor.Id = _nextId++;
                return newAuthor;
            }

            public Author UpdateAuthor(Author author)
            {
                ExistingAuthor = author;
                return author;
            }

            public Author UpdateAuthorProgressiveSettings(
                Author author,
                int? audiobookQualityProfileId,
                int? audiobookMetadataProfileId,
                int? audiobookMonitorExisting,
                bool? audiobookMonitorFuture,
                int? ebookQualityProfileId,
                int? ebookMetadataProfileId,
                int? ebookMonitorExisting,
                bool? ebookMonitorFuture,
                string rootFolderPath)
            {
                if (audiobookQualityProfileId > 0)
                {
                    author.AudiobookQualityProfileId ??= audiobookQualityProfileId;
                    author.AudiobookMetadataProfileId ??= audiobookMetadataProfileId;
                    author.AudiobookMonitorExisting ??= audiobookMonitorExisting;
                    author.AudiobookMonitorFuture ??= audiobookMonitorFuture;
                    if (string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
                    {
                        author.AudiobookRootFolderPath = rootFolderPath;
                    }
                }

                if (ebookQualityProfileId > 0)
                {
                    author.EbookQualityProfileId ??= ebookQualityProfileId;
                    author.EbookMetadataProfileId ??= ebookMetadataProfileId;
                    author.EbookMonitorExisting ??= ebookMonitorExisting;
                    author.EbookMonitorFuture ??= ebookMonitorFuture;
                    if (string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
                    {
                        author.EbookRootFolderPath = rootFolderPath;
                    }
                }

                ExistingAuthor = author;
                return author;
            }

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByName(string title) => null;
            public Author FindByNameInexact(string title) => null;
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
            private int _nextId = 1000;

            public List<Book> InsertedBooks { get; } = new();
            public List<Book> UpdatedBooks { get; } = new();
            public List<Book> ExistingBooks { get; set; } = new();

            public void InsertMany(List<Book> books)
            {
                foreach (var book in books)
                {
                    book.Id = _nextId++;
                    InsertedBooks.Add(book);
                }
            }

            public void UpdateMany(List<Book> books)
            {
                if (books != null)
                {
                    UpdatedBooks.AddRange(books);
                }
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => ExistingBooks;
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
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
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

        private sealed class StubEditionService : IEditionService
        {
            private int _nextId = 2000;
            private readonly List<Edition> _editions = new();

            public void InsertMany(List<Edition> editions)
            {
                foreach (var edition in editions)
                {
                    edition.Id = _nextId++;
                    _editions.Add(edition);
                }
            }

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                var idSet = new HashSet<int>(bookIds ?? Enumerable.Empty<int>());
                return _editions.Where(e => idSet.Contains(e.BookId)).ToList();
            }

            public void UpdateMany(List<Edition> editions)
            {
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
            public List<Edition> GetEditionsByBook(int bookId) => _editions.Where(e => e.BookId == bookId).ToList();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            public List<int> FilterProfileIds { get; } = new();

            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => throw new NotImplementedException();
            public MetadataProfile Get(int id) => new MetadataProfile { Id = id, Name = $"Profile {id}" };
            public bool Exists(int id) => true;
            public List<Book> FilterBooks(Author input, int profileId)
            {
                FilterProfileIds.Add(profileId);
                return input?.Books?.ToList() ?? new List<Book>();
            }
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
            public RootFolder GetBestRootFolder(string path) => GetBestRootFolder(path, _rootFolders);
            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path, allRootFolders)?.Path;

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return (allRootFolders ?? new List<RootFolder>())
                    .FirstOrDefault(r => !string.IsNullOrWhiteSpace(path) &&
                                         !string.IsNullOrWhiteSpace(r.Path) &&
                                         path.StartsWith(r.Path.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class StubAuthorPathBuilder : IBuildAuthorPaths
        {
            public string BuildPath(Author author, bool useExistingRelativeFolder) => $"/authors/{author?.Name ?? "unknown"}";
            public string BuildPathForQuality(Author author, NzbDrone.Core.Qualities.Quality quality, bool useExistingRelativeFolder) => BuildPath(author, useExistingRelativeFolder);
            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder)
            {
            }
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, NzbDrone.Common.Messaging.IEvent
            {
            }
        }

        [Test]
        public async Task add_author_should_not_hydrate_unconfigured_media_type_from_stray_profile()
        {
            var remoteAuthor = new Author
            {
                Name = "Full Catalog Author",
                Books = new List<Book>
                {
                    BuildAudiobook("Audio Book", "hc:1001"),
                    BuildEbook("Ebook Book", "hc:2001")
                },
                Series = new List<Series>()
            };

            var bookService = new StubBookService();
            var service = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: new StubMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                EbookMetadataProfileId = 3
            });

            var insertedBook = bookService.InsertedBooks.Single();

            Assert.Multiple(() =>
            {
                Assert.That(insertedBook.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(insertedBook.Title, Is.EqualTo("Audio Book"));
            });
        }

        [Test]
        public async Task existing_author_should_backfill_missing_profile_enabled_media_type()
        {
            var remoteAuthor = new Author
            {
                Name = "Existing Author",
                Books = new List<Book>
                {
                    BuildAudiobook("Existing Audio", "hc:1001"),
                    BuildEbook("Missing Ebook", "hc:2001")
                },
                Series = new List<Series>()
            };

            var existingAuthor = new Author
            {
                Id = 77,
                Name = remoteAuthor.Name,
                AudiobookMetadataProfileId = 1,
                EbookMetadataProfileId = 3
            };

            var authorService = new StubAuthorService
            {
                ExistingAuthor = existingAuthor
            };

            var bookService = new StubBookService
            {
                ExistingBooks = new List<Book>
                {
                    BuildAudiobook("Existing Audio", "hc:1001")
                }
            };

            var service = new AuthorLibraryService(
                authorService: authorService,
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: new StubMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false
            });

            var insertedBook = bookService.InsertedBooks.Single();

            Assert.Multiple(() =>
            {
                Assert.That(insertedBook.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(insertedBook.Title, Is.EqualTo("Missing Ebook"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task existing_author_should_apply_fill_missing_settings_before_monitoring_backfilled_books(bool hasManualTargetSettings)
        {
            var remoteAuthor = new Author
            {
                Name = "Existing Author",
                Books = new List<Book>
                {
                    BuildAudiobook("Missing Audio", "hc:1001"),
                    BuildEbook("Existing Ebook", "hc:2001")
                },
                Series = new List<Series>()
            };

            var existingAuthor = new Author
            {
                Id = 77,
                Name = remoteAuthor.Name,
                Monitored = true,
                AudiobookQualityProfileId = hasManualTargetSettings ? 77 : null,
                AudiobookMetadataProfileId = hasManualTargetSettings ? 78 : null,
                AudiobookMonitorExisting = hasManualTargetSettings ? 1 : null,
                AudiobookMonitorFuture = hasManualTargetSettings ? true : null,
                AudiobookRootFolderPath = hasManualTargetSettings ? "/manual-audiobooks" : null,
                EbookQualityProfileId = 9,
                EbookMetadataProfileId = 8,
                EbookMonitorExisting = 2,
                EbookMonitorFuture = false,
                EbookRootFolderPath = "/manual-ebooks"
            };

            var authorService = new StubAuthorService
            {
                ExistingAuthor = existingAuthor
            };

            var bookService = new StubBookService
            {
                ExistingBooks = new List<Book>
                {
                    BuildEbook("Existing Ebook", "hc:2001")
                }
            };

            var metadataProfileService = new StubMetadataProfileService();
            var service = new AuthorLibraryService(
                authorService: authorService,
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: metadataProfileService,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true
            });

            var insertedBook = bookService.InsertedBooks.Single();

            var expectedQualityProfileId = hasManualTargetSettings ? 77 : 2;
            var expectedMetadataProfileId = hasManualTargetSettings ? 78 : 1;
            var expectedRootFolderPath = hasManualTargetSettings ? "/manual-audiobooks" : "/audiobooks";
            Assert.Multiple(() =>
            {
                Assert.That(insertedBook.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(insertedBook.AudiobookMonitored, Is.True);
                Assert.That(authorService.ExistingAuthor.AudiobookQualityProfileId, Is.EqualTo(expectedQualityProfileId));
                Assert.That(authorService.ExistingAuthor.AudiobookMetadataProfileId, Is.EqualTo(expectedMetadataProfileId));
                Assert.That(authorService.ExistingAuthor.AudiobookRootFolderPath, Is.EqualTo(expectedRootFolderPath));
                Assert.That(authorService.ExistingAuthor.AudiobookMonitorExisting, Is.EqualTo(1));
                Assert.That(authorService.ExistingAuthor.AudiobookMonitorFuture, Is.True);
                Assert.That(authorService.ExistingAuthor.EbookQualityProfileId, Is.EqualTo(9));
                Assert.That(authorService.ExistingAuthor.EbookMetadataProfileId, Is.EqualTo(8));
                Assert.That(authorService.ExistingAuthor.EbookMonitorExisting, Is.EqualTo(2));
                Assert.That(authorService.ExistingAuthor.EbookMonitorFuture, Is.False);
                Assert.That(authorService.ExistingAuthor.EbookRootFolderPath, Is.EqualTo("/manual-ebooks"));
                Assert.That(metadataProfileService.FilterProfileIds, Does.Contain(expectedMetadataProfileId));
            });
        }

        [Test]
        public async Task add_author_should_monitor_selected_audiobook_books_from_import_list_config()
        {
            var remoteAuthor = new Author
            {
                Name = "Shelf Author",
                Books = new List<Book>
                {
                    BuildAudiobook("Selected Book", "hc:1001"),
                    BuildAudiobook("Other Book", "hc:1002")
                },
                Series = new List<Series>()
            };

            var bookService = new StubBookService();
            var service = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: new StubMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            var addedAuthor = await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                AudiobookBooksToMonitor = new List<string> { "hc:1001" }
            });

            Assert.That(addedAuthor, Is.Not.Null);
            Assert.That(bookService.InsertedBooks, Has.Count.EqualTo(2));

            var selectedBook = bookService.InsertedBooks.Single(b => b.HardcoverBookId == "hc:1001");
            var otherBook = bookService.InsertedBooks.Single(b => b.HardcoverBookId == "hc:1002");

            Assert.Multiple(() =>
            {
                Assert.That(selectedBook.AudiobookMonitored, Is.True);
                Assert.That(otherBook.AudiobookMonitored, Is.False);
                Assert.That(selectedBook.EbookMonitored, Is.False);
                Assert.That(otherBook.EbookMonitored, Is.False);
            });
        }

        // "Only This Book" limits MONITORING, not the catalog. Readarr paired it with the built-in
        // "None" metadata profile so a specific-book add imported nothing else; Chaptarr keeps the
        // configured profile on purpose, because unmonitored siblings are matcher evidence
        // (TitleMatchProblemCode.SiblingTitleContradiction, ReleaseTitleMatchScorer.cs:22).
        // Declined community PR #37 substituted the None profile here and the full suite stayed
        // green — the sibling assertions above cannot see a profile swap, because the stub returns
        // every book regardless of profile. These two pin the profile that actually filtered.
        [Test]
        public async Task add_author_with_only_this_book_should_keep_siblings_and_the_configured_audiobook_profile()
        {
            var remoteAuthor = new Author
            {
                Name = "Shelf Author",
                Books = new List<Book>
                {
                    BuildAudiobook("Selected Book", "hc:1001"),
                    BuildAudiobook("Other Book", "hc:1002")
                },
                Series = new List<Series>()
            };

            var bookService = new StubBookService();
            var metadataProfileService = new StubMetadataProfileService();
            var service = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: metadataProfileService,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            var addedAuthor = await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                AudiobookBooksToMonitor = new List<string> { "hc:1001" }
            });

            Assert.That(addedAuthor, Is.Not.Null);

            Assert.Multiple(() =>
            {
                // Catalog retained: the unselected sibling is still a row, just unmonitored.
                Assert.That(bookService.InsertedBooks, Has.Count.EqualTo(2));
                Assert.That(bookService.InsertedBooks.Single(b => b.HardcoverBookId == "hc:1001").AudiobookMonitored, Is.True);
                Assert.That(bookService.InsertedBooks.Single(b => b.HardcoverBookId == "hc:1002").AudiobookMonitored, Is.False);

                // The CONFIGURED profile is what filtered, never a substituted None profile.
                Assert.That(metadataProfileService.FilterProfileIds, Is.Not.Empty);
                Assert.That(metadataProfileService.FilterProfileIds, Has.All.EqualTo(1));
                Assert.That(addedAuthor.AudiobookMetadataProfileId, Is.EqualTo(1));

                // The media type that was not requested is left alone.
                Assert.That(addedAuthor.EbookMetadataProfileId, Is.Null);
            });
        }

        [Test]
        public async Task add_author_with_only_this_book_should_keep_siblings_and_the_configured_ebook_profile()
        {
            var remoteAuthor = new Author
            {
                Name = "Shelf Author",
                Books = new List<Book>
                {
                    BuildEbook("Selected Book", "hc:2001"),
                    BuildEbook("Other Book", "hc:2002")
                },
                Series = new List<Series>()
            };

            var bookService = new StubBookService();
            var metadataProfileService = new StubMetadataProfileService();
            var service = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: metadataProfileService,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildEbookRoot("/ebooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            var addedAuthor = await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = false,
                CreateEbook = true,
                EbookQualityProfileId = 4,
                EbookMetadataProfileId = 3,
                EbookRootFolderPath = "/ebooks",
                EbookMonitorExisting = 2,
                EbookMonitorFuture = false,
                EbookBooksToMonitor = new List<string> { "hc:2001" }
            });

            Assert.That(addedAuthor, Is.Not.Null);

            Assert.Multiple(() =>
            {
                Assert.That(bookService.InsertedBooks, Has.Count.EqualTo(2));
                Assert.That(bookService.InsertedBooks.Single(b => b.HardcoverBookId == "hc:2001").EbookMonitored, Is.True);
                Assert.That(bookService.InsertedBooks.Single(b => b.HardcoverBookId == "hc:2002").EbookMonitored, Is.False);

                Assert.That(metadataProfileService.FilterProfileIds, Is.Not.Empty);
                Assert.That(metadataProfileService.FilterProfileIds, Has.All.EqualTo(3));
                Assert.That(addedAuthor.EbookMetadataProfileId, Is.EqualTo(3));

                Assert.That(addedAuthor.AudiobookMetadataProfileId, Is.Null);
            });
        }

        [Test]
        public async Task add_author_should_monitor_selected_book_by_remote_provider_alias()
        {
            var remoteAuthor = new Author
            {
                Name = "J. K. Rowling",
                Books = new List<Book>
                {
                    BuildAudiobook("Harry Potter and the Goblet of Fire", "hc:383236", "gr:3046572"),
                    BuildAudiobook("Harry Potter and the Order of the Phoenix", "hc:383237", "gr:1234567")
                },
                Series = new List<Series>()
            };

            var bookService = new StubBookService();
            var service = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: new StubMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            await service.AddAuthorAsync("hc:author-1", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                AudiobookBooksToMonitor = new List<string> { "gr:3046572" }
            });

            var selectedBook = bookService.InsertedBooks.Single(b => b.Title == "Harry Potter and the Goblet of Fire");
            var otherBook = bookService.InsertedBooks.Single(b => b.Title == "Harry Potter and the Order of the Phoenix");

            Assert.Multiple(() =>
            {
                Assert.That(selectedBook.AudiobookMonitored, Is.True);
                Assert.That(otherBook.AudiobookMonitored, Is.False);
            });
        }

        [Test]
        public async Task add_author_should_monitor_selected_book_by_goodreads_work_alias_from_bookshelf()
        {
            var canonicalWork = BuildAudiobook("The Count of Monte Cristo", "hc:1151710", "gr:391568");
            canonicalWork.GoodreadsWorkId = "gr:182750455";

            var otherBook = BuildAudiobook("The Three Musketeers", "hc:1151711", "gr:11588");
            otherBook.GoodreadsWorkId = "gr:1263215";

            var remoteAuthor = new Author
            {
                Name = "Alexandre Dumas",
                Books = new List<Book>
                {
                    canonicalWork,
                    otherBook
                },
                Series = new List<Series>()
            };

            var bookService = new StubBookService();
            var service = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfo(remoteAuthor),
                bookService: bookService,
                refreshSeriesService: null,
                editionService: new StubEditionService(),
                narratorLinkService: null,
                metadataProfileService: new StubMetadataProfileService(),
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: new StubRootFolderService(BuildAudiobookRoot("/audiobooks")),
                commandQueueManager: null,
                eventAggregator: new StubEventAggregator(),
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));

            await service.AddAuthorAsync("gr:4785", new MonitoringConfig
            {
                AuthorName = remoteAuthor.Name,
                MonitorNewItems = true,
                CreateAudiobook = true,
                CreateEbook = false,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = false,
                AudiobookBooksToMonitor = new List<string> { "gr:391568" }
            });

            var selectedBook = bookService.InsertedBooks.Single(b => b.Title == "The Count of Monte Cristo");
            var otherInsertedBook = bookService.InsertedBooks.Single(b => b.Title == "The Three Musketeers");

            Assert.Multiple(() =>
            {
                Assert.That(selectedBook.GoodreadsWorkId, Is.EqualTo("gr:182750455"));
                Assert.That(selectedBook.RemoteProviderIds, Does.Contain("gr:391568"));
                Assert.That(selectedBook.AudiobookMonitored, Is.True);
                Assert.That(otherInsertedBook.AudiobookMonitored, Is.False);
            });
        }

        private static Book BuildAudiobook(string title, string hardcoverBookId, params string[] remoteProviderIds)
        {
            return new Book
            {
                Title = title,
                CleanTitle = title.ToLowerInvariant(),
                TitleSlug = title.ToLowerInvariant().Replace(" ", "-"),
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = hardcoverBookId,
                BaseBookId = hardcoverBookId,
                RemoteProviderIds = remoteProviderIds?.Any() == true
                    ? new HashSet<string>(remoteProviderIds, StringComparer.OrdinalIgnoreCase)
                    : null,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = title,
                        ForeignEditionId = $"{hardcoverBookId}:audio",
                        ReadingFormatId = 2,
                        Language = "eng"
                    }
                }
            };
        }

        private static Book BuildEbook(string title, string hardcoverBookId, params string[] remoteProviderIds)
        {
            return new Book
            {
                Title = title,
                CleanTitle = title.ToLowerInvariant(),
                TitleSlug = title.ToLowerInvariant().Replace(" ", "-"),
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = hardcoverBookId,
                BaseBookId = hardcoverBookId,
                RemoteProviderIds = remoteProviderIds?.Any() == true
                    ? new HashSet<string>(remoteProviderIds, StringComparer.OrdinalIgnoreCase)
                    : null,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = title,
                        ForeignEditionId = $"{hardcoverBookId}:ebook",
                        ReadingFormatId = 3,
                        Language = "eng"
                    }
                }
            };
        }

        private static RootFolder BuildAudiobookRoot(string path)
        {
            var root = new RootFolder
            {
                Path = path,
                FolderType = FolderType.Audiobook
            };

            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 2,
                MetadataProfileId = 1,
                MonitorExisting = 2,
                MonitorFuture = false
            });

            return root;
        }

        private static RootFolder BuildEbookRoot(string path)
        {
            var root = new RootFolder
            {
                Path = path,
                FolderType = FolderType.Ebook
            };

            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 4,
                MetadataProfileId = 3,
                MonitorExisting = 2,
                MonitorFuture = false
            });

            return root;
        }
    }
}
