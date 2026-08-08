using System;
using System.Collections.Generic;
using System.Data;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookNarratorServiceHydrationFixture
    {
        private sealed class StubBookRepository : IBookRepository
        {
            private readonly Book _rawBook;

            public StubBookRepository(Book rawBook)
            {
                _rawBook = rawBook;
            }

            public Book Get(int id) => _rawBook;
            public IEnumerable<Book> All() => new[] { _rawBook };

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
            public Book FindByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
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
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => throw new NotImplementedException();
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
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => new List<Author> { _author };

            public Author GetAuthor(int authorId) => _author;
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

        private sealed class StubNarratorOptionRepository : IBookNarratorOptionRepository
        {
            public List<BookNarratorOption> GetByBookId(int bookId) => new List<BookNarratorOption>();
            public List<BookNarratorOption> GetPreferredByBookId(int bookId) => new List<BookNarratorOption>();
            public BookNarratorOption FindByBookIdAndNarrator(int bookId, string narrator) => null;

            public void DeleteByBookId(int bookId) => throw new NotImplementedException();
            public void SetPreferred(int bookId, string narrator) => throw new NotImplementedException();
            public void ClearPreferred(int bookId) => throw new NotImplementedException();
            public IEnumerable<BookNarratorOption> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public BookNarratorOption Find(int id) => throw new NotImplementedException();
            public BookNarratorOption Get(int id) => throw new NotImplementedException();
            public BookNarratorOption Insert(BookNarratorOption model) => throw new NotImplementedException();
            public BookNarratorOption Update(BookNarratorOption model) => throw new NotImplementedException();
            public BookNarratorOption Upsert(BookNarratorOption model) => throw new NotImplementedException();
            public void SetFields(BookNarratorOption model, params System.Linq.Expressions.Expression<Func<BookNarratorOption, object>>[] properties) => throw new NotImplementedException();
            public void Delete(BookNarratorOption model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<BookNarratorOption> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public void InsertMany(IList<BookNarratorOption> model) => throw new NotImplementedException();
            public void InsertMany(IList<BookNarratorOption> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<BookNarratorOption> model) => throw new NotImplementedException();
            public void SetFields(IList<BookNarratorOption> models, params System.Linq.Expressions.Expression<Func<BookNarratorOption, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<BookNarratorOption> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public BookNarratorOption Single() => throw new NotImplementedException();
            public BookNarratorOption SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<BookNarratorOption> GetPaged(PagingSpec<BookNarratorOption> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class StubSeriesRepository : ISeriesRepository
        {
            public Series Get(int id) => null;
            public Series FindById(string providerIdOrLocalId) => throw new NotImplementedException();
            public Series FindById(string providerIdOrLocalId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Series> FindById(List<string> providerIds) => throw new NotImplementedException();
            public List<Series> FindById(List<string> providerIds, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Series> GetByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Series> GetAllSeriesWithoutBooks() => throw new NotImplementedException();
            public IEnumerable<Series> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Series Find(int id) => throw new NotImplementedException();
            public Series Insert(Series model) => throw new NotImplementedException();
            public Series Update(Series model) => throw new NotImplementedException();
            public Series Upsert(Series model) => throw new NotImplementedException();
            public void SetFields(Series model, params System.Linq.Expressions.Expression<Func<Series, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Series model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<Series> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public void InsertMany(IList<Series> model) => throw new NotImplementedException();
            public void InsertMany(IList<Series> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Series> model) => throw new NotImplementedException();
            public void SetFields(IList<Series> models, params System.Linq.Expressions.Expression<Func<Series, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Series> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Series Single() => throw new NotImplementedException();
            public Series SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Series> GetPaged(PagingSpec<Series> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class RecordingNarratorSearchService : INarratorSearchService
        {
            public readonly List<(string AuthorName, string BookTitle)> Calls = new();

            public List<string> SearchForNarrators(string authorName, string bookTitle, bool useCache = true)
            {
                Calls.Add((authorName, bookTitle));
                return new List<string>();
            }

            public List<string> SearchForNarratorsByAsin(string asin, bool useCache = true) => new List<string>();
            public List<string> GetStoredNarrators(int bookId) => new List<string>();
            public void StoreNarrators(int bookId, List<string> narrators, string source = "search") { }
            public void StoreNarratorOptions(int bookId, List<string> narrators, string source) { }
            public void FetchNarratorPhotosForBook(int bookId, string authorName, string bookTitle) { }
        }

        [Test]
        public void discover_narrators_for_all_copies_should_use_hydrated_author_name()
        {
            var rawBook = new Book
            {
                Id = 42,
                Title = "Infinite Shores",
                HardcoverBookId = "hc:123",
                MediaType = BookMediaType.Audiobook,
                AuthorId = 1189
            };

            var author = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle"
            };

            var narratorSearch = new RecordingNarratorSearchService();
            var subject = new BookNarratorService(
                new StubNarratorOptionRepository(),
                new StubBookRepository(rawBook),
                new StubSeriesRepository(),
                narratorSearch,
                new StubAuthorService(author),
                LogManager.GetCurrentClassLogger());

            subject.DiscoverNarratorsForAllCopies(42);

            Assert.That(narratorSearch.Calls.Count, Is.EqualTo(1));
            Assert.That(narratorSearch.Calls[0].AuthorName, Is.EqualTo("Pascale Lacelle"));
            Assert.That(narratorSearch.Calls[0].BookTitle, Is.EqualTo("Infinite Shores"));
        }
    }
}
