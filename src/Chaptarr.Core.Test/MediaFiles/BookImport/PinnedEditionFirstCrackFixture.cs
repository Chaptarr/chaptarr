using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class PinnedEditionFirstCrackFixture
    {
        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000)
            {
                return new List<MatchingLogEntry>();
            }

            public void ClearLogs()
            {
            }
        }

        private sealed class ThrowingEditionFtsRepository : IEditionFtsRepository
        {
            public bool FtsTableExists() => true;
            public void RebuildIndex()
            {
            }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                throw new AssertionException("FTS should not be called when pinned edition first-crack match succeeds");
            }

        }

        private sealed class EmptyEditionFtsRepository : IEditionFtsRepository
        {
            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => new List<EditionFtsMatch>();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId) => _author;

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new System.NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new System.NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new System.NotImplementedException();
            public Author FindByName(string title) => throw new System.NotImplementedException();
            public Author FindByNameInexact(string title) => throw new System.NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new System.NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new System.NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new System.NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new System.NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new System.NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new System.NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new System.NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new System.NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new System.NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new System.NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new System.NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new System.NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new System.NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new System.NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new System.NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new System.NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new System.NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            private readonly Book _book;

            public StubBookService(Book book)
            {
                _book = book;
            }

            public Book GetBook(int bookId) => _book;

            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new System.NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new System.NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new System.NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new System.NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new System.NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new System.NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new System.NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new System.NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new System.NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new System.NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new System.NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new System.NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new System.NotImplementedException();
            public Book FindByISBN(string isbn) => throw new System.NotImplementedException();
            public Book FindByASIN(string asin) => throw new System.NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new System.NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new System.NotImplementedException();
            public List<Book> GetAllBooks() => throw new System.NotImplementedException();
            public Book UpdateBook(Book book) => throw new System.NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new System.NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new System.NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new System.NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new System.NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new System.NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new System.NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new System.NotImplementedException();
            public void InsertMany(List<Book> books) => throw new System.NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new System.NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new System.NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new System.NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new System.NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new System.NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new System.NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new System.NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new System.NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new System.NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new System.NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new System.NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new System.NotImplementedException();
        }

        [Test]
        public async Task should_match_pinned_edition_before_any_identifier_short_circuit_or_fts()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author
            {
                Id = 5,
                Name = "Tamora Pierce",
                Path = "/audiobooks/Tamora Pierce"
            };

            var pinnedEdition = new Edition
            {
                Id = 100,
                BookId = 42,
                Title = "Alanna: The first adventure: song of the lioness #1",
                Monitored = true,
                ManualAdd = true,
                NarratorNames = new List<string> { "Steven Pacey" }
            };

            var book = new Book
            {
                Id = 42,
                Title = "Alanna: The First Adventure",
                Author = author,
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                Editions = new List<Edition> { pinnedEdition }
            };

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new ThrowingEditionFtsRepository(),
                bookService: new StubBookService(book),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Alanna - Song of the Lioness 01.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { pinnedEdition.Title } },
                    { "ARTIST", new List<string> { author.Name } },
                    { "ALBUMARTIST", new List<string> { "Narrated by Steven Pacey" } },
                    { "ASIN", new List<string> { "B0WRONG" } }
                }
            };

            var ctx = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true,
                TargetBookIds = new List<int> { 42 }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: author.Id, ctx);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].BookId, Is.EqualTo(book.Id));
            Assert.That(result.MatchedFiles[0].EditionId, Is.EqualTo(pinnedEdition.Id));
            Assert.That(result.MatchedFiles[0].AuthorName, Is.EqualTo(author.Name));
        }

        [Test]
        public async Task should_not_validate_a_pinned_edition_author_from_comment_text()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var author = new Author
            {
                Id = 5,
                Name = "Tamora Pierce",
                Path = "/audiobooks/Tamora Pierce"
            };
            var pinnedEdition = new Edition
            {
                Id = 100,
                BookId = 42,
                Title = "Alanna: The First Adventure",
                Monitored = true,
                ManualAdd = true,
                NarratorNames = new List<string> { "Trini Alvarado" }
            };
            var book = new Book
            {
                Id = 42,
                Title = pinnedEdition.Title,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                Editions = new List<Edition> { pinnedEdition }
            };
            var service = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new EmptyEditionFtsRepository(),
                bookService: new StubBookService(book),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Alanna.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new List<string> { pinnedEdition.Title },
                    ["NARRATOR"] = new List<string> { "Trini Alvarado" },
                    ["COMMENT"] = new List<string> { "Written by Tamora Pierce" }
                }
            };
            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = true,
                TargetBookIds = new List<int> { book.Id }
            };

            var result = await service.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: author.Id, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
        }
    }
}
