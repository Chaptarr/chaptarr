using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Metadata.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class UpdateEditionSelectionOnMetadataProfileChangeServiceFixture
    {
        private sealed class StubAuthorService : IAuthorService
        {
            private readonly List<Author> _authors;

            public StubAuthorService(params Author[] authors)
            {
                _authors = (authors ?? Array.Empty<Author>()).ToList();
            }

            public List<Author> GetAllAuthors(bool bypassCache = false) => _authors;
            public Author GetAuthor(int authorId) => _authors.FirstOrDefault(a => a.Id == authorId);
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            private readonly Dictionary<int, List<Book>> _booksByAuthorId;
            public List<Book> UpdatedBooks { get; } = new List<Book>();

            public StubBookService(params Book[] books)
            {
                _booksByAuthorId = (books ?? Array.Empty<Book>())
                    .GroupBy(b => b.AuthorId)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }

            public List<Book> GetBooksByAuthor(int authorId) =>
                _booksByAuthorId.TryGetValue(authorId, out var books) ? books : new List<Book>();

            public Book UpdateBook(Book book)
            {
                UpdatedBooks.Add(book);
                return book;
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => GetBooksByAuthor(authorId);
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
            public List<Book> GetAllBooks() => _booksByAuthorId.Values.SelectMany(x => x).ToList();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly Dictionary<int, List<Edition>> _editionsByBookId;
            public List<Edition> UpdatedEditions { get; private set; }

            public StubEditionService(Dictionary<int, List<Edition>> editionsByBookId)
            {
                _editionsByBookId = editionsByBookId;
            }

            public List<Edition> GetEditionsByBook(int bookId) =>
                _editionsByBookId.TryGetValue(bookId, out var editions) ? editions : new List<Edition>();

            public void UpdateMany(List<Edition> editions)
            {
                UpdatedEditions = editions;
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
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly Dictionary<int, List<BookFile>> _filesByBookId;

            public StubMediaFileService(Dictionary<int, List<BookFile>> filesByBookId = null)
            {
                _filesByBookId = filesByBookId ?? new Dictionary<int, List<BookFile>>();
            }

            public List<BookFile> GetFilesByBook(int bookId) =>
                _filesByBookId.TryGetValue(bookId, out var files) ? files : new List<BookFile>();

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
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

        private static UpdateEditionSelectionOnMetadataProfileChangeService CreateSut(
            StubAuthorService authorService,
            StubBookService bookService,
            StubEditionService editionService,
            StubMediaFileService mediaFileService)
        {
            return new UpdateEditionSelectionOnMetadataProfileChangeService(
                authorService,
                bookService,
                editionService,
                mediaFileService,
                new EditionSelector(LogManager.GetCurrentClassLogger()),
                new EditionMetadataProfileFilter(new TestTermMatcherService()),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_reselect_to_allowed_audiobook_and_update_book_foreign_edition_id()
        {
            var profile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng" };
            var author = new Author { Id = 5, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions }),
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile));

            Assert.That(editions.Single(e => e.ForeignEditionId == "eng-audio").Monitored, Is.True);
            Assert.That(editions.Single(e => e.ForeignEditionId == "fra-audio").Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(bookService.UpdatedBooks.LastOrDefault(), Is.SameAs(book));
        }

        [Test]
        public void should_treat_null_language_as_allowed_bucket_when_reselecting()
        {
            var profile = new MetadataProfile { Id = 8, Name = "Unknown", AllowedLanguages = "null" };
            var author = new Author { Id = 6, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 12,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "eng-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "null-audio", Title = "Dune Audio Unknown", Language = null, ReadingFormatId = 2, Monitored = false },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "null-ebook", Title = "Dune", Language = null, ReadingFormatId = 3, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions }),
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile));

            Assert.That(editions.Single(e => e.ForeignEditionId == "null-audio").Monitored, Is.True);
            Assert.That(editions.Single(e => e.ForeignEditionId == "eng-audio").Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("null-audio"));
            Assert.That(bookService.UpdatedBooks.LastOrDefault(), Is.SameAs(book));
        }

        [Test]
        public void should_skip_reselection_when_allowed_languages_are_unchanged()
        {
            var previousProfile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng" };
            var profile = new MetadataProfile { Id = 7, Name = "English Restored", AllowedLanguages = "eng" };
            var author = new Author { Id = 5, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var editionService = new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions });
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                editionService,
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile, previousProfile));

            Assert.That(editions.Single(e => e.ForeignEditionId == "fra-audio").Monitored, Is.True);
            Assert.That(editions.Single(e => e.ForeignEditionId == "eng-audio").Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("fra-audio"));
            Assert.That(editionService.UpdatedEditions, Is.Null);
            Assert.That(bookService.UpdatedBooks, Is.Empty);
        }

        [Test]
        public void should_skip_reselection_when_allowed_languages_are_equivalent()
        {
            var previousProfile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng, null" };
            var profile = new MetadataProfile { Id = 7, Name = "English Restored", AllowedLanguages = "unknown, ENG" };
            var author = new Author { Id = 5, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var editionService = new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions });
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                editionService,
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile, previousProfile));

            Assert.That(editionService.UpdatedEditions, Is.Null);
            Assert.That(bookService.UpdatedBooks, Is.Empty);
        }
    }
}
