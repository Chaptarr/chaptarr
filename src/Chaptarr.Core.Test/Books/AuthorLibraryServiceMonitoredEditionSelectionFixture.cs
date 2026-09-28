using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServiceMonitoredEditionSelectionFixture
    {
        private sealed class StubBookService : IBookService
        {
            public List<Book> UpdatedBooks { get; } = new List<Book>();

            public void UpdateMany(List<Book> books)
            {
                if (books != null)
                {
                    UpdatedBooks.AddRange(books);
                }
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly List<Edition> _editions;
            public List<Edition> UpdatedEditions { get; } = new List<Edition>();

            public StubEditionService(IEnumerable<Edition> editions)
            {
                _editions = editions?.ToList() ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                var set = new HashSet<int>(bookIds ?? Enumerable.Empty<int>());
                return _editions.Where(e => set.Contains(e.BookId)).ToList();
            }

            public void UpdateMany(List<Edition> editions)
            {
                if (editions != null)
                {
                    UpdatedEditions.AddRange(editions);
                }
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
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private static AuthorLibraryService BuildService(IBookService bookService, IEditionService editionService)
        {
            return new AuthorLibraryService(
                authorService: null,
                authorInfo: null,
                bookService: bookService,
                refreshSeriesService: null,
                editionService: editionService,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: null,
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger(),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()));
        }

        private static void InvokeSelectMonitoredEditionsForMediaType(AuthorLibraryService service, List<Book> books, MetadataProfile profile)
        {
            var method = typeof(AuthorLibraryService).GetMethod("SelectMonitoredEditionsForMediaType", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AuthorLibraryService.SelectMonitoredEditionsForMediaType via reflection");
            }

            method.Invoke(service, new object[] { books, profile });
        }

        [Test]
        public void should_use_representative_ebook_and_sync_book_foreign_edition_id_when_no_audio_candidate_survives_filters()
        {
            var book = new Book
            {
                Id = 100,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune FR Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3, Monitored = false }
            };

            var profile = new MetadataProfile
            {
                Name = "Audio EN",
                AllowedLanguages = "eng"
            };

            var bookService = new StubBookService();
            var editionService = new StubEditionService(editions);
            var service = BuildService(bookService, editionService);

            InvokeSelectMonitoredEditionsForMediaType(service, new List<Book> { book }, profile);

            Assert.That(editions.Single(e => e.Id == 1).Monitored, Is.False);
            Assert.That(editions.Single(e => e.Id == 2).Monitored, Is.True);
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-ebook"));
            Assert.That(bookService.UpdatedBooks.Count, Is.EqualTo(1));
            Assert.That(bookService.UpdatedBooks.Single(), Is.SameAs(book));
            Assert.That(editionService.UpdatedEditions.Count, Is.EqualTo(2));
        }

        [Test]
        public void should_skip_automatic_selection_when_manual_monitored_edition_exists()
        {
            var book = new Book
            {
                Id = 101,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "eng-audio-manual"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-audio-manual", Title = "Dune Audio Manual", Language = "eng", ReadingFormatId = 2, Monitored = true, ManualAdd = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio-other", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false, ManualAdd = false }
            };

            var profile = new MetadataProfile
            {
                Name = "Audio EN",
                AllowedLanguages = "eng"
            };

            var bookService = new StubBookService();
            var editionService = new StubEditionService(editions);
            var service = BuildService(bookService, editionService);

            InvokeSelectMonitoredEditionsForMediaType(service, new List<Book> { book }, profile);

            Assert.That(editions.Single(e => e.Id == 1).Monitored, Is.True);
            Assert.That(editions.Single(e => e.Id == 2).Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio-manual"));
            Assert.That(bookService.UpdatedBooks, Is.Empty);
            Assert.That(editionService.UpdatedEditions, Is.Empty);
        }

        [Test]
        public void should_keep_existing_monitored_when_profile_filters_remove_everything()
        {
            var book = new Book
            {
                Id = 102,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "eng-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3, Monitored = false }
            };

            var profile = new MetadataProfile
            {
                Name = "Audio EN",
                AllowedLanguages = "eng",
                SkipMissingIsbn = true
            };

            var bookService = new StubBookService();
            var editionService = new StubEditionService(editions);
            var service = BuildService(bookService, editionService);

            InvokeSelectMonitoredEditionsForMediaType(service, new List<Book> { book }, profile);

            Assert.That(editions.Single(e => e.Id == 1).Monitored, Is.True);
            Assert.That(editions.Single(e => e.Id == 2).Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(bookService.UpdatedBooks, Is.Empty);
            Assert.That(editionService.UpdatedEditions, Is.Empty);
        }
    }
}
