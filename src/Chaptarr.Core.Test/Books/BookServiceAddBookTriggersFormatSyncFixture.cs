using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    // "Sync Monitored Across Formats" only reconciles on a later monitor-toggle (SetBookMonitored,
    // SetMonitored, UpdateBook, UpdateMany). A book inserted already monitored - which is exactly
    // what every book request does (POST /api/v1/book with monitored: true) - never goes through
    // any of those, so its sibling format is left behind. This fixture pins AddBook into the same
    // reconciliation the mutation methods already use.
    [TestFixture]
    public class BookServiceAddBookTriggersFormatSyncFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
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

            public Book Upsert(Book model)
            {
                if (model.Id == 0)
                {
                    model.Id = _nextId++;
                }

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
            public List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions)
            {
                // AddBook only reads the count/ids of what it passed in; nothing here needs to persist for these tests.
            }
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => InsertMany(editions);
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => new() { edition };
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authors;

            public StubAuthorService(IEnumerable<Author> authors) => _authors = authors.ToDictionary(author => author.Id);

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
            private readonly List<RootFolder> _rootFolders = new()
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/ebooks", FolderType = FolderType.Ebook }
            };

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

        private static Author BuildAuthor(bool sync) => new Author
        {
            Id = 1,
            Name = "Matt Dinniman",
            SyncMonitoredAcrossFormats = sync,
            AudiobookRootFolderPath = "/audiobooks",
            EbookRootFolderPath = "/ebooks"
        };

        // An existing, unmonitored sibling row - exactly what a refresh leaves behind for a book
        // nobody has requested yet.
        private static Book BuildExistingSibling(int id, int authorId, BookMediaType mediaType, string workId)
        {
            var book = new Book { Id = id, AuthorId = authorId, Title = "Carl's Doomsday Scenario", MediaType = mediaType, GoodreadsWorkId = workId };
            book.SetMonitored(false);
            return book;
        }

        // What AddBookService hands to BookService.AddBook for a fresh request: no Id yet, already monitored.
        private static Book BuildNewRequestedBook(int authorId, BookMediaType mediaType, string workId, bool monitored)
        {
            var book = new Book
            {
                AuthorId = authorId,
                Title = "Carl's Doomsday Scenario",
                MediaType = mediaType,
                GoodreadsWorkId = workId,
                Editions = new List<Edition> { new Edition { Monitored = true, ForeignEditionId = "ed-1" } }
            };
            book.SetMonitored(monitored);
            return book;
        }

        private static BookService BuildService(StubBookRepository repository, StubAuthorService authorService)
        {
            return new BookService(
                repository,
                new StubEditionService(),
                new StubEventAggregator(),
                authorService,
                mediaFileService: null,
                rootFolderService: new StubRootFolderService(),
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void add_book_should_monitor_the_unmonitored_sibling_format()
        {
            var author = BuildAuthor(sync: true);
            var ebook = BuildExistingSibling(11, author.Id, BookMediaType.Ebook, "gr:work-1");
            var repository = new StubBookRepository(new[] { ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var newAudiobook = BuildNewRequestedBook(author.Id, BookMediaType.Audiobook, "gr:work-1", monitored: true);

            service.AddBook(newAudiobook, doRefresh: false);

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True, "the pre-existing ebook sibling should have been monitored by the sync");
        }

        [Test]
        public void add_book_should_not_touch_the_sibling_when_sync_is_disabled()
        {
            var author = BuildAuthor(sync: false);
            var ebook = BuildExistingSibling(11, author.Id, BookMediaType.Ebook, "gr:work-1");
            var repository = new StubBookRepository(new[] { ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var newAudiobook = BuildNewRequestedBook(author.Id, BookMediaType.Audiobook, "gr:work-1", monitored: true);

            service.AddBook(newAudiobook, doRefresh: false);

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void add_book_should_not_touch_the_sibling_when_the_new_book_is_not_monitored()
        {
            var author = BuildAuthor(sync: true);
            var ebook = BuildExistingSibling(11, author.Id, BookMediaType.Ebook, "gr:work-1");
            var repository = new StubBookRepository(new[] { ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var newAudiobook = BuildNewRequestedBook(author.Id, BookMediaType.Audiobook, "gr:work-1", monitored: false);

            service.AddBook(newAudiobook, doRefresh: false);

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void add_book_with_no_sibling_at_all_should_not_throw()
        {
            var author = BuildAuthor(sync: true);
            var repository = new StubBookRepository(Array.Empty<Book>());
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var newAudiobook = BuildNewRequestedBook(author.Id, BookMediaType.Audiobook, "gr:work-solo", monitored: true);

            Assert.DoesNotThrow(() => service.AddBook(newAudiobook, doRefresh: false));
        }
    }
}
