using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceResyncDenormalizedSeriesFieldsFixture
    {
        private const int AuthorId = 7;

        [Test]
        public void should_rewrite_stale_series_fields_from_the_links()
        {
            var book = new Book
            {
                Id = 1,
                AuthorId = AuthorId,
                Title = "Dust of Dreams",
                SeriesName = "La caduta di Malazan",
                SeriesPosition = "28"
            };

            var links = new List<SeriesBookLink>
            {
                Link(book.Id, 3447, "Malazan Book of the Fallen", "9")
            };

            var repo = new StubBookRepository(book);
            var sut = BuildService(repo, new StubSeriesBookLinkRepository(links));

            var repaired = sut.ResyncDenormalizedSeriesFields(AuthorId);

            Assert.Multiple(() =>
            {
                Assert.That(repaired, Is.EqualTo(1));
                Assert.That(book.SeriesName, Is.EqualTo("Malazan Book of the Fallen"));
                Assert.That(book.SeriesPosition, Is.EqualTo("9"));
                Assert.That(repo.UpdatedBookIds, Is.EquivalentTo(new[] { book.Id }));
            });
        }

        [Test]
        public void should_leave_books_without_links_untouched()
        {
            var book = new Book
            {
                Id = 2,
                AuthorId = AuthorId,
                Title = "Wuthering Heights",
                SeriesName = "Jardín Secreto",
                SeriesPosition = "1"
            };

            var repo = new StubBookRepository(book);
            var sut = BuildService(repo, new StubSeriesBookLinkRepository(new List<SeriesBookLink>()));

            var repaired = sut.ResyncDenormalizedSeriesFields(AuthorId);

            Assert.Multiple(() =>
            {
                Assert.That(repaired, Is.Zero);
                Assert.That(book.SeriesName, Is.EqualTo("Jardín Secreto"));
                Assert.That(repo.UpdatedBookIds, Is.Empty);
            });
        }

        [Test]
        public void should_not_write_when_the_stored_pair_already_matches()
        {
            var book = new Book
            {
                Id = 3,
                AuthorId = AuthorId,
                Title = "Gardens of the Moon",
                SeriesName = "Malazan Book of the Fallen",
                SeriesPosition = "1"
            };

            var links = new List<SeriesBookLink>
            {
                Link(book.Id, 3447, "Malazan Book of the Fallen", "1")
            };

            var repo = new StubBookRepository(book);
            var sut = BuildService(repo, new StubSeriesBookLinkRepository(links));

            Assert.Multiple(() =>
            {
                Assert.That(sut.ResyncDenormalizedSeriesFields(AuthorId), Is.Zero);
                Assert.That(repo.UpdatedBookIds, Is.Empty);
            });
        }

        private static SeriesBookLink Link(int bookId, int seriesId, string title, string position)
        {
            return new SeriesBookLink
            {
                BookId = bookId,
                SeriesId = seriesId,
                Position = position,
                SeriesPosition = int.TryParse(position, out var parsed) ? parsed : 0,
                IsPrimary = true,
                Series = new Series { Id = seriesId, Title = title, PrimaryWorkCount = 10 }
            };
        }

        private static BookService BuildService(StubBookRepository bookRepository, StubSeriesBookLinkRepository linkRepository)
        {
            return new BookService(
                bookRepository,
                editionService: null,
                eventAggregator: null,
                authorService: null,
                mediaFileService: null,
                rootFolderService: null,
                seriesBookLinkRepository: linkRepository,
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        private sealed class StubBookRepository : IBookRepository
        {
            private readonly List<Book> _books;

            public StubBookRepository(params Book[] books)
            {
                _books = (books ?? Array.Empty<Book>()).Where(b => b != null).ToList();
            }

            public List<int> UpdatedBookIds { get; } = new List<int>();

            public List<Book> GetBooksByAuthorId(int authorId) => _books.Where(b => b.AuthorId == authorId).ToList();

            public void SetFields(IList<Book> models, params Expression<Func<Book, object>>[] properties)
            {
                UpdatedBookIds.AddRange(models.Select(b => b.Id));
            }

            public List<Book> GetBooks(int authorId) => GetBooksByAuthorId(authorId);

            public IEnumerable<Book> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => throw new NotImplementedException();
            public Book Get(int id) => throw new NotImplementedException();
            public IEnumerable<Book> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Update(Book model) => throw new NotImplementedException();
            public Book Upsert(Book model) => throw new NotImplementedException();
            public void SetFields(Book model, params Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void InsertMany(IList<Book> model) => throw new NotImplementedException();
            public void InsertMany(IList<Book> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Book> model) => throw new NotImplementedException();
            public void DeleteMany(List<Book> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Book Single() => throw new NotImplementedException();
            public Book SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Book> GetPaged(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> GetLastBooks(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetNextBooks(IEnumerable<int> authorIds) => throw new NotImplementedException();
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

        private sealed class StubSeriesBookLinkRepository : ISeriesBookLinkRepository
        {
            private readonly List<SeriesBookLink> _links;

            public StubSeriesBookLinkRepository(List<SeriesBookLink> links)
            {
                _links = links ?? new List<SeriesBookLink>();
            }

            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds)
            {
                var ids = bookIds?.ToHashSet() ?? new HashSet<int>();
                return _links.Where(l => ids.Contains(l.BookId)).ToList();
            }

            public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId) => new HashSet<int>();

            public IEnumerable<SeriesBookLink> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public SeriesBookLink Find(int id) => throw new NotImplementedException();
            public SeriesBookLink Get(int id) => throw new NotImplementedException();
            public IEnumerable<SeriesBookLink> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public SeriesBookLink Insert(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Update(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Upsert(SeriesBookLink model) => throw new NotImplementedException();
            public void SetFields(SeriesBookLink model, params Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void SetFields(IList<SeriesBookLink> models, params Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void Delete(SeriesBookLink model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
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
    }
}
