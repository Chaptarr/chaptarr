using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceProviderIdLookupFixture
    {
        private sealed class StubBookRepository : IBookRepository
        {
            private readonly Dictionary<(string provider, string providerId, BookMediaType mediaType), Book> _matches = new();
            private readonly Dictionary<int, Book> _books = new();

            public List<(string provider, string providerId, BookMediaType mediaType)> FindByProviderIdCalls { get; } = new();

            public void AddMatch(string provider, string providerId, BookMediaType mediaType, Book book)
            {
                _matches[(provider, providerId, mediaType)] = book;
                AddBook(book);
            }

            public void AddBook(Book book)
            {
                if (book?.Id > 0)
                {
                    _books[book.Id] = book;
                }
            }

            public List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType)
            {
                FindByProviderIdCalls.Add((provider, providerId, mediaType));

                return _matches.TryGetValue((provider, providerId, mediaType), out var book) && book != null
                    ? new List<Book> { book }
                    : new List<Book>();
            }

            public Book FindByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType)
            {
                FindByProviderIdCalls.Add((provider, providerId, mediaType));

                return _matches.TryGetValue((provider, providerId, mediaType), out var book) ? book : null;
            }

            public IEnumerable<Book> All() => throw new AssertionException("BookService provider-id lookup must not call IBookRepository.All()");

            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => throw new NotImplementedException();
            public Book Get(int id) => throw new NotImplementedException();
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Update(Book model) => throw new NotImplementedException();
            public Book Upsert(Book model) => throw new NotImplementedException();
            public void SetFields(Book model, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<Book> Get(IEnumerable<int> ids) => ids.Select(id => _books.TryGetValue(id, out var book) ? book : null).Where(book => book != null).ToList();
            public void InsertMany(IList<Book> model) => throw new NotImplementedException();
            public void InsertMany(IList<Book> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Book> model) => throw new NotImplementedException();
            public void SetFields(IList<Book> models, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Book> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Book Single() => throw new NotImplementedException();
            public Book SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Book> GetPaged(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();

            public List<Book> GetBooks(int authorId) => throw new NotImplementedException();
            public List<Book> GetLastBooks(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetNextBooks(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
	            public List<Book> GetBooksForRefresh(int authorId, IEnumerable<string> providerIds) => throw new NotImplementedException();
	            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
	            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
	            public Book FindByIsbn(string isbn) => throw new NotImplementedException();
	            public Book FindByAsin(string asin) => throw new NotImplementedException();
	            public Book FindByProviderIds(string hardcoverBookId = null, string goodreadsBookId = null, string openLibraryWorkId = null) => throw new NotImplementedException();
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
            private readonly Dictionary<(string provider, string providerId), List<Edition>> _providerMatches = new();

            public void AddProviderMatch(string provider, string providerId, params Edition[] editions)
            {
                _providerMatches[(provider, providerId)] = editions?.ToList() ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => new List<Edition>();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => GetEditionsByProviderAndId(providerPrefix, providerId).FirstOrDefault();
            public List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => _providerMatches.TryGetValue((providerPrefix, providerId), out var editions) ? editions : new List<Edition>();

            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubProviderAliasService : IProviderAliasService
        {
            private readonly Dictionary<string, List<int>> _idsByScope = new(StringComparer.OrdinalIgnoreCase);

            public List<string> RequestedScopes { get; } = new();

            public void Set(string scope, params int[] ids)
            {
                _idsByScope[scope] = ids?.ToList() ?? new List<int>();
            }

            public void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<string> providerIds) => throw new NotImplementedException();
            public void DeleteAliases(string entityType, int entityId) => throw new NotImplementedException();
            public List<int> FindBookIds(string scope, IEnumerable<string> providerIds)
            {
                RequestedScopes.Add(scope);
                return _idsByScope.TryGetValue(scope, out var ids) ? ids.ToList() : new List<int>();
            }

            public List<int> FindAuthorIds(IEnumerable<string> providerIds) => throw new NotImplementedException();
            public List<(string Provider, string NormalizedProviderId)> NormalizeProviderIds(IEnumerable<string> providerIds) => throw new NotImplementedException();
        }

        [Test]
        public void should_query_canonical_provider_id_for_media_type_lookup()
        {
            var repo = new StubBookRepository();
            repo.AddMatch("hc", "hc:123", BookMediaType.Audiobook, new Book { Id = 1, MediaType = BookMediaType.Audiobook });

            var service = new BookService(repo, editionService: new StubEditionService(), eventAggregator: null, authorService: null,
                mediaFileService: null, rootFolderService: null, seriesBookLinkRepository: null,
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());

            var book = service.FindByProviderId("hc", "hc:123", BookMediaType.Audiobook);

            Assert.That(book, Is.Not.Null);
            Assert.That(book.Id, Is.EqualTo(1));
            Assert.That(repo.FindByProviderIdCalls.Select(c => c.providerId).ToList(), Is.EqualTo(new List<string> { "hc:123" }));
        }

        [Test]
        public void should_query_both_media_types_and_choose_lowest_id()
        {
            var repo = new StubBookRepository();
            repo.AddMatch("gr", "gr:42", BookMediaType.Audiobook, new Book { Id = 10, MediaType = BookMediaType.Audiobook });
            repo.AddMatch("gr", "gr:42", BookMediaType.Ebook, new Book { Id = 5, MediaType = BookMediaType.Ebook });

            var service = new BookService(repo, editionService: new StubEditionService(), eventAggregator: null, authorService: null,
                mediaFileService: null, rootFolderService: null, seriesBookLinkRepository: null,
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());

            var book = service.FindByProviderId("gr", "42");

            Assert.That(book, Is.Not.Null);
            Assert.That(book.Id, Is.EqualTo(5));
            Assert.That(repo.FindByProviderIdCalls.Count, Is.EqualTo(2));
            Assert.That(repo.FindByProviderIdCalls.Select(c => c.mediaType).ToList(), Is.EqualTo(new List<BookMediaType> { BookMediaType.Audiobook, BookMediaType.Ebook }));
            Assert.That(repo.FindByProviderIdCalls.Select(c => c.providerId).ToList(), Is.EqualTo(new List<string> { "gr:42", "gr:42" }));
        }
        [Test]
        public void should_return_all_media_scoped_books_for_duplicate_edition_provider_id()
        {
            var repo = new StubBookRepository();
            repo.AddBook(new Book { Id = 10, MediaType = BookMediaType.Audiobook });
            repo.AddBook(new Book { Id = 12, MediaType = BookMediaType.Audiobook });
            repo.AddBook(new Book { Id = 20, MediaType = BookMediaType.Ebook });

            var editionService = new StubEditionService();
            editionService.AddProviderMatch("gr", "123",
                new Edition { Id = 1, BookId = 10 },
                new Edition { Id = 2, BookId = 12 },
                new Edition { Id = 3, BookId = 20 });

            var service = new BookService(repo, editionService: editionService, eventAggregator: null, authorService: null,
                mediaFileService: null, rootFolderService: null, seriesBookLinkRepository: null,
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());

            var books = service.FindAllByProviderId("gr", "123", BookMediaType.Audiobook);

            Assert.That(books.Select(book => book.Id).ToList(), Is.EqualTo(new List<int> { 10, 12 }));
        }

        [Test]
        public void should_query_isbn_book_columns_through_media_scoped_plural_lookup()
        {
            var repo = new StubBookRepository();
            repo.AddMatch("isbn", "9780123456789", BookMediaType.Ebook, new Book { Id = 7, MediaType = BookMediaType.Ebook });

            var service = new BookService(repo, editionService: new StubEditionService(), eventAggregator: null, authorService: null,
                mediaFileService: null, rootFolderService: null, seriesBookLinkRepository: null,
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());

            var books = service.FindAllByProviderId("isbn", "isbn:978-0-123456-78-9", BookMediaType.Ebook);

            Assert.That(books.Select(book => book.Id).ToList(), Is.EqualTo(new List<int> { 7 }));
            Assert.That(repo.FindByProviderIdCalls.Select(c => c.provider).ToList(), Is.EqualTo(new List<string> { "isbn" }));
            Assert.That(repo.FindByProviderIdCalls.Select(c => c.providerId).ToList(), Is.EqualTo(new List<string> { "9780123456789" }));
            Assert.That(repo.FindByProviderIdCalls.Select(c => c.mediaType).ToList(), Is.EqualTo(new List<BookMediaType> { BookMediaType.Ebook }));
        }

        [Test]
        public void work_lookup_should_consume_only_work_aliases_and_never_edition_aliases()
        {
            var repo = new StubBookRepository();
            repo.AddBook(new Book { Id = 10, MediaType = BookMediaType.Audiobook });
            repo.AddBook(new Book { Id = 11, MediaType = BookMediaType.Audiobook });
            var aliases = new StubProviderAliasService();
            aliases.Set("work", 10);
            aliases.Set("edition", 11);
            var service = new BookService(
                repo,
                editionService: new StubEditionService(),
                eventAggregator: null,
                authorService: null,
                mediaFileService: null,
                rootFolderService: null,
                seriesBookLinkRepository: null,
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger(),
                providerAliasService: aliases);

            var books = service.FindAllByWorkProviderId("hc", "hc:1987747", BookMediaType.Audiobook);

            Assert.Multiple(() =>
            {
                Assert.That(books.Select(book => book.Id), Is.EqualTo(new[] { 10 }));
                Assert.That(aliases.RequestedScopes, Is.EqualTo(new[] { "work" }));
            });
        }

    }
}
