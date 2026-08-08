using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
    public class MatchingProvenancePopulationFixture
    {
        private sealed class CapturingMatchingUploadLogger : IMatchingUploadLogger
        {
            public readonly List<(string FilePath, MatchResult Result, Dictionary<string, List<string>> Tags)> FinalDecisions = new();

            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null)
            {
                FinalDecisions.Add((filePath, matchResult, extractedTags));
            }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
                FinalDecisions.Add((filePath, new MatchResult
                {
                    Success = decision == "MATCHED",
                    Reason = reason,
                    Decision = decision,
                    AuthorMatched = authorMatched,
                    BookMatched = bookMatched,
                    EditionMatched = editionMatched,
                    Rejections = rejections
                }, extractedTags));
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000) => new();

            public void ClearLogs()
            {
                FinalDecisions.Clear();
            }
        }

        private sealed class ThrowingEditionFtsRepository : IEditionFtsRepository
        {
            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => throw new AssertionException("FTS should not run for pinned first-crack success");
        }

        private sealed class PathSensitiveEditionFtsRepository : IEditionFtsRepository
        {
            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                return Evaluate(tokens);
            }

            private static List<EditionFtsMatch> Evaluate(IEnumerable<string> tokens)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.ToLowerInvariant())
                    .ToList();

                if (tokenList.Contains("frank") && tokenList.Contains("herbert") && tokenList.Contains("whipping") && tokenList.Contains("star"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 675,
                            BookId = 224,
                            EditionTitle = "Whipping Star",
                            BookTitle = "Whipping Star",
                            AuthorId = 6,
                            AuthorName = "Frank Herbert",
                            MatchScore = 12.0
                        }
                    };
                }

                return new List<EditionFtsMatch>();
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authorsById;

            public StubAuthorService(params Author[] authors)
            {
                _authorsById = (authors ?? Array.Empty<Author>()).ToDictionary(a => a.Id);
            }

            public Author GetAuthor(int authorId) => _authorsById.TryGetValue(authorId, out var author) ? author : null;
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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            private readonly Dictionary<int, Book> _booksById;

            public StubBookService(params Book[] books)
            {
                _booksById = (books ?? Array.Empty<Book>()).ToDictionary(b => b.Id);
            }

            public Book GetBook(int bookId) => _booksById.TryGetValue(bookId, out var book) ? book : null;
            public List<Book> GetBooks(IEnumerable<int> bookIds) => (bookIds ?? Array.Empty<int>()).Select(GetBook).Where(b => b != null).ToList();
            public List<Book> GetBooksByAuthor(int authorId) => _booksById.Values.Where(b => b.AuthorId == authorId).ToList();
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
            public List<Book> GetAllBooks() => _booksById.Values.ToList();
            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
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

            public StubEditionService(params Edition[] editions)
            {
                _editionsById = (editions ?? Array.Empty<Edition>()).ToDictionary(e => e.Id);
            }

            public Edition GetEdition(int id) => _editionsById.TryGetValue(id, out var edition) ? edition : null;
            public List<Edition> GetEditions(IEnumerable<int> ids)
            {
                var wanted = (ids ?? Array.Empty<int>()).ToHashSet();
                return _editionsById.Values.Where(e => wanted.Contains(e.Id)).ToList();
            }

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
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        [Test]
        public async Task pinned_first_crack_should_emit_full_provenance()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var matchingLogger = new CapturingMatchingUploadLogger();
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
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                Editions = new List<Edition> { pinnedEdition }
            };

            var sut = new FileMatchingService(
                matchingLogger: matchingLogger,
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
                editionService: new StubEditionService(pinnedEdition),
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Alanna/Part 01.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { pinnedEdition.Title } },
                    { "ALBUM", new List<string> { pinnedEdition.Title } },
                    { "ARTIST", new List<string> { author.Name } },
                    { "ALBUMARTIST", new List<string> { "Narrated by Steven Pacey" } },
                    { "COMMENT", new List<string> { "From the author of another series" } },
                    { "MP4:©cpy", new List<string> { "© 1990 another author and series" } },
                    { "XIPH:COPYRIGHT", new List<string> { "Copyright boilerplate" } },
                    { "rights", new List<string> { "All rights reserved" } },
                    { "ASIN", new List<string> { "B0WRONG" } }
                }
            };
            var secondFile = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Alanna/Part 02.m4b",
                AllTags = file.AllTags
            };

            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = false,
                TargetBookIds = new List<int> { book.Id }
            };

            var result = await sut.MatchFilesToLibraryAsync(new[] { file, secondFile }, author.Id, context);

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(2));
            var finalDecision = matchingLogger.FinalDecisions.Last().Result;
            Assert.That(finalDecision.Decision, Is.EqualTo("MATCHED"));
            Assert.That(finalDecision.PinnedTargetResult, Is.EqualTo("matched"));
            Assert.That(finalDecision.PinnedTargetFailure, Is.Null);
            Assert.That(finalDecision.MatchedEditionTitle, Is.EqualTo(pinnedEdition.Title));
            Assert.That(finalDecision.MatchedEditionNarrators, Is.EquivalentTo(new[] { "Steven Pacey" }));
            Assert.That(finalDecision.AuthorProvedBy.Select(e => e.Field), Has.Member("ARTIST"));
            Assert.That(finalDecision.BookProvedBy.Select(e => e.Field), Has.Member("TITLE"));
            Assert.That(finalDecision.NarratorProvedBy.Select(e => e.Field), Has.Member("ALBUMARTIST"));
            var provenance = result.MatchedFiles.First().Provenance;
            Assert.That(provenance.Mode, Is.EqualTo("Balanced"));
            Assert.That(provenance.Route, Is.EqualTo("pinned_target/embedded_tags"));
            Assert.That(provenance.SchemaVersion, Is.EqualTo(2));
            Assert.That(provenance.SupportingSignals.Select(signal => signal.Type), Does.Contain("title"));
            Assert.That(provenance.SupportingSignals.Select(signal => signal.Type), Does.Contain("author"));
            Assert.That(provenance.SupportingSignals.Select(signal => signal.Type), Does.Contain("narrator"));
            Assert.That(provenance.ExcludedSignals.Select(signal => signal.Field), Does.Contain("COMMENT"));
            Assert.That(provenance.ExcludedSignals.Select(signal => signal.Field), Does.Contain("MP4:©cpy"));
            Assert.That(provenance.ExcludedSignals.Select(signal => signal.Field), Does.Contain("XIPH:COPYRIGHT"));
            Assert.That(provenance.ExcludedSignals.Select(signal => signal.Field), Does.Contain("rights"));
            var titleEvidence = provenance.EvidenceValues.Single(value => value.Value == pinnedEdition.Title);
            Assert.That(titleEvidence.Fields, Does.Contain("TITLE"));
            Assert.That(titleEvidence.Fields, Does.Contain("ALBUM"));
            Assert.That(titleEvidence.Ranges.Select(range => range.Type), Does.Contain("title"));
            Assert.That(titleEvidence.Ranges.All(range => range.End <= titleEvidence.Value.Length), Is.True);
            Assert.That(result.MatchedFiles.Select(match => match.Provenance?.DecisionId),
                Has.All.EqualTo(provenance.DecisionId));
            Assert.That(finalDecision.Provenance.DecisionId, Is.EqualTo(provenance.DecisionId));
        }

        [Test]
        public async Task unrestricted_path_match_should_record_path_fallback_used()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var matchingLogger = new CapturingMatchingUploadLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" };
            var book = new Book { Id = 224, Title = "Whipping Star", Author = author, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var edition = new Edition { Id = 675, BookId = book.Id, Title = "Whipping Star", NarratorNames = new List<string> { "Simon Vance" } };

            var sut = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new PathSensitiveEditionFtsRepository(),
                bookService: new StubBookService(book),
                editionService: new StubEditionService(edition),
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };

            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            };

            var result = await sut.MatchFilesToLibraryAsync(new[] { file }, null, context);

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            var finalDecision = matchingLogger.FinalDecisions.Last().Result;
            Assert.That(finalDecision.Decision, Is.EqualTo("MATCHED"));
            Assert.That(finalDecision.PathFallbackUsed, Is.True);
            Assert.That(finalDecision.PathFallbackSuppressedReason, Is.Null);
            Assert.That(finalDecision.AuthorProvedBy.Select(e => e.Field), Has.Member("PATH:AUTHOR_VALUE"));
            Assert.That(finalDecision.BookProvedBy.Any(e => e.Field == "PATH:BOOK_VALUE" || e.Field == "PATH:FILE_VALUE"), Is.True);
            Assert.That(result.MatchedFiles.Single().Provenance.Mode, Is.EqualTo("Balanced"));
            Assert.That(result.MatchedFiles.Single().Provenance.Route, Is.EqualTo("global/path_tags"));
            Assert.That(result.MatchedFiles.Single().Provenance.Summary, Is.EqualTo("Matched from the file path"));
            Assert.That(result.MatchedFiles.Single().Provenance.SupportingSignals.Select(signal => signal.Type), Does.Contain("title"));
            Assert.That(result.MatchedFiles.Single().Provenance.EvidenceValues, Is.Not.Empty);
            Assert.That(result.MatchedFiles.Single().Provenance.EvidenceValues,
                Has.Some.Matches<MatchEvidenceValue>(value => value.Source == "path"));
            Assert.That(result.MatchedFiles.Single().Provenance.EvidenceValues,
                Has.Some.Matches<MatchEvidenceValue>(value => value.Source == "filename"));
        }

        [Test]
        public async Task comment_only_metadata_should_remain_excluded_and_record_a_path_only_decision()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var matchingLogger = new CapturingMatchingUploadLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" };
            var book = new Book { Id = 224, Title = "Whipping Star", Author = author, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var edition = new Edition { Id = 675, BookId = book.Id, Title = "Whipping Star", NarratorNames = new List<string> { "Simon Vance" } };

            var sut = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new PathSensitiveEditionFtsRepository(),
                bookService: new StubBookService(book),
                editionService: new StubEditionService(edition),
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["COMMENT"] = new List<string> { "From the author who brought you Dune" }
                }
            };

            var result = await sut.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            var provenance = result.MatchedFiles.Single().Provenance;
            Assert.That(provenance.Route, Is.EqualTo("global/path_tags"));
            Assert.That(provenance.Summary, Is.EqualTo("Matched from the file path"));
            Assert.That(provenance.ExcludedSignals.Select(signal => signal.Field), Does.Contain("COMMENT"));
            Assert.That(provenance.EvidenceValues, Has.None.Matches<MatchEvidenceValue>(value => value.Source == "embedded_tag"));
            Assert.That(provenance.EvidenceValues, Has.Some.Matches<MatchEvidenceValue>(value => value.Source == "path"));
        }

        [Test]
        public async Task supplemental_path_match_should_preserve_embedded_title_and_path_author_provenance()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var matchingLogger = new CapturingMatchingUploadLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" };
            var book = new Book { Id = 224, Title = "Whipping Star", Author = author, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
            var edition = new Edition { Id = 675, BookId = book.Id, Title = "Whipping Star", NarratorNames = new List<string> { "Simon Vance" } };

            var sut = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new PathSensitiveEditionFtsRepository(),
                bookService: new StubBookService(book),
                editionService: new StubEditionService(edition),
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Whipping Star" } }
                }
            };

            var result = await sut.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            var provenance = result.MatchedFiles.Single().Provenance;
            Assert.That(provenance.Route, Is.EqualTo("global/supplemental_path"));
            Assert.That(provenance.Summary, Is.EqualTo("Matched from file tags and the file path"));
            Assert.That(provenance.SupportingSignals.Any(signal =>
                signal.Type == "title" && signal.Source == "embedded_tag" && signal.Field == "TITLE"), Is.True);
            Assert.That(provenance.SupportingSignals.Any(signal =>
                signal.Type == "author" && signal.Source == "path" && signal.Field == "PATH:AUTHOR_VALUE"), Is.True);
            Assert.That(provenance.NeutralSignals.Any(signal =>
                signal.Type == "author" &&
                signal.Source == "embedded_tag" &&
                signal.Detail.Contains("No embedded tag value supplied author evidence", StringComparison.Ordinal)), Is.True);
            Assert.That(provenance.EvidenceValues.Any(value =>
                value.Source == "embedded_tag" && value.Value == "Whipping Star"), Is.True);
            Assert.That(provenance.EvidenceValues.Any(value =>
                value.Source == "path" && value.Value == "Frank Herbert"), Is.True);
        }

        [Test]
        public async Task author_restricted_unmatched_should_record_path_fallback_suppressed_reason()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var matchingLogger = new CapturingMatchingUploadLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var author = new Author { Id = 31, Name = "Brian Herbert", Path = "/audiobooks/Brian Herbert" };

            var sut = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new PathSensitiveEditionFtsRepository(),
                bookService: new StubBookService(),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Whipping Star" } }
                }
            };

            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = true
            };

            var result = await sut.MatchFilesToLibraryAsync(new[] { file }, author.Id, context);

            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            var finalDecision = matchingLogger.FinalDecisions.Last().Result;
            Assert.That(finalDecision.Decision, Is.EqualTo("UNMATCHED"));
            Assert.That(finalDecision.PathFallbackUsed, Is.False);
            Assert.That(finalDecision.PathFallbackSuppressedReason, Is.EqualTo("disabled_by_context"));
        }

        [Test]
        public void grouped_duration_gate_diagnostic_should_wrap_existing_duration_rejection_additively()
        {
            var method = typeof(FileMatchingService).GetMethod(
                "BuildGroupedDurationGateRejections",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);

            var sourceRejections = new List<CandidateRejection>
            {
                new CandidateRejection
                {
                    Phase = "scoped/main-tags",
                    EditionId = 3101,
                    Score = 10.0,
                    TitleSnippet = "Salt to the Sea",
                    Reason = "NEAR_EXACT_DURATION_GATE",
                    Detail = "candidateSec=3600 observedSec=7200 durationDiffSec=3600 allowedSec=300"
                }
            };

            var files = new List<DiscoveredFileWithMetadata>
            {
                ChapterFile("/audiobooks/Ruta Sepetys/Slat to the Sea/Slat to the Sea (01).mp3", 2400, 123456, "Slat to the Sea", "Ruta Sepetys"),
                ChapterFile("/audiobooks/Ruta Sepetys/Slat to the Sea/Slat to the Sea (02).mp3", 2400, 123456, "Slat to the Sea", "Ruta Sepetys"),
                ChapterFile("/audiobooks/Ruta Sepetys/Slat to the Sea/Slat to the Sea (03).mp3", 2400, 654321, "Slat to the Sea", "Ruta Sepetys")
            };

            var result = (List<CandidateRejection>)method.Invoke(null, new object[] { sourceRejections, files, (int?)7200 });

            Assert.That(result, Has.Count.EqualTo(1));
            var groupedDurationGate = result.Single();
            Assert.That(groupedDurationGate.Reason, Is.EqualTo("GROUP_DURATION_GATE"));
            Assert.That(groupedDurationGate.Phase, Is.EqualTo("scoped/main-tags"));
            Assert.That(groupedDurationGate.EditionId, Is.EqualTo(3101));
            Assert.That(groupedDurationGate.Detail, Does.Contain("candidateSec=3600"));
            Assert.That(groupedDurationGate.Detail, Does.Contain("observedSec=7200"));
            Assert.That(groupedDurationGate.Detail, Does.Contain("durationDiffSec=3600"));
            Assert.That(groupedDurationGate.Detail, Does.Contain("grouped=true"));
            Assert.That(groupedDurationGate.Detail, Does.Contain("files=3"));
            Assert.That(groupedDurationGate.Detail, Does.Contain("duplicateSuspect=true"));
        }

        private static DiscoveredFileWithMetadata ChapterFile(string path, int durationSeconds, long size, string album, string artist)
        {
            var trackNumber = Path.GetFileNameWithoutExtension(path)?.Split('(', ')').Skip(1).FirstOrDefault() ?? "1";
            return new DiscoveredFileWithMetadata
            {
                Path = path,
                DurationSeconds = durationSeconds,
                Size = size,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUM", new List<string> { album } },
                    { "ARTIST", new List<string> { artist } },
                    { "TITLE", new List<string> { album } },
                    { "TRCK", new List<string> { trackNumber } }
                }
            };
        }
    }
}
