using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceFormatMonitoringSyncFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public readonly List<IEvent> Published = new();
            public Action<IEvent> OnPublish { get; set; }

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                Published.Add(@event);
                OnPublish?.Invoke(@event);
            }
        }

        private sealed class StubBookRepository : IBookRepository
        {
            private readonly Dictionary<int, Book> _booksById = new();
            private int _nextId = 1000;

            public StubBookRepository(IEnumerable<Book> books)
            {
                foreach (var book in books ?? Enumerable.Empty<Book>())
                {
                    _booksById[book.Id] = book;
                    _nextId = Math.Max(_nextId, book.Id + 1);
                }
            }

            public Book Get(int id) => _booksById.TryGetValue(id, out var book) ? book : null;

            public IEnumerable<Book> Get(IEnumerable<int> ids) => (ids ?? Enumerable.Empty<int>()).Select(Get).Where(book => book != null).ToList();

            public Book Update(Book model)
            {
                _booksById[model.Id] = model;
                return model;
            }

            public void InsertMany(IList<Book> model)
            {
                foreach (var book in model)
                {
                    if (book.Id == 0)
                    {
                        book.Id = _nextId++;
                    }

                    _booksById[book.Id] = book;
                }
            }

            public void InsertMany(IList<Book> model, IDbConnection connection, IDbTransaction transaction) => InsertMany(model);

            public void UpdateMany(IList<Book> model)
            {
                foreach (var book in model)
                {
                    _booksById[book.Id] = book;
                }
            }

            public List<Book> GetBooksByAuthorId(int authorId) => _booksById.Values.Where(book => book.AuthorId == authorId).ToList();

            public IEnumerable<Book> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => Get(id);
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Upsert(Book model) => throw new NotImplementedException();
            public void SetFields(Book model, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => _booksById.Remove(id);
            public void SetFields(IList<Book> models, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Book> model) => DeleteMany(model.Select(book => book.Id));
            public void DeleteMany(IEnumerable<int> ids)
            {
                foreach (var id in ids ?? Enumerable.Empty<int>())
                {
                    _booksById.Remove(id);
                }
            }
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Book Single() => throw new NotImplementedException();
            public Book SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Book> GetPaged(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> GetBooks(int authorId) => _booksById.Values.Where(book => book.AuthorId == authorId).ToList();
            public List<Book> GetLastBooks(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetNextBooks(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, IEnumerable<string> providerIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByIsbn(string isbn) => throw new NotImplementedException();
            public Book FindByAsin(string asin) => throw new NotImplementedException();
            public Book FindByProviderIds(string hardcoverBookId = null, string goodreadsBookId = null, string openLibraryWorkId = null) => throw new NotImplementedException();
            public Book FindByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => new();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec, List<QualitiesBelowCutoff> qualitiesBelowCutoff) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime startDate, DateTime endDate, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime startDate, DateTime endDate, bool includeUnmonitored) => throw new NotImplementedException();
            public void SetMonitoredFlat(Book book, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksBySeries(int seriesId) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public void SetMonitoringForAuthorBooks(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds = null) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            public List<Edition> GetEditionsByBook(int bookId) => new();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => new();
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
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authors;

            public StubAuthorService(IEnumerable<Author> authors)
            {
                _authors = authors.ToDictionary(author => author.Id);
            }

            public Author GetAuthor(int authorId) => _authors.TryGetValue(authorId, out var author) ? author : null;

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubSeriesBookLinkRepository : ISeriesBookLinkRepository
        {
            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds) => new();
            public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId) => new();
            public IEnumerable<SeriesBookLink> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public SeriesBookLink Find(int id) => throw new NotImplementedException();
            public SeriesBookLink Get(int id) => throw new NotImplementedException();
            public IEnumerable<SeriesBookLink> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public SeriesBookLink Insert(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Update(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Upsert(SeriesBookLink model) => throw new NotImplementedException();
            public void SetFields(SeriesBookLink model, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void Delete(SeriesBookLink model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void SetFields(IList<SeriesBookLink> models, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<SeriesBookLink> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public SeriesBookLink Single() => throw new NotImplementedException();
            public SeriesBookLink SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<SeriesBookLink> GetPaged(PagingSpec<SeriesBookLink> pagingSpec) => throw new NotImplementedException();
            public List<SeriesBookLink> GetLinksBySeries(int seriesId) => throw new NotImplementedException();
            public List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(IEnumerable<RootFolder> rootFolders = null)
            {
                _rootFolders = (rootFolders ?? new[]
                {
                    new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook },
                    new RootFolder { Id = 2, Path = "/ebooks", FolderType = FolderType.Ebook }
                }).ToList();
            }

            public List<RootFolder> All() => _rootFolders.ToList();
            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => _rootFolders.FirstOrDefault(r => r.Id == id);
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => _rootFolders.FirstOrDefault(r => r.Path == path);
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => allRootFolders?.FirstOrDefault(r => r.Path == path);
            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path, allRootFolders)?.Path;
        }

        private static Author BuildAuthor(int id, bool? sync = true, bool? audiobookMonitored = true, bool? ebookMonitored = true, string audiobookRootFolderPath = "/audiobooks", string ebookRootFolderPath = "/ebooks")
        {
            return new Author
            {
                Id = id,
                Name = $"Author {id}",
                SyncMonitoredAcrossFormats = sync,
                AudiobookMonitored = audiobookMonitored,
                EbookMonitored = ebookMonitored,
                AudiobookRootFolderPath = audiobookRootFolderPath,
                EbookRootFolderPath = ebookRootFolderPath
            };
        }

        private static Book BuildBook(int id, int authorId, BookMediaType mediaType, string workId, bool monitored)
        {
            var author = new Author { Id = authorId };
            var book = new Book
            {
                Id = id,
                Author = author,
                Title = $"Book {id}",
                CleanTitle = $"book{id}",
                MediaType = mediaType,
                BaseBookId = workId
            };

            if (workId?.StartsWith("hc:", StringComparison.OrdinalIgnoreCase) == true)
            {
                book.HardcoverBookId = workId;
            }
            else if (workId?.StartsWith("gr:", StringComparison.OrdinalIgnoreCase) == true)
            {
                book.GoodreadsWorkId = workId;
            }
            else if (workId?.StartsWith("ol:", StringComparison.OrdinalIgnoreCase) == true)
            {
                book.OpenLibraryWorkId = workId;
            }

            book.SetMonitored(monitored);
            return book;
        }

        private static BookService BuildService(StubBookRepository repository, StubAuthorService authorService, StubEventAggregator eventAggregator = null, IRootFolderService rootFolderService = null)
        {
            return new BookService(
                repository,
                new StubEditionService(),
                eventAggregator ?? new StubEventAggregator(),
                authorService,
                mediaFileService: null,
                rootFolderService: rootFolderService ?? new StubRootFolderService(),
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void set_book_monitored_should_enable_one_sibling_format()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetBookMonitored(audiobook.Id, true);

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.True);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void set_monitored_for_media_type_should_not_enable_sibling_format(string mediaType)
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var requestedBook = mediaType == "audiobook" ? audiobook : ebook;

            service.SetMonitoredForMediaType(new[] { requestedBook.Id }, mediaType, true);

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.EqualTo(mediaType == "audiobook"));
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.EqualTo(mediaType == "ebook"));
        }

        [Test]
        public void set_book_monitored_should_disable_sibling_format_when_last_copy_is_unmonitored()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetBookMonitored(audiobook.Id, false);

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.False);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void bulk_reconcile_should_enable_missing_monitored_sibling()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.Execute(new BulkSyncFormatMonitoringCommand(new List<int> { author.Id }));

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
        }

        [Test]
        public void bulk_reconcile_should_not_enable_missing_sibling_when_target_monitor_existing_is_none()
        {
            var author = BuildAuthor(1, sync: false, ebookMonitored: false);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.Execute(new BulkSyncFormatMonitoringCommand(new List<int> { author.Id }));

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void set_book_monitored_should_not_enable_sibling_when_target_monitor_existing_is_none()
        {
            var author = BuildAuthor(1, sync: false, ebookMonitored: false);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetBookMonitored(audiobook.Id, true);

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.True);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void bulk_reconcile_should_ignore_monitored_unit_clone_when_syncing_opposite_format()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var audiobookClone = BuildBook(12, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            audiobookClone.UnitKeyHash = "unit-copy";

            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, audiobookClone, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.Execute(new BulkSyncFormatMonitoringCommand(new List<int> { author.Id }));

            Assert.That(repository.Get(audiobookClone.Id).AudiobookMonitored, Is.True);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void set_book_monitored_should_ignore_unit_clone_change_for_cross_format_sync()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var audiobookClone = BuildBook(12, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            audiobookClone.UnitKeyHash = "unit-copy";

            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, audiobookClone, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetBookMonitored(audiobookClone.Id, true);

            Assert.That(repository.Get(audiobookClone.Id).AudiobookMonitored, Is.True);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void bulk_reconcile_should_enable_canonical_sibling_even_when_clone_is_already_monitored()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var ebookClone = BuildBook(12, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: true);
            ebookClone.UnitKeyHash = "unit-copy";

            var repository = new StubBookRepository(new[] { audiobook, ebook, ebookClone });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.Execute(new BulkSyncFormatMonitoringCommand(new List<int> { author.Id }));

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
            Assert.That(repository.Get(ebookClone.Id).EbookMonitored, Is.True);
        }

        [Test]
        public void insert_many_should_seed_new_format_from_monitored_sibling()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            var newEbook = BuildBook(0, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);

            service.InsertMany(new List<Book> { newEbook });

            Assert.That(newEbook.EbookMonitored, Is.True);
            Assert.That(repository.Get(newEbook.Id).EbookMonitored, Is.True);
        }

        [Test]
        public void insert_many_should_not_seed_new_copy_from_monitored_sibling()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            var newEbookCopy = BuildBook(0, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            newEbookCopy.UnitKeyHash = "unit-copy";

            service.InsertMany(new List<Book> { newEbookCopy });

            Assert.That(newEbookCopy.EbookMonitored, Is.False);
            Assert.That(repository.Get(newEbookCopy.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void set_book_monitored_should_not_enable_sibling_format_without_compatible_root_folder()
        {
            var author = BuildAuthor(1, ebookRootFolderPath: null);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetBookMonitored(audiobook.Id, true);

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.True);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void set_book_monitored_should_not_enable_cross_format_book_when_only_edition_identity_matches()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            audiobook.ASIN = "B00SHAREDASIN";

            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-2", monitored: false);
            ebook.ASIN = "B00SHAREDASIN";

            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetBookMonitored(audiobook.Id, true);

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.True);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void delete_book_should_delete_cross_format_siblings_when_work_id_matches()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.DeleteBook(audiobook.Id, deleteFiles: false, applyToBothFormats: true);

            Assert.That(repository.Get(audiobook.Id), Is.Null);
            Assert.That(repository.Get(ebook.Id), Is.Null);
        }

        [Test]
        public void delete_book_should_delete_same_format_clone_when_work_id_matches()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var audiobookClone = BuildBook(11, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook, audiobookClone });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.DeleteBook(audiobook.Id, deleteFiles: false, applyToBothFormats: true);

            Assert.That(repository.Get(audiobook.Id), Is.Null);
            Assert.That(repository.Get(audiobookClone.Id), Is.Null);
        }

        [Test]
        public void delete_book_should_delete_clones_and_cross_format_siblings_together()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var audiobookClone = BuildBook(11, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(12, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook, audiobookClone, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.DeleteBook(audiobook.Id, deleteFiles: false, applyToBothFormats: true);

            Assert.That(repository.Get(audiobook.Id), Is.Null);
            Assert.That(repository.Get(audiobookClone.Id), Is.Null);
            Assert.That(repository.Get(ebook.Id), Is.Null);
        }

        [Test]
        public void delete_book_should_not_delete_cross_format_book_when_only_edition_identity_matches()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            audiobook.ASIN = "B00SHAREDASIN";

            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-2", monitored: true);
            ebook.ASIN = "B00SHAREDASIN";

            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.DeleteBook(audiobook.Id, deleteFiles: false, applyToBothFormats: true);

            Assert.That(repository.Get(audiobook.Id), Is.Null);
            Assert.That(repository.Get(ebook.Id), Is.Not.Null);
        }

        [Test]
        public void delete_book_should_publish_while_parent_row_is_still_available()
        {
            var author = BuildAuthor(1);
            var book = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { book });
            var events = new StubEventAggregator();
            events.OnPublish = published =>
            {
                if (published is BookDeletedEvent deleted)
                {
                    Assert.That(repository.Get(deleted.Book.Id), Is.Not.Null,
                        "Edition deletion handlers need the Book row for the FTS delete trigger");
                }
            };
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.DeleteBook(book.Id, deleteFiles: false);

            Assert.That(repository.Get(book.Id), Is.Null);
        }

        [Test]
        public void delete_many_should_publish_all_events_before_deleting_parent_rows()
        {
            var author = BuildAuthor(1);
            var first = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var second = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-2", monitored: true);
            var repository = new StubBookRepository(new[] { first, second });
            var events = new StubEventAggregator();
            events.OnPublish = published =>
            {
                if (published is BookDeletedEvent deleted)
                {
                    Assert.That(repository.Get(deleted.Book.Id), Is.Not.Null);
                }
            };
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.DeleteMany(new List<Book> { first, second });

            Assert.That(repository.Get(first.Id), Is.Null);
            Assert.That(repository.Get(second.Id), Is.Null);
            Assert.That(events.Published.OfType<BookDeletedEvent>().Count(), Is.EqualTo(2));
        }
    }
}
