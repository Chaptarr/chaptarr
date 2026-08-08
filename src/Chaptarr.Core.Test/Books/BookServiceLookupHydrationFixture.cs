using System;
using System.Collections.Generic;
using System.Data;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceLookupHydrationFixture
    {
        private sealed class StubBookRepository : IBookRepository
        {
            private readonly Book _bareBook;
            private readonly List<Book> _booksByAuthor;

            public StubBookRepository(Book bareBook, List<Book> booksByAuthor = null)
            {
                _bareBook = bareBook;
                _booksByAuthor = booksByAuthor ?? new List<Book> { bareBook };
            }

            public Book FindBySlug(string titleSlug) => _bareBook;
            public Book FindByTitle(int authorId, string title) => _bareBook;
            public Book FindByIsbn(string isbn) => _bareBook;
            public Book FindByAsin(string asin) => _bareBook;
            public Book FindByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => _bareBook;
            public List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => new List<Book> { _bareBook };

            public IEnumerable<Book> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => throw new NotImplementedException();
            public Book Get(int id) => throw new NotImplementedException();
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Update(Book model) => throw new NotImplementedException();
            public Book Upsert(Book model) => throw new NotImplementedException();
            public void SetFields(Book model, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<Book> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public void InsertMany(IList<Book> model) => throw new NotImplementedException();
            public void InsertMany(IList<Book> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
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
            public List<Book> GetBooksByAuthorId(int authorId) => _booksByAuthor;
            public List<Book> GetBooksForRefresh(int authorId, IEnumerable<string> providerIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book FindByProviderIds(string hardcoverBookId = null, string goodreadsBookId = null, string openLibraryWorkId = null) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec, List<NzbDrone.Core.Qualities.QualitiesBelowCutoff> qualitiesBelowCutoff) => throw new NotImplementedException();
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

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId) => _author;

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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => null;
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => new List<Edition>();

            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
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

        private sealed class StubSeriesBookLinkRepository : ISeriesBookLinkRepository
        {
            public List<SeriesBookLink> GetLinksBySeries(int seriesId) => new List<SeriesBookLink>();
            public List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId) => new List<SeriesBookLink>();
            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds) => new List<SeriesBookLink>();
            public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId) => new HashSet<int>();
            public IEnumerable<SeriesBookLink> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public SeriesBookLink Find(int id) => throw new NotImplementedException();
            public SeriesBookLink Get(int id) => throw new NotImplementedException();
            public IEnumerable<SeriesBookLink> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public SeriesBookLink Insert(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Update(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Upsert(SeriesBookLink model) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void SetFields(SeriesBookLink model, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void SetFields(IList<SeriesBookLink> models, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void Delete(SeriesBookLink model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void DeleteMany(List<SeriesBookLink> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public SeriesBookLink Single() => throw new NotImplementedException();
            public SeriesBookLink SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<SeriesBookLink> GetPaged(PagingSpec<SeriesBookLink> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
            }
        }

        private static BookService CreateSubject(IBookRepository repository, IAuthorService authorService)
        {
            return new BookService(
                repository,
                editionService: new StubEditionService(),
                eventAggregator: new StubEventAggregator(),
                authorService: authorService,
                mediaFileService: null,
                rootFolderService: null,
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void find_by_provider_id_should_return_book_with_author_loaded()
        {
            var bareBook = new Book
            {
                Id = 42,
                Title = "Infinite Shores",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:123",
                AuthorId = 1189
            };

            var author = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle"
            };

            var subject = CreateSubject(new StubBookRepository(bareBook), new StubAuthorService(author));

            var result = subject.FindByProviderId("hc", "123", BookMediaType.Audiobook);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Author, Is.SameAs(author));
            Assert.That(result.Author.Name, Is.EqualTo("Pascale Lacelle"));
        }

        [Test]
        public void find_by_isbn_should_return_book_with_author_loaded()
        {
            var bareBook = new Book
            {
                Id = 43,
                Title = "Infinite Shores",
                MediaType = BookMediaType.Ebook,
                ISBN13 = "9781234567890",
                AuthorId = 1189
            };

            var author = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle"
            };

            var subject = CreateSubject(new StubBookRepository(bareBook), new StubAuthorService(author));

            var result = subject.FindByISBN("9781234567890");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Author, Is.SameAs(author));
            Assert.That(result.Author.Name, Is.EqualTo("Pascale Lacelle"));
        }

        [Test]
        public void find_by_title_inexact_should_return_book_with_author_loaded()
        {
            var bareBook = new Book
            {
                Id = 44,
                Title = "Infinite Shores",
                CleanTitle = "infiniteshores",
                MediaType = BookMediaType.Audiobook,
                AuthorId = 1189
            };

            var author = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle"
            };

            var subject = CreateSubject(new StubBookRepository(bareBook, new List<Book> { bareBook }), new StubAuthorService(author));

            var result = subject.FindByTitleInexact(1189, "Infinite Shores");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Author, Is.SameAs(author));
            Assert.That(result.Author.Name, Is.EqualTo("Pascale Lacelle"));
        }

        [Test]
        public void find_by_title_inexact_should_match_containment_titles()
        {
            var bareBook = new Book
            {
                Id = 45,
                Title = "Mistborn: The Final Empire",
                CleanTitle = "mistbornthefinalempire",
                MediaType = BookMediaType.Audiobook,
                AuthorId = 1189
            };

            var author = new Author
            {
                Id = 1189,
                Name = "Brandon Sanderson"
            };

            var subject = CreateSubject(new StubBookRepository(bareBook, new List<Book> { bareBook }), new StubAuthorService(author));

            var result = subject.FindByTitleInexact(1189, "Mistborn");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Title, Is.EqualTo("Mistborn: The Final Empire"));
            Assert.That(result.Author, Is.SameAs(author));
        }

        [Test]
        public void find_by_title_inexact_should_match_main_title_without_series_suffix()
        {
            var bareBook = new Book
            {
                Id = 46,
                Title = "The Blade Itself Book 1",
                CleanTitle = "thebladeitselfbook1",
                MediaType = BookMediaType.Audiobook,
                AuthorId = 1189
            };

            var author = new Author
            {
                Id = 1189,
                Name = "Joe Abercrombie"
            };

            var subject = CreateSubject(new StubBookRepository(bareBook, new List<Book> { bareBook }), new StubAuthorService(author));

            var result = subject.FindByTitleInexact(1189, "The Blade Itself");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Title, Is.EqualTo("The Blade Itself Book 1"));
            Assert.That(result.Author, Is.SameAs(author));
        }
    }
}
