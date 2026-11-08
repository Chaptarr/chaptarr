using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public Action<object> OnPublish { get; set; }
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                OnPublish?.Invoke(@event);
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authorsById;
            public Action BeforeGet { get; set; }

            public StubAuthorService(params Author[] authors)
            {
                _authorsById = (authors ?? Array.Empty<Author>()).Where(a => a != null).ToDictionary(a => a.Id);
            }

            public Author GetAuthor(int authorId)
            {
                BeforeGet?.Invoke();
                return _authorsById.TryGetValue(authorId, out var a) ? a : null;
            }

            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => GetAuthors(authorIds);

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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public void ValidateReplacementFiles(System.Collections.Generic.List<BookFile> replacedFiles,
                System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles)
                => throw new System.NotSupportedException("This test adapter does not support replacement validation.");

            public void ReplaceMany(System.Collections.Generic.List<BookFile> bookFiles, System.Collections.Generic.List<BookFile> replacedFiles,
                DeleteMediaFileReason reason, System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles,
                System.Action<System.Action<System.Data.IDbConnection, System.Data.IDbTransaction>> persist)
                => throw new System.NotSupportedException("This test adapter does not support transactional replacement.");

            public Dictionary<int, List<BookFile>> FilesByBookId { get; } = new Dictionary<int, List<BookFile>>();
            public MediaFileRepository Repository { get; set; }

            public List<BookFile> GetFilesByEdition(int editionId) => new List<BookFile>();

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => FilesByBookId.Values.SelectMany(files => files).ToList();
            public List<BookFile> GetFilesByBook(int bookId) => FilesByBookId.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            public List<List<int>> BatchQueries { get; } = new();
            public Action BeforeBatchRead { get; set; }
            public List<BookFile> GetFilesByBooks(List<int> bookIds)
            {
                BeforeBatchRead?.Invoke();
                lock (BatchQueries) { BatchQueries.Add(bookIds.ToList()); }
                if (Repository != null) return Repository.GetFilesByBooks(bookIds);
                return bookIds.SelectMany(id => FilesByBookId.TryGetValue(id, out var files) ? files : new List<BookFile>()).ToList();
            }
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
                var editions = GetEditionsByBook(edition.BookId);
                foreach (var item in editions)
                {
                    item.Monitored = item.Id == edition.Id;
                    item.ManualAdd = item.Monitored && (isManualSelection || item.ManualAdd);
                }

                return editions;
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
            public void UpdateSelection(List<Edition> editions) => UpdateMany(editions);
            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions) { foreach (var group in editions.GroupBy(edition => edition.BookId)) { Seed(group.Key, group.ToArray()); } }
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();

            public void RefreshProviderAliases(List<Edition> editions) { }
        }

        private sealed class InMemoryBookRepository : IBookRepository
        {
            public List<Book> GetImportListBookIdentities() => All().ToList();

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
            public IEnumerable<Book> FindExisting(IEnumerable<int> ids) => Get(ids);

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
            public Book Find(int id) => Get(id);
            public Book Insert(Book model) => throw new NotImplementedException();
            public Book Update(Book model) => throw new NotImplementedException();
            public void SetFields(Book model, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Book model) => throw new NotImplementedException();
            public void Delete(int id) => _booksById.Remove(id);
            public void InsertMany(IList<Book> model) => throw new NotImplementedException();
            public void InsertMany(IList<Book> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateManyAtomic(IList<Book> books, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated) => throw new System.NotSupportedException();
            public void UpdateMany(IList<Book> model) { foreach (var book in model) { Upsert(book); } }
            public void SetMonitoring(IList<Book> books)
            {
                foreach (var book in books)
                {
                    var stored = Get(book.Id);
                    stored.AudiobookMonitored = book.AudiobookMonitored;
                    stored.EbookMonitored = book.EbookMonitored;
                }
            }
            public void SetFields(IList<Book> models, params System.Linq.Expressions.Expression<Func<Book, object>>[] properties)
            {
                Assert.That(properties.Select(property => ((System.Linq.Expressions.MemberExpression)((System.Linq.Expressions.UnaryExpression)property.Body).Operand).Member.Name), Is.EqualTo(new[] { nameof(Book.AnyEditionOk) }));
                foreach (var book in models) { Upsert(book); }
            }
            public void DeleteMany(List<Book> model) => throw new NotImplementedException();
            public void DeleteManyAtomic(IEnumerable<int> ids, IEnumerable<int> deletedFileIds = null)
            {
                foreach (var id in ids) Delete(id);
            }
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
            public List<Book> FindTitleSlugConflicts(IEnumerable<string> slugs, IDbConnection connection = null, IDbTransaction transaction = null) => new List<Book>();
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

        private static Book MakeBook(int id, string selected, bool pinned = false)
        {
            var book = new Book
            {
                Id = id, AuthorId = 1, Title = "Requested Work", TitleSlug = "requested-work-" + id,
                HardcoverBookId = "hc:101", MediaType = BookMediaType.Audiobook,
                AnyEditionOk = !pinned, AudiobookMonitored = true
            };
            book.Editions = new[] { "Jim Dale", "Stephen Fry", "Full Cast" }.Select((name, index) => new Edition
            {
                Id = id * 10 + index + 1, BookId = id, Title = book.Title,
                HardcoverEditionId = "hc:" + (201 + index), ForeignEditionId = "hc:" + (201 + index),
                ReadingFormatId = 2, NarratorNames = new List<string> { name },
                Monitored = name == selected, ManualAdd = pinned && name == selected
            }).ToList();
            return book;
        }

        private static (BookService Service, InMemoryBookRepository Repository, StubMediaFileService Files) BuildWantedService(List<Book> books, params int[] downloaded)
        {
            var author = new Author { Id = 1, Name = "Test Author", AudiobookRootFolderPath = @"C:\Audio", AudiobookQualityProfileId = 1 };
            var editions = new InMemoryEditionService();
            books.ForEach(book => editions.Seed(book.Id, book.Editions.ToArray()));
            var files = new StubMediaFileService();
            foreach (var id in downloaded)
            {
                files.FilesByBookId[id] = new List<BookFile> { new() { Id = id, Edition = BookEditionIdentity.GetMonitoredEdition(books.Single(book => book.Id == id)) } };
            }

            var repository = new InMemoryBookRepository(books.ToArray());
            return (new BookService(repository, editions, new StubEventAggregator(), new StubAuthorService(author), files,
                null, null, new StubMultiCopySeriesService(), LogManager.GetCurrentClassLogger()), repository, files);
        }

        [TestCase("Jim Dale", "Jim Dale")]
        [TestCase("Daniel Jones", "Danielle Jones")]
        public void compatible_existing_selection_is_pinned_without_changing_edition(string selectedNarrator, string requestedNarrator)
        {
            var first = MakeBook(1, "Jim Dale");
            var second = MakeBook(2, "Jim Dale");
            first.Editions[0].NarratorNames = new List<string> { selectedNarrator };
            second.Editions[0].NarratorNames = new List<string> { requestedNarrator };
            second.Editions[0].HardcoverEditionId = "hc:991";
            second.Editions[0].ForeignEditionId = "hc:991";
            var books = new List<Book> { first, second };
            var (service, repository, files) = BuildWantedService(books);
            var result = service.AddWantedEditions(new List<Edition> { second.Editions[0] });
            Assert.That(result[21].Id, Is.EqualTo(1));
            Assert.That(first.AnyEditionOk, Is.False);
            Assert.That(first.Editions.Single(edition => edition.Monitored).Id, Is.EqualTo(11));
            Assert.That(first.Editions.Single(edition => edition.ManualAdd).Id, Is.EqualTo(11));
            Assert.That(repository.GetBooksByAuthorId(1), Has.Count.EqualTo(2));
        }

        [Test]
        public void incompatible_pins_create_one_copy_then_all_later_requests_reuse_it()
        {
            var first = MakeBook(1, "Jim Dale", true);
            var books = new List<Book> { first };
            var (service, repository, files) = BuildWantedService(books);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                service.AddWantedEditions(new List<Edition> { first.Editions[1], first.Editions[0] });
            }

            var stored = repository.GetBooksByAuthorId(1);
            Assert.That(stored, Has.Count.EqualTo(2));
            Assert.That(files.BatchQueries, Has.Count.EqualTo(3));
            Assert.That(files.BatchQueries[0], Is.EquivalentTo(new[] { 1 }));
            Assert.That(files.BatchQueries.Skip(1).All(query => query.Count == 2), Is.True);
            Assert.That(BookEditionIdentity.GetMonitoredEdition(first).NarratorNames, Is.EqualTo(new[] { "Jim Dale" }));
            Assert.That(BookEditionIdentity.GetMonitoredEdition(stored.Single(book => book.Id != first.Id)).NarratorNames, Is.EqualTo(new[] { "Stephen Fry" }));
            Assert.That(stored.All(book => !book.AnyEditionOk && book.Editions.Count(edition => edition.Monitored && edition.ManualAdd) == 1), Is.True);
        }

        [Test]
        public void later_compatible_downloaded_row_is_reused_before_creating_third_narration()
        {
            var first = MakeBook(1, "Jim Dale", true);
            var second = MakeBook(2, "Stephen Fry", true);
            var books = new List<Book> { first, second };
            var (service, repository, files) = BuildWantedService(books, 1, 2);
            var fry = service.AddWantedEditions(new List<Edition> { first.Editions[1] });
            Assert.That(fry[12].Id, Is.EqualTo(second.Id));
            Assert.That(repository.GetBooksByAuthorId(1), Has.Count.EqualTo(2));
            for (var repeat = 0; repeat < 3; repeat++)
            {
                service.AddWantedEditions(new List<Edition> { first.Editions[2] });
            }

            Assert.That(repository.GetBooksByAuthorId(1), Has.Count.EqualTo(3));
            Assert.That(repository.GetBooksByAuthorId(first.AuthorId).Select(BookEditionIdentity.GetMonitoredEdition).Select(edition => edition.NarratorNames.Single()),
                Is.EquivalentTo(new[] { "Jim Dale", "Stephen Fry", "Full Cast" }));
        }

        [Test]
        public void wanted_copy_preserves_foreign_only_edition_identity_and_provider_aliases()
        {
            var original = MakeBook(1, "Stephen Fry", true);
            var requested = original.Editions[2];
            requested.HardcoverEditionId = null;
            requested.ForeignEditionId = "gr:243729281-audiobook";
            original.Editions[0].Asins = new List<string> { "B000000001", "B000000002" };
            var books = new List<Book> { original };
            var (service, repository, files) = BuildWantedService(books, original.Id);

            var wanted = service.AddWantedEditionCopy(original.Id, requested.Id).Book;
            var persisted = service.GetBook(wanted.Id);
            var selected = persisted.Editions.Single(edition => edition.Monitored);
            Assert.Multiple(() =>
            {
                Assert.That(persisted.Id, Is.Not.EqualTo(original.Id));
                Assert.That(persisted.AnyEditionOk, Is.False);
                Assert.That(selected.ManualAdd, Is.True);
                Assert.That(selected.ForeignEditionId, Is.EqualTo(requested.ForeignEditionId));
                Assert.That(selected.GoodreadsEditionId, Is.Null);
                Assert.That(selected.HardcoverEditionId, Is.Null);
                Assert.That(BookEditionIdentity.EditionsMatch(selected, requested), Is.True);
                Assert.That(persisted.Editions.Single(edition => edition.ForeignEditionId == original.Editions[0].ForeignEditionId).Asins,
                    Is.EqualTo(original.Editions[0].Asins));
                Assert.That(BookEditionIdentity.GetMonitoredEdition(original).NarratorNames, Is.EqualTo(new[] { "Stephen Fry" }));
            });

            Assert.That(service.AddWantedEditionCopy(original.Id, requested.Id).Book.Id, Is.EqualTo(persisted.Id));
            Assert.That(repository.GetBooksByAuthorId(original.AuthorId), Has.Count.EqualTo(2));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void wanted_copy_identity_survives_database_reload_and_repeat_request(bool postgres, bool concurrent)
        {
            RunWithDatabase(postgres, database =>
            {
                using var logFactory = new LogFactory();
                var memory = new MemoryTarget { Layout = "${level}|${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, memory);
                logFactory.Configuration = logging;
                var logger = logFactory.GetLogger("WantedWriter");
                var events = new StubEventAggregator();
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author
                {
                    Name = "Test Author", CleanName = "testauthor", TitleSlug = "test-author", Path = @"C:\Audio\Test Author", Added = DateTime.UtcNow,
                    AudiobookRootFolderPath = @"C:\Audio", AudiobookQualityProfileId = 1,
                    AudiobookMonitored = false
                });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var source = MakeBook(1, "Stephen Fry", true);
                source.Id = 0;
                source.AuthorId = author.Id;
                source.Added = DateTime.UtcNow;
                source.CleanTitle = "requestedwork";
                source.Subtitle = "Preserved subtitle";
                source.OriginalTitle = "Preserved original title";
                source.IsOmnibus = true;
                source.RelatedBooks = new List<int> { 11, 12 };
                var requested = source.Editions[2];
                requested.HardcoverEditionId = null;
                requested.ForeignEditionId = "gr:243729281-audiobook";
                source.Editions[0].Asins = new List<string> { "B000000001", "B000000002" };
                books.Insert(source);
                foreach (var edition in source.Editions)
                {
                    edition.Id = 0;
                    edition.BookId = source.Id;
                }

                editions.InsertMany(source.Editions);
                var files = new StubMediaFileService();
                var authorService = new StubAuthorService(author);
                BookService Service() => new BookService(new BookRepository(database, events),
                    new EditionService(new EditionRepository(database, events), events, logger), events,
                    authorService, files, null, null, new StubMultiCopySeriesService(), logger);

                Book wanted;
                if (concurrent)
                {
                    using var firstRead = new ManualResetEventSlim();
                    using var releaseRead = new ManualResetEventSlim();
                    using var secondStarted = new ManualResetEventSlim();
                    using var secondRead = new ManualResetEventSlim();
                    var reads = 0;
                    authorService.BeforeGet = () =>
                    {
                        if (Interlocked.Increment(ref reads) == 1)
                        {
                            firstRead.Set();
                            Assert.That(releaseRead.Wait(TimeSpan.FromSeconds(10)), Is.True);
                        }
                        else
                        {
                            secondRead.Set();
                        }
                    };
                    var firstTask = Task.Run(() => Service().AddWantedEditionCopy(source.Id, requested.Id).Book);
                    Task<Book> secondTask = null;
                    try
                    {
                        Assert.That(firstRead.Wait(TimeSpan.FromSeconds(5)), Is.True);
                        secondTask = Task.Run(() =>
                        {
                            secondStarted.Set();
                            return Service().AddWantedEditionCopy(source.Id, requested.Id).Book;
                        });
                        Assert.That(secondStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
                        Assert.That(secondRead.Wait(TimeSpan.FromMilliseconds(300)), Is.False,
                            "a concurrent request must wait before reading its selection snapshot");
                    }
                    finally
                    {
                        releaseRead.Set();
                        firstTask.GetAwaiter().GetResult();
                        secondTask?.GetAwaiter().GetResult();
                        authorService.BeforeGet = null;
                    }

                    wanted = firstTask.Result;
                    Assert.That(secondTask.Result.Id, Is.EqualTo(wanted.Id));
                }
                else
                {
                    wanted = Service().AddWantedEditionCopy(source.Id, requested.Id).Book;
                }
                Assert.That(memory.Logs.Count(message => message.StartsWith("Info|[AUTHOR-REFRESH-GATE] Waiting")), Is.EqualTo(concurrent ? 1 : 0));
                Assert.That(memory.Logs, Has.None.Contains("('')"));
                Assert.That(files.BatchQueries, Has.Count.EqualTo(concurrent ? 2 : 1), "file-linked editions are loaded once per request");
                Assert.That(memory.Logs.Where(message => message.StartsWith("Info|Wanted edition writer")), Is.Not.Empty);
                Assert.That(memory.Logs.Where(message => message.StartsWith("Info|Wanted edition writer")),
                    Has.All.Contains("waited ").And.All.Contains("held ").And.All.Contains("1 copy request")
                        .And.All.Contains("candidate rows").And.All.Contains("editions").And.All.Contains("author rows")
                        .And.All.Contains("files; total "));
                Assert.That(memory.Logs, Has.Some.Contains("1 candidate rows, 3 editions"));
                if (concurrent) Assert.That(memory.Logs, Has.Some.Contains("2 candidate rows, 6 editions"));
                var reloaded = Service().GetBook(wanted.Id);
                var selected = reloaded.Editions.Single(edition => edition.Monitored);
                Assert.Multiple(() =>
                {
                    Assert.That(reloaded.Id, Is.Not.EqualTo(source.Id));
                    Assert.That(reloaded.AnyEditionOk, Is.False);
                    Assert.That(reloaded.Subtitle, Is.EqualTo(source.Subtitle));
                    Assert.That(reloaded.OriginalTitle, Is.EqualTo(source.OriginalTitle));
                    Assert.That(reloaded.RelatedBooks, Is.EqualTo(source.RelatedBooks));
                    Assert.That(reloaded.IsOmnibus, Is.True);
                    Assert.That(selected.ManualAdd, Is.True);
                    Assert.That(selected.ForeignEditionId, Is.EqualTo(requested.ForeignEditionId));
                    Assert.That(BookEditionIdentity.EditionsMatch(selected, requested), Is.True);
                    Assert.That(reloaded.Editions.Single(edition => edition.ForeignEditionId == source.Editions[0].ForeignEditionId).Asins,
                        Is.EqualTo(source.Editions[0].Asins));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(Service().GetBook(source.Id)).NarratorNames,
                        Is.EqualTo(new[] { "Stephen Fry" }));
                    Assert.That(authors.Get(author.Id).AudiobookMonitored, Is.False);
                });

                Assert.That(Service().AddWantedEditionCopy(source.Id, requested.Id).Book.Id, Is.EqualTo(wanted.Id));
                Assert.That(books.GetBooksByAuthorId(author.Id), Has.Count.EqualTo(2));
            });
        }

        private static void RunWithDatabase(bool postgres, Action<IMainDatabase> test)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, test);
        }

        [Test]
        public void fileless_unpinned_selection_changes_once_and_has_only_one_monitored_edition()
        {
            var first = MakeBook(1, "Jim Dale");
            var books = new List<Book> { first };
            var (service, repository, files) = BuildWantedService(books);
            service.AddWantedEditions(new List<Edition> { first.Editions[1] });
            Assert.That(repository.GetBooksByAuthorId(1), Has.Count.EqualTo(1));
            Assert.That(first.Editions.Single(edition => edition.Monitored).Id, Is.EqualTo(12));
            Assert.That(first.Editions.Single(edition => edition.ManualAdd).Id, Is.EqualTo(12));
            Assert.That(first.AnyEditionOk, Is.False);
        }

        [TestCase("Jim Dale", "Jimmy Dale", true)]
        [TestCase("Stephen Fry", "Steven Fry", true)]
        [TestCase("Shay Oneil", "SHay O'Neill", true)]
        [TestCase("Oneil", "O'Neil", true)]
        [TestCase("Shay Oneil", "Shay O’Neil", true)]
        [TestCase("Shay Oneil", "Shay O‘Neill", true)]
        [TestCase("Daniel Jones", "Danielle Jones", true)]
        [TestCase("Francesco Rossi", "Francesca Rossi", true)]
        [TestCase("Alexander Petrov", "Alexander Petrova", true)]
        [TestCase("Jim Dale", "Jim dal", true)]
        [TestCase("Aleksandr Ivanov", "Alexandr Ivanov", true)]
        [TestCase("Élodie Martin", "Elodie Martinn", true)]
        [TestCase("Jim Doe", "Jimmy Doe", false)]
        [TestCase("Jim Dale", "Tim Dale", false)]
        [TestCase("Brian Papadopoulos", "Frank Papadopoulos", false)]
        [TestCase("Stephen Fry", "Steven Frey", false)]
        [TestCase("かとう たろう", "がとう たろう", false)]
        [TestCase("Brian Herbert", "Frank Herbert", false)]
        [TestCase("Jim Dale", "Stephen Fry", false)]
        [TestCase("", "", false)]
        public void narrator_tolerance_is_narrow_and_same_book_only(string left, string right, bool expected)
        {
            Assert.That(BookEditionIdentity.HasSameNarration(new Edition { Narrator = left }, new Edition { Narrator = right }), Is.EqualTo(expected));
            Assert.That(BookEditionIdentity.HasSameNarration(new Edition { Narrator = right }, new Edition { Narrator = left }), Is.EqualTo(expected));
        }

        [Test]
        public void solo_narrator_is_not_equivalent_to_a_cast_containing_that_person()
        {
            Assert.That(BookEditionIdentity.HasSameNarration(new Edition { NarratorNames = new List<string> { "Jim Dale" } },
                new Edition { NarratorNames = new List<string> { "Jim Dale", "Stephen Fry" } }), Is.False);
        }

        [Test]
        public void should_not_coalesce_wanted_edition_with_same_title_different_work()
        {
            var author = new Author { Id = 1, Name = "Test Author", AudiobookRootFolderPath = @"C:\Audio", AudiobookQualityProfileId = 1 };
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
            mediaFiles.FilesByBookId[baseBook.Id] = new List<BookFile> { new BookFile { Id = 1, Edition = selectedEdition } };

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

            var wanted = sut.AddWantedEditionCopy(baseBook.Id, selectedEdition.Id).Book;

            Assert.Multiple(() =>
            {
                Assert.That(wanted.Id, Is.Not.EqualTo(otherWanted.Id));
                Assert.That(wanted.GoodreadsWorkId, Is.EqualTo(baseBook.GoodreadsWorkId));
            });
        }
        [Test]
        public void unpinned_row_with_files_must_never_be_retargeted([Values(false, true)] bool copy)
        {
            var source = MakeBook(1, "Jim Dale", false);
            var (service, repository, _) = BuildWantedService(new List<Book> { source }, source.Id);
            var wanted = copy ? service.AddWantedEditionCopy(source.Id, source.Editions[1].Id).Book
                : service.AddWantedEditions(new List<Edition> { source.Editions[1] })[source.Editions[1].Id];
            Assert.Multiple(() =>
            {
                Assert.That(wanted.Id, Is.Not.EqualTo(source.Id));
                Assert.That(BookEditionIdentity.GetMonitoredEdition(source).NarratorNames, Is.EqualTo(new[] { "Jim Dale" }));
                Assert.That(source.AnyEditionOk, Is.True);
                Assert.That(repository.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void compatible_copy_found_only_by_edition_identity_is_reused()
        {
            var source = MakeBook(1, "Jim Dale", true);
            var other = MakeBook(2, "Stephen Fry", true);
            other.HardcoverBookId = "hc:9999";
            other.GoodreadsWorkId = null;
            other.RemoteProviderIds = null;
            var requested = source.Editions[1];
            var selected = BookEditionIdentity.GetMonitoredEdition(other);
            selected.ForeignEditionId = requested.ForeignEditionId;
            selected.HardcoverEditionId = requested.HardcoverEditionId;
            var (service, repository, _) = BuildWantedService(new List<Book> { source, other });
            Assert.That(service.AddWantedEditions(new List<Edition> { requested })[requested.Id].Id, Is.EqualTo(other.Id));
            Assert.That(repository.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
        }

        [Test]
        public void microphone_returns_automatic_origin_unchanged_even_when_a_pinned_copy_exists()
        {
            var first = MakeBook(1, "Stephen Fry", false);
            var pinned = MakeBook(2, "Stephen Fry", true);
            var (service, repository, _) = BuildWantedService(new List<Book> { first, pinned });
            var result = service.AddWantedEditionCopy(first.Id, first.Editions[1].Id);
            Assert.That(result.Book.Id, Is.EqualTo(first.Id));
            Assert.That(result.ExistingCopy, Is.True);
            Assert.That(first.AnyEditionOk, Is.True);
            Assert.That(pinned.AnyEditionOk, Is.False);
            Assert.That(repository.GetBooksByAuthorId(first.AuthorId), Has.Count.EqualTo(2));
        }

        [Test]
        public void failed_wanted_copy_validation_should_remove_the_new_book_through_normal_deletion()
        {
            var source = MakeBook(1, "Jim Dale", true);
            var author = new Author { Id = 1, Name = "Test Author", AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic() };
            var repository = new InMemoryBookRepository(source);
            var editions = new InMemoryEditionService();
            editions.Seed(source.Id, source.Editions.ToArray());
            var deleted = new List<int>();
            var events = new StubEventAggregator { OnPublish = message =>
            {
                if (message is NzbDrone.Core.Books.Events.BookAddedEvent added)
                {
                    added.Book.Editions.ForEach(edition => edition.ManualAdd = false);
                }
                if (message is NzbDrone.Core.Books.Events.BookDeletedEvent removed)
                {
                    deleted.Add(removed.Book.Id);
                    Assert.That(removed.DeleteFiles, Is.False);
                }
            } };
            var service = new BookService(repository, editions, events, new StubAuthorService(author), new StubMediaFileService(),
                null, null, new StubMultiCopySeriesService(), LogManager.GetCurrentClassLogger());
            Assert.Throws<InvalidOperationException>(() => service.AddWantedEditionCopy(source.Id, source.Editions[1].Id));
            Assert.That(deleted, Has.Count.EqualTo(1));
            Assert.That(repository.GetBooksByAuthorId(author.Id).Single().Id, Is.EqualTo(source.Id));
            Assert.That(BookEditionIdentity.GetMonitoredEdition(source).ManualAdd, Is.True);
        }

        [Test]
        public void failed_narration_does_not_stop_other_requests_in_the_author_batch()
        {
            var source = MakeBook(1, "Jim Dale", true);
            var bad = new Edition { Id = 999, BookId = source.Id, ForeignEditionId = "gr:999" };
            var (service, repository, files) = BuildWantedService(new List<Book> { source });
            var failures = new List<int>();
            var result = service.AddWantedEditions(new List<Edition> { bad, source.Editions[1] }, (edition, _) => failures.Add(edition.Id));
            Assert.That(failures, Is.EqualTo(new[] { 999 }));
            Assert.That(result.Keys, Is.EqualTo(new[] { source.Editions[1].Id }));
            Assert.That(repository.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
            Assert.That(files.BatchQueries, Has.Count.EqualTo(1));
        }

    }
}
