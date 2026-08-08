using System;
using System.Collections.Generic;
using System.Data;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceWantedNarratorSeriesLinksFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authorsById;

            public StubAuthorService(params Author[] authors)
            {
                _authorsById = (authors ?? Array.Empty<Author>()).Where(a => a != null).ToDictionary(a => a.Id);
            }

            public Author GetAuthor(int authorId) => _authorsById.TryGetValue(authorId, out var a) ? a : null;

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

        private sealed class StubMediaFileService : IMediaFileService
        {
            public Dictionary<int, List<BookFile>> FilesByBookId { get; } = new Dictionary<int, List<BookFile>>();

            public List<BookFile> GetFilesByEdition(int editionId) => new List<BookFile>();

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => FilesByBookId.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => bookIds.SelectMany(id => FilesByBookId.TryGetValue(id, out var files) ? files : new List<BookFile>()).ToList();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class InMemoryEditionService : IEditionService
        {
            private readonly Dictionary<int, List<Edition>> _editionsByBookId = new Dictionary<int, List<Edition>>();
            private int _nextId = 1000;

            public void Seed(int bookId, params Edition[] editions)
            {
                _editionsByBookId[bookId] = editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByBook(int bookId)
            {
                return _editionsByBookId.TryGetValue(bookId, out var editions) ? editions.ToList() : new List<Edition>();
            }

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                var result = new List<Edition>();
                foreach (var bookId in bookIds ?? Array.Empty<int>())
                {
                    result.AddRange(GetEditionsByBook(bookId));
                }
                return result;
            }

            public void InsertMany(List<Edition> editions)
            {
                foreach (var edition in editions ?? new List<Edition>())
                {
                    if (edition.Id <= 0)
                    {
                        edition.Id = _nextId++;
                    }

                    if (!_editionsByBookId.TryGetValue(edition.BookId, out var list))
                    {
                        list = new List<Edition>();
                        _editionsByBookId[edition.BookId] = list;
                    }

                    list.Add(edition);
                }
            }

            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
            {
                return new List<Edition>();
            }

            public Edition GetEdition(int id) => _editionsByBookId.Values.SelectMany(e => e).FirstOrDefault(e => e.Id == id);
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
        }

        private sealed class InMemoryBookRepository : IBookRepository
        {
            private readonly Dictionary<int, Book> _booksById = new Dictionary<int, Book>();
            private int _nextId = 1000;

            public InMemoryBookRepository(params Book[] seed)
            {
                foreach (var b in seed ?? Array.Empty<Book>())
                {
                    if (b == null) continue;
                    _booksById[b.Id] = b;
                }
            }

            public Book Get(int id) => _booksById.TryGetValue(id, out var book) ? book : null;

            public IEnumerable<Book> Get(IEnumerable<int> ids)
            {
                return (ids ?? Array.Empty<int>())
                    .Select(Get)
                    .Where(b => b != null)
                    .ToList();
            }

            public Book Upsert(Book model)
            {
                if (model.Id <= 0)
                {
                    model.Id = _nextId++;
                }

                _booksById[model.Id] = model;
                return model;
            }

            public List<Book> GetBooksByAuthorId(int authorId) => _booksById.Values.Where(b => b.AuthorId == authorId).ToList();
            public List<Book> GetBooks(int authorId) => GetBooksByAuthorId(authorId);

            public IEnumerable<Book> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => throw new NotImplementedException();
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Update(Book model) => throw new NotImplementedException();
            public void SetFields(Book model, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
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
            public List<SeriesBookLink> BaseLinks { get; set; } = new List<SeriesBookLink>();
            public List<SeriesBookLink> Inserted { get; } = new List<SeriesBookLink>();

            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds)
            {
                var idSet = bookIds?.ToHashSet() ?? new HashSet<int>();
                return BaseLinks.Where(l => idSet.Contains(l.BookId)).ToList();
            }

            public void InsertMany(IList<SeriesBookLink> model)
            {
                Inserted.AddRange(model);
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
            public void SetFields(SeriesBookLink model, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void Delete(SeriesBookLink model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
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

        private sealed class StubMultiCopySeriesService : IMultiCopySeriesService
        {
            public Dictionary<string, List<Series>> VariantsByBaseId { get; } = new Dictionary<string, List<Series>>(StringComparer.OrdinalIgnoreCase);

            public List<Series> GetAllVariants(string baseSeriesId, BookMediaType? mediaType = null)
            {
                var variants = VariantsByBaseId.TryGetValue(baseSeriesId, out var list) ? list : new List<Series>();
                return mediaType.HasValue ? variants.Where(s => s.MediaType == mediaType.Value).ToList() : variants;
            }

            public string GenerateSeriesVariantId(string baseSeriesId, string narratorName) => throw new NotImplementedException();
            public Series CreateNarratorVariant(Series baseSeries, string narratorName) => throw new NotImplementedException();
            public Series GetOrCreateNarratorVariant(Series baseSeries, string narratorName) => throw new NotImplementedException();
            public int GetNextVariantNumber(string baseSeriesId, BookMediaType? mediaType = null) => throw new NotImplementedException();
        }

        [Test]
        public void should_not_coalesce_wanted_edition_with_same_title_different_work()
        {
            var author = new Author { Id = 1, Name = "Test Author" };
            var selectedEdition = new Edition
            {
                Id = 101,
                BookId = 20,
                Title = "Selected Audio",
                ForeignEditionId = "edition-shared",
                ReadingFormatId = 2,
                Monitored = true
            };

            var baseBook = new Book
            {
                Id = selectedEdition.BookId,
                AuthorId = author.Id,
                Title = "Shared Title",
                TitleSlug = "shared-title-one",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:work-1",
                Editions = new List<Edition> { selectedEdition }
            };

            var otherWanted = new Book
            {
                Id = 21,
                AuthorId = author.Id,
                Title = baseBook.Title,
                TitleSlug = "shared-title-two",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:work-2",
                AddOptions = new AddBookOptions { AddType = BookAddType.Manual }
            };

            var editions = new InMemoryEditionService();
            editions.Seed(baseBook.Id, selectedEdition);
            editions.Seed(otherWanted.Id, new Edition
            {
                Id = 201,
                BookId = otherWanted.Id,
                Title = "Pinned Other Work",
                ForeignEditionId = selectedEdition.ForeignEditionId,
                ReadingFormatId = 2,
                ManualAdd = true
            });

            var repo = new InMemoryBookRepository(baseBook, otherWanted);
            var mediaFiles = new StubMediaFileService();
            mediaFiles.FilesByBookId[baseBook.Id] = new List<BookFile> { new BookFile { Id = 1 } };

            var sut = new BookService(
                repo,
                editions,
                new StubEventAggregator(),
                new StubAuthorService(author),
                mediaFiles,
                rootFolderService: null,
                seriesBookLinkRepository: null,
                multiCopySeriesService: new StubMultiCopySeriesService(),
                logger: LogManager.GetCurrentClassLogger());

            var wanted = sut.AddWantedEdition(baseBook.Id, selectedEdition.Id);

            Assert.Multiple(() =>
            {
                Assert.That(wanted.Id, Is.Not.EqualTo(otherWanted.Id));
                Assert.That(wanted.GoodreadsWorkId, Is.EqualTo(baseBook.GoodreadsWorkId));
            });
        }
    }
}
