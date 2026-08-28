using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceUpdateBookRelinksPinnedEditionFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public readonly List<IEvent> PublishedEvents = new();

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                PublishedEvents.Add(@event);
            }
        }

        private sealed class StubBookRepository : IBookRepository
        {
            private readonly Dictionary<int, Book> _booksById;

            public StubBookRepository(IEnumerable<Book> books)
            {
                _booksById = books.ToDictionary(b => b.Id);
            }

            public Book Get(int id)
            {
                return _booksById.TryGetValue(id, out var book) ? book : null;
            }

            public IEnumerable<Book> Get(IEnumerable<int> ids)
            {
                return ids.Select(id => _booksById[id]).ToList();
            }

            public Book Update(Book model)
            {
                _booksById[model.Id] = model;
                return model;
            }

            public IEnumerable<Book> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Book Find(int id) => throw new NotImplementedException();
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Upsert(Book model) => throw new NotImplementedException();
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
            public List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
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
            public List<Book> GetBooksBySeriesId(int seriesId) => throw new NotImplementedException();
            public void SetMonitoringForAuthorBooks(int authorId, string mediaType, bool monitored, IEnumerable<int> exceptBookIds = null) => throw new NotImplementedException();
            public void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds = null, IDbConnection connection = null, IDbTransaction transaction = null) => throw new NotImplementedException();
            public void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds, IDbConnection connection, IDbTransaction transaction, bool skipCacheInvalidation) => throw new NotImplementedException();
            public void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds, bool skipCacheInvalidation) => throw new NotImplementedException();
            public void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds, bool skipCacheInvalidation, bool skipRescan) => throw new NotImplementedException();
            public void SetMonitoringForAuthorBooks(int authorId, string mediaType, bool monitored, IEnumerable<int> exceptBookIds, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void SetMonitoringForAuthorBooks(int authorId, string mediaType, bool monitored, IEnumerable<int> exceptBookIds, IDbConnection connection, IDbTransaction transaction, bool skipCacheInvalidation) => throw new NotImplementedException();
            public void SetMonitoringForAuthorBooks(int authorId, string mediaType, bool monitored, IEnumerable<int> exceptBookIds, bool skipCacheInvalidation) => throw new NotImplementedException();
            public void SetMonitoringForAuthorBooks(int authorId, string mediaType, bool monitored, IEnumerable<int> exceptBookIds, bool skipCacheInvalidation, bool skipRescan) => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly List<Edition> _editions;

            public StubEditionService(IEnumerable<Edition> editions)
            {
                _editions = editions.ToList();
            }

            public List<Edition> GetEditionsByBook(int bookId) => _editions.Where(e => e.BookId == bookId).ToList();

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                var idSet = bookIds?.ToHashSet() ?? new HashSet<int>();
                return _editions.Where(e => idSet.Contains(e.BookId)).ToList();
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
                _authors = authors.ToDictionary(a => a.Id);
            }

            public Author GetAuthor(int authorId)
            {
                return _authors.TryGetValue(authorId, out var author) ? author : null;
            }

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
            public void ClearAuthorCache() => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId)
            {
                return _authors.Values
                    .Where(a => a.MetadataProfileId == metadataProfileId)
                    .Select(a => a.Id)
                    .ToList();
            }
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly List<BookFile> _files;

            public List<BookFile> UpdatedFiles { get; } = new();

            public StubMediaFileService(IEnumerable<BookFile> files)
            {
                _files = files.ToList();
            }

            public List<BookFile> GetFilesByBook(int bookId) => _files.Where(f => f.Edition?.BookId == bookId).ToList();

            public List<BookFile> GetFilesByEdition(int editionId) => _files.Where(f => f.EditionId == editionId).ToList();

            public void Update(List<BookFile> bookFiles)
            {
                UpdatedFiles.AddRange(bookFiles);
            }

            public void Update(BookFile bookFile)
            {
                UpdatedFiles.Add(bookFile);
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => _files.Where(f => f.Edition != null && bookIds.Contains(f.Edition.BookId)).ToList();
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

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void should_preserve_book_monitoring_when_author_side_is_none(BookMediaType mediaType)
        {
            var author = new Author
            {
                Id = 10,
                Name = "Susanna Clarke",
                AudiobookMonitored = false,
                EbookMonitored = false
            };

            var storedBook = new Book
            {
                Id = 1,
                AuthorId = author.Id,
                Title = "Piranesi",
                MediaType = mediaType
            };

            var requestBook = new Book
            {
                Id = storedBook.Id,
                AuthorId = storedBook.AuthorId,
                Title = storedBook.Title,
                MediaType = mediaType,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook,
                EbookMonitored = mediaType == BookMediaType.Ebook
            };

            var repository = new StubBookRepository(new[] { storedBook });
            var service = new BookService(
                repository,
                new StubEditionService(Array.Empty<Edition>()),
                new StubEventAggregator(),
                new StubAuthorService(new[] { author }),
                new StubMediaFileService(Array.Empty<BookFile>()),
                rootFolderService: null,
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());

            var updated = service.UpdateBook(requestBook);

            Assert.That(updated.AudiobookMonitored, Is.EqualTo(mediaType == BookMediaType.Audiobook));
            Assert.That(updated.EbookMonitored, Is.EqualTo(mediaType == BookMediaType.Ebook));
        }

        [Test]
        public void should_relink_existing_book_files_to_pinned_monitored_edition()
        {
            var author = new Author
            {
                Id = 10,
                Name = "J.K. Rowling",
                AudiobookMonitored = true
            };

            var storedBook = new Book
            {
                Id = 1,
                AuthorId = 10,
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var oldEdition = new Edition { Id = 100, BookId = 1, Title = "Old Edition", Monitored = true };
            var newEdition = new Edition { Id = 101, BookId = 1, Title = "New Edition", Monitored = false };

            var file = new BookFile
            {
                Id = 55,
                EditionId = oldEdition.Id,
                Edition = oldEdition,
                Path = "/audiobooks/file.m4b",
                MediaType = "audiobook"
            };

            var repo = new StubBookRepository(new[] { storedBook });
            var editions = new StubEditionService(new[] { oldEdition, newEdition });
            var authors = new StubAuthorService(new[] { author });
            var mediaFiles = new StubMediaFileService(new[] { file });
            var events = new StubEventAggregator();

            var service = new BookService(repo,
                editions,
                events,
                authors,
                mediaFiles,
                rootFolderService: null,
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());

            // Simulate a user update that pins a specific edition (AnyEditionOk=false) and selects newEdition.
            var requestBook = new Book
            {
                Id = 1,
                AuthorId = 10,
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                AudiobookMonitored = true,
                Editions = new List<Edition>
                {
                    new Edition { Id = oldEdition.Id, BookId = 1, Monitored = false },
                    new Edition { Id = newEdition.Id, BookId = 1, Monitored = true }
                }
            };

            service.UpdateBook(requestBook);

            Assert.That(mediaFiles.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFiles.UpdatedFiles[0].Id, Is.EqualTo(file.Id));
            Assert.That(mediaFiles.UpdatedFiles[0].EditionId, Is.EqualTo(newEdition.Id));
        }
    }
}
