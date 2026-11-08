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
            public List<Book> GetImportListBookIdentities() => All().ToList();

            private readonly Dictionary<int, Book> _booksById = new();
            private int _nextId = 1000;

            public int AuthorCatalogLookupCount { get; private set; }
            public int SlugLookupCount { get; private set; }
            public int UpdatedRowCount { get; private set; }

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

            public void UpdateManyAtomic(IList<Book> books, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated) => throw new System.NotSupportedException();
            public void UpdateMany(IList<Book> model)
            {
                UpdatedRowCount += model.Count;
                foreach (var book in model)
                {
                    _booksById[book.Id] = book;
                }
            }

            public void SetMonitoring(IList<Book> books)
            {
                UpdatedRowCount += books.Count;
                foreach (var book in books)
                {
                    var stored = _booksById[book.Id];
                    stored.AudiobookMonitored = book.AudiobookMonitored;
                    stored.EbookMonitored = book.EbookMonitored;
                }
            }

            public List<Book> GetBooksByAuthorId(int authorId)
            {
                AuthorCatalogLookupCount++;
                return _booksById.Values.Where(book => book.AuthorId == authorId).ToList();
            }

            public IEnumerable<Book> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => Get(id);
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Upsert(Book model)
            {
                if (model.Id == 0)
                {
                    model.Id = _nextId++;
                }

                _booksById[model.Id] = model;
                return model;
            }
            public void SetFields(Book model, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => _booksById.Remove(id);
            public void SetFields(IList<Book> models, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Book> model) => DeleteMany(model.Select(book => book.Id));
            public void DeleteManyAtomic(IEnumerable<int> ids, IEnumerable<int> deletedFileIds = null) => DeleteMany(ids);
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
            public List<Book> FindTitleSlugConflicts(IEnumerable<string> slugs, IDbConnection connection = null, IDbTransaction transaction = null) {
                SlugLookupCount++;
                return _booksById.Values.Where(book => slugs.Any(slug => book.TitleSlug != null && (book.TitleSlug.Equals(slug, StringComparison.OrdinalIgnoreCase) || book.TitleSlug.StartsWith(slug + "_", StringComparison.OrdinalIgnoreCase)))).ToList();
            }
            public List<Book> FindBySlug(string titleSlug) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec, List<QualitiesBelowCutoff> qualitiesBelowCutoff) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime startDate, DateTime endDate, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime startDate, DateTime endDate, bool includeUnmonitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
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
            private readonly List<Edition> _editions;

            public StubEditionService(IEnumerable<Edition> editions = null)
            {
                _editions = (editions ?? Enumerable.Empty<Edition>()).ToList();
            }

            public int SingleBookLookupCount { get; private set; }
            public int BulkBookLookupCount { get; private set; }
            public int RowOnlyLookupCount { get; private set; }

            public List<Edition> GetEditionsByBook(int bookId)
            {
                SingleBookLookupCount++;
                return _editions.Where(edition => edition.BookId == bookId).ToList();
            }

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                BulkBookLookupCount++;
                var ids = bookIds.ToHashSet();
                return _editions.Where(edition => ids.Contains(edition.BookId)).ToList();
            }

            public List<Edition> GetEditionRowsByBook(IEnumerable<int> bookIds)
            {
                RowOnlyLookupCount++;
                var ids = bookIds.ToHashSet();
                return _editions.Where(edition => ids.Contains(edition.BookId)).ToList();
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
            public void InsertMany(List<Edition> editions) => _editions.AddRange(editions);
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
            {
                foreach (var item in _editions.Where(item => item.BookId == edition.BookId))
                {
                    item.Monitored = item == edition;
                }

                return _editions.Where(item => item.BookId == edition.BookId).ToList();
            }

            public void RefreshProviderAliases(List<Edition> editions) { }
            public void UpdateSelection(List<Edition> editions) => UpdateMany(editions);
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authors;

            public StubAuthorService(IEnumerable<Author> authors)
            {
                _authors = authors.ToDictionary(author => author.Id);
            }

            public int GetAuthorCallCount { get; private set; }

            public Author GetAuthor(int authorId)
            {
                GetAuthorCallCount++;
                return _authors.TryGetValue(authorId, out var author) ? author : null;
            }

            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => authorIds.Distinct().Select(GetAuthor).Where(author => author != null).ToList();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void SetAddOptions(Author author) => throw new NotImplementedException();
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
                    new RootFolder { Id = 1, Path = @"C:\audiobooks", FolderType = FolderType.Audiobook },
                    new RootFolder { Id = 2, Path = @"C:\ebooks", FolderType = FolderType.Ebook }
                }).ToList();
            }

            public int AllCount { get; private set; }

            public List<RootFolder> All()
            {
                AllCount++;
                return _rootFolders.ToList();
            }
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

        private sealed class ThrowingLazyLoad<T> : LazyLoaded<T>
        {
            public override void LazyLoad() => throw new AssertionException("Unrelated work-backed rows must not load navigation properties");
        }

        private static Author BuildAuthor(int id, bool? sync = true, bool? audiobookMonitored = true, bool? ebookMonitored = true, string audiobookRootFolderPath = @"C:\audiobooks", string ebookRootFolderPath = @"C:\ebooks")
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

        private static BookService BuildService(StubBookRepository repository, StubAuthorService authorService, StubEventAggregator eventAggregator = null, IRootFolderService rootFolderService = null, StubEditionService editionService = null)
        {
            return new BookService(
                repository,
                editionService ?? new StubEditionService(),
                eventAggregator ?? new StubEventAggregator(),
                authorService,
                mediaFileService: null,
                rootFolderService: rootFolderService ?? new StubRootFolderService(),
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void author_refresh_slug_index_should_preserve_suffixes_without_reloading_the_catalog()
        {
            var author = BuildAuthor(1, sync: false);
            var books = Enumerable.Range(1, 1000)
                .Select(id => BuildBook(id, author.Id, BookMediaType.Ebook, $"hc:work-{id}", monitored: false))
                .ToList();
            foreach (var book in books)
            {
                book.TitleSlug = $"book-{book.Id}";
            }

            books[9].TitleSlug = "résumé";
            books[10].TitleSlug = "résumé_2";
            var repository = new StubBookRepository(books);
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var titleSlugs = new BookTitleSlugIndex(author.Id, repository.GetBooksByAuthorId(author.Id));

            books[11].TitleSlug = "RÉSUMÉ";
            service.UpdateMany(new List<Book> { books[11] }, titleSlugs);
            Assert.That(books[11].TitleSlug, Is.EqualTo("résumé_3"));

            books[9].TitleSlug = "another";
            service.UpdateMany(new List<Book> { books[9] }, titleSlugs);
            books[10].TitleSlug = "RÉSUMÉ";
            service.UpdateMany(new List<Book> { books[10] }, titleSlugs);

            Assert.That(books[10].TitleSlug, Is.EqualTo("résumé"));
            foreach (var book in books.Skip(12))
            {
                service.UpdateMany(new List<Book> { book }, titleSlugs);
            }

            Assert.That(repository.AuthorCatalogLookupCount, Is.EqualTo(1));
            Assert.That(repository.SlugLookupCount, Is.EqualTo(2), "One preloaded batch, then the one newly introduced title; never one read per book.");
        }

        [Test]
        public void author_refresh_slug_index_should_keep_a_legacy_duplicate_reserved()
        {
            var author = BuildAuthor(1, sync: false);
            var first = BuildBook(1, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var second = BuildBook(2, author.Id, BookMediaType.Ebook, "hc:work-2", monitored: false);
            var third = BuildBook(3, author.Id, BookMediaType.Ebook, "hc:work-3", monitored: false);
            first.TitleSlug = "same";
            second.TitleSlug = "SAME";
            third.TitleSlug = "other";
            var repository = new StubBookRepository(new[] { first, second, third });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));
            var titleSlugs = new BookTitleSlugIndex(author.Id, repository.GetBooksByAuthorId(author.Id));

            first.TitleSlug = "new";
            service.UpdateMany(new List<Book> { first }, titleSlugs);
            third.TitleSlug = "Same";
            service.UpdateMany(new List<Book> { third }, titleSlugs);

            Assert.That(third.TitleSlug, Is.EqualTo("same_2"));
            Assert.That(repository.AuthorCatalogLookupCount, Is.EqualTo(1));
        }

        [Test]
        public void bulk_monitoring_should_publish_individual_and_one_aggregate_edit_event()
        {
            var author = BuildAuthor(1, sync: false);
            var first = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var second = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { first, second });
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.SetMonitored(new[] { first.Id, second.Id }, true);

            var changes = events.Published.OfType<BookEditedEvent>().ToList();
            var aggregate = events.Published.OfType<BooksEditedEvent>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(changes, Has.Count.EqualTo(2));
                Assert.That(aggregate.Changes.Select(change => change.Book.Id), Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(events.Published.Last(), Is.SameAs(aggregate));
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void quiet_bulk_update_should_publish_only_aggregate_event_for_a_newly_monitored_book(BookMediaType mediaType)
        {
            var author = BuildAuthor(1, sync: false);
            var storedBook = BuildBook(10, author.Id, mediaType, "hc:work-1", monitored: false);
            var updatedBook = BuildBook(10, author.Id, mediaType, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { storedBook });
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.UpdateMany(new List<Book> { updatedBook });

            var aggregate = events.Published.OfType<BooksEditedEvent>().Single();
            var change = aggregate.Changes.Single();
            Assert.Multiple(() =>
            {
                Assert.That(change.Book.Id, Is.EqualTo(updatedBook.Id));
                Assert.That(change.OldBook.IsMonitored(), Is.False);
                Assert.That(change.Book.IsMonitored(), Is.True);
                Assert.That(events.Published.OfType<BookEditedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void quiet_bulk_update_should_publish_sync_sibling_with_its_stored_monitoring_snapshot()
        {
            var author = BuildAuthor(1);
            var storedAudiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var storedEbook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var updatedAudiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { storedAudiobook, storedEbook });
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.UpdateMany(new List<Book> { updatedAudiobook });

            var aggregate = events.Published.OfType<BooksEditedEvent>().Single();
            var audiobookChange = aggregate.Changes.Single(change => change.Book.Id == storedAudiobook.Id);
            var ebookChange = aggregate.Changes.Single(change => change.Book.Id == storedEbook.Id);
            Assert.Multiple(() =>
            {
                Assert.That(audiobookChange.OldBook.AudiobookMonitored, Is.False);
                Assert.That(audiobookChange.Book.AudiobookMonitored, Is.True);
                Assert.That(ebookChange.OldBook.EbookMonitored, Is.False);
                Assert.That(ebookChange.Book.EbookMonitored, Is.True);
                Assert.That(ebookChange.OldBook, Is.Not.SameAs(storedEbook));
                Assert.That(repository.Get(storedEbook.Id).EbookMonitored, Is.True);
                Assert.That(events.Published.OfType<BookEditedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void quiet_bulk_update_should_not_publish_cover_event_for_unchanged_or_unmonitored_rows()
        {
            var author = BuildAuthor(1, sync: false);
            var storedMonitored = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var storedUnmonitored = BuildBook(11, author.Id, BookMediaType.Audiobook, "hc:work-2", monitored: false);
            var updatedMonitored = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var unchangedUnmonitored = BuildBook(11, author.Id, BookMediaType.Audiobook, "hc:work-2", monitored: false);
            var repository = new StubBookRepository(new[] { storedMonitored, storedUnmonitored });
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.UpdateMany(new List<Book> { updatedMonitored, unchangedUnmonitored });

            Assert.Multiple(() =>
            {
                Assert.That(events.Published.OfType<BooksEditedEvent>(), Is.Empty);
                Assert.That(events.Published.OfType<BookEditedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void quiet_bulk_update_should_ignore_rows_missing_from_the_repository()
        {
            var author = BuildAuthor(1, sync: false);
            var storedBook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var updatedBook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var deletedBook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-2", monitored: true);
            var repository = new StubBookRepository(new[] { storedBook });
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            Assert.DoesNotThrow(() => service.UpdateMany(new List<Book> { updatedBook, deletedBook }));

            var aggregate = events.Published.OfType<BooksEditedEvent>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(aggregate.Changes.Select(change => change.Book.Id), Is.EqualTo(new[] { updatedBook.Id }));
                Assert.That(events.Published.OfType<BookEditedEvent>(), Is.Empty);
            });
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

        [TestCase("audiobook", false)]
        [TestCase("audiobook", true)]
        [TestCase("ebook", false)]
        [TestCase("ebook", true)]
        public void explicit_request_should_monitor_matching_sibling_even_when_target_was_already_monitored(string mediaType, bool alreadyMonitored)
        {
            var author = BuildAuthor(1, audiobookMonitored: false, ebookMonitored: false);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: mediaType == "audiobook" && alreadyMonitored);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: mediaType == "ebook" && alreadyMonitored);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.MonitorRequestedBooks(new[] { mediaType == "audiobook" ? audiobook.Id : ebook.Id }, mediaType);

            Assert.Multiple(() =>
            {
                Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.True);
                Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
            });
        }

        [Test]
        public void explicit_request_should_ignore_ids_from_the_other_media_type()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.MonitorRequestedBooks(new[] { ebook.Id }, "audiobook");

            Assert.That(repository.Get(audiobook.Id).AudiobookMonitored, Is.False);
            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void explicit_request_should_leave_sibling_unmonitored_when_sync_is_off()
        {
            var author = BuildAuthor(1, sync: false);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.MonitorRequestedBooks(new[] { audiobook.Id }, "audiobook");

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
            Assert.That(repository.UpdatedRowCount, Is.Zero);
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void unchanged_request_should_not_write_or_publish_but_still_repair_its_sibling(string mediaType)
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);
            var target = mediaType == "audiobook" ? audiobook : ebook;
            var sibling = mediaType == "audiobook" ? ebook : audiobook;

            service.MonitorRequestedBooks(new[] { target.Id }, mediaType);

            Assert.That(repository.UpdatedRowCount, Is.Zero);
            Assert.That(events.Published.OfType<BookEditedEvent>(), Is.Empty);
            sibling.SetMonitored(false);
            service.MonitorRequestedBooks(new[] { target.Id }, mediaType);
            Assert.That(repository.UpdatedRowCount, Is.EqualTo(1));
            Assert.That(sibling.IsMonitored(), Is.True);
            Assert.That(events.Published.OfType<BookEditedEvent>().Select(e => e.Book.Id), Is.EqualTo(new[] { sibling.Id }));
        }

        [Test]
        public void explicit_request_should_not_load_unrelated_work_backed_navigation_rows()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var unrelated = Enumerable.Range(0, 25)
                .Select(index => BuildBook(100 + index, author.Id, BookMediaType.Audiobook, $"hc:unrelated-{index}", monitored: false))
                .ToList();
            foreach (var book in unrelated)
            {
                book.LazyAuthor = new ThrowingLazyLoad<Author>();
                book.LazyEditions = new ThrowingLazyLoad<List<Edition>>();
            }

            var repository = new StubBookRepository(new[] { audiobook, ebook }.Concat(unrelated));
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.MonitorRequestedBooks(new[] { audiobook.Id }, "audiobook");

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
        }

        [Test]
        public void direct_add_of_existing_monitored_row_should_repair_sibling()
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var edition = new Edition { Id = 100, BookId = audiobook.Id, Title = audiobook.Title, Monitored = true, ForeignEditionId = "hc:edition:100" };
            audiobook.Editions = new List<Edition> { edition };
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }), editionService: new StubEditionService(new[] { edition }));

            service.AddBook(audiobook);

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
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

            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void initial_ebook_none_should_select_only_siblings_of_five_monitored_audiobooks(bool ebookAuthorGate)
        {
            var author = BuildAuthor(1, audiobookMonitored: true, ebookMonitored: ebookAuthorGate);
            var audiobooks = Enumerable.Range(1, 5)
                .Select(index => BuildBook(10 + index, author.Id, BookMediaType.Audiobook, $"hc:work-{index}", monitored: true))
                .ToList();
            var ebooks = Enumerable.Range(1, 8)
                .Select(index => BuildBook(100 + index, author.Id, BookMediaType.Ebook, $"hc:work-{index}", monitored: false))
                .ToList();
            var repository = new StubBookRepository(audiobooks.Concat(ebooks));
            var events = new StubEventAggregator();
            var rootFolders = new StubRootFolderService();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events, rootFolders);

            service.SetMonitoredForMediaType(ebooks.Select(book => book.Id), "ebook", false);
            Assert.That(ebooks.All(book => !repository.Get(book.Id).EbookMonitored), Is.True);
            events.Published.Clear();
            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

            Assert.Multiple(() =>
            {
                Assert.That(audiobooks.All(book => repository.Get(book.Id).AudiobookMonitored), Is.True);
                Assert.That(ebooks.Take(5).All(book => repository.Get(book.Id).EbookMonitored), Is.True);
                Assert.That(ebooks.Skip(5).All(book => !repository.Get(book.Id).EbookMonitored), Is.True);
                Assert.That(ebooks.All(book => book.AddOptions?.AddType != BookAddType.Manual), Is.True);
                Assert.That(author.AudiobookMonitored, Is.True);
                Assert.That(author.EbookMonitored, Is.EqualTo(ebookAuthorGate));
                Assert.That(repository.AuthorCatalogLookupCount, Is.EqualTo(1));
                Assert.That(rootFolders.AllCount, Is.EqualTo(1));
                Assert.That(events.Published.OfType<BookEditedEvent>().Select(item => item.Book.Id),
                    Is.EquivalentTo(ebooks.Take(5).Select(book => book.Id)));
            });
        }

        [Test]
        public void bulk_reconcile_should_batch_edition_fallback_for_workless_rows()
        {
            var author = BuildAuthor(1);
            var books = Enumerable.Range(1, 20)
                .Select(index => BuildBook(index, author.Id,
                    index % 2 == 0 ? BookMediaType.Audiobook : BookMediaType.Ebook,
                    workId: null, monitored: false))
                .ToList();
            foreach (var book in books)
            {
                book.LazyEditions = new ThrowingLazyLoad<List<Edition>>();
            }

            books[0].SetMonitored(true);
            var editions = books.Select(book => new Edition
            {
                BookId = book.Id,
                Asin = "B000SHARED"
            });
            var editionService = new StubEditionService(editions);
            var repository = new StubBookRepository(books);
            var service = BuildService(repository, new StubAuthorService(new[] { author }), editionService: editionService);

            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

            Assert.Multiple(() =>
            {
                Assert.That(editionService.RowOnlyLookupCount, Is.EqualTo(1));
                Assert.That(editionService.BulkBookLookupCount, Is.Zero);
                Assert.That(editionService.SingleBookLookupCount, Is.Zero);
                Assert.That(books.Count(book => book.IsMonitored()), Is.EqualTo(1));
            });
        }

        [Test]
        public void bulk_reconcile_with_no_selected_rows_should_skip_identity_and_root_lookups()
        {
            var author = BuildAuthor(1);
            var books = Enumerable.Range(1, 8)
                .Select(index => BuildBook(index, author.Id, BookMediaType.Ebook, workId: null, monitored: false))
                .ToList();
            foreach (var book in books)
            {
                book.LazyEditions = new ThrowingLazyLoad<List<Edition>>();
            }

            var repository = new StubBookRepository(books);
            var editionService = new StubEditionService();
            var rootFolders = new StubRootFolderService();
            var service = BuildService(repository, new StubAuthorService(new[] { author }),
                rootFolderService: rootFolders, editionService: editionService);

            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

            Assert.Multiple(() =>
            {
                Assert.That(repository.AuthorCatalogLookupCount, Is.EqualTo(1));
                Assert.That(repository.UpdatedRowCount, Is.Zero);
                Assert.That(editionService.RowOnlyLookupCount, Is.Zero);
                Assert.That(editionService.BulkBookLookupCount, Is.Zero);
                Assert.That(rootFolders.AllCount, Is.Zero);
            });
        }

        [Test]
        public void initial_none_should_not_write_rows_that_are_already_unmonitored()
        {
            var author = BuildAuthor(1);
            var ebooks = Enumerable.Range(1, 8)
                .Select(index => BuildBook(index, author.Id, BookMediaType.Ebook, $"hc:work-{index}", monitored: false))
                .ToList();
            var repository = new StubBookRepository(ebooks);
            var events = new StubEventAggregator();
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.SetMonitoredForMediaType(ebooks.Select(book => book.Id), "ebook", false);

            Assert.Multiple(() =>
            {
                Assert.That(repository.UpdatedRowCount, Is.Zero);
                Assert.That(repository.AuthorCatalogLookupCount, Is.Zero);
                Assert.That(events.Published, Is.Empty);
            });
        }

        [Test]
        public void initial_none_should_still_clear_an_invalid_opposite_row_flag()
        {
            var author = BuildAuthor(1);
            var ebook = BuildBook(1, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            ebook.AudiobookMonitored = true;
            var repository = new StubBookRepository(new[] { ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SetMonitoredForMediaType(new[] { ebook.Id }, "ebook", false);

            Assert.That(repository.UpdatedRowCount, Is.EqualTo(1));
            Assert.That(repository.Get(ebook.Id).AudiobookMonitored, Is.False);
        }

        [Test]
        public void bulk_reconcile_should_not_enable_missing_sibling_when_sync_is_off()
        {
            var author = BuildAuthor(1, sync: false, ebookMonitored: false);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

            Assert.That(repository.Get(ebook.Id).EbookMonitored, Is.False);
        }

        [Test]
        public void set_book_monitored_should_not_enable_sibling_when_sync_is_off()
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

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_reconcile_should_ignore_monitored_unit_clone_when_syncing_opposite_format(bool wantedNarration)
        {
            var author = BuildAuthor(1);
            var audiobook = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: false);
            var audiobookClone = BuildBook(12, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            audiobookClone.UnitKeyHash = wantedNarration ? null : "unit-copy";
            audiobookClone.TitleSlug = wantedNarration ? "book_wanted_12" : "book_copy_12";

            var ebook = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-1", monitored: false);
            var repository = new StubBookRepository(new[] { audiobook, audiobookClone, ebook });
            var service = BuildService(repository, new StubAuthorService(new[] { author }));

            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

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

            service.SyncMonitoredAcrossFormats(new List<int> { author.Id });

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
        public void delete_book_should_publish_after_parent_row_is_deleted()
        {
            var author = BuildAuthor(1);
            var book = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var repository = new StubBookRepository(new[] { book });
            var events = new StubEventAggregator();
            events.OnPublish = published =>
            {
                if (published is BookDeletedEvent deleted)
                {
                    Assert.That(repository.Get(deleted.Book.Id), Is.Null);
                }
            };
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.DeleteBook(book.Id, deleteFiles: false);

            Assert.That(repository.Get(book.Id), Is.Null);
        }

        [Test]
        public void delete_many_should_publish_all_events_after_deleting_parent_rows()
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
                    Assert.That(repository.Get(first.Id), Is.Null);
                    Assert.That(repository.Get(second.Id), Is.Null);
                }
            };
            var service = BuildService(repository, new StubAuthorService(new[] { author }), events);

            service.DeleteMany(new List<Book> { first, second });

            Assert.That(repository.Get(first.Id), Is.Null);
            Assert.That(repository.Get(second.Id), Is.Null);
            Assert.That(events.Published.OfType<BookDeletedEvent>().Count(), Is.EqualTo(2));
        }

        [Test]
        public void delete_many_should_bulk_hydrate_author_and_editions_before_publishing_events()
        {
            var author = BuildAuthor(1);
            var first = BuildBook(10, author.Id, BookMediaType.Audiobook, "hc:work-1", monitored: true);
            var second = BuildBook(11, author.Id, BookMediaType.Ebook, "hc:work-2", monitored: true);
            first.Author = null;
            first.AuthorId = author.Id;
            second.Author = null;
            second.AuthorId = author.Id;

            var firstEdition = new Edition { Id = 100, BookId = first.Id, Title = "First edition" };
            var secondEdition = new Edition { Id = 101, BookId = second.Id, Title = "Second edition" };
            var editions = new StubEditionService(new[] { firstEdition, secondEdition });
            var authors = new StubAuthorService(new[] { author });
            var repository = new StubBookRepository(new[] { first, second });
            var events = new StubEventAggregator();
            var service = BuildService(repository, authors, events, editionService: editions);

            service.DeleteMany(new List<Book> { first, second });

            var deleted = events.Published.OfType<BookDeletedEvent>().ToList();
            Assert.Multiple(() =>
            {
                Assert.That(authors.GetAuthorCallCount, Is.EqualTo(1));
                Assert.That(editions.BulkBookLookupCount, Is.EqualTo(1));
                Assert.That(editions.SingleBookLookupCount, Is.Zero);
                Assert.That(deleted.Single(item => item.Book.Id == first.Id).Book.Editions, Is.EqualTo(new[] { firstEdition }));
                Assert.That(deleted.Single(item => item.Book.Id == second.Id).Book.Editions, Is.EqualTo(new[] { secondEdition }));
            });
        }
    }
}
