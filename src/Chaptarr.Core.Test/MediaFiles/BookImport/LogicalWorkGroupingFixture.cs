using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class LogicalWorkGroupingFixture
    {
        private sealed class StubEditionFtsRepository : IEditionFtsRepository
        {
            private readonly List<EditionFtsMatch> _results;

            public StubEditionFtsRepository(List<EditionFtsMatch> results)
            {
                _results = results ?? new List<EditionFtsMatch>();
            }

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => _results.Take(limit).ToList();
        }

        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null) { }
            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null) { }
            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null) { }
            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000) => new List<MatchingLogEntry>();
            public void ClearLogs() { }
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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            private readonly Dictionary<int, Book> _booksById;

            public StubBookService(IEnumerable<Book> books)
            {
                _booksById = (books ?? Array.Empty<Book>()).ToDictionary(b => b.Id);
            }

            public Book GetBook(int bookId) => _booksById.TryGetValue(bookId, out var book) ? book : null;
            public List<Book> GetBooks(IEnumerable<int> bookIds) => (bookIds ?? Array.Empty<int>()).Select(GetBook).Where(book => book != null).ToList();
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
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
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
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
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
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly Dictionary<int, Edition> _editionsById;

            public StubEditionService(IEnumerable<Edition> editions)
            {
                _editionsById = (editions ?? Array.Empty<Edition>()).ToDictionary(e => e.Id);
            }

            public Edition GetEdition(int id) => _editionsById.TryGetValue(id, out var edition) ? edition : null;
            public List<Edition> GetEditions(IEnumerable<int> ids)
            {
                var editionIds = (ids ?? Array.Empty<int>()).ToHashSet();
                return _editionsById.Values.Where(e => editionIds.Contains(e.Id)).ToList();
            }
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => _editionsById.Values.FirstOrDefault(e => string.Equals(e.ForeignEditionId, foreignEditionId, StringComparison.OrdinalIgnoreCase));
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => _editionsById.Values.FirstOrDefault(e => string.Equals(e.HardcoverEditionId, hardcoverEditionId, StringComparison.OrdinalIgnoreCase));
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => _editionsById.Values.FirstOrDefault(e => e.GoodreadsEditionId == goodreadsEditionId);
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => _editionsById.Values.FirstOrDefault(e => string.Equals(e.GoogleBooksEditionId, googleBooksEditionId, StringComparison.OrdinalIgnoreCase));
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => _editionsById.Values.FirstOrDefault(e => string.Equals(e.OpenLibraryEditionId, openLibraryEditionId, StringComparison.OrdinalIgnoreCase));
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => _editionsById.Values.Where(e => e.Monitored).ToList();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => GetEditionsByBook(bookId);
            public List<Edition> GetEditionsByBook(int bookId) => _editionsById.Values.Where(e => e.BookId == bookId).ToList();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
            {
                var ids = (bookIds ?? Array.Empty<int>()).ToHashSet();
                return _editionsById.Values.Where(e => ids.Contains(e.BookId)).ToList();
            }
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private static FileMatchingService CreateSut(
            List<EditionFtsMatch> candidates,
            List<Book> books,
            List<Edition> editions = null,
            BookMatchingStrictness strictness = BookMatchingStrictness.Balanced)
        {
            var logger = LogManager.GetCurrentClassLogger();

            return new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: strictness),
                authorService: new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(candidates),
                bookService: new StubBookService(books),
                editionService: editions != null ? new StubEditionService(editions) : null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
        }

        [Test]
        public void should_treat_same_provider_work_clones_as_one_title_candidate()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 100,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 101,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1001,
                    BookId = 100,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                },
                new EditionFtsMatch
                {
                    EditionId = 1002,
                    BookId = 101,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 9.5,
                    DurationSeconds = 36000
                }
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter 5/Harry Potter 5.m4b",
                DurationSeconds = 36010,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Harry Potter Order of the Phoenix" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(1001));
            Assert.That(match.BookId, Is.EqualTo(100));
        }

        [Test]
        public void should_cluster_same_logical_work_clones_when_base_book_id_is_missing_but_provider_ids_overlap()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 110,
                    Title = "Harry Potter and the Order of the Phoenix",
                    GoodreadsWorkId = "gr:work:phoenix",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 111,
                    Title = "Harry Potter and the Order of the Phoenix",
                    GoodreadsWorkId = "gr:work:phoenix",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1101,
                    BookId = 110,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 9.0,
                    DurationSeconds = 36000
                },
                new EditionFtsMatch
                {
                    EditionId = 1111,
                    BookId = 111,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                },
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter 5/Harry Potter 5.m4b",
                DurationSeconds = 36010,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Harry Potter Order of the Phoenix" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(111), "provider-id fallback should still cluster clone rows into one logical work");
            Assert.That(match.EditionId, Is.EqualTo(1111));
        }

        [Test]
        public void should_select_native_representative_when_multipart_chapter_cannot_prove_full_book_duration()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 130,
                    Title = "The Casual Vacancy",
                    BaseBookId = "casual-vacancy",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1301, BookId = 130, Title = "The Casual Vacancy", ReadingFormatId = 2, DurationSeconds = 3600 },
                new Edition { Id = 1302, BookId = 130, Title = "The Casual Vacancy: Alternate", ReadingFormatId = 2, DurationSeconds = 7200, NarratorNames = new List<string> { "Other Narrator" } }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1301,
                    BookId = 130,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 10.0,
                    DurationSeconds = 3600,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1302,
                    BookId = 130,
                    EditionTitle = "The Casual Vacancy: Alternate",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 9.0,
                    DurationSeconds = 7200,
                    ReadingFormatId = 2,
                    NarratorNames = "Other Narrator"
                }
            };

            var sut = CreateSut(candidates, books, editions);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/The Casual Vacancy/01.m4b",
                DurationSeconds = 1200,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Chapter 1" } },
                    { "ALBUM", new List<string> { "The Casual Vacancy" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(1301), "one chapter duration must remain neutral while the existing title scorer selects the representative");
            Assert.That(match.MatchedVia, Is.EqualTo("undistinguished_audiobook_edition"));
            Assert.That(match.Provenance.NeutralSignals.Any(signal => signal.Type == "duration"), Is.True);
            Assert.That(match.Provenance.ConflictingSignals.Any(signal => signal.Type == "duration"), Is.False);
        }

        [Test]
        public async System.Threading.Tasks.Task manual_preview_should_retry_group_path_only_after_samples_agree_on_provider_work()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 131,
                    Title = "Some Book",
                    BaseBookId = "hc:some-book",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1311,
                    BookId = 131,
                    EditionTitle = "Some Book",
                    BookTitle = "Some Book",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    ForeignEditionId = "az:native-audio",
                    MatchScore = 10.0,
                    DurationSeconds = 2000,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1312,
                    BookId = 131,
                    EditionTitle = "Some Book",
                    BookTitle = "Some Book",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    ForeignEditionId = "gr:ebook-copy",
                    MatchScore = 10.0,
                    ReadingFormatId = 3
                }
            };

            var sut = CreateSut(candidates, books);
            var files = Enumerable.Range(1, 2)
                .Select(part => new DiscoveredFileWithMetadata
                {
                    Path = $"/audiobooks/J.K. Rowling/Some Book/Some Book ({part}).mp3",
                    DurationSeconds = 1000,
                    AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "TITLE", new List<string> { $"Some Book (Unabridged) Part {part}" } },
                        { "ARTIST", new List<string> { "J.K. Rowling" } }
                    }
                })
                .ToArray();

            var result = await sut.MatchFilesToLibraryAsync(
                files,
                restrictToAuthorId: null,
                MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(match => match.EditionId == 1311), Is.True,
                    "unanimous per-file work identity should permit the group total to select the native audiobook");
            });
        }

        [Test]
        public void should_match_author_restricted_multifile_audiobook_by_group_total_duration()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 140,
                    Title = "The Casual Vacancy",
                    BaseBookId = "casual-vacancy",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1401, BookId = 140, Title = "The Casual Vacancy", ReadingFormatId = 2, DurationSeconds = 3600 },
                new Edition { Id = 1402, BookId = 140, Title = "The Casual Vacancy: Alternate", ReadingFormatId = 2, DurationSeconds = 7200, NarratorNames = new List<string> { "Other Narrator" } }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1401,
                    BookId = 140,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 10.0,
                    DurationSeconds = 3600,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1402,
                    BookId = 140,
                    EditionTitle = "The Casual Vacancy: Alternate",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 9.0,
                    DurationSeconds = 7200,
                    ReadingFormatId = 2,
                    NarratorNames = "Other Narrator"
                }
            };

            var sut = CreateSut(candidates, books, editions);
            var files = new[]
            {
                ChapterFile("/audiobooks/J.K. Rowling/The Casual Vacancy/01.m4b", "Chapter 1", 1200),
                ChapterFile("/audiobooks/J.K. Rowling/The Casual Vacancy/02.m4b", "Chapter 2", 1200),
                ChapterFile("/audiobooks/J.K. Rowling/The Casual Vacancy/03.m4b", "Chapter 3", 1200)
            };

            var result = sut.MatchFilesToLibraryAsync(files, 25, MatchingContextPresets.ForScanScopedRematch()).GetAwaiter().GetResult();

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(3));
            Assert.That(result.MatchedFiles.Select(m => m.EditionId), Is.All.EqualTo(1401));
        }

        private static DiscoveredFileWithMetadata ChapterFile(string path, string title, int durationSeconds)
        {
            return new DiscoveredFileWithMetadata
            {
                Path = path,
                DurationSeconds = durationSeconds,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { title } },
                    { "ALBUM", new List<string> { "The Casual Vacancy" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } }
                }
            };
        }

        [Test]
        public void should_keep_highest_scored_logical_work_candidate_and_leave_destination_routing_for_later()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 120,
                    Title = "Dreamsongs",
                    TitleSlug = "dreamsongs",
                    BaseBookId = "dreamsongs",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    AnyEditionOk = true
                },
                new Book
                {
                    Id = 121,
                    Title = "Dreamsongs",
                    TitleSlug = "dreamsongs_wanted_77",
                    BaseBookId = "dreamsongs",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    AnyEditionOk = false,
                    WantedNarratorId = 77
                }
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1201,
                    BookId = 120,
                    Title = "Dreamsongs",
                    ForeignEditionId = "gr:ed:dreamsongs-main"
                },
                new Edition
                {
                    Id = 1211,
                    BookId = 121,
                    Title = "Dreamsongs",
                    ForeignEditionId = "gr:ed:dreamsongs-main"
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1211,
                    BookId = 121,
                    EditionTitle = "Dreamsongs",
                    BookTitle = "Dreamsongs",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                },
                new EditionFtsMatch
                {
                    EditionId = 1201,
                    BookId = 120,
                    EditionTitle = "Dreamsongs",
                    BookTitle = "Dreamsongs",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 9.5,
                    DurationSeconds = 36000
                }
            };

            var sut = CreateSut(candidates, books, editions);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Dreamsongs/Dreamsongs.m4b",
                DurationSeconds = 36010,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Dreamsongs" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(121), "identity-stage matching should keep the highest-scored candidate and not silently swap rows post-scoring");
            Assert.That(match.EditionId, Is.EqualTo(1211));
        }

        [Test]
        public void should_not_let_lower_scored_fileless_candidate_override_higher_scored_match_from_different_work()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 122,
                    Title = "Dreamsongs",
                    BaseBookId = "dreamsongs-main",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    BookFiles = new List<BookFile> { new BookFile { Id = 1, Path = "/library/Dreamsongs.m4b" } }
                },
                new Book
                {
                    Id = 123,
                    Title = "Dreamsongs",
                    BaseBookId = "dreamsongs-sidecar",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1221,
                    BookId = 122,
                    EditionTitle = "Dreamsongs",
                    BookTitle = "Dreamsongs",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                },
                new EditionFtsMatch
                {
                    EditionId = 1231,
                    BookId = 123,
                    EditionTitle = "Dreamsongs",
                    BookTitle = "Dreamsongs",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 1.0,
                    DurationSeconds = 36000
                }
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Dreamsongs/Dreamsongs.m4b",
                DurationSeconds = 36010,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Dreamsongs" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(122), "a lower-scored fileless row from a different work should not override the top-ranked candidate");
            Assert.That(match.EditionId, Is.EqualTo(1221));
        }

        [Test]
        public void should_hard_reject_wrong_audiobook_narrator_when_same_work_sibling_matches_tags()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 130,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 131,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1301,
                    BookId = 130,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    MatchScore = 9.5,
                    DurationSeconds = 36000
                },
                new EditionFtsMatch
                {
                    EditionId = 1311,
                    BookId = 131,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Stephen Fry",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                }
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter 5/Harry Potter 5.m4b",
                DurationSeconds = 36005,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Harry Potter and the Order of the Phoenix" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } },
                    { "ARTIST", new List<string> { "Jim Dale" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(130));
            Assert.That(match.EditionId, Is.EqualTo(1301));
        }

        [Test]
        public void should_reject_pocket_potters_when_narrator_is_missing_and_order_of_the_phoenix_has_narrator_match()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 150,
                    Title = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                    BaseBookId = "pocket-potters-harry",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    SeriesName = "Pocket Potters",
                    SeriesPosition = "1"
                },
                new Book
                {
                    Id = 151,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    SeriesName = "Harry Potter",
                    SeriesPosition = "5"
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1501,
                    BookId = 150,
                    EditionTitle = "Pocket Potters: Harry Potter",
                    BookTitle = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 20.0
                },
                new EditionFtsMatch
                {
                    EditionId = 1511,
                    BookId = 151,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    MatchScore = 10.0
                }
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter and the Order of the Phoenix/Harry Potter and the Order of the Phoenix (1).m4b",
                DurationSeconds = 126664,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Harry Potter 5 - The Order of the Phoenix" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } },
                    { "ARTIST", new List<string> { "Jim Dale" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(151));
            Assert.That(match.EditionId, Is.EqualTo(1511));
        }

        [Test]
        public void should_use_ordered_title_alignment_when_only_weak_connectors_are_missing()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 152,
                    Title = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                    BaseBookId = "pocket-potters-harry",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    SeriesName = "Pocket Potters",
                    SeriesPosition = "1"
                },
                new Book
                {
                    Id = 153,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    SeriesName = "Harry Potter",
                    SeriesPosition = "5"
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1521,
                    BookId = 152,
                    EditionTitle = "Pocket Potters: Harry Potter",
                    BookTitle = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    MatchScore = 20.0
                },
                new EditionFtsMatch
                {
                    EditionId = 1531,
                    BookId = 153,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    MatchScore = 10.0
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter and the Order of the Phoenix/Harry Potter and the Order of the Phoenix (1).m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Harry Potter 5 - The Order of the Phoenix" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } },
                    { "ARTIST", new List<string> { "Jim Dale" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(153));
            Assert.That(match.EditionId, Is.EqualTo(1531));
        }

        [Test]
        public void should_compare_group_winners_globally_instead_of_accepting_the_first_group_that_passes()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 160,
                    Title = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                    BaseBookId = "pocket-potters-harry",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    SeriesName = "Pocket Potters",
                    SeriesPosition = "1"
                },
                new Book
                {
                    Id = 161,
                    Title = "Harry Potter and the Order of the Phoenix",
                    BaseBookId = "hp5",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook,
                    SeriesName = "Harry Potter",
                    SeriesPosition = "5"
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1601,
                    BookId = 160,
                    EditionTitle = "Pocket Potters: Harry Potter",
                    BookTitle = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    MatchScore = 20.0,
                    DurationSeconds = 48000
                },
                new EditionFtsMatch
                {
                    EditionId = 1611,
                    BookId = 161,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                }
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter and the Order of the Phoenix/Harry Potter and the Order of the Phoenix (1).m4b",
                DurationSeconds = 36010,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Harry Potter and the Order of the Phoenix" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } },
                    { "ARTIST", new List<string> { "Jim Dale" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(161));
            Assert.That(match.EditionId, Is.EqualTo(1611));
        }

        [Test]
        public void should_allow_missing_audiobook_narrator_when_title_matches_and_duration_is_within_looser_tolerance()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 140,
                    Title = "Dreamer of Dune",
                    BaseBookId = "dreamer",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1401,
                    BookId = 140,
                    EditionTitle = "Dreamer of Dune",
                    BookTitle = "Dreamer of Dune",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Scott Brick",
                    MatchScore = 10.0,
                    DurationSeconds = 36000
                }
            };

            var sut = CreateSut(candidates, books);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Brian Herbert/Dreamer of Dune/Dreamer of Dune.m4b",
                DurationSeconds = 36280,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Dreamer of Dune" } },
                    { "ALBUMARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(140));
            Assert.That(match.EditionId, Is.EqualTo(1401));
        }

        [Test]
        public void should_not_require_duration_fallback_for_same_work_same_narrator_duplicates()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 170,
                    Title = "Dune",
                    BaseBookId = "dune",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 171,
                    Title = "Dune",
                    BaseBookId = "dune",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1701,
                    BookId = 170,
                    EditionTitle = "Dune",
                    BookTitle = "Dune",
                    AuthorId = 25,
                    AuthorName = "Frank Herbert",
                    NarratorNames = "Scott Brick",
                    MatchScore = 10.0,
                    DurationSeconds = 31500
                },
                new EditionFtsMatch
                {
                    EditionId = 1711,
                    BookId = 171,
                    EditionTitle = "Dune",
                    BookTitle = "Dune",
                    AuthorId = 25,
                    AuthorName = "Frank Herbert",
                    NarratorNames = "Scott Brick",
                    MatchScore = 9.5,
                    DurationSeconds = 30780
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Dune/Dune (17).mp3",
                DurationSeconds = 720,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Dune" } },
                    { "ALBUMARTIST", new List<string> { "Frank Herbert" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(new[] { 170, 171 }, Does.Contain(match.BookId));
            Assert.That(new[] { 1701, 1711 }, Does.Contain(match.EditionId));
        }

        [Test]
        public void should_require_two_distinct_fields_for_self_narrated_evidence_when_same_work_sibling_has_different_narrator()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 172,
                    Title = "Great by Choice",
                    BaseBookId = "great-choice",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 173,
                    Title = "Great by Choice",
                    BaseBookId = "great-choice",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1721,
                    BookId = 172,
                    EditionTitle = "Great by Choice",
                    BookTitle = "Great by Choice",
                    AuthorId = 25,
                    AuthorName = "Jim Collins",
                    NarratorNames = "Jim Collins",
                    MatchScore = 10.0,
                    DurationSeconds = 31500,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1731,
                    BookId = 173,
                    EditionTitle = "Great by Choice",
                    BookTitle = "Great by Choice",
                    AuthorId = 25,
                    AuthorName = "Jim Collins",
                    NarratorNames = "Michael Beck",
                    MatchScore = 9.5,
                    DurationSeconds = 31500,
                    ReadingFormatId = 2
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Jim Collins/Great by Choice/Great by Choice (18).mp3",
                DurationSeconds = 720,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Great by Choice" } },
                    { "ALBUMARTIST", new List<string> { "Jim Collins" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Null);
        }

        [Test]
        public void should_accept_self_narrated_evidence_when_author_name_appears_in_two_distinct_fields()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 174,
                    Title = "Great by Choice",
                    BaseBookId = "great-choice",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 175,
                    Title = "Great by Choice",
                    BaseBookId = "great-choice",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1741,
                    BookId = 174,
                    EditionTitle = "Great by Choice",
                    BookTitle = "Great by Choice",
                    AuthorId = 25,
                    AuthorName = "Jim Collins",
                    NarratorNames = "Jim Collins",
                    MatchScore = 10.0,
                    DurationSeconds = 31500,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1751,
                    BookId = 175,
                    EditionTitle = "Great by Choice",
                    BookTitle = "Great by Choice",
                    AuthorId = 25,
                    AuthorName = "Jim Collins",
                    NarratorNames = "Michael Beck",
                    MatchScore = 9.5,
                    DurationSeconds = 31500,
                    ReadingFormatId = 2
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Jim Collins/Great by Choice/Great by Choice (19).mp3",
                DurationSeconds = 720,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Great by Choice" } },
                    { "ALBUMARTIST", new List<string> { "Jim Collins" } },
                    { "ARTIST", new List<string> { "Jim Collins" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(174));
            Assert.That(match.EditionId, Is.EqualTo(1741));
        }

        [Test]
        public void should_not_let_same_author_other_books_with_matching_narrator_veto_thin_title_proven_audio_edition()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 180,
                    Title = "Attack Surface",
                    BaseBookId = "attack-surface",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 181,
                    Title = "Pirate Cinema",
                    BaseBookId = "pirate-cinema",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1801,
                    BookId = 180,
                    EditionTitle = "Attack Surface",
                    BookTitle = "Attack Surface",
                    AuthorId = 25,
                    AuthorName = "Cory Doctorow",
                    MatchScore = 10.0,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1811,
                    BookId = 181,
                    EditionTitle = "Pirate Cinema",
                    BookTitle = "Pirate Cinema",
                    AuthorId = 25,
                    AuthorName = "Cory Doctorow",
                    NarratorNames = "Amber Benson",
                    MatchScore = 8.0,
                    ReadingFormatId = 2
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Cory Doctorow/Attack Surface/Attack Surface.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Attack Surface" } },
                    { "ARTIST", new List<string> { "Cory Doctorow" } },
                    { "ALBUMARTIST", new List<string> { "Amber Benson" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(180));
            Assert.That(match.EditionId, Is.EqualTo(1801));
        }

        [Test]
        public void should_still_prefer_same_work_narrator_proven_audio_edition_over_thin_audio_edition()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 182,
                    Title = "Attack Surface",
                    BaseBookId = "attack-surface",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 183,
                    Title = "Attack Surface",
                    BaseBookId = "attack-surface",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1821,
                    BookId = 182,
                    EditionTitle = "Attack Surface",
                    BookTitle = "Attack Surface",
                    AuthorId = 25,
                    AuthorName = "Cory Doctorow",
                    MatchScore = 10.0,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1831,
                    BookId = 183,
                    EditionTitle = "Attack Surface",
                    BookTitle = "Attack Surface",
                    AuthorId = 25,
                    AuthorName = "Cory Doctorow",
                    NarratorNames = "Amber Benson",
                    MatchScore = 8.0,
                    ReadingFormatId = 2
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Cory Doctorow/Attack Surface/Attack Surface.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Attack Surface" } },
                    { "ARTIST", new List<string> { "Cory Doctorow" } },
                    { "ALBUMARTIST", new List<string> { "Amber Benson" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(183));
            Assert.That(match.EditionId, Is.EqualTo(1831));
        }

        [Test]
        public void should_not_treat_same_work_ebook_as_distinct_audiobook_sibling_for_multipart_track()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 190,
                    Title = "Rules of Engagement",
                    BaseBookId = "rules-of-engagement",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1901,
                    BookId = 190,
                    EditionTitle = "Rules of Engagement",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    NarratorNames = "Jamie Renell",
                    MatchScore = 10.0,
                    DurationSeconds = 31680,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1902,
                    BookId = 190,
                    EditionTitle = "Rules of Engagement: A Novel",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    MatchScore = 9.0,
                    ReadingFormatId = 3
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/David Bruns/Rules of Engagement/Rules of Engagement (14).mp3",
                DurationSeconds = 371,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Rules of Engagement" } },
                    { "ARTIST", new List<string> { "David Bruns" } },
                    { "TITLE", new List<string> { "Rules of Engagement" } },
                    { "TRCK", new List<string> { "14" } },
                    { "COMMENT", new List<string> { "Narrated by Jamie Renell" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(190));
            Assert.That(match.EditionId, Is.EqualTo(1901));
            Assert.That(match.MatchedVia, Is.Null);
        }

        [Test]
        public void should_select_native_representative_for_strict_multipart_track_without_edition_corrob()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 191,
                    Title = "Rules of Engagement",
                    BaseBookId = "rules-of-engagement",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1911,
                    BookId = 191,
                    EditionTitle = "Rules of Engagement",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    NarratorNames = "Jamie Renell",
                    MatchScore = 10.0,
                    DurationSeconds = 31680,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1912,
                    BookId = 191,
                    EditionTitle = "Rules of Engagement: Alternate Narration",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    NarratorNames = "Other Narrator",
                    MatchScore = 9.0,
                    DurationSeconds = 31680,
                    ReadingFormatId = 2
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/David Bruns/Rules of Engagement/Rules of Engagement (14).mp3",
                DurationSeconds = 371,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Rules of Engagement" } },
                    { "ARTIST", new List<string> { "David Bruns" } },
                    { "TITLE", new List<string> { "Rules of Engagement" } },
                    { "TRCK", new List<string> { "14" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(1911));
            Assert.That(match.MatchedVia, Is.EqualTo("undistinguished_audiobook_edition"));
        }

        [Test]
        public void should_use_ebook_representative_when_ebook_edition_title_is_in_tags_and_audio_is_unproven()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 192,
                    Title = "Rules of Engagement",
                    BaseBookId = "rules-of-engagement",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1921,
                    BookId = 192,
                    EditionTitle = "Rules of Engagement",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    NarratorNames = "Jamie Renell",
                    MatchScore = 10.0,
                    DurationSeconds = 31680,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1922,
                    BookId = 192,
                    EditionTitle = "Rules of Engagement: A Novel",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    MatchScore = 9.0,
                    ReadingFormatId = 3
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/David Bruns/Rules of Engagement/Rules of Engagement (14).mp3",
                DurationSeconds = 371,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Rules of Engagement" } },
                    { "ARTIST", new List<string> { "David Bruns" } },
                    { "TITLE", new List<string> { "Rules of Engagement: A Novel" } },
                    { "TRCK", new List<string> { "14" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(192));
            Assert.That(match.EditionId, Is.EqualTo(1922));
            Assert.That(match.MatchedVia, Is.EqualTo("escape_hatch"));
        }

        [Test]
        public void should_keep_audio_edition_when_duration_proves_it_even_if_ebook_title_is_in_tags()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 193,
                    Title = "Rules of Engagement",
                    BaseBookId = "rules-of-engagement",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1931,
                    BookId = 193,
                    EditionTitle = "Rules of Engagement",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    NarratorNames = "Jamie Renell",
                    MatchScore = 10.0,
                    DurationSeconds = 31680,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1932,
                    BookId = 193,
                    EditionTitle = "Rules of Engagement: A Novel",
                    BookTitle = "Rules of Engagement",
                    AuthorId = 25,
                    AuthorName = "David Bruns",
                    MatchScore = 9.0,
                    ReadingFormatId = 3
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/David Bruns/Rules of Engagement/Rules of Engagement.m4b",
                DurationSeconds = 31680,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Rules of Engagement: A Novel" } },
                    { "ARTIST", new List<string> { "David Bruns" } },
                    { "TITLE", new List<string> { "Rules of Engagement: A Novel" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(193));
            Assert.That(match.EditionId, Is.EqualTo(1931));
            Assert.That(match.MatchedVia, Is.Null);
        }

        [Test]
        public void should_use_ebook_representative_for_indistinguishable_audio_narration_tie()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 194,
                    Title = "Some Book",
                    BaseBookId = "some-book",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1941,
                    BookId = 194,
                    EditionTitle = "Some Book",
                    BookTitle = "Some Book",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    NarratorNames = "Narrator One",
                    MatchScore = 10.0,
                    DurationSeconds = 36000,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1942,
                    BookId = 194,
                    EditionTitle = "Some Book",
                    BookTitle = "Some Book",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    NarratorNames = "Narrator Two",
                    MatchScore = 9.5,
                    DurationSeconds = 36000,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1943,
                    BookId = 194,
                    EditionTitle = "Some Book",
                    BookTitle = "Some Book",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    MatchScore = 9.0,
                    ReadingFormatId = 3
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Test Author/Some Book/Some Book (02).mp3",
                DurationSeconds = 420,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Some Book" } },
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "TITLE", new List<string> { "Some Book" } },
                    { "TRCK", new List<string> { "2" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(194));
            Assert.That(match.EditionId, Is.EqualTo(1943));
            Assert.That(match.MatchedVia, Is.EqualTo("escape_hatch"));
        }

        [Test]
        public void should_use_print_representative_when_no_ebook_representative_is_evidenced()
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = 195,
                    Title = "Shadow Book",
                    BaseBookId = "shadow-book",
                    AuthorId = 25,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var candidates = new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1951,
                    BookId = 195,
                    EditionTitle = "Shadow Book",
                    BookTitle = "Shadow Book",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    NarratorNames = "Narrator One",
                    MatchScore = 10.0,
                    DurationSeconds = 36000,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 1952,
                    BookId = 195,
                    EditionTitle = "Shadow Book: Collector's Edition",
                    BookTitle = "Shadow Book",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    MatchScore = 9.0,
                    ReadingFormatId = 1
                }
            };

            var sut = CreateSut(candidates, books, strictness: BookMatchingStrictness.Strict);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Test Author/Shadow Book/Shadow Book (01).mp3",
                DurationSeconds = 420,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { "Shadow Book" } },
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "TITLE", new List<string> { "Shadow Book: Collector's Edition" } },
                    { "TRCK", new List<string> { "1" } }
                }
            };

            var match = sut.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(195));
            Assert.That(match.EditionId, Is.EqualTo(1952));
            Assert.That(match.MatchedVia, Is.EqualTo("escape_hatch"));
        }

    }
}
