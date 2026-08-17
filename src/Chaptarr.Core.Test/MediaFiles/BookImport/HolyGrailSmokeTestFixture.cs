using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using Chaptarr.Core.Test;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
        public class HolyGrailSmokeTestFixture
        {
            private sealed class StubEditionFtsRepository : IEditionFtsRepository, IEditionFtsTraceRepository
            {
            private readonly List<EditionFtsMatch> _results;
            private readonly bool _throwOnSearch;

            public StubEditionFtsRepository(List<EditionFtsMatch> results, bool throwOnSearch = false)
            {
                _results = results ?? new List<EditionFtsMatch>();
                _throwOnSearch = throwOnSearch;
            }

            public int Calls { get; private set; }

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                Calls++;
                if (_throwOnSearch)
                {
                    throw new AssertionException("FTS should not be called when identifier short-circuit succeeds");
                }

                return _results.Take(limit).ToList();
            }

            public List<EditionFtsMatch> SearchWithTwoStepWithTrace(
                int? authorId,
                IEnumerable<string> tokens,
                BookMediaType mediaType,
                Action<EditionFtsTraceEvent> trace,
                int limit = 20)
            {
                var tokenList = (tokens ?? Enumerable.Empty<string>()).ToList();
                trace?.Invoke(new EditionFtsTraceEvent
                {
                    EventType = "input",
                    Step = "input",
                    Terms = tokenList
                });

                var results = SearchWithTwoStep(authorId, tokenList, mediaType, limit);
                for (var i = 0; i < results.Count; i++)
                {
                    var candidate = results[i];
                    trace?.Invoke(new EditionFtsTraceEvent
                    {
                        EventType = "candidate",
                        Step = "edition_expansion",
                        RawRank = i + 1,
                        DistinctBookRank = i + 1,
                        EditionId = candidate.EditionId,
                        BookId = candidate.BookId,
                        AuthorId = candidate.AuthorId,
                        Score = candidate.MatchScore,
                        EditionTitle = candidate.EditionTitle,
                        BookTitle = candidate.BookTitle,
                        AuthorName = candidate.AuthorName
                    });
                }

                trace?.Invoke(new EditionFtsTraceEvent
                {
                    EventType = "summary",
                    Step = "edition_expansion",
                    ResultCount = results.Count,
                    DistinctBookCount = results.Select(result => result.BookId).Distinct().Count()
                });

                return results;
            }

            }

            private sealed class RecordingStagedEditionFtsRepository : IEditionFtsRepository, IStagedEditionFtsRepository
            {
                private readonly List<BookFtsMatch> _recalls;
                private readonly List<EditionFtsMatch> _rankedEditions;

                public RecordingStagedEditionFtsRepository(
                    IEnumerable<BookFtsMatch> recalls,
                    IEnumerable<EditionFtsMatch> rankedEditions)
                {
                    _recalls = recalls?.ToList() ?? new List<BookFtsMatch>();
                    _rankedEditions = rankedEditions?.ToList() ?? new List<EditionFtsMatch>();
                }

                public int RecallCalls { get; private set; }
                public int RankCalls { get; private set; }
                public List<BookFtsMatch> GatedBooks { get; private set; } = new();
                public List<EditionFtsFieldQuery> ResidualQueries { get; private set; } = new();

                public bool FtsTableExists() => true;
                public void RebuildIndex() { }

                public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
                {
                    throw new AssertionException("The staged production contract should replace the historical combined search.");
                }

                public List<BookFtsMatch> RecallBooks(
                    int? authorId,
                    IEnumerable<string> tokens,
                    BookMediaType mediaType,
                    Action<EditionFtsTraceEvent> trace = null,
                    int limit = 20,
                    bool monitoredOnly = false)
                {
                    RecallCalls++;
                    return _recalls.Take(limit).ToList();
                }

                public List<EditionFtsMatch> RankEditions(
                    IReadOnlyCollection<BookFtsMatch> recalledBooks,
                    IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries,
                    BookMediaType mediaType,
                    Action<EditionFtsTraceEvent> trace = null)
                {
                    RankCalls++;
                    GatedBooks = recalledBooks?.ToList() ?? new List<BookFtsMatch>();
                    ResidualQueries = fieldQueries?.ToList() ?? new List<EditionFtsFieldQuery>();
                    bool HasAnyQueryToken(string value, EditionFtsFieldQuery query)
                    {
                        if (string.IsNullOrWhiteSpace(value) || query?.Terms == null)
                        {
                            return false;
                        }

                        var valueTokens = System.Text.RegularExpressions.Regex
                            .Matches(value.ToLowerInvariant(), @"[\p{L}\p{Nd}]+")
                            .Select(match => match.Value)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        return query.Terms.Any(term => valueTokens.Contains(term));
                    }

                    var ranked = _rankedEditions.ToList();
                    foreach (var candidate in ranked.Where(candidate =>
                                 candidate != null &&
                                 (candidate.Stage2FieldHits == null || candidate.Stage2FieldHits.Count == 0)))
                    {
                        var titleText = candidate.MatchingTitle ?? candidate.EditionTitle ?? candidate.BookTitle;
                        var detailText = mediaType == BookMediaType.Audiobook
                            ? candidate.NarratorNames
                            : candidate.Publisher;
                        candidate.Stage2FieldHits = ResidualQueries
                            .Select(query => new EditionFtsFieldHit
                            {
                                FieldKey = query.Key,
                                SourceFields = query.SourceFields,
                                TitleHit = HasAnyQueryToken(titleText, query),
                                DetailHit = HasAnyQueryToken(detailText, query),
                                TitleBm25 = HasAnyQueryToken(titleText, query) ? 1.0 : 0.0,
                                DetailBm25 = HasAnyQueryToken(detailText, query) ? 1.0 : 0.0
                            })
                            .Where(hit => hit.TitleHit || hit.DetailHit)
                            .ToList();
                    }

                    return ranked;
                }
            }

            private FileMatch MatchStagedStructuralScenario(
                string scenario,
                BookMediaType mediaType,
                IDictionary<string, List<string>> tags,
                IReadOnlyList<BookFtsMatch> recalls,
                IReadOnlyList<EditionFtsMatch> editions,
                IReadOnlyList<Book> books,
                int expectedEditionId,
                int? durationSeconds = null,
                string expectedMatchedVia = "staged_field_representation",
                IReadOnlyList<IDictionary<string, List<string>>> groupMemberTags = null)
            {
                var logger = LogManager.GetCurrentClassLogger();
                var stagedFts = new RecordingStagedEditionFtsRepository(recalls, editions);
                var author = recalls.First();
                var service = new FileMatchingService(
                    matchingLogger: new NullMatchingUploadLogger(),
                    v5MatchingService: null,
                    containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(
                        strictness: BookMatchingStrictness.Balanced,
                        usePathAsTagsFallback: false),
                    authorService: new StubAuthorService(new Author
                    {
                        Id = author.AuthorId,
                        Name = author.AuthorName
                    }),
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: stagedFts,
                    bookService: new StubBookService(books),
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);
                var file = new DiscoveredFileWithMetadata
                {
                    Path = $"/library/{author.AuthorName}/{scenario}.{(mediaType == BookMediaType.Audiobook ? "m4b" : "epub")}",
                    DurationSeconds = durationSeconds,
                    AllTags = tags.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value?.ToList() ?? new List<string>(),
                        StringComparer.OrdinalIgnoreCase),
                    GroupMemberTags = groupMemberTags?
                        .Select(member => member.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value?.ToList() ?? new List<string>(),
                            StringComparer.OrdinalIgnoreCase))
                        .ToList()
                };

                var match = service.HolyGrailMatchFile(file, mediaType, restrictToAuthorId: null);

                Assert.That(match, Is.Not.Null, scenario);
                Assert.That(match.EditionId, Is.EqualTo(expectedEditionId), scenario);
                if (expectedMatchedVia != null)
                {
                    Assert.That(match.MatchedVia, Is.EqualTo(expectedMatchedVia), scenario);
                }
                Assert.That(stagedFts.RecallCalls, Is.EqualTo(1), scenario);
                Assert.That(stagedFts.RankCalls, Is.EqualTo(1), scenario);
                return match;
            }

            private (FileMatch Match, RecordingStagedEditionFtsRepository Repository) RunStagedAuthorGateScenario(
                string scenario,
                IDictionary<string, List<string>> tags,
                BookFtsMatch recalledBook,
                EditionFtsMatch edition,
                Book book,
                Author author,
                int? restrictToAuthorId)
            {
                var logger = LogManager.GetCurrentClassLogger();
                var stagedFts = new RecordingStagedEditionFtsRepository(
                    new[] { recalledBook },
                    new[] { edition });
                var service = new FileMatchingService(
                    matchingLogger: new NullMatchingUploadLogger(),
                    v5MatchingService: null,
                    containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(
                        strictness: BookMatchingStrictness.Balanced,
                        usePathAsTagsFallback: false),
                    authorService: new StubAuthorService(author),
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: stagedFts,
                    bookService: new StubBookService(new[] { book }),
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);
                var file = new DiscoveredFileWithMetadata
                {
                    Path = $"/library/{author.Name}/{scenario}.m4b",
                    DurationSeconds = edition.DurationSeconds > 0 ? edition.DurationSeconds : null,
                    AllTags = tags.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value?.ToList() ?? new List<string>(),
                        StringComparer.OrdinalIgnoreCase)
                };

                return (service.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId), stagedFts);
            }

            private sealed class CountingContainmentValidator : IContainmentValidator
            {
                private readonly IContainmentValidator _inner;

                public CountingContainmentValidator(IContainmentValidator inner)
                {
                    _inner = inner;
                }

                public Dictionary<string, int> NeighborhoodAuthorChecks { get; } = new(StringComparer.OrdinalIgnoreCase);

                public bool Contains(string haystack, string needle) => _inner.Contains(haystack, needle);

                public bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags)
                {
                    if (allTags != null && allTags.Count > 1)
                    {
                        NeighborhoodAuthorChecks[authorName] = NeighborhoodAuthorChecks.TryGetValue(authorName, out var count)
                            ? count + 1
                            : 1;
                    }

                    return _inner.ValidateAuthorInTags(authorName, allTags);
                }

                public bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags) =>
                    _inner.ValidateEditionInTags(editionTitle, allTags);

                public IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(
                    string editionTitle,
                    IDictionary<string, List<string>> allTags,
                    bool includeDurationGatedNearExact = false) =>
                    _inner.GetEditionTitleEvidence(editionTitle, allTags, includeDurationGatedNearExact);
            }

            private sealed class StubEditionRepository : IEditionRepository
            {
                private readonly Dictionary<string, List<Edition>> _editionsByAsin;

                public StubEditionRepository(IEnumerable<Edition> editions)
                {
                    _editionsByAsin = new Dictionary<string, List<Edition>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var edition in editions ?? Array.Empty<Edition>())
                    {
                        if (!string.IsNullOrWhiteSpace(edition.Asin))
                        {
                            Add(edition.Asin, edition);
                        }

                        if (!string.IsNullOrWhiteSpace(edition.AudibleASIN))
                        {
                            Add(edition.AudibleASIN, edition);
                        }
                    }
                }

                private void Add(string asin, Edition edition)
                {
                    var key = asin.Trim().ToUpperInvariant();
                    if (!_editionsByAsin.TryGetValue(key, out var editions))
                    {
                        editions = new List<Edition>();
                        _editionsByAsin[key] = editions;
                    }

                    editions.Add(edition);
                }

                public List<Edition> FindAllByAsin(string asin)
                {
                    return FindAllByAsin(asin, null);
                }

                public List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType)
                {
                    if (string.IsNullOrWhiteSpace(asin))
                    {
                        return new List<Edition>();
                    }

                    if (!_editionsByAsin.TryGetValue(asin.Trim().ToUpperInvariant(), out var editions))
                    {
                        return new List<Edition>();
                    }

                    return editions
                        .Where(edition => !mediaType.HasValue || edition.Book?.MediaType == mediaType.Value)
                        .ToList();
                }

                public IEnumerable<Edition> All() => throw new NotImplementedException();
                public int Count() => throw new NotImplementedException();
                public Edition Find(int id) => throw new NotImplementedException();
                public Edition Get(int id) => throw new NotImplementedException();
                public Edition Insert(Edition model) => throw new NotImplementedException();
                public Edition Update(Edition model) => throw new NotImplementedException();
                public Edition Upsert(Edition model) => throw new NotImplementedException();
                public void SetFields(Edition model, params System.Linq.Expressions.Expression<Func<Edition, object>>[] properties) => throw new NotImplementedException();
                public void Delete(Edition model) => throw new NotImplementedException();
                public void Delete(int id) => throw new NotImplementedException();
                public IEnumerable<Edition> Get(IEnumerable<int> ids) => throw new NotImplementedException();
                public void InsertMany(IList<Edition> model) => throw new NotImplementedException();
                public void InsertMany(IList<Edition> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
                public void UpdateMany(IList<Edition> model) => throw new NotImplementedException();
                public void SetFields(IList<Edition> models, params System.Linq.Expressions.Expression<Func<Edition, object>>[] properties) => throw new NotImplementedException();
                public void DeleteMany(List<Edition> model) => throw new NotImplementedException();
                public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
                public void Purge(bool vacuum = false) => throw new NotImplementedException();
                public bool HasItems() => throw new NotImplementedException();
                public Edition Single() => throw new NotImplementedException();
                public Edition SingleOrDefault() => throw new NotImplementedException();
                public PagingSpec<Edition> GetPaged(PagingSpec<Edition> pagingSpec) => throw new NotImplementedException();
                public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
                public Edition FindByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
                public List<Edition> FindAllByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
                public Edition FindByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
                public List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
                public Edition FindByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
                public List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
                public Edition FindByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
                public List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
                public Edition FindByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
                public List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
                public List<Edition> FindByBook(IEnumerable<int> ids) => throw new NotImplementedException();
                public List<Edition> FindByAuthor(int id) => throw new NotImplementedException();
                public List<Edition> FindByAuthorId(int id, bool onlyMonitored) => throw new NotImplementedException();
                public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
                public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
                public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
                public HashSet<string> FindExistingTitleSlugsForUniqueness(IEnumerable<string> baseTitleSlugs) => throw new NotImplementedException();
                public Edition FindByIsbn(string isbn) => throw new NotImplementedException();
                public List<Edition> FindAllByIsbn(string isbn) => throw new NotImplementedException();
                public int CountMissingMatchingTitles() => throw new NotImplementedException();
                public List<Edition> GetMissingMatchingTitles(int afterId, int limit) => throw new NotImplementedException();
            }

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

            private sealed class RecordingTraceSink : IMatchingTraceSink
            {
                public List<MatchingTraceEvent> Events { get; } = new List<MatchingTraceEvent>();
                public bool ThrowOnRecord { get; set; }

                public void Record(MatchingTraceEvent evt)
                {
                    if (ThrowOnRecord)
                    {
                        throw new InvalidOperationException("diagnostic sink failure");
                    }

                    Events.Add(evt);
                }
            }

            private sealed class StubMediaInfoExtractor : IMediaInfoExtractor
            {
                private readonly Dictionary<string, TimeSpan> _durations;

                public StubMediaInfoExtractor(Dictionary<string, TimeSpan> durations)
                {
                    _durations = durations ?? new Dictionary<string, TimeSpan>();
                }

                public NzbDrone.Core.Parser.Model.MediaInfoModel ExtractMediaInfo(string filePath)
                {
                    return new NzbDrone.Core.Parser.Model.MediaInfoModel();
                }

                public TimeSpan GetDuration(string filePath)
                {
                    return filePath != null && _durations.TryGetValue(filePath, out var duration) ? duration : TimeSpan.Zero;
                }

                public bool IsAudiobookFile(string filePath, NzbDrone.Core.Parser.Model.MediaInfoModel mediaInfo = null)
                {
                    return true;
                }
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

            public Book GetBook(int bookId)
            {
                return _booksById.TryGetValue(bookId, out var book) ? book : null;
            }

            public List<Book> GetBooks(IEnumerable<int> bookIds)
            {
                if (bookIds == null)
                {
                    return new List<Book>();
                }

                return bookIds
                    .Select(id => _booksById.TryGetValue(id, out var book) ? book : null)
                    .Where(book => book != null)
                    .ToList();
            }
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
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private static FileMatch MatchSubtitleRescueScenario(
            IReadOnlyList<EditionFtsMatch> candidates,
            IReadOnlyList<Book> books,
            IDictionary<string, List<string>> tags,
            BookMatchingStrictness strictness = BookMatchingStrictness.Balanced,
            int? durationSeconds = null)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var first = candidates.First();
            var service = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(
                    strictness: strictness,
                    usePathAsTagsFallback: false),
                authorService: new StubAuthorService(new Author
                {
                    Id = first.AuthorId,
                    Name = first.AuthorName
                }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(candidates.ToList()),
                bookService: new StubBookService(books),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            return service.HolyGrailMatchFile(
                new DiscoveredFileWithMetadata
                {
                    Path = $"/audiobooks/{first.AuthorName}/{first.BookTitle}/book.m4b",
                    DurationSeconds = durationSeconds,
                    AllTags = new Dictionary<string, List<string>>(tags, StringComparer.OrdinalIgnoreCase)
                },
                BookMediaType.Audiobook,
                restrictToAuthorId: first.AuthorId);
        }

        [Test]
        public async System.Threading.Tasks.Task diagnostic_trace_should_report_raw_fts_and_actual_production_rank_without_changing_the_winner()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var candidate = new EditionFtsMatch
            {
                EditionId = 7001,
                BookId = 8001,
                EditionTitle = "Alpha",
                BookTitle = "Alpha",
                AuthorId = 25,
                AuthorName = "Test Author",
                ReadingFormatId = 2,
                MatchScore = 12.5
            };
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch> { candidate });
            var trace = new RecordingTraceSink();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(new Author { Id = 25, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var context = MatchingContextPresets.ForScanLocal(false);
            context.TraceSink = trace;
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Test Author/Alpha/Alpha.m4b",
                Size = 1,
                Modified = DateTime.UtcNow,
                AllTags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new List<string> { "Alpha" },
                    ["ARTIST"] = new List<string> { "Test Author" }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(
                new[] { file },
                restrictToAuthorId: 25,
                context);

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].EditionId, Is.EqualTo(candidate.EditionId));
            Assert.That(trace.Events.Any(evt => evt.EventType == "fts_input"), Is.True);
            Assert.That(trace.Events.Any(evt => evt.EventType == "fts_edition_expansion_candidate" && evt.Rank == 1 && evt.EditionId == candidate.EditionId), Is.True);
            Assert.That(trace.Events.Any(evt => evt.EventType == "candidate_ranked" && evt.Rank == 1 && evt.EditionId == candidate.EditionId), Is.True);
            Assert.That(trace.Events.Any(evt => evt.EventType == "match_selected" && evt.EditionId == candidate.EditionId), Is.True);

            trace.ThrowOnRecord = true;
            var throwingContext = MatchingContextPresets.ForScanLocal(false);
            throwingContext.TraceSink = trace;
            var resultWithBrokenDiagnostics = await svc.MatchFilesToLibraryAsync(
                new[] { file },
                restrictToAuthorId: 25,
                throwingContext);

            Assert.That(resultWithBrokenDiagnostics.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(resultWithBrokenDiagnostics.MatchedFiles[0].EditionId, Is.EqualTo(candidate.EditionId));
        }

        [Test]
        public void staged_fts_should_gate_unique_authors_and_rank_independent_residual_fields()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new CountingContainmentValidator(new ContainmentValidator(new TagNormalizer(), logger));
            var stagedFts = new RecordingStagedEditionFtsRepository(
                new[]
                {
                    new BookFtsMatch { BookId = 101, AuthorId = 1, AuthorName = "Ryan Cahill", BookTitle = "The Fall", SeriesName = "The Bound and the Broken", MatchScore = 20 },
                    new BookFtsMatch { BookId = 102, AuthorId = 1, AuthorName = "Ryan Cahill", BookTitle = "The Exile", SeriesName = "The Bound and the Broken", MatchScore = 18 },
                    new BookFtsMatch { BookId = 201, AuthorId = 2, AuthorName = "Frank Herbert", BookTitle = "The Dune Audio Collection", SeriesName = "Dune", MatchScore = 10 }
                },
                new[]
                {
                    new EditionFtsMatch
                    {
                        EditionId = 1001,
                        BookId = 101,
                        EditionTitle = "The Fall: The Bound and The Broken Novella",
                        MatchingTitle = "The Fall: The Bound and The Broken Novella",
                        BookTitle = "The Fall",
                        AuthorId = 1,
                        AuthorName = "Ryan Cahill",
                        NarratorNames = "Derek Perkins",
                        ReadingFormatId = 2,
                        MatchScore = 18,
                        Stage2TitleScore = 6,
                        Stage2DetailScore = 12
                    }
                });
            var books = new[]
            {
                new Book { Id = 101, AuthorId = 1, Title = "The Fall", SeriesName = "The Bound and the Broken", HardcoverBookId = "hc:fall", MediaType = BookMediaType.Audiobook },
                new Book { Id = 102, AuthorId = 1, Title = "The Exile", SeriesName = "The Bound and the Broken", HardcoverBookId = "hc:exile", MediaType = BookMediaType.Audiobook }
            };
            var service = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(new Author { Id = 1, Name = "Ryan Cahill" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: stagedFts,
                bookService: new StubBookService(books),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Ryan Cahill/The Fall/The Fall - Ryan Cahill.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "The Fall" },
                    ["MP4:©nam"] = new() { "The Fall" },
                    ["ALBUM"] = new() { "The Fall" },
                    ["ARTIST"] = new() { "Ryan Cahill" },
                    ["ALBUMARTIST"] = new() { "Ryan Cahill (The Bound and The Broken)" },
                    ["COMPOSER"] = new() { "Derek Perkins" },
                    ["mP4:----"] = new() { "The Bound and The Broken Novella" }
                }
            };

            var match = service.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: null);

            Assert.That(match?.EditionId, Is.EqualTo(1001));
            Assert.That(stagedFts.RecallCalls, Is.EqualTo(1));
            Assert.That(stagedFts.RankCalls, Is.EqualTo(1));
            Assert.That(stagedFts.GatedBooks.Select(book => book.BookId), Is.EquivalentTo(new[] { 101, 102 }));
            Assert.That(containment.NeighborhoodAuthorChecks["Ryan Cahill"], Is.EqualTo(1));
            Assert.That(containment.NeighborhoodAuthorChecks["Frank Herbert"], Is.EqualTo(1));

            var residualValues = stagedFts.ResidualQueries
                .Select(query => string.Join(" ", query.Terms))
                .ToList();
            Assert.That(residualValues, Does.Contain("the fall"));
            var residualFields = stagedFts.ResidualQueries.SelectMany(query => query.SourceFields).ToList();
            Assert.That(residualFields, Does.Contain("MP4:©nam[0]"));
            Assert.That(residualFields, Does.Not.Contain("TITLE[0]"), "a generated canonical view must not duplicate its raw physical field");

            Assert.That(residualValues, Does.Contain("derek perkins"));
            Assert.That(residualValues.Any(value => value.Contains("novella", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(residualValues.Any(value => value.Contains("ryan", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(residualValues.Any(value => value.Contains("cahill", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(residualValues.Any(value => value.Contains("bound", StringComparison.OrdinalIgnoreCase)), Is.True, "series text must remain available for sibling discrimination");
            Assert.That(match.MatchedVia, Is.EqualTo("staged_field_representation"));
        }

        [Test]
        public void staged_author_gate_should_trust_explicit_scope_but_reject_identical_unscoped_title_only_evidence()
        {
            const int authorId = 344;
            const int bookId = 21529;
            const int editionId = 55003;
            const string title = "A Game of Thrones";
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { title },
                ["ALBUM"] = new() { "A Game of Thrones: A Song of Ice and Fire" },
                ["COMPOSER"] = new() { "Roy Dotrice" }
            };

            (FileMatch Match, RecordingStagedEditionFtsRepository Repository) Run(int? scope)
            {
                return RunStagedAuthorGateScenario(
                    scope.HasValue ? "scoped-title-only" : "unscoped-title-only",
                    tags,
                    new BookFtsMatch
                    {
                        BookId = bookId,
                        AuthorId = authorId,
                        AuthorName = "George R.R. Martin",
                        BookTitle = title,
                        SeriesName = "A Song of Ice and Fire",
                        MatchScore = 10
                    },
                    new EditionFtsMatch
                    {
                        EditionId = editionId,
                        BookId = bookId,
                        AuthorId = authorId,
                        AuthorName = "George R.R. Martin",
                        BookTitle = title,
                        EditionTitle = title,
                        MatchingTitle = title,
                        NarratorNames = "Roy Dotrice",
                        ReadingFormatId = 2,
                        DurationSeconds = 63238
                    },
                    new Book
                    {
                        Id = bookId,
                        AuthorId = authorId,
                        Title = title,
                        SeriesName = "A Song of Ice and Fire",
                        HardcoverBookId = "hc:644",
                        MediaType = BookMediaType.Audiobook
                    },
                    new Author { Id = authorId, Name = "George R.R. Martin" },
                    scope);
            }

            var scoped = Run(authorId);
            Assert.That(scoped.Match?.EditionId, Is.EqualTo(editionId));
            Assert.That(scoped.Repository.GatedBooks, Has.Count.EqualTo(1));
            Assert.That(scoped.Repository.RankCalls, Is.EqualTo(1));

            var unscoped = Run(null);
            Assert.That(unscoped.Match, Is.Null);
            Assert.That(unscoped.Repository.GatedBooks, Is.Empty);
            Assert.That(unscoped.Repository.RankCalls, Is.Zero);
        }

        [Test]
        public void staged_author_gate_should_prove_and_consume_real_pseudonyms_only()
        {
            const int authorId = 901;
            const int bookId = 902;
            const int editionId = 903;
            const string title = "The Martian";
            var run = RunStagedAuthorGateScenario(
                "pseudonym-proof",
                new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { title },
                    ["ALBUM"] = new() { title },
                    ["ARTIST"] = new() { "Jack Sharp" }
                },
                new BookFtsMatch
                {
                    BookId = bookId,
                    AuthorId = authorId,
                    AuthorName = "Andy Weir",
                    BookTitle = title,
                    MatchScore = 10
                },
                new EditionFtsMatch
                {
                    EditionId = editionId,
                    BookId = bookId,
                    AuthorId = authorId,
                    AuthorName = "Andy Weir",
                    BookTitle = title,
                    EditionTitle = title,
                    MatchingTitle = title,
                    ReadingFormatId = 2,
                    DurationSeconds = 36000
                },
                new Book
                {
                    Id = bookId,
                    AuthorId = authorId,
                    Title = title,
                    HardcoverBookId = "hc:the-martian",
                    MediaType = BookMediaType.Audiobook
                },
                new Author
                {
                    Id = authorId,
                    Name = "Andy Weir",
                    Pseudonyms = new List<string> { "Jack Sharp" },
                    Aliases = new List<string> { "gr:6540057", "hc:16706" }
                },
                restrictToAuthorId: null);

            Assert.That(run.Match?.EditionId, Is.EqualTo(editionId));
            Assert.That(run.Repository.GatedBooks, Has.Count.EqualTo(1));
            Assert.That(
                run.Repository.ResidualQueries.SelectMany(query => query.Terms),
                Has.None.Matches<string>(term =>
                    string.Equals(term, "jack", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(term, "sharp", StringComparison.OrdinalIgnoreCase)));
        }

        [Test]
        public void staged_author_gate_should_not_treat_provider_id_aliases_as_author_names()
        {
            const int authorId = 901;
            const int bookId = 902;
            const string title = "The Martian";
            var run = RunStagedAuthorGateScenario(
                "provider-alias-is-not-a-name",
                new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { title },
                    ["ALBUM"] = new() { title },
                    ["ARTIST"] = new() { "gr:6540057" }
                },
                new BookFtsMatch
                {
                    BookId = bookId,
                    AuthorId = authorId,
                    AuthorName = "Andy Weir",
                    BookTitle = title,
                    MatchScore = 10
                },
                new EditionFtsMatch
                {
                    EditionId = 903,
                    BookId = bookId,
                    AuthorId = authorId,
                    AuthorName = "Andy Weir",
                    BookTitle = title,
                    EditionTitle = title,
                    MatchingTitle = title,
                    ReadingFormatId = 2,
                    DurationSeconds = 36000
                },
                new Book
                {
                    Id = bookId,
                    AuthorId = authorId,
                    Title = title,
                    HardcoverBookId = "hc:the-martian",
                    MediaType = BookMediaType.Audiobook
                },
                new Author
                {
                    Id = authorId,
                    Name = "Andy Weir",
                    Pseudonyms = new List<string> { "Jack Sharp" },
                    Aliases = new List<string> { "gr:6540057", "hc:16706" }
                },
                restrictToAuthorId: null);

            Assert.That(run.Match, Is.Null);
            Assert.That(run.Repository.GatedBooks, Is.Empty);
            Assert.That(run.Repository.RankCalls, Is.Zero);
        }
        [Test]
        public void staged_fields_should_not_let_wizarding_world_beat_sorcerers_stone()
        {
            const string title = "Harry Potter and the Sorcerer's Stone";
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { title },
                ["ALBUM"] = new() { title },
                ["GROUPING"] = new() { "Wizarding World" },
                ["ARTIST"] = new() { "J.K. Rowling" },
                ["COMPOSER"] = new() { "Jim Dale" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 202, AuthorId = 25, AuthorName = "J.K. Rowling", BookTitle = "Keys & Curios", MatchScore = 50 },
                new BookFtsMatch { BookId = 201, AuthorId = 25, AuthorName = "J.K. Rowling", BookTitle = title, MatchScore = 10 },
                new BookFtsMatch { BookId = 203, AuthorId = 25, AuthorName = "J.K. Rowling", BookTitle = title, MatchScore = 8 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 2002,
                    BookId = 202,
                    BookTitle = "Keys & Curios",
                    EditionTitle = "Wizarding World",
                    MatchingTitle = "Wizarding World",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    ReadingFormatId = 2,
                    DurationSeconds = 12000
                },
                new EditionFtsMatch
                {
                    EditionId = 2001,
                    BookId = 201,
                    BookTitle = title,
                    EditionTitle = title,
                    MatchingTitle = title,
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    ReadingFormatId = 2,
                    DurationSeconds = 30000
                },
                new EditionFtsMatch
                {
                    EditionId = 2003,
                    BookId = 203,
                    BookTitle = title,
                    EditionTitle = title,
                    MatchingTitle = title,
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    ReadingFormatId = 2,
                    DurationSeconds = 12000
                }
            };
            var books = new[]
            {
                new Book { Id = 201, AuthorId = 25, Title = title, HardcoverBookId = "hc:sorcerers-stone", MediaType = BookMediaType.Audiobook },
                new Book { Id = 202, AuthorId = 25, Title = "Keys & Curios", HardcoverBookId = "hc:keys-curios", MediaType = BookMediaType.Audiobook },
                new Book { Id = 203, AuthorId = 25, Title = title, BaseBookId = "gr:sorcerers-stone-duplicate", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "wizarding-world",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 2001,
                durationSeconds: 30030);
        }

        [Test]
        public void staged_fields_should_not_let_publisher_noise_create_a_wrong_title_vote()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { "The Boyfriend" },
                ["ALBUM"] = new() { "The Boyfriend" },
                ["ARTIST"] = new() { "Freida McFadden" },
                ["PUBLISHER"] = new() { "Hollywood Upstairs Press" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 302, AuthorId = 72, AuthorName = "Freida McFadden", BookTitle = "The Wife Upstairs", MatchScore = 100 },
                new BookFtsMatch { BookId = 301, AuthorId = 72, AuthorName = "Freida McFadden", BookTitle = "The Boyfriend", MatchScore = 1 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 3002,
                    BookId = 302,
                    BookTitle = "The Wife Upstairs",
                    EditionTitle = "The Wife Upstairs",
                    MatchingTitle = "The Wife Upstairs",
                    AuthorId = 72,
                    AuthorName = "Freida McFadden",
                    Publisher = "Hollywood Upstairs Press",
                    ReadingFormatId = 3
                },
                new EditionFtsMatch
                {
                    EditionId = 3001,
                    BookId = 301,
                    BookTitle = "The Boyfriend",
                    EditionTitle = "The Boyfriend",
                    MatchingTitle = "The Boyfriend",
                    AuthorId = 72,
                    AuthorName = "Freida McFadden",
                    Publisher = "Grand Central Publishing",
                    ReadingFormatId = 3
                }
            };
            var books = new[]
            {
                new Book { Id = 301, AuthorId = 72, Title = "The Boyfriend", HardcoverBookId = "hc:boyfriend", MediaType = BookMediaType.Ebook },
                new Book { Id = 302, AuthorId = 72, Title = "The Wife Upstairs", HardcoverBookId = "hc:wife-upstairs", MediaType = BookMediaType.Ebook }
            };

            MatchStagedStructuralScenario(
                "publisher-noise",
                BookMediaType.Ebook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 3001);
        }

        [Test]
        public void staged_group_fields_should_keep_a_shared_book_title_when_only_part_text_varies_and_duration_matches()
        {
            const string bookTitle = "Dune: House Harkonnen";
            const int observedDuration = 95757;
            const int audibleDuration = 95520;
            var consensusTags = new Dictionary<string, List<string>>
            {
                ["MP4:©alb"] = new() { "House Harkonnen (Unabridged)" },
                ["MP4:©ART"] = new() { "Brian Herbert & Kevin J. Anderson" },
                ["MP4:aART"] = new() { "Brian Herbert & Kevin J. Anderson" },
                ["MP4:©wrt"] = new() { "Brian Herbert & Kevin J. Anderson" },
                ["MP4:©grp"] = new() { "Dune Series" },
                ["ALBUM"] = new() { "House Harkonnen (Unabridged)" },
                ["ARTIST"] = new() { "Brian Herbert & Kevin J. Anderson" },
                ["ALBUMARTIST"] = new() { "Brian Herbert & Kevin J. Anderson" },
                ["COMPOSER"] = new() { "Brian Herbert & Kevin J. Anderson" }
            };

            IDictionary<string, List<string>> Member(int part)
            {
                var partTitle = "Dune_House Harkonnen - Part " + part;
                return new Dictionary<string, List<string>>
                {
                    ["MP4:©nam"] = new() { partTitle },
                    ["MP4:©alb"] = new() { "House Harkonnen (Unabridged)" },
                    ["MP4:©ART"] = new() { "Brian Herbert & Kevin J. Anderson" },
                    ["MP4:aART"] = new() { "Brian Herbert & Kevin J. Anderson" },
                    ["MP4:©wrt"] = new() { "Brian Herbert & Kevin J. Anderson" },
                    ["MP4:©grp"] = new() { "Dune Series" },
                    ["TITLE"] = new() { partTitle },
                    ["ALBUM"] = new() { "House Harkonnen (Unabridged)" },
                    ["ARTIST"] = new() { "Brian Herbert & Kevin J. Anderson" },
                    ["ALBUMARTIST"] = new() { "Brian Herbert & Kevin J. Anderson" },
                    ["COMPOSER"] = new() { "Brian Herbert & Kevin J. Anderson" }
                };
            }

            var recalls = new[]
            {
                new BookFtsMatch
                {
                    BookId = 1101,
                    AuthorId = 81,
                    AuthorName = "Brian Herbert",
                    BookTitle = bookTitle,
                    SeriesName = "Dune Universe",
                    MatchScore = 5
                },
                new BookFtsMatch
                {
                    BookId = 1102,
                    AuthorId = 81,
                    AuthorName = "Brian Herbert",
                    BookTitle = "Dune",
                    SeriesName = "Dune",
                    MatchScore = 50
                }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 11001,
                    BookId = 1101,
                    BookTitle = bookTitle,
                    EditionTitle = bookTitle,
                    MatchingTitle = bookTitle,
                    AuthorId = 81,
                    AuthorName = "Brian Herbert",
                    NarratorNames = "Scott Brick",
                    ReadingFormatId = 2,
                    DurationSeconds = audibleDuration,
                    ForeignEditionId = "az:1004027915-audiobook"
                },
                new EditionFtsMatch
                {
                    EditionId = 11002,
                    BookId = 1101,
                    BookTitle = bookTitle,
                    EditionTitle = "House Harkonnen",
                    MatchingTitle = "House Harkonnen",
                    AuthorId = 81,
                    AuthorName = "Brian Herbert",
                    ReadingFormatId = 2,
                    ForeignEditionId = "gr:1529062-audiobook"
                },
                new EditionFtsMatch
                {
                    EditionId = 11003,
                    BookId = 1102,
                    BookTitle = "Dune",
                    EditionTitle = "Dune: House Harkonnen #1",
                    MatchingTitle = "Dune: House Harkonnen #1",
                    AuthorId = 81,
                    AuthorName = "Brian Herbert",
                    ReadingFormatId = 2,
                    ForeignEditionId = "gr:comic-issue-1"
                }
            };
            var books = new[]
            {
                new Book
                {
                    Id = 1101,
                    AuthorId = 81,
                    Title = bookTitle,
                    SeriesName = "Dune Universe",
                    HardcoverBookId = "hc:468842",
                    MediaType = BookMediaType.Audiobook
                },
                new Book
                {
                    Id = 1102,
                    AuthorId = 81,
                    Title = "Dune",
                    SeriesName = "Dune",
                    BaseBookId = "gr:comic-dune",
                    MediaType = BookMediaType.Audiobook
                }
            };

            MatchStagedStructuralScenario(
                "house-harkonnen-multipart",
                BookMediaType.Audiobook,
                consensusTags,
                recalls,
                editions,
                books,
                expectedEditionId: 11001,
                durationSeconds: observedDuration,
                groupMemberTags: new[] { Member(1), Member(2), Member(3), Member(4) });
        }

        [Test]
        public void staged_group_fields_should_not_merge_same_key_when_the_observed_title_span_differs()
        {
            const string title = "Harry Potter and the Goblet of Fire";
            var logger = LogManager.GetCurrentClassLogger();
            var recalls = new[]
            {
                new BookFtsMatch
                {
                    BookId = 1201,
                    AuthorId = 82,
                    AuthorName = "J K Rowling",
                    BookTitle = title,
                    MatchScore = 10
                }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 12001,
                    BookId = 1201,
                    BookTitle = title,
                    EditionTitle = title,
                    MatchingTitle = title,
                    AuthorId = 82,
                    AuthorName = "J K Rowling",
                    ReadingFormatId = 2,
                    ForeignEditionId = "az:goblet-audiobook"
                }
            };
            var stagedFts = new RecordingStagedEditionFtsRepository(recalls, editions);
            var service = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(
                    strictness: BookMatchingStrictness.Balanced,
                    usePathAsTagsFallback: false),
                authorService: new StubAuthorService(new Author { Id = 82, Name = "J K Rowling" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: stagedFts,
                bookService: new StubBookService(new[]
                {
                    new Book
                    {
                        Id = 1201,
                        AuthorId = 82,
                        Title = title,
                        HardcoverBookId = "hc:goblet",
                        MediaType = BookMediaType.Audiobook
                    }
                }),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            IDictionary<string, List<string>> Member(string observedTitle)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ARTIST"] = new() { "J K Rowling" },
                    ["TITLE"] = new() { observedTitle }
                };
            }

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/library/J K Rowling/goblet.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ARTIST"] = new() { "J K Rowling" }
                },
                GroupMemberTags = new[]
                {
                    Member("Chapter 1 Harry Potter Goblet of Fire"),
                    Member("Chapter 2 Harry Potter Goblet of Fire"),
                    Member("Chapter 1 Harry Potter and the Goblet of Fire"),
                    Member("Chapter 2 Harry Potter and the Goblet of Fire")
                }.Select(tags => tags.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)).ToList()
            };

            var match = service.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: null);

            Assert.Multiple(() =>
            {
                Assert.That(match, Is.Null);
                Assert.That(stagedFts.RecallCalls, Is.EqualTo(1));
                Assert.That(stagedFts.RankCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public void staged_fields_should_use_same_field_specificity_for_dune_messiah()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { "Dune Messiah" },
                ["ALBUM"] = new() { "Dune Messiah" },
                ["SERIES"] = new() { "Dune" },
                ["ARTIST"] = new() { "Frank Herbert" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 401, AuthorId = 11, AuthorName = "Frank Herbert", BookTitle = "Dune", SeriesName = "Dune", MatchScore = 100 },
                new BookFtsMatch { BookId = 402, AuthorId = 11, AuthorName = "Frank Herbert", BookTitle = "The Dune Audio Collection", SeriesName = "Dune", MatchScore = 90 },
                new BookFtsMatch { BookId = 403, AuthorId = 11, AuthorName = "Frank Herbert", BookTitle = "Dune Messiah", SeriesName = "Dune", MatchScore = 5 }
            };
            var editions = new[]
            {
                new EditionFtsMatch { EditionId = 4001, BookId = 401, BookTitle = "Dune", EditionTitle = "Dune", MatchingTitle = "Dune", AuthorId = 11, AuthorName = "Frank Herbert", ReadingFormatId = 2 },
                new EditionFtsMatch { EditionId = 4002, BookId = 402, BookTitle = "The Dune Audio Collection", EditionTitle = "The Dune Audio Collection", MatchingTitle = "The Dune Audio Collection", AuthorId = 11, AuthorName = "Frank Herbert", ReadingFormatId = 2 },
                new EditionFtsMatch { EditionId = 4003, BookId = 403, BookTitle = "Dune Messiah", EditionTitle = "Dune Messiah", MatchingTitle = "Dune Messiah", AuthorId = 11, AuthorName = "Frank Herbert", ReadingFormatId = 2 }
            };
            var books = new[]
            {
                new Book { Id = 401, AuthorId = 11, Title = "Dune", HardcoverBookId = "hc:dune", MediaType = BookMediaType.Audiobook },
                new Book { Id = 402, AuthorId = 11, Title = "The Dune Audio Collection", HardcoverBookId = "hc:dune-collection", MediaType = BookMediaType.Audiobook },
                new Book { Id = 403, AuthorId = 11, Title = "Dune Messiah", HardcoverBookId = "hc:dune-messiah", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "dune-messiah",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 4003);
        }

        [Test]
        public void staged_fields_should_require_the_omnibus_title_phrase_in_full()
        {
            const string title = "Harry Potter and the Goblet of Fire";
            const string omnibus = "Harry Potter and the Goblet of Fire / Harry Potter and the Order of the Phoenix";
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { title },
                ["ALBUM"] = new() { title },
                ["ARTIST"] = new() { "J.K. Rowling" },
                ["COMPOSER"] = new() { "Jim Dale" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 502, AuthorId = 25, AuthorName = "J.K. Rowling", BookTitle = omnibus, MatchScore = 100 },
                new BookFtsMatch { BookId = 501, AuthorId = 25, AuthorName = "J.K. Rowling", BookTitle = title, MatchScore = 2 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 5002,
                    BookId = 502,
                    BookTitle = omnibus,
                    EditionTitle = omnibus,
                    MatchingTitle = omnibus,
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 5001,
                    BookId = 501,
                    BookTitle = title,
                    EditionTitle = title,
                    MatchingTitle = title,
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    ReadingFormatId = 2
                }
            };
            var books = new[]
            {
                new Book { Id = 501, AuthorId = 25, Title = title, HardcoverBookId = "hc:goblet", MediaType = BookMediaType.Audiobook },
                new Book { Id = 502, AuthorId = 25, Title = omnibus, HardcoverBookId = "hc:goblet-order-omnibus", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "omnibus-totality",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 5001);
        }

        [Test]
        public void staged_fields_should_not_let_a_series_container_outvote_prince_caspian()
        {
            const string series = "The Chronicles of Narnia";
            var tags = new Dictionary<string, List<string>>
            {
                ["iD3v2:TIT2"] = new() { "The Chronicles Of Narnia - 04 - Prince Caspian - The Return To Narnia" },
                ["iD3v2:TALB"] = new() { series },
                ["iD3v2:TPE1"] = new() { "C.S. Lewis" },
                ["iD3v2:TPE2"] = new() { "C.S. Lewis" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 702, AuthorId = 61, AuthorName = "C.S. Lewis", BookTitle = series, SeriesName = $"{series} (Publication Order)", MatchScore = 100 },
                new BookFtsMatch { BookId = 701, AuthorId = 61, AuthorName = "C.S. Lewis", BookTitle = "Prince Caspian", SeriesName = series, MatchScore = 5 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 7002,
                    BookId = 702,
                    BookTitle = series,
                    EditionTitle = "The Complete Chronicles of Narnia",
                    MatchingTitle = "The Complete Chronicles of Narnia",
                    AuthorId = 61,
                    AuthorName = "C.S. Lewis",
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 7001,
                    BookId = 701,
                    BookTitle = "Prince Caspian",
                    EditionTitle = "Prince Caspian",
                    MatchingTitle = "Prince Caspian",
                    AuthorId = 61,
                    AuthorName = "C.S. Lewis",
                    ReadingFormatId = 2
                }
            };
            var books = new[]
            {
                new Book { Id = 701, AuthorId = 61, Title = "Prince Caspian", SeriesName = series, HardcoverBookId = "hc:prince-caspian", MediaType = BookMediaType.Audiobook },
                new Book { Id = 702, AuthorId = 61, Title = series, SeriesName = $"{series} (Publication Order)", HardcoverBookId = "hc:narnia-collection", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "prince-caspian-series-container",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 7001);
        }

        [Test]
        public void staged_fields_should_not_let_wild_cards_series_and_trust_fields_outvote_mississippi_roll()
        {
            const string observed = "Mississippi Roll: Wild Cards Book 1";
            var tags = new Dictionary<string, List<string>>
            {
                ["iD3v2:TIT2"] = new() { observed },
                ["iD3v2:TALB"] = new() { observed },
                ["iD3v2:TPE1"] = new() { "Wild Cards Trust, George R.R. Martin" },
                ["iD3v2:TPE2"] = new() { "Wild Cards Trust, George R.R. Martin" },
                ["iD3v2:TCOM"] = new() { "William Hope" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 802, AuthorId = 62, AuthorName = "George R.R. Martin", BookTitle = "Wild Cards", SeriesName = "Wild Cards", MatchScore = 100 },
                new BookFtsMatch { BookId = 801, AuthorId = 62, AuthorName = "George R.R. Martin", BookTitle = "Mississippi Roll", SeriesName = "Wild Cards", MatchScore = 5 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 8002,
                    BookId = 802,
                    BookTitle = "Wild Cards",
                    EditionTitle = "Wild Cards I",
                    MatchingTitle = "Wild Cards I",
                    AuthorId = 62,
                    AuthorName = "George R.R. Martin",
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 8001,
                    BookId = 801,
                    BookTitle = "Mississippi Roll",
                    EditionTitle = "Mississippi Roll",
                    MatchingTitle = "Mississippi Roll",
                    AuthorId = 62,
                    AuthorName = "George R.R. Martin",
                    ReadingFormatId = 2
                }
            };
            var books = new[]
            {
                new Book { Id = 801, AuthorId = 62, Title = "Mississippi Roll", SeriesName = "Wild Cards", BaseBookId = "gr:mississippi-roll", MediaType = BookMediaType.Audiobook },
                new Book { Id = 802, AuthorId = 62, Title = "Wild Cards", SeriesName = "Wild Cards", HardcoverBookId = "hc:wild-cards", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "mississippi-roll-series-container",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 8001);
        }

        [Test]
        public void staged_fields_should_not_let_a_short_edition_title_prove_a_three_book_set()
        {
            const string observed = "Fourth Wing: Fourth Wing, Book 1";
            var tags = new Dictionary<string, List<string>>
            {
                ["mP4:©nam"] = new() { observed },
                ["mP4:©alb"] = new() { observed },
                ["mP4:©ART"] = new() { "Rebecca Yarros" },
                ["mP4:©wrt"] = new() { "Rebecca Soler, Teddy Hamilton" },
                ["publisher"] = new() { "Recorded Books" },
                ["date"] = new() { "2023" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 902, AuthorId = 63, AuthorName = "Rebecca Yarros", BookTitle = "The Empyrean Series 3 Book Set", SeriesName = "The Empyrean", MatchScore = 100 },
                new BookFtsMatch { BookId = 901, AuthorId = 63, AuthorName = "Rebecca Yarros", BookTitle = "Fourth Wing", SeriesName = "The Empyrean", MatchScore = 5 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 9002,
                    BookId = 902,
                    BookTitle = "The Empyrean Series 3 Book Set",
                    EditionTitle = "Fourth Wing",
                    MatchingTitle = "Fourth Wing",
                    AuthorId = 63,
                    AuthorName = "Rebecca Yarros",
                    NarratorNames = "Rebecca Soler, Teddy Hamilton",
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 9001,
                    BookId = 901,
                    BookTitle = "Fourth Wing",
                    EditionTitle = "Fourth Wing",
                    MatchingTitle = "Fourth Wing",
                    AuthorId = 63,
                    AuthorName = "Rebecca Yarros",
                    NarratorNames = "Rebecca Soler, Teddy Hamilton",
                    Publisher = "Recorded Books",
                    ReadingFormatId = 2,
                    DurationSeconds = 79320
                }
            };
            var books = new[]
            {
                new Book { Id = 901, AuthorId = 63, Title = "Fourth Wing", SeriesName = "The Empyrean", HardcoverBookId = "hc:fourth-wing", MediaType = BookMediaType.Audiobook },
                new Book { Id = 902, AuthorId = 63, Title = "The Empyrean Series 3 Book Set", SeriesName = "The Empyrean", HardcoverBookId = "hc:empyrean-set", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "fourth-wing-set-totality",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 9001,
                durationSeconds: 74859,
                expectedMatchedVia: null);
        }


        [Test]
        public void staged_fields_should_fall_back_when_a_localized_book_pocket_is_proven_by_edition_physics()
        {
            const string observed = "Harry Potter and the Prisoner of Azkaban, Book 3";
            var tags = new Dictionary<string, List<string>>
            {
                ["mP4:©nam"] = new() { observed },
                ["mP4:©alb"] = new() { observed },
                ["mP4:©ART"] = new() { "J.K. Rowling" },
                ["mP4:©wrt"] = new() { "Jim Dale" },
                ["date"] = new() { "2015" }
            };
            var recalls = new[]
            {
                new BookFtsMatch
                {
                    BookId = 1001,
                    AuthorId = 64,
                    AuthorName = "J.K. Rowling",
                    BookTitle = "해리포터와 아즈카반의 죄수 Vol. 1 of 2",
                    SeriesName = "Harry Potter Japanese Split-Volume",
                    MatchScore = 100
                },
                new BookFtsMatch
                {
                    BookId = 1002,
                    AuthorId = 64,
                    AuthorName = "J.K. Rowling",
                    BookTitle = "Harry Potter and the Prisoner of Azkaban",
                    SeriesName = "Harry Potter (Full-Cast Edition)",
                    MatchScore = 5
                }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 10001,
                    BookId = 1001,
                    BookTitle = "해리포터와 아즈카반의 죄수 Vol. 1 of 2",
                    EditionTitle = observed,
                    MatchingTitle = observed,
                    AuthorId = 64,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    ReadingFormatId = 2,
                    DurationSeconds = 42540
                },
                new EditionFtsMatch
                {
                    EditionId = 10002,
                    BookId = 1002,
                    BookTitle = "Harry Potter and the Prisoner of Azkaban",
                    EditionTitle = "Harry Potter and the Prisoner of Azkaban",
                    MatchingTitle = "Harry Potter and the Prisoner of Azkaban",
                    AuthorId = 64,
                    AuthorName = "J.K. Rowling",
                    ReadingFormatId = 2
                }
            };
            var books = new[]
            {
                new Book { Id = 1001, AuthorId = 64, Title = "해리포터와 아즈카반의 죄수 Vol. 1 of 2", SeriesName = "Harry Potter Japanese Split-Volume", HardcoverBookId = "hc:localized-azkaban", MediaType = BookMediaType.Audiobook },
                new Book { Id = 1002, AuthorId = 64, Title = "Harry Potter and the Prisoner of Azkaban", SeriesName = "Harry Potter (Full-Cast Edition)", BaseBookId = "gr:full-cast-azkaban", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "localized-provider-pocket",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 10001,
                durationSeconds: 42455,
                expectedMatchedVia: null);
        }


        [Test]
        public void staged_fields_should_remove_anthony_ryan_everywhere_before_ranking_the_martyr()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { "The Martyr" },
                ["ALBUM"] = new() { "02 The Martyr" },
                ["ARTIST"] = new() { "Anthony Ryan" },
                ["ALBUMARTIST"] = new() { "Anthony Ryan (Covenant of Steel)" },
                ["COMPOSER"] = new() { "Steven Brand" },
                ["DATE"] = new() { "2022" }
            };
            var recalls = new[]
            {
                new BookFtsMatch { BookId = 602, AuthorId = 91, AuthorName = "Anthony Ryan", BookTitle = "Anthony Ryan Interview", SeriesName = "Covenant of Steel", MatchScore = 100 },
                new BookFtsMatch { BookId = 601, AuthorId = 91, AuthorName = "Anthony Ryan", BookTitle = "The Martyr", SeriesName = "Covenant of Steel", MatchScore = 3 }
            };
            var editions = new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 6002,
                    BookId = 602,
                    BookTitle = "Anthony Ryan Interview",
                    EditionTitle = "Anthony Ryan Interview",
                    MatchingTitle = "Anthony Ryan Interview",
                    AuthorId = 91,
                    AuthorName = "Anthony Ryan",
                    NarratorNames = "Anthony Ryan",
                    ReadingFormatId = 2,
                    DurationSeconds = 3600
                },
                new EditionFtsMatch
                {
                    EditionId = 6001,
                    BookId = 601,
                    BookTitle = "The Martyr",
                    EditionTitle = "The Martyr",
                    MatchingTitle = "The Martyr",
                    AuthorId = 91,
                    AuthorName = "Anthony Ryan",
                    NarratorNames = "Steven Brand",
                    ReadingFormatId = 2,
                    DurationSeconds = 20000
                }
            };
            var books = new[]
            {
                new Book { Id = 601, AuthorId = 91, Title = "The Martyr", HardcoverBookId = "hc:martyr", MediaType = BookMediaType.Audiobook },
                new Book { Id = 602, AuthorId = 91, Title = "Anthony Ryan Interview", HardcoverBookId = "hc:anthony-ryan-interview", MediaType = BookMediaType.Audiobook }
            };

            MatchStagedStructuralScenario(
                "the-martyr",
                BookMediaType.Audiobook,
                tags,
                recalls,
                editions,
                books,
                expectedEditionId: 6001,
                durationSeconds: 20010);
        }


        [Test]
	        public void should_accept_trailing_parenthetical_when_file_has_matching_evidence()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            // FTS order: Stephen Fry edition first (correct), plain edition second.
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 6895,
                    BookId = 2748,
                    EditionTitle = "Harry Potter and the Order of the Phoenix (Narrated by Stephen Fry)",
                    BookTitle = "Harry Potter and the Order of the Phoenix (Narrated by Stephen Fry)",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Stephen Fry",
                },
                new EditionFtsMatch
                {
                    EditionId = 6605,
                    BookId = 2582,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

		            var svc = new FileMatchingService(
		                matchingLogger: null,
		                v5MatchingService: null,
		                containmentValidator: containment,
		                pendingAuthorImportService: null,
		                commandQueue: null,
		                authorFolderMatchingService: null,
		                rootFolderService: null,
		                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
		                authorService: authorService,
		                eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: null,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter and the Order of the Phoenix/Harry Potter and the Order of the Phoenix.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Harry Potter and the Order of the Phoenix" } },
                    // Stephen Fry is not in TITLE, but is present in another tag field.
                    { "COMPOSER", new List<string> { "Stephen Fry" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } },
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(6895));
	            Assert.That(match.BookId, Is.EqualTo(2748));
	        }

	        [Test]
	        public void should_ignore_generic_noise_trailing_parenthetical()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            // Only candidate has a generic noise parenthetical that is often absent from embedded tags.
	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 9001,
	                    BookId = 9001,
	                    EditionTitle = "Harry Potter and the Philosopher's Stone (Unabridged)",
	                    BookTitle = "Harry Potter and the Philosopher's Stone (Unabridged)",
	                    AuthorId = 25,
	                    AuthorName = "J.K. Rowling",
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

		            var svc = new FileMatchingService(
		                matchingLogger: null,
		                v5MatchingService: null,
		                containmentValidator: containment,
		                pendingAuthorImportService: null,
		                commandQueue: null,
		                authorFolderMatchingService: null,
		                rootFolderService: null,
		                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
		                authorService: authorService,
		                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/J.K. Rowling/Harry Potter/Harry Potter and the Philosopher's Stone.mp3",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "Harry Potter and the Philosopher's Stone" } },
	                    { "ARTIST", new List<string> { "J.K. Rowling" } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(9001));
	        }

        [Test]
        public void should_match_by_leaf_filename_asin_for_download_and_mapped_root_routes()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 25, Name = "J.K. Rowling" };
            var book = new Book
            {
                Id = 2582,
                AuthorId = 25,
                Author = author,
                Title = "Harry Potter and the Order of the Phoenix",
                MediaType = BookMediaType.Audiobook
            };
            var edition = new Edition
            {
                Id = 6605,
                BookId = 2582,
                Book = book,
                Title = "Harry Potter and the Order of the Phoenix",
                Asin = "B017V4IM1G"
            };

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch>(), throwOnSearch: true),
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { edition }),
                mediaInfoExtractor: null,
                logger: logger);

            var tags = new Dictionary<string, List<string>>
            {
                { "TITLE", new List<string> { "Harry Potter and the Order of the Phoenix" } },
                { "ARTIST", new List<string> { "J.K. Rowling" } }
            };
            var downloadFile = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Harry Potter and the Order of the Phoenix [B017V4IM1G].m4b",
                AllTags = tags
            };
            var mappedRootFile = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter and the Order of the Phoenix [B017V4IM1G].m4b",
                AllTags = tags
            };

            var downloadMatch = svc.HolyGrailMatchFile(downloadFile, BookMediaType.Audiobook, restrictToAuthorId: 25);
            var mappedRootMatch = svc.HolyGrailMatchFile(mappedRootFile, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(downloadMatch, Is.Not.Null);
            Assert.That(mappedRootMatch, Is.Not.Null);
            Assert.That(downloadMatch.EditionId, Is.EqualTo(6605));
            Assert.That(mappedRootMatch.EditionId, Is.EqualTo(downloadMatch.EditionId));
            Assert.That(mappedRootMatch.BookId, Is.EqualTo(2582));
        }

        [Test]
        public void should_not_extract_identifier_from_ancestor_path()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 25, Name = "J.K. Rowling" };
            var book = new Book
            {
                Id = 2582,
                AuthorId = 25,
                Author = author,
                Title = "Harry Potter and the Order of the Phoenix",
                MediaType = BookMediaType.Audiobook
            };
            var edition = new Edition
            {
                Id = 6605,
                BookId = 2582,
                Book = book,
                Title = "Harry Potter and the Order of the Phoenix",
                Asin = "B017V4IM1G"
            };

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch>()),
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { edition }),
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/[B017V4IM1G]/Unknown.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Unknown" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Null);
        }

        [Test]
        public void excluded_unresolved_identifier_should_not_suppress_filename_identifier_fallback()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 25, Name = "J.K. Rowling" };
            var book = new Book
            {
                Id = 2582,
                AuthorId = 25,
                Author = author,
                Title = "Harry Potter and the Order of the Phoenix",
                MediaType = BookMediaType.Audiobook
            };
            var edition = new Edition
            {
                Id = 6605,
                BookId = 2582,
                Book = book,
                Title = "Harry Potter and the Order of the Phoenix",
                Asin = "B017V4IM1G"
            };

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch>()),
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { edition }),
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Harry Potter and the Order of the Phoenix [B017V4IM1G].m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Harry Potter and the Order of the Phoenix" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } },
                    { "COMMENT", new List<string> { "legacy id B000000000" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(6605));
            Assert.That(match.Provenance.Route, Does.Contain("filename_identifier"));
            Assert.That(match.Provenance.SupportingSignals, Has.Some.Matches<MatchSignal>(signal =>
                signal.Source == "filename" && signal.Field == "PATH:FILE_VALUE"));
        }

        [Test]
        public void eligible_but_unresolved_identifier_should_not_suppress_filename_identifier_fallback()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var author = new Author { Id = 25, Name = "J.K. Rowling" };
            var book = new Book
            {
                Id = 2582,
                AuthorId = author.Id,
                Author = author,
                Title = "Harry Potter and the Order of the Phoenix",
                MediaType = BookMediaType.Audiobook
            };
            var edition = new Edition
            {
                Id = 6605,
                BookId = book.Id,
                Book = book,
                Title = book.Title,
                Asin = "B017V4IM1G"
            };
            var svc = new FileMatchingService(
                matchingLogger: null,
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
                editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch>(), throwOnSearch: true),
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { edition }),
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Harry Potter and the Order of the Phoenix [B017V4IM1G].m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "CUSTOM", new List<string> { "unresolved token B000000000" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: author.Id);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(edition.Id));
            Assert.That(match.Provenance.Route, Does.Contain("filename_identifier"));
        }

        [Test]
        public void provider_identifier_must_not_bypass_author_and_title_proof()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var wrongAuthor = new Author { Id = 101, Name = "May Dawson" };
            var wrongBook = new Book
            {
                Id = 1001,
                AuthorId = wrongAuthor.Id,
                Author = wrongAuthor,
                Title = "Resurrection",
                MediaType = BookMediaType.Audiobook
            };
            var wrongIdentifierEdition = new Edition
            {
                Id = 10001,
                BookId = wrongBook.Id,
                Book = wrongBook,
                Title = wrongBook.Title,
                Asin = "B0CSLTBRJB"
            };
            var correctAuthor = new Author { Id = 202, Name = "C.R. Jane" };
            var correctCandidate = new EditionFtsMatch
            {
                EditionId = 20002,
                BookId = 2002,
                EditionTitle = "Pucking Wrong Date",
                BookTitle = "Pucking Wrong Date",
                AuthorId = correctAuthor.Id,
                AuthorName = correctAuthor.Name
            };
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch> { correctCandidate });
            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(correctAuthor),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { wrongIdentifierEdition }),
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/C.R. Jane/Pucking Wrong Date/Pucking Wrong Date.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "M4PTITLE", new List<string> { "Pucking Wrong Date" } },
                    { "M4PARTIST", new List<string> { "C.R. Jane" } },
                    { "BOOKIDENTITY", new List<string> { "B0CSLTBRJB" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: null);

            Assert.That(fts.Calls, Is.GreaterThan(0), "a provider identifier may select a candidate but may not bypass author/title proof");
            Assert.That(match, Is.Not.Null);
            Assert.That(match.AuthorId, Is.EqualTo(correctAuthor.Id));
            Assert.That(match.BookId, Is.EqualTo(correctCandidate.BookId));
            Assert.That(match.EditionId, Is.EqualTo(correctCandidate.EditionId));
            Assert.That(match.Provenance.MatchedVia, Is.Not.EqualTo("provider_identifier"));
        }

        [Test]
        public void provider_identifier_for_specific_edition_must_not_be_proven_by_generic_work_title()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var author = new Author { Id = 303, Name = "Stephen King" };
            var book = new Book
            {
                Id = 3003,
                AuthorId = author.Id,
                Author = author,
                Title = "It",
                MediaType = BookMediaType.Audiobook
            };
            var specificEdition = new Edition
            {
                Id = 30003,
                BookId = book.Id,
                Book = book,
                Title = "Το Αυτό—Τόμος ΙΙ",
                Asin = "B0ABCDEF12"
            };
            var correctGenericCandidate = new EditionFtsMatch
            {
                EditionId = 40004,
                BookId = 4004,
                EditionTitle = "It",
                BookTitle = "It",
                AuthorId = author.Id,
                AuthorName = author.Name
            };
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch> { correctGenericCandidate });
            var svc = new FileMatchingService(
                matchingLogger: null,
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
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { specificEdition }),
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Stephen King/It/It.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "CUSTOM_AUTHOR", new List<string> { "Stephen King" } },
                    { "CUSTOM_TITLE", new List<string> { "It" } },
                    { "BOOKIDENTITY", new List<string> { "B0ABCDEF12" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: null);

            Assert.That(fts.Calls, Is.GreaterThan(0), "a generic work title must not prove a specific provider edition");
            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(correctGenericCandidate.EditionId));
            Assert.That(match.Provenance.MatchedVia, Is.Not.EqualTo("provider_identifier"));
        }

        [Test]
        public void duplicate_local_rows_for_one_provider_edition_should_remain_one_identifier_match()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var author = new Author { Id = 72, Name = "Freida McFadden" };
            var canonicalBook = new Book
            {
                Id = 1978,
                AuthorId = author.Id,
                Author = author,
                Title = "The Boyfriend",
                MediaType = BookMediaType.Audiobook
            };
            var copyBook = new Book
            {
                Id = 2978,
                AuthorId = author.Id,
                Author = author,
                Title = canonicalBook.Title,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "hc:book:1978",
                UnitKeyHash = "copy-unit"
            };
            var canonicalEdition = new Edition
            {
                Id = 4865,
                BookId = canonicalBook.Id,
                Book = canonicalBook,
                Title = canonicalBook.Title,
                Asin = "B0D3QV4S65",
                ForeignEditionId = "az:B0D3QV4S65"
            };
            var copyEdition = new Edition
            {
                Id = 5865,
                BookId = copyBook.Id,
                Book = copyBook,
                Title = copyBook.Title,
                Asin = "B0D3QV4S65",
                ForeignEditionId = "az:B0D3QV4S65"
            };
            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch>(), throwOnSearch: true),
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { canonicalEdition, copyEdition }),
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/The Boyfriend.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "BOOKIDENTITY", new List<string> { "catalog B0D3QV4S65" } },
                    { "CUSTOM_TITLE", new List<string> { "The Boyfriend" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: author.Id);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(canonicalEdition.Id));
            Assert.That(match.Provenance.SupportingSignals, Has.Some.Matches<MatchSignal>(signal =>
                signal.Field == "BOOKIDENTITY" && signal.Observed == "catalog B0D3QV4S65"));
        }

        [Test]
        public void duplicate_local_rows_for_one_provider_audiobook_should_not_create_a_false_sibling()
        {
            var logger = LogManager.GetCurrentClassLogger();
            const string providerEditionId = "az:B0719KKW5W-audiobook";
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 55257,
                    ForeignEditionId = providerEditionId,
                    BookId = 21586,
                    EditionTitle = "Wild Cards VII",
                    BookTitle = "Dead Man's Hand",
                    AuthorId = 344,
                    AuthorName = "George R.R. Martin",
                    DurationSeconds = 47580,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 60313,
                    ForeignEditionId = providerEditionId,
                    BookId = 21586,
                    EditionTitle = "Wild Cards VII",
                    BookTitle = "Dead Man's Hand",
                    AuthorId = 344,
                    AuthorName = "George R.R. Martin",
                    DurationSeconds = 47580,
                    ReadingFormatId = 2
                }
            });
            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 344, Name = "George R.R. Martin" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/George R.R. Martin/Wild Cards II/Wild Cards II (1).mp3",
                DurationSeconds = 1072,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { "Wild Cards VII" } },
                    { "ALBUMARTIST", new List<string> { "George R. R. Martin" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 344);

            Assert.That(match, Is.Not.Null, "copy rows for one provider edition are one edition, not competing audiobook siblings");
            Assert.That(match.EditionId, Is.AnyOf(55257, 60313));
        }

        [Test]
        public void conflicting_tag_and_filename_identifiers_with_shared_isbn_should_continue_normal_matching()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var author = new Author { Id = 25, Name = "J.K. Rowling" };
            var firstBook = new Book
            {
                Id = 1,
                AuthorId = author.Id,
                Author = author,
                Title = "First Book",
                MediaType = BookMediaType.Audiobook
            };
            var secondBook = new Book
            {
                Id = 2,
                AuthorId = author.Id,
                Author = author,
                Title = "Second Book",
                MediaType = BookMediaType.Audiobook
            };
            var firstEdition = new Edition
            {
                Id = 11,
                BookId = 1,
                Book = firstBook,
                Title = firstBook.Title,
                Asin = "B000000001",
                Isbn13 = "9781464241765"
            };
            var secondEdition = new Edition
            {
                Id = 22,
                BookId = 2,
                Book = secondBook,
                Title = secondBook.Title,
                Asin = "B000000002",
                Isbn13 = "9781464241765"
            };
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new()
                {
                    EditionId = firstEdition.Id,
                    BookId = firstBook.Id,
                    EditionTitle = firstBook.Title,
                    BookTitle = firstBook.Title,
                    AuthorId = author.Id,
                    AuthorName = author.Name
                }
            });
            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { firstEdition, secondEdition }),
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/First Book [B000000002].m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "First Book" } },
                    { "ARTIST", new List<string> { author.Name } },
                    { "CUSTOM", new List<string> { "B000000001" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: author.Id);

            Assert.That(fts.Calls, Is.GreaterThan(0));
            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(firstEdition.Id));
            Assert.That(match.Provenance.MatchedVia, Is.Not.EqualTo("provider_identifier"));
        }

	        [Test]
	        public void should_reject_series_trailing_parenthetical_when_series_not_evidenced_in_tags()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            // FTS order: series-qualified edition first (should be rejected), plain edition second (should win).
	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 517,
	                    BookId = 143,
	                    EditionTitle = "Never Come Back (Nora McTavish, Book 2)",
	                    BookTitle = "Never Come Back (Nora McTavish, Book 2)",
	                    AuthorId = 6,
	                    AuthorName = "Joe Hart",
	                },
	                new EditionFtsMatch
	                {
	                    EditionId = 518,
	                    BookId = 143,
	                    EditionTitle = "Never Come Back",
	                    BookTitle = "Never Come Back",
	                    AuthorId = 6,
	                    AuthorName = "Joe Hart",
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 6, Name = "Joe Hart" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                authorService: authorService,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/Joe Hart/Never Come Back/Never Come Back.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "Never Come Back" } },
	                    { "ARTIST", new List<string> { "Joe Hart" } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 6);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(518));
	        }

	        [Test]
	        public void should_allow_bracketed_series_label_when_series_metadata_explains_suffix()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 3401,
	                    BookId = 919,
	                    EditionTitle = "The Red Herring",
	                    BookTitle = "The Red Herring",
	                    AuthorId = 88,
	                    AuthorName = "Katie Ginger",
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 88, Name = "Katie Ginger" });
	            var bookService = new StubBookService(new[]
	            {
	                new Book
	                {
	                    Id = 919,
	                    SeriesName = "Belgrave Dynasty",
	                    SeriesPosition = "3"
	                }
	            });

                var svc = new FileMatchingService(
                    matchingLogger: null,
                    v5MatchingService: null,
                    containmentValidator: containment,
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                    authorService: authorService,
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: bookService,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/Katie Ginger/The Red Herring/The Red Herring.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "The Red Herring [Belgrave Dynasty Series, Book 3]" } },
	                    { "ALBUM", new List<string> { "The Red Herring [Belgrave Dynasty Series, Book 3]" } },
	                    { "ARTIST", new List<string> { "Katie Ginger" } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 88);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(3401));
	            Assert.That(match.BookId, Is.EqualTo(919));
	        }

	        [Test]
        public void should_require_author_evidence_for_unscoped_contributor_name_title_match()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 46570,
	                    BookId = 9999,
	                    EditionTitle = "Ransom",
	                    BookTitle = "Ransom",
	                    AuthorId = 111,
	                    AuthorName = "Callie Hart",
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 111, Name = "Callie Hart" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(),
	                authorService: authorService,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/Frank Herbert/The Jesus Incident/The Jesus Incident.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "The Jesus Incident" } },
	                    { "ARTIST", new List<string> { "Frank Herbert & Bill Ransom" } },
	                }
	            };

		            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: null);

	            Assert.That(match, Is.Null);
	        }

	        [Test]
	        public void should_not_use_comment_field_as_edition_title_evidence()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 101,
	                    BookId = 202,
	                    EditionTitle = "Paul of Dune",
	                    BookTitle = "Paul of Dune",
	                    AuthorId = 303,
	                    AuthorName = "Brian Herbert",
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 303, Name = "Brian Herbert" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(),
	                authorService: authorService,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/Frank Herbert/Dune/Dune Messiah.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "Dune Messiah" } },
	                    { "ARTIST", new List<string> { "Frank Herbert" } },
	                    { "MP4:©cmt", new List<string> { "Based on the classic series. Paul of Dune is referenced here." } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 303);

	            Assert.That(match, Is.Null);
	        }

	        [Test]
	        public void should_reject_unscoped_candidate_when_author_not_in_tags()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            // Unscoped search returns a wrong-author candidate before the correct one.
	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 1,
	                    BookId = 1,
	                    EditionTitle = "Dune",
	                    BookTitle = "Dune",
	                    AuthorId = 10,
	                    AuthorName = "Brian Herbert",
	                },
	                new EditionFtsMatch
	                {
	                    EditionId = 2,
	                    BookId = 2,
	                    EditionTitle = "Dune",
	                    BookTitle = "Dune",
	                    AuthorId = 11,
	                    AuthorName = "Frank Herbert",
	                }
	            });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(),
	                authorService: null,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/Frank Herbert/Dune/Dune.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "Dune" } },
	                    { "ARTIST", new List<string> { "Frank Herbert" } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: null);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(2));
	        }

	        [Test]
	        public void should_not_match_series_and_position_only_without_title_evidence()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 2001,
	                    BookId = 2001,
	                    EditionTitle = "Dune Messiah",
	                    BookTitle = "Dune Messiah",
	                    AuthorId = 11,
	                    AuthorName = "Frank Herbert",
	                }
	            });

	            var bookService = new StubBookService(new[]
	            {
	                new Book
	                {
	                    Id = 2001,
	                    Title = "Dune Messiah",
	                    SeriesLinks = new List<SeriesBookLink>
	                    {
	                        new SeriesBookLink
	                        {
	                            BookId = 2001,
	                            Series = new LazyLoaded<Series>(new Series { Title = "Dune" }),
	                            Position = "2",
	                            SeriesPosition = 2
	                        }
	                    }
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 11, Name = "Frank Herbert" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                authorService: authorService,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: bookService,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/Frank Herbert/Dune/Dune Messiah.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "SERIES", new List<string> { "Dune" } },
	                    { "SERIESPOSITION", new List<string> { "2" } },
	                    { "ARTIST", new List<string> { "Frank Herbert" } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 11);

	            Assert.That(match, Is.Null);
	        }

	        [Test]
	        public void should_prefer_dedicated_narrator_over_author_as_narrator_when_both_in_tags()
	        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            // FTS/BM25 order: author-as-narrator first (wrong), dedicated narrator second (correct).
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "C.S. Lewis",
                    NarratorNames = "C.S. Lewis",
                    ReleaseDate = new DateTime(2012, 1, 1),
                },
                new EditionFtsMatch
                {
                    EditionId = 2,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "C.S. Lewis",
                    NarratorNames = "Tom Hollander",
                    ReleaseDate = new DateTime(2012, 1, 1),
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "C.S. Lewis" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/C.S. Lewis/The Casual Vacancy/The Casual Vacancy.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "The Casual Vacancy" } },
	                    // Both names are present somewhere in tags; dedicated narrator should win.
	                    { "COMPOSER", new List<string> { "Tom Hollander" } },
	                    { "ARTIST", new List<string> { "C.S. Lewis" } },
	                }
	            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(2));
            Assert.That(match.BookId, Is.EqualTo(1));
        }

        [Test]
        public void should_use_release_year_as_tiebreak_for_same_narrator()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            // Same narrator match; year should pick the closer ReleaseDate.
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 10,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Tom Hollander",
                    ReleaseDate = new DateTime(2010, 1, 1),
                },
                new EditionFtsMatch
                {
                    EditionId = 11,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Tom Hollander",
                    ReleaseDate = new DateTime(2012, 1, 1),
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

		            var file = new DiscoveredFileWithMetadata
		            {
		                Path = "/audiobooks/J.K. Rowling/The Casual Vacancy/The Casual Vacancy.m4b",
		                DurationSeconds = 36600,
		                AllTags = new Dictionary<string, List<string>>
		                {
		                    { "TITLE", new List<string> { "The Casual Vacancy" } },
		                    { "ARTIST", new List<string> { "J.K. Rowling" } },
	                    { "COMPOSER", new List<string> { "Tom Hollander" } },
	                    { "YEAR", new List<string> { "2012" } },
	                }
	            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(11));
        }

        [Test]
        public void should_prefer_narrator_bearing_sibling_over_generic_duration_closer_sibling()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 30,
                    BookId = 1,
                    EditionTitle = "Black Sun Rising",
                    BookTitle = "Black Sun Rising",
                    AuthorId = 25,
                    AuthorName = "C.S. Friedman",
                    MatchScore = 11.0,
                    DurationSeconds = 39990,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 31,
                    BookId = 1,
                    EditionTitle = "Black Sun Rising",
                    BookTitle = "Black Sun Rising",
                    AuthorId = 25,
                    AuthorName = "C.S. Friedman",
                    NarratorNames = "R.C. Bray",
                    MatchScore = 10.0,
                    DurationSeconds = 40100,
                    ReadingFormatId = 2
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "C.S. Friedman" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/C.S. Friedman/Black Sun Rising/Black Sun Rising.m4b",
                DurationSeconds = 40000,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Black Sun Rising" } },
                    { "ARTIST", new List<string> { "C.S. Friedman" } },
                    { "COMPOSER", new List<string> { "R.C. Bray" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(31));
        }

        [Test]
        public void should_prefer_series_suffixed_narrator_edition_when_series_suffix_is_explainable()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 3243,
                    BookId = 887,
                    EditionTitle = "Dawn of Forever: Jack & Jill Series, Book 3",
                    EditionSubTitle = "Jack & Jill Series, Book 3",
                    BookTitle = "Dawn of Forever",
                    AuthorId = 25,
                    AuthorName = "Jewel E. Ann",
                    NarratorNames = "Stefanie Kay",
                    MatchScore = 34.40,
                    DurationSeconds = 39900,
                    ReleaseDate = new DateTime(2024, 10, 22),
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 3244,
                    BookId = 887,
                    EditionTitle = "Dawn of Forever",
                    EditionSubTitle = "Jack & Jill Series, Book 3",
                    BookTitle = "Dawn of Forever",
                    AuthorId = 25,
                    AuthorName = "Jewel E. Ann",
                    MatchScore = 32.00,
                    DurationSeconds = 39900,
                    ReleaseDate = new DateTime(2024, 10, 22),
                    ReadingFormatId = 2
                }
            });

            var books = new List<Book>
            {
                new Book { Id = 887, AuthorId = 25, Title = "Dawn of Forever", SeriesName = "Jack & Jill", SeriesPosition = "3" }
            };

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "Jewel E. Ann" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: new StubBookService(books),
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Jewel E. Ann/Dawn of Forever/Dawn of Forever.mp3",
                DurationSeconds = 39900,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Dawn of Forever" } },
                    { "ALBUM", new List<string> { "Dawn of Forever" } },
                    { "ARTIST", new List<string> { "Jewel E. Ann" } },
                    { "COMPOSER", new List<string> { "Stefanie Kay" } },
                    { "SERIES", new List<string> { "Jack & Jill 3" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(3243));
        }

        [Test]
        public void should_select_neutral_duration_sibling_and_reject_concrete_duration_conflict()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 70,
                    BookId = 1,
                    EditionTitle = "Short Story",
                    BookTitle = "Short Story",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    MatchScore = 15.0,
                    DurationSeconds = 0,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 71,
                    BookId = 1,
                    EditionTitle = "Short Story",
                    BookTitle = "Short Story",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    MatchScore = 16.0,
                    DurationSeconds = 36000,
                    ReadingFormatId = 2
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "Test Author" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Test Author/Short Story/Short Story.m4b",
                DurationSeconds = 300,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Short Story" } },
                    { "ARTIST", new List<string> { "Test Author" } }
                }
            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(70), "missing catalog duration is neutral; the stronger-scoring 10-hour mismatch must remain ineligible");
	            Assert.That(match.MatchedVia, Is.EqualTo("undistinguished_audiobook_edition"));
        }

        [Test]
        public void should_not_fallback_when_every_full_book_audiobook_duration_concretely_conflicts()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 72,
                    BookId = 1,
                    EditionTitle = "Short Story",
                    BookTitle = "Short Story",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    MatchScore = 16.0,
                    DurationSeconds = 36000,
                    ReadingFormatId = 2
                },
                new EditionFtsMatch
                {
                    EditionId = 73,
                    BookId = 1,
                    EditionTitle = "Short Story",
                    BookTitle = "Short Story",
                    AuthorId = 25,
                    AuthorName = "Test Author",
                    MatchScore = 15.0,
                    DurationSeconds = 40000,
                    ReadingFormatId = 2
                }
            });
            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 25, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Test Author/Short Story/Short Story.m4b",
                DurationSeconds = 300,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Short Story" } },
                    { "ARTIST", new List<string> { "Test Author" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Null, "real full-book duration conflicts must not be relabeled as missing edition evidence");
        }

        [Test]
	        public void should_use_duration_as_tiebreak_for_same_narrator_and_year()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            // Same narrator + year; duration should pick the closer DurationSeconds.
            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 20,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Tom Hollander",
                    ReleaseDate = new DateTime(2012, 1, 1),
                    DurationSeconds = 36000, // 10:00:00
                },
                new EditionFtsMatch
                {
                    EditionId = 21,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Tom Hollander",
                    ReleaseDate = new DateTime(2012, 1, 1),
                    DurationSeconds = 36600, // 10:10:00
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

		            var file = new DiscoveredFileWithMetadata
		            {
		                Path = "/audiobooks/J.K. Rowling/The Casual Vacancy/The Casual Vacancy.m4b",
		                DurationSeconds = 36600,
		                AllTags = new Dictionary<string, List<string>>
		                {
		                    { "TITLE", new List<string> { "The Casual Vacancy" } },
		                    { "ARTIST", new List<string> { "J.K. Rowling" } },
		                    { "COMPOSER", new List<string> { "Tom Hollander" } },
		                    { "YEAR", new List<string> { "2012" } },
		                }
		            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

		            Assert.That(match, Is.Not.Null);
		            Assert.That(match.EditionId, Is.EqualTo(21));
		        }

		        [Test]
		        public void should_match_when_album_is_clean_even_if_title_contains_packaging_noise()
		        {
		            var logger = LogManager.GetCurrentClassLogger();
		            var containment = new ContainmentValidator(new TagNormalizer(), logger);

		            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
		            {
		                new EditionFtsMatch
		                {
		                    EditionId = 100,
		                    BookId = 1,
		                    EditionTitle = "The Inmate",
		                    BookTitle = "The Inmate",
		                    AuthorId = 25,
		                    AuthorName = "Freida McFadden",
		                }
		            });

		            var authorService = new StubAuthorService(new Author { Id = 25, Name = "Freida McFadden" });

		            var svc = new FileMatchingService(
		                matchingLogger: null,
		                v5MatchingService: null,
		                containmentValidator: containment,
		                pendingAuthorImportService: null,
		                commandQueue: null,
		                authorFolderMatchingService: null,
		                rootFolderService: null,
		                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
		                authorService: authorService,
		                eventAggregator: null,
		                authorLibraryService: null,
		                editionFtsRepository: fts,
		                bookService: null,
		                editionService: null,
		                editionRepository: null,
		                mediaInfoExtractor: null,
		                logger: logger);

		            var file = new DiscoveredFileWithMetadata
		            {
		                Path = "/audiobooks/Freida McFadden/The Inmate/The Inmate.m4b",
		                AllTags = new Dictionary<string, List<string>>
		                {
		                    { "ALBUM", new List<string> { "The Inmate" } },
		                    { "TITLE", new List<string> { "Freida McFadden - The Inmate Pt01 of 58" } },
		                    { "ARTIST", new List<string> { "Freida McFadden" } },
		                    { "DATE", new List<string> { "2022" } },
		                    { "GENRE", new List<string> { "Thriller" } },
		                    { "COMMENT", new List<string> { "Created by FileFlows https://fileflows.com" } },
		                }
		            };

		            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

		            Assert.That(match, Is.Not.Null);
		            Assert.That(match.EditionId, Is.EqualTo(100));
		        }

		        [Test]
	        public void should_select_deterministic_native_when_siblings_have_no_usable_narrator_or_duration_evidence()
		        {
		            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            // The lower local row ID is deliberately assigned the higher provider ID. This proves
	            // deterministic selection follows provider identity and does not prefer a narrator-less edition.
	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 40,
	                    ForeignEditionId = "gr:0002-audiobook",
	                    BookId = 1,
	                    EditionTitle = "Harry Potter and the Order of the Phoenix",
	                    BookTitle = "Harry Potter and the Order of the Phoenix",
	                    AuthorId = 25,
	                    AuthorName = "J.K. Rowling",
	                    NarratorNames = "Jim Dale",
	                    ReadingFormatId = 2
	                },
	                new EditionFtsMatch
	                {
	                    EditionId = 41,
	                    ForeignEditionId = "gr:0001-audiobook",
	                    BookId = 1,
	                    EditionTitle = "Harry Potter and the Order of the Phoenix",
	                    BookTitle = "Harry Potter and the Order of the Phoenix",
	                    AuthorId = 25,
	                    AuthorName = "J.K. Rowling",
	                    NarratorNames = null,
	                    ReadingFormatId = 2
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(),
	                authorService: authorService,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/J.K. Rowling/Harry Potter/Harry Potter and the Order of the Phoenix.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "Harry Potter and the Order of the Phoenix" } },
	                    { "ARTIST", new List<string> { "J.K. Rowling" } },
	                    { "COMPOSER", new List<string> { "Stephen Fry" } }
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(41), "missing evidence must not create a narratorless-edition preference; the deterministic final tie-break selects the lower provider-owned edition id, never the lower local row id");
	            Assert.That(match.MatchedVia, Is.EqualTo("undistinguished_audiobook_edition"));
	            Assert.That(
	                match.Provenance.NeutralSignals.Any(signal => signal.Type == "edition_selection"),
	                Is.True,
	                "the Matching tab must disclose that book identity was proven but edition evidence was inconclusive");
	        }

	        [Test]
	        public void should_select_native_representative_when_author_proof_is_not_narrator_proof()
	        {
	            var logger = LogManager.GetCurrentClassLogger();
	            var containment = new ContainmentValidator(new TagNormalizer(), logger);

	            // The author field proves the book, but a self-narrator needs a second field before it can
	            // distinguish an edition. Missing edition evidence is neutral, not a narrator contradiction.
	            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	            {
	                new EditionFtsMatch
	                {
	                    EditionId = 50,
	                    ForeignEditionId = "gr:0001-audiobook",
	                    BookId = 1,
	                    EditionTitle = "Can't Hurt Me",
	                    BookTitle = "Can't Hurt Me",
	                    AuthorId = 25,
	                    AuthorName = "David Goggins",
	                    NarratorNames = "Scott Brick",
	                    ReadingFormatId = 2
	                },
	                new EditionFtsMatch
	                {
	                    EditionId = 51,
	                    ForeignEditionId = "gr:0000-audiobook",
	                    BookId = 1,
	                    EditionTitle = "Can't Hurt Me",
	                    BookTitle = "Can't Hurt Me",
	                    AuthorId = 25,
	                    AuthorName = "David Goggins",
	                    NarratorNames = "David Goggins",
	                    ReadingFormatId = 2
	                },
	                new EditionFtsMatch
	                {
	                    EditionId = 52,
	                    ForeignEditionId = "gr:0002-audiobook",
	                    BookId = 1,
	                    EditionTitle = "Can't Hurt Me",
	                    BookTitle = "Can't Hurt Me",
	                    AuthorId = 25,
	                    AuthorName = "David Goggins",
	                    NarratorNames = null,
	                    ReadingFormatId = 2
	                }
	            });

	            var authorService = new StubAuthorService(new Author { Id = 25, Name = "David Goggins" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(),
	                authorService: authorService,
	                eventAggregator: null,
	                authorLibraryService: null,
	                editionFtsRepository: fts,
	                bookService: null,
	                editionService: null,
	                editionRepository: null,
	                mediaInfoExtractor: null,
	                logger: logger);

	            var file = new DiscoveredFileWithMetadata
	            {
	                Path = "/audiobooks/David Goggins/Can't Hurt Me/Can't Hurt Me.m4b",
	                AllTags = new Dictionary<string, List<string>>
	                {
	                    { "TITLE", new List<string> { "Can't Hurt Me" } },
	                    { "ARTIST", new List<string> { "David Goggins" } },
	                }
	            };

	            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	            Assert.That(match, Is.Not.Null);
	            Assert.That(match.EditionId, Is.EqualTo(50));
	            Assert.That(match.MatchedVia, Is.EqualTo("undistinguished_audiobook_edition"));
	        }

	        [Test]
	        public void should_not_match_wrong_book_when_subtitle_contains_other_title()
	        {
	                var logger = LogManager.GetCurrentClassLogger();
                var containment = new ContainmentValidator(new TagNormalizer(), logger);

                // FTS/BM25 order: subtitle-only match first (wrong), title match second (correct).
                var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 30,
                        BookId = 1,
                        EditionTitle = "Hogwarts Library",
                        BookTitle = "Hogwarts Library",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 31,
                        BookId = 2,
                        EditionTitle = "Tales of Beedle the Bard",
                        BookTitle = "Tales of Beedle the Bard",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                    }
                });

                var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling", AudiobookPath = "/audiobooks/J.K. Rowling" });

                var svc = new FileMatchingService(
                    matchingLogger: null,
                    v5MatchingService: null,
                    containmentValidator: containment,
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                    authorService: authorService,
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: null,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

                var file = new DiscoveredFileWithMetadata
                {
                    Path = "/audiobooks/J.K. Rowling/Tales of Beedle the Bard/Tales of Beedle the Bard.m4b",
                    AllTags = new Dictionary<string, List<string>>
                    {
                        { "TITLE", new List<string> { "Tales of Beedle the Bard" } },
                        // Misleading metadata: subtitle contains another title/collection identifier.
                        { "SUBTITLE", new List<string> { "Hogwarts Library" } },
                        { "ARTIST", new List<string> { "J.K. Rowling" } },
                    }
                };

                var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                Assert.That(match, Is.Not.Null);
                Assert.That(match.EditionId, Is.EqualTo(31));
            }

            [Test]
            public async System.Threading.Tasks.Task should_sum_duration_across_multi_part_audiobook_group_for_matching()
            {
                var logger = LogManager.GetCurrentClassLogger();
                var containment = new ContainmentValidator(new TagNormalizer(), logger);

                var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                {
                    // Wrong candidate first (BM25 order), correct candidate second.
                    // Representative-file duration would pick EditionId=23; summed group duration should pick EditionId=22.
                    new EditionFtsMatch
                    {
                        EditionId = 23,
                        BookId = 1,
                        EditionTitle = "The Casual Vacancy",
                        BookTitle = "The Casual Vacancy",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                        NarratorNames = "Tom Hollander",
                        ReleaseDate = new DateTime(2012, 1, 1),
                        DurationSeconds = 3600,
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 22,
                        BookId = 1,
                        EditionTitle = "The Casual Vacancy",
                        BookTitle = "The Casual Vacancy",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                        NarratorNames = "Tom Hollander",
                        ReleaseDate = new DateTime(2012, 1, 1),
                        DurationSeconds = 7200,
                    }
                });

                var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

                var part1 = "/audiobooks/J.K. Rowling/The Casual Vacancy/The Casual Vacancy (1).mp3";
                var part2 = "/audiobooks/J.K. Rowling/The Casual Vacancy/The Casual Vacancy (2).mp3";

                var mediaInfoExtractor = new StubMediaInfoExtractor(new Dictionary<string, TimeSpan>
                {
                    { part1, TimeSpan.FromHours(1) },
                    { part2, TimeSpan.FromHours(1) }
                });

	                    var svc = new FileMatchingService(
	                        matchingLogger: new NullMatchingUploadLogger(),
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: null,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: mediaInfoExtractor,
                    logger: logger);

                var tags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "The Casual Vacancy" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } },
                    { "COMPOSER", new List<string> { "Tom Hollander" } },
                    { "YEAR", new List<string> { "2012" } },
                };

                var result = await svc.MatchFilesToLibraryAsync(
                    new[]
                    {
                        new DiscoveredFileWithMetadata { Path = part1, Size = 1, Modified = DateTime.UtcNow, AllTags = new Dictionary<string, List<string>>(tags) },
                        new DiscoveredFileWithMetadata { Path = part2, Size = 1, Modified = DateTime.UtcNow, AllTags = new Dictionary<string, List<string>>(tags) },
                    },
                    restrictToAuthorId: 25,
                    forDownloads: false);

                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(2));
                Assert.That(result.MatchedFiles.Select(m => m.EditionId).Distinct().Single(), Is.EqualTo(22));
            }

            [Test]
            public async System.Threading.Tasks.Task balanced_group_membership_should_not_rematch_unabridged_parts_by_chapter_duration()
            {
                var logger = LogManager.GetCurrentClassLogger();
                var containment = new ContainmentValidator(new TagNormalizer(), logger);
                var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 100,
                        BookId = 20,
                        EditionTitle = "Wild Cards VII",
                        BookTitle = "Dead Man's Hand",
                        AuthorId = 38,
                        AuthorName = "George R.R. Martin",
                        DurationSeconds = 7200,
                        ReadingFormatId = 2,
                        MatchScore = 30.0,
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 200,
                        BookId = 10,
                        EditionTitle = "Wild Cards",
                        BookTitle = "Wild Cards",
                        AuthorId = 38,
                        AuthorName = "George R.R. Martin",
                        DurationSeconds = 0,
                        ReadingFormatId = 3,
                        MatchScore = 20.0,
                    }
                });
                var books = new[]
                {
                    new Book
                    {
                        Id = 10,
                        AuthorId = 38,
                        Title = "Wild Cards",
                        BaseBookId = "hc:1175234",
                        SeriesName = "Wild Cards",
                        SeriesPosition = "1"
                    },
                    new Book
                    {
                        Id = 20,
                        AuthorId = 38,
                        Title = "Dead Man's Hand",
                        BaseBookId = "hc:468268",
                        SeriesName = "Wild Cards",
                        SeriesPosition = "7"
                    }
                };
                var part1 = "/audiobooks/George R. R. Martin/Dead Man's Hand/Dead Man's Hand 01.mp3";
                var part2 = "/audiobooks/George R. R. Martin/Dead Man's Hand/Dead Man's Hand 02.mp3";
                var mediaInfoExtractor = new StubMediaInfoExtractor(new Dictionary<string, TimeSpan>
                {
                    { part1, TimeSpan.FromHours(1) },
                    { part2, TimeSpan.FromHours(1) }
                });
                var svc = new FileMatchingService(
                    matchingLogger: new NullMatchingUploadLogger(),
                    v5MatchingService: null,
                    containmentValidator: containment,
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                    authorService: new StubAuthorService(new Author { Id = 38, Name = "George R.R. Martin" }),
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: new StubBookService(books),
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: mediaInfoExtractor,
                    logger: logger);
                Dictionary<string, List<string>> Tags() => new Dictionary<string, List<string>>
                {
                    { "BOOKIDENTITY", new List<string> { "Wild Cards VII (Unabridged)" } },
                    { "CONTRIBUTOR", new List<string> { "George R. R. Martin, Wild Cards Trust" } },
                };
                var trace = new RecordingTraceSink();
                var context = MatchingContextPresets.ForDirectDefault();
                context.TraceSink = trace;

                var result = await svc.MatchFilesToLibraryAsync(
                    new[]
                    {
                        new DiscoveredFileWithMetadata { Path = part1, Size = 1, Modified = DateTime.UtcNow, AllTags = Tags() },
                        new DiscoveredFileWithMetadata { Path = part2, Size = 1, Modified = DateTime.UtcNow, AllTags = Tags() },
                    },
                    restrictToAuthorId: 38,
                    context);

                var traceSummary = string.Join(
                    Environment.NewLine,
                    trace.Events
                        .Where(evt => evt.EventType is "candidate_rejected" or "candidate_ranked" or "match_selected")
                        .Select(evt => $"{evt.EventType} edition={evt.EditionId} book={evt.BookId} rank={evt.Rank} reason={evt.Reason} detail={evt.Detail}"));

                Assert.Multiple(() =>
                {
                    Assert.That(result.UnmatchedFiles, Is.Empty);
                    Assert.That(result.MatchedFiles, Has.Length.EqualTo(2));
                    Assert.That(result.MatchedFiles.Select(match => match.BookId).Distinct().Single(), Is.EqualTo(20), traceSummary);
                    Assert.That(result.MatchedFiles.Select(match => match.EditionId).Distinct().Single(), Is.EqualTo(100), traceSummary);
                });
            }

        [Test]
        public void should_match_possessives_with_curly_apostrophes()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            Assert.That(
                containment.ValidateEditionInTags(
                    "Harry Potter and the Sorcerer's Stone",
                    new Dictionary<string, List<string>>
                    {
                        { "BOOKFOLDER", new List<string> { "Harry Potter and the Sorcerer’s Stone" } }
                    }),
                Is.True);

            Assert.That(
                containment.ValidateEditionInTags(
                    "Harry Potter and the Philosopher's Stone",
                    new Dictionary<string, List<string>>
                    {
                        { "BOOKFOLDER", new List<string> { "Harry Potter and the Philosopher’s Stone" } }
                    }),
                Is.True);
        }

            [Test]
                public void should_not_collapse_multiple_single_file_books_directly_under_author_folder()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                // Provide two distinct editions for the same author; smoke test should pick the right one per file.
                var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 100,
                        BookId = 1000,
                        EditionTitle = "Baby City",
                        BookTitle = "Baby City",
                        AuthorId = 25,
                        AuthorName = "Freida McFadden",
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 101,
                        BookId = 1001,
                        EditionTitle = "The Coworker",
                        BookTitle = "The Coworker",
                        AuthorId = 25,
                        AuthorName = "Freida McFadden",
                    }
                });

                var authorService = new StubAuthorService(new Author
                {
                    Id = 25,
                    Name = "Freida McFadden",
                    AudiobookPath = "/audiobooks/Freida McFadden"
                });

	                    var svc = new FileMatchingService(
	                        matchingLogger: new NullMatchingUploadLogger(),
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: null,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

                var files = new[]
                {
                    new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Freida McFadden/Baby City.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Baby City" } },
                            { "ARTIST", new List<string> { "Freida McFadden" } },
                        }
                    },
                    new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Freida McFadden/The Coworker.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "The Coworker" } },
                            { "ARTIST", new List<string> { "Freida McFadden" } },
                        }
                    }
                };

                var result = svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: 25, forDownloads: false).GetAwaiter().GetResult();

                Assert.That(result, Is.Not.Null);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(2));
                Assert.That(result.UnmatchedFiles, Is.Empty);

                var byPath = result.MatchedFiles.ToDictionary(m => m.File.Path, m => m.EditionId);
                    Assert.That(byPath["/audiobooks/Freida McFadden/Baby City.m4b"], Is.EqualTo(100));
                    Assert.That(byPath["/audiobooks/Freida McFadden/The Coworker.m4b"], Is.EqualTo(101));
                }

                [Test]
                public void should_fail_closed_for_loose_author_root_files_without_title_evidence()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    // FTS can return candidates, but without title evidence in tags we should NOT match via filename/path.
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 1000,
                            EditionTitle = "Baby City",
                            BookTitle = "Baby City",
                            AuthorId = 25,
                            AuthorName = "Freida McFadden",
                        }
                    });

                    var authorService = new StubAuthorService(new Author
                    {
                        Id = 25,
                        Name = "Freida McFadden",
                        AudiobookPath = "/audiobooks/Freida McFadden"
                    });

	                    var svc = new FileMatchingService(
	                        matchingLogger: new NullMatchingUploadLogger(),
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    // Title is intentionally missing. With filename/path fallback disabled for author-root loose files,
                    // this must not match even though the filename contains the title.
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Freida McFadden/Baby City.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "ARTIST", new List<string> { "Freida McFadden" } }
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_not_match_loose_author_root_file_by_filename_when_title_tags_missing()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 1000,
                            EditionTitle = "Baby City",
                            BookTitle = "Baby City",
                            AuthorId = 25,
                            AuthorName = "Freida McFadden",
                        }
                    });

                    var authorService = new StubAuthorService(new Author
                    {
                        Id = 25,
                        Name = "Freida McFadden",
                        AudiobookPath = "/audiobooks/Freida McFadden"
                    });

	                    var svc = new FileMatchingService(
	                        matchingLogger: new NullMatchingUploadLogger(),
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var files = new[]
                    {
                        new DiscoveredFileWithMetadata
                        {
                            Path = "/audiobooks/Freida McFadden/Baby City.m4b",
                            AllTags = new Dictionary<string, List<string>>
                            {
                                { "ARTIST", new List<string> { "Freida McFadden" } }
                            }
                        }
                    };

                    var result = svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: 25, forDownloads: false).GetAwaiter().GetResult();

                    Assert.That(result.MatchedFiles, Is.Empty);
                    Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
                    Assert.That(result.UnmatchedFiles[0].File.Path, Is.EqualTo("/audiobooks/Freida McFadden/Baby City.m4b"));
                }

        [Test]
        public void should_accept_structural_packaging_tokens_in_title_field()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 1,
                    BookId = 1,
                    EditionTitle = "Dune Messiah",
                    BookTitle = "Dune Messiah",
                    AuthorId = 25,
                    AuthorName = "Frank Herbert",
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "Frank Herbert" });

	                var svc = new FileMatchingService(
	                    matchingLogger: null,
	                    v5MatchingService: null,
	                    containmentValidator: containment,
	                    pendingAuthorImportService: null,
	                    commandQueue: null,
	                    authorFolderMatchingService: null,
	                    rootFolderService: null,
	                    configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                    authorService: authorService,
	                    eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Dune Messiah/Dune Messiah 01.mp3",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Dune Messiah Part 1" } },
                    { "ARTIST", new List<string> { "Frank Herbert" } },
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(1));
            Assert.That(match.BookId, Is.EqualTo(1));
        }

        [Test]
            public void should_validate_multi_value_field_and_ignore_season_when_number_present()
            {
                var logger = LogManager.GetCurrentClassLogger();
                var containment = new ContainmentValidator(new TagNormalizer(), logger);

            Assert.That(
                containment.ValidateEditionInTags(
                    "Impact Winter Season 3",
                    new Dictionary<string, List<string>>
                    {
                        { "MP4:----", new List<string> { "Impact Winter", "3" } }
                    }),
                Is.True);

            Assert.That(
                containment.ValidateEditionInTags(
                    "Season of Storms",
                    new Dictionary<string, List<string>>
                    {
                        { "TITLE", new List<string> { "Storms" } }
                        }),
                    Is.False);
            }

            [TestCase(BookMatchingStrictness.Strict)]
            [TestCase(BookMatchingStrictness.Balanced)]
            [TestCase(BookMatchingStrictness.Aggressive)]
            public void should_use_series_position_from_same_tag_field_when_db_series_matches(BookMatchingStrictness strictness)
            {
                var logger = LogManager.GetCurrentClassLogger();
                var containment = new ContainmentValidator(new TagNormalizer(), logger);

                var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                {
                    // Wrong candidate first: base title (would incorrectly match "Impact Winter 3" if numbers are ignored)
                    new EditionFtsMatch
                    {
                        EditionId = 1,
                        BookId = 1,
                        EditionTitle = "Impact Winter",
                        BookTitle = "Impact Winter",
                        AuthorId = 44,
                        AuthorName = "Travis Beacham",
                    },
                    // Correct candidate second: numbered season
                    new EditionFtsMatch
                    {
                        EditionId = 2,
                        BookId = 2,
                        EditionTitle = "Impact Winter Season 3",
                        BookTitle = "Impact Winter Season 3",
                        AuthorId = 44,
                        AuthorName = "Travis Beacham",
                    }
                });

                var authorService = new StubAuthorService(new Author { Id = 44, Name = "Travis Beacham" });
                var bookService = new StubBookService(new[]
                {
                    new Book { Id = 1, AuthorId = 44, Title = "Impact Winter", SeriesName = "Impact Winter", SeriesPosition = "1" },
                    new Book { Id = 2, AuthorId = 44, Title = "Impact Winter Season 3", SeriesName = "Impact Winter", SeriesPosition = "3" }
                });

                var svc = new FileMatchingService(
                    matchingLogger: null,
                    v5MatchingService: null,
                    containmentValidator: containment,
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                    authorService: authorService,
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: bookService,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

                var file = new DiscoveredFileWithMetadata
                {
                    Path = "/audiobooks/Impact Winter Season 3/Impact Winter Season 3.m4b",
                    AllTags = new Dictionary<string, List<string>>
                    {
                        { "MP4:----", new List<string> { "Audible Originals", "English", "B0D6GQWSBK", "Impact Winter", "3" } },
                        { "ALBUMARTIST", new List<string> { "Travis Beacham" } }
                    }
                };

                var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 44);

                Assert.That(match, Is.Not.Null);
                Assert.That(match.EditionId, Is.EqualTo(2));
                Assert.That(match.BookId, Is.EqualTo(2));
            }

            [Test]
                public void should_fail_closed_when_only_series_tags_exist_and_author_is_detected()
                {
                var logger = LogManager.GetCurrentClassLogger();
                var containment = new ContainmentValidator(new TagNormalizer(), logger);

                // FTS order: generic series/boxed-set-ish title first (wrong), real book title second (correct).
                var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 1,
                        BookId = 49,
                        EditionTitle = "Harry Potter",
                        BookTitle = "Complete Harry Potter Boxed Set",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 2,
                        BookId = 999,
                        EditionTitle = "Harry Potter and the Order of the Phoenix",
                        BookTitle = "Harry Potter and the Order of the Phoenix",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                    }
                });

                var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

                var svc = new FileMatchingService(
                    matchingLogger: null,
                    v5MatchingService: null,
                    containmentValidator: containment,
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: null,
                    rootFolderService: null,
                    configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                    authorService: authorService,
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: fts,
                    bookService: null,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

                var file = new DiscoveredFileWithMetadata
                {
                    // Common library layout: /Author/Series/Book Title/file
                    Path = "/audiobooks/J.K. Rowling/Harry Potter/5 - Harry Potter and the Order of the Phoenix/Harry Potter and the Order of the Phoenix (2003).m4b",
                    AllTags = new Dictionary<string, List<string>>
                    {
                        // A common failure mode: tags contain series/collection title but not the actual book title.
                        { "SERIES", new List<string> { "Harry Potter" } },
                        { "ARTIST", new List<string> { "J.K. Rowling" } },
                    }
                };

                var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                Assert.That(match, Is.Null);
                }

                [TestCase(false, false)]
                [TestCase(true, true)]
                public void should_only_block_generic_series_title_when_no_clean_title_field_remains(
                    bool includeCleanTitleField,
                    bool expectedMatch)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        // Wrong candidate first: series-level/boxed-set-ish title
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 49,
                            EditionTitle = "Harry Potter",
                            BookTitle = "Complete Harry Potter Boxed Set",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        // Correct candidate second: specific book in the series with position 5
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 999,
                            EditionTitle = "Harry Potter and the Order of the Phoenix",
                            BookTitle = "Harry Potter and the Order of the Phoenix",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 49, SeriesName = "Harry Potter", SeriesPosition = null },
                        new Book { Id = 999, SeriesName = "Harry Potter", SeriesPosition = "5" }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);

	                    var tags = new Dictionary<string, List<string>>
	                    {
	                        // MAM-style multi-value field: [publisher, language, asin, series, position]
	                        { "MP4:----", new List<string> { "Pottermore Publishing", "English", "B079N96LN3", "Harry Potter", "5" } },
	                        { "ALBUMARTIST", new List<string> { "J.K. Rowling" } }
	                    };

	                    if (includeCleanTitleField)
	                    {
	                        tags["TITLE"] = new List<string> { "Harry Potter" };
	                    }

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        // Without TITLE, the only title-shaped field is the series/position field.
	                        Path = "/audiobooks/J.K. Rowling/Harry Potter/5/01.m4b",
	                        AllTags = tags
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	                    if (expectedMatch)
	                    {
	                        Assert.That(match, Is.Not.Null);
	                        Assert.That(match.EditionId, Is.EqualTo(1));
	                    }
	                    else
	                    {
	                        Assert.That(match, Is.Null);
	                    }
		                }

	                [Test]
	                public void should_match_when_file_title_contains_subtitle_and_series_tokens()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 3001,
	                            BookId = 3001,
	                            EditionTitle = "Uru's Third Temple",
	                            BookTitle = "Uru's Third Temple",
	                            EditionSubTitle = "A Fantasy LitRPG Adventure: Divine Apostasy, Book 3",
	                            AuthorId = 101,
	                            AuthorName = "A. F. Kay",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 101, Name = "A. F. Kay" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/A. F. Kay/Uru's Third Temple/Uru's Third Temple (1).mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Uru's Third Temple: A Fantasy LitRPG Adventure: Divine Apostasy, Book 3" } },
	                            { "ARTIST", new List<string> { "A. F. Kay" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 101);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(3001));
	                    Assert.That(match.BookId, Is.EqualTo(3001));
	                }

	                [Test]
	                public void should_ignore_trailing_noise_after_subtitle_metadata_wall()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 3001,
	                            BookId = 3001,
	                            EditionTitle = "Uru's Third Temple",
	                            BookTitle = "Uru's Third Temple",
	                            EditionSubTitle = "A Fantasy LitRPG Adventure: Divine Apostasy, Book 3",
	                            AuthorId = 101,
	                            AuthorName = "A. F. Kay",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 101, Name = "A. F. Kay" });
	                    var bookService = new StubBookService(new[]
	                    {
	                        new Book
	                        {
	                            Id = 3001,
	                            SeriesName = "Divine Apostasy",
	                            SeriesPosition = "3"
	                        }
	                    });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: bookService,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/A. F. Kay/Uru's Third Temple/Uru's Third Temple.mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Uru's Third Temple: A Fantasy LitRPG Adventure: Divine Apostasy, Book 3 Complete random trailing garbage" } },
	                            { "ARTIST", new List<string> { "A. F. Kay" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 101);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(3001));
	                    Assert.That(match.BookId, Is.EqualTo(3001));
	                }

	                [Test]
	                public void should_ignore_trailing_noise_after_subtitle_metadata_wall_in_strict_mode()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 3001,
	                            BookId = 3001,
	                            EditionTitle = "Uru's Third Temple",
	                            BookTitle = "Uru's Third Temple",
	                            EditionSubTitle = "A Fantasy LitRPG Adventure: Divine Apostasy, Book 3",
	                            AuthorId = 101,
	                            AuthorName = "A. F. Kay",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 101, Name = "A. F. Kay" });
	                    var bookService = new StubBookService(new[]
	                    {
	                        new Book
	                        {
	                            Id = 3001,
	                            SeriesName = "Divine Apostasy",
	                            SeriesPosition = "3"
	                        }
	                    });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: bookService,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/A. F. Kay/Uru's Third Temple/Uru's Third Temple.mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Uru's Third Temple: A Fantasy LitRPG Adventure: Divine Apostasy, Book 3 Complete random trailing garbage" } },
	                            { "ARTIST", new List<string> { "A. F. Kay" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 101);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(3001));
	                    Assert.That(match.BookId, Is.EqualTo(3001));
	                }

	                [Test]
	                public void should_prefer_album_when_title_is_series_only()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 4001,
	                            BookId = 4001,
	                            EditionTitle = "Sharp Ends",
	                            BookTitle = "Sharp Ends",
	                            AuthorId = 202,
	                            AuthorName = "Joe Abercrombie",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 202, Name = "Joe Abercrombie" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/Joe Abercrombie/Sharp Ends/Sharp Ends.m4b",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "First Law World, Book 4" } },
	                            { "ALBUM", new List<string> { "Sharp Ends" } },
	                            { "ARTIST", new List<string> { "Joe Abercrombie" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 202);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(4001));
	                    Assert.That(match.BookId, Is.EqualTo(4001));
	                }

	                [Test]
		                public void should_prefer_album_when_title_is_short_alphanumeric_noise()
		                {
		                    var logger = LogManager.GetCurrentClassLogger();
		                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 5001,
	                            BookId = 5001,
	                            EditionTitle = "First Grave on the Right",
	                            BookTitle = "First Grave on the Right",
	                            AuthorId = 303,
	                            AuthorName = "Darynda Jones",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 303, Name = "Darynda Jones" });

		                    var bookService = new StubBookService(new[]
		                    {
		                        new Book { Id = 5001, SeriesPosition = "1" }
		                    });

		                    var svc = new FileMatchingService(
		                        matchingLogger: null,
		                        v5MatchingService: null,
		                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                    configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
		                        authorLibraryService: null,
		                        editionFtsRepository: fts,
		                        bookService: bookService,
		                        editionService: null,
		                        editionRepository: null,
		                        mediaInfoExtractor: null,
		                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/Darynda Jones/First Grave on the Right/First Grave on the Right.m4a",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "8l" } },
	                            { "ALBUM", new List<string> { "First Grave on the Right, #1" } },
	                            { "ARTIST", new List<string> { "Darynda Jones" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 303);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(5001));
	                    Assert.That(match.BookId, Is.EqualTo(5001));
	                }

	                [Test]
	                public void should_match_single_clean_title_when_book_title_equals_series_name()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 6001,
	                            BookId = 6001,
	                            EditionTitle = "Warriors",
	                            BookTitle = "Warriors",
	                            AuthorId = 404,
	                            AuthorName = "George R.R. Martin",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 404, Name = "George R.R. Martin" });
	                    var bookService = new StubBookService(new[]
	                    {
	                        new Book
	                        {
	                            Id = 6001,
	                            AuthorId = 404,
	                            Title = "Warriors",
	                            SeriesName = "Warriors",
	                            SeriesPosition = "1"
	                        }
	                    });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: bookService,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/George R.R. Martin/Warriors/Warriors (8).mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Warriors" } },
	                            { "ALBUM", new List<string> { "Edited by George R Martin" } },
	                            { "ARTIST", new List<string> { "George R.R. Martin" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 404);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(6001));
	                    Assert.That(match.BookId, Is.EqualTo(6001));
	                }

	                [Test]
	                public void should_not_match_when_tags_are_empty()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 9001,
	                            BookId = 9001,
	                            EditionTitle = "Dreamsongs: Volume II",
	                            BookTitle = "Dreamsongs: Volume II",
	                            AuthorId = 24,
	                            AuthorName = "George R.R. Martin",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 24, Name = "George R.R. Martin" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/George R.R. Martin/Dreamsongs - Volume II/Dreamsongs - Volume II (9).mp3",
	                        AllTags = new Dictionary<string, List<string>>()
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 24);

	                    Assert.That(match, Is.Null);
	                }

	                [Test]
	                public void should_match_when_title_is_short_but_album_has_full_title()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 7001,
	                            BookId = 7001,
	                            EditionTitle = "Inner Excellence: Train Your Mind for Extraordinary Performance and the Best Possible Life",
	                            BookTitle = "Inner Excellence: Train Your Mind for Extraordinary Performance and the Best Possible Life",
	                            AuthorId = 505,
	                            AuthorName = "Jim Murphy",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 505, Name = "Jim Murphy" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/Jim Murphy/Inner Excellence/Inner Excellence.m4b",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Inner Excellence" } },
	                            { "ALBUM", new List<string> { "Inner Excellence: Train Your Mind for Extraordinary Performance and the Best Possible Life" } },
	                            { "ALBUMARTIST", new List<string> { "Jim Murphy" } },
	                            { "ARTIST", new List<string> { "Jim Murphy" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 505);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(7001));
	                    Assert.That(match.BookId, Is.EqualTo(7001));
	                }

                [Test]
                public void should_prefer_specific_dreamsongs_volume_over_generic_dreamsongs()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        // Generic candidate first (wrong)
                        new EditionFtsMatch
                        {
                            EditionId = 3957,
                            BookId = 1048,
                            EditionTitle = "Dreamsongs",
                            BookTitle = "Selections from Dreamsongs Section One",
                            AuthorId = 25,
                            AuthorName = "George R.R. Martin",
                        },
                        // Specific candidate second (correct)
                        new EditionFtsMatch
                        {
                            EditionId = 3867,
                            BookId = 1002,
                            EditionTitle = "Dreamsongs: Volume II",
                            EditionSubTitle = "Volume II",
                            BookTitle = "Dreamsongs: Volume II",
                            AuthorId = 25,
                            AuthorName = "George R.R. Martin",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "George R.R. Martin" });

	            var svc = new FileMatchingService(
	                matchingLogger: null,
	                v5MatchingService: null,
	                containmentValidator: containment,
	                pendingAuthorImportService: null,
	                commandQueue: null,
	                authorFolderMatchingService: null,
	                rootFolderService: null,
	                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                authorService: authorService,
	                eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/George R. R. Martin/Dreamsongs - Volume II (9).mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Dreamsongs - Volume II (9)" } },
                            { "ALBUM", new List<string> { "Dreamsongs - Volume II" } },
                            { "ARTIST", new List<string> { "George R. R. Martin" } },
                            { "TRACKNUMBER", new List<string> { "9" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(3867));
                    Assert.That(match.BookId, Is.EqualTo(1002));
                }

                [Test]
                public void should_match_base_book_title_when_monitored_edition_adds_subtitle()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 21106,
                            BookId = 7315,
                            EditionTitle = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson",
                            EditionSubTitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                            BookTitle = "Tuesdays with Morrie",
                            AuthorId = 103,
                            AuthorName = "Mitch Albom",
                            MatchScore = 19.43,
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 103, Name = "Mitch Albom" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book
                        {
                            Id = 7315,
                            Title = "Tuesdays with Morrie",
                            AuthorId = 103,
                            Author = new Author { Id = 103, Name = "Mitch Albom" },
                            MediaType = BookMediaType.Audiobook,
                        }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/downloads/incomplete/Mitch Albom - Tuesdays with Morrie/Mitch Albom - Tuesdays with Morrie - 1 of 3.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "1 of 3" } },
                            { "ALBUM", new List<string> { "Tuesdays with Morrie" } },
                            { "ALBUMARTIST", new List<string> { "Mitch Albom" } },
                            { "ARTIST", new List<string> { "Mitch Albom" } },
                            { "TRACKNUMBER", new List<string> { "1" } },
                            { "DATE", new List<string> { "2000" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 103);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(21106));
                    Assert.That(match.BookId, Is.EqualTo(7315));
                }

                [TestCase(BookMatchingStrictness.Balanced)]
                [TestCase(BookMatchingStrictness.Strict)]
                public void should_rescue_short_title_when_explicit_subtitle_is_the_only_missing_candidate_text(
                    BookMatchingStrictness strictness)
                {
                    const string fullTitle = "Inner Excellence: Train Your Mind for Extraordinary Performance and the Best Possible Life";
                    const string subtitle = "Train Your Mind for Extraordinary Performance and the Best Possible Life";
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 54761,
                        BookId = 21425,
                        EditionTitle = fullTitle,
                        EditionSubTitle = subtitle,
                        BookTitle = fullTitle,
                        ForeignEditionId = "gr:228418316-audiobook",
                        AuthorId = 505,
                        AuthorName = "Jim Murphy",
                        NarratorNames = "Jim Murphy",
                        DurationSeconds = 36660,
                        ReadingFormatId = 2
                    };
                    var book = new Book
                    {
                        Id = 21425,
                        AuthorId = 505,
                        Title = fullTitle,
                        BaseBookId = "hc:1036375",
                        MediaType = BookMediaType.Audiobook
                    };

                    var match = MatchSubtitleRescueScenario(
                        new[] { candidate },
                        new[] { book },
                        new Dictionary<string, List<string>>
                        {
                            { "ALBUM", new List<string> { "Inner Excellence - Julian Mehne" } },
                            { "TITLE", new List<string> { "Inner Excellence" } },
                            { "ALBUMARTIST", new List<string> { "Jim Murphy" } },
                            { "ARTIST", new List<string> { "Jim Murphy" } }
                        },
                        strictness,
                        durationSeconds: 37807);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match?.BookId, Is.EqualTo(21425));
                        Assert.That(match?.EditionId, Is.EqualTo(54761));
                        Assert.That(candidate.EditionTitle, Is.EqualTo(fullTitle));
                        Assert.That(book.Title, Is.EqualTo(fullTitle));
                    });
                }

                [Test]
                public void should_rescue_non_english_short_title_from_exact_metadata_subtitle()
                {
                    const string fullTitle = "Excelencia interior: Entrena tu mente para un rendimiento extraordinario";
                    const string subtitle = "Entrena tu mente para un rendimiento extraordinario";
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 701,
                        BookId = 701,
                        EditionTitle = fullTitle,
                        EditionSubTitle = subtitle,
                        BookTitle = fullTitle,
                        AuthorId = 506,
                        AuthorName = "Jim Murphy",
                        ReadingFormatId = 2
                    };

                    var match = MatchSubtitleRescueScenario(
                        new[] { candidate },
                        new[]
                        {
                            new Book
                            {
                                Id = 701,
                                AuthorId = 506,
                                Title = fullTitle,
                                BaseBookId = "hc:excelencia",
                                MediaType = BookMediaType.Audiobook
                            }
                        },
                        new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Excelencia interior" } },
                            { "ARTIST", new List<string> { "Jim Murphy" } }
                        });

                    Assert.That(match?.BookId, Is.EqualTo(701));
                }

                [TestCase(
                    "Sherlock Holmes: A Study in Scarlet",
                    "A Study in Scarlet",
                    "Sherlock Holmes",
                    "Sherlock Holmes")]
                [TestCase(
                    "Star Wars: Path of the Lightsaber",
                    "Path of the Lightsaber",
                    "Star Wars",
                    "Star Wars Legends")]
                public void should_not_rescue_a_base_title_that_is_a_series_prefix(
                    string fullTitle,
                    string subtitle,
                    string baseTitle,
                    string seriesName)
                {
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 801,
                        BookId = 801,
                        EditionTitle = fullTitle,
                        EditionSubTitle = subtitle,
                        BookTitle = fullTitle,
                        AuthorId = 507,
                        AuthorName = "Test Author",
                        ReadingFormatId = 2
                    };

                    var match = MatchSubtitleRescueScenario(
                        new[] { candidate },
                        new[]
                        {
                            new Book
                            {
                                Id = 801,
                                AuthorId = 507,
                                Title = fullTitle,
                                BaseBookId = "hc:series-prefix",
                                SeriesName = seriesName,
                                MediaType = BookMediaType.Audiobook
                            }
                        },
                        new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { baseTitle } },
                            { "ARTIST", new List<string> { "Test Author" } }
                        });

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_not_rescue_when_explicit_subtitle_carries_position_identity()
                {
                    const string fullTitle = "Dreamsongs: Volume II";
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 901,
                        BookId = 901,
                        EditionTitle = fullTitle,
                        EditionSubTitle = "Volume II",
                        BookTitle = fullTitle,
                        AuthorId = 508,
                        AuthorName = "George R. R. Martin",
                        ReadingFormatId = 2
                    };

                    var match = MatchSubtitleRescueScenario(
                        new[] { candidate },
                        new[]
                        {
                            new Book
                            {
                                Id = 901,
                                AuthorId = 508,
                                Title = fullTitle,
                                BaseBookId = "hc:dreamsongs-volume-two",
                                MediaType = BookMediaType.Audiobook
                            }
                        },
                        new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Dreamsongs" } },
                            { "ARTIST", new List<string> { "George R. R. Martin" } }
                        });

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_not_rescue_when_two_distinct_works_explain_the_same_base_title()
                {
                    var candidates = new[]
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1001,
                            BookId = 1001,
                            EditionTitle = "Legacy: A Novel",
                            EditionSubTitle = "A Novel",
                            BookTitle = "Legacy: A Novel",
                            AuthorId = 509,
                            AuthorName = "Test Author",
                            ReadingFormatId = 2
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 1002,
                            BookId = 1002,
                            EditionTitle = "Legacy: A Memoir",
                            EditionSubTitle = "A Memoir",
                            BookTitle = "Legacy: A Memoir",
                            AuthorId = 509,
                            AuthorName = "Test Author",
                            ReadingFormatId = 2
                        }
                    };

                    var match = MatchSubtitleRescueScenario(
                        candidates,
                        new[]
                        {
                            new Book { Id = 1001, AuthorId = 509, Title = candidates[0].BookTitle, BaseBookId = "hc:legacy-novel", MediaType = BookMediaType.Audiobook },
                            new Book { Id = 1002, AuthorId = 509, Title = candidates[1].BookTitle, BaseBookId = "hc:legacy-memoir", MediaType = BookMediaType.Audiobook }
                        },
                        new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Legacy" } },
                            { "ARTIST", new List<string> { "Test Author" } }
                        });

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_not_rescue_when_candidate_text_beyond_the_attested_subtitle_is_missing()
                {
                    const string fullTitle = "Target: Known Subtitle - Deluxe Edition";
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 1101,
                        BookId = 1101,
                        EditionTitle = fullTitle,
                        EditionSubTitle = "Known Subtitle",
                        BookTitle = fullTitle,
                        AuthorId = 510,
                        AuthorName = "Test Author",
                        ReadingFormatId = 2
                    };

                    var match = MatchSubtitleRescueScenario(
                        new[] { candidate },
                        new[]
                        {
                            new Book
                            {
                                Id = 1101,
                                AuthorId = 510,
                                Title = fullTitle,
                                BaseBookId = "hc:extra-missing-text",
                                MediaType = BookMediaType.Audiobook
                            }
                        },
                        new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Target" } },
                            { "ARTIST", new List<string> { "Test Author" } }
                        });

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_not_use_missing_subtitle_fallback_when_base_title_points_to_another_book()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "Aliens: Resurrection",
                            EditionSubTitle = "Resurrection",
                            BookTitle = "Aliens",
                            AuthorId = 42,
                            AuthorName = "Alan Dean Foster",
                            MatchScore = 20,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "Aliens",
                            BookTitle = "Aliens",
                            AuthorId = 42,
                            AuthorName = "Alan Dean Foster",
                            MatchScore = 18,
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 42, Name = "Alan Dean Foster" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 100, Title = "Aliens: Resurrection", AuthorId = 42, MediaType = BookMediaType.Audiobook },
                        new Book { Id = 101, Title = "Aliens", AuthorId = 42, MediaType = BookMediaType.Audiobook },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Alan Dean Foster/Aliens/Aliens.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Aliens" } },
                            { "ALBUM", new List<string> { "Aliens" } },
                            { "ARTIST", new List<string> { "Alan Dean Foster" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 42);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(101));
                }

                [Test]
                public void should_not_let_subtitle_presence_override_closer_duration_for_sibling_audiobook_editions()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        // The generic sibling has a perfect duration but explains only the base title.
                        new EditionFtsMatch
                        {
                            EditionId = 517,
                            BookId = 517,
                            EditionTitle = "Never Come Back",
                            BookTitle = "Never Come Back",
                            AuthorId = 6,
                            AuthorName = "Joe Hart",
                            NarratorNames = "Test Narrator",
                            DurationSeconds = 3600,
                            ReadingFormatId = 2,
                        },
                        // The specific sibling explains the complete title + subtitle in one field.
                        new EditionFtsMatch
                        {
                            EditionId = 518,
                            BookId = 517,
                            EditionTitle = "Never Come Back",
                            EditionSubTitle = "A Thriller",
                            BookTitle = "Never Come Back",
                            AuthorId = 6,
                            AuthorName = "Joe Hart",
                            NarratorNames = "Test Narrator",
                            DurationSeconds = 3660,
                            ReadingFormatId = 2,
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 6, Name = "Joe Hart" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Joe Hart/Never Come Back/Never Come Back.m4b",
                        DurationSeconds = 3600,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Never Come Back: A Thriller" } },
                            { "ALBUM", new List<string> { "Never Come Back: A Thriller" } },
                            { "ARTIST", new List<string> { "Joe Hart" } },
                            { "COMPOSER", new List<string> { "Test Narrator" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 6);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(517));
	                }

	                [Test]
	                public void should_match_short_title_when_album_is_clean_and_title_has_non_discriminative_track_range_leftover()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 315,
	                            BookId = 1001,
	                            EditionTitle = "Dark King",
	                            BookTitle = "Dark King",
	                            AuthorId = 900,
	                            AuthorName = "C.N. Crawford",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 900, Name = "C.N. Crawford" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/C.N. Crawford/Dark King/Dark King.mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Dark King 6-7" } },
	                            { "ALBUM", new List<string> { "Dark King" } },
	                            { "ARTIST", new List<string> { "C.N. Crawford" } },
	                            { "TRACKNUMBER", new List<string> { "6" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 900);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(315));
	                    Assert.That(match.BookId, Is.EqualTo(1001));
	                }

	                [Test]
	                public void should_prefer_specific_title_when_dirty_field_points_past_clean_generic_field()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        // Generic candidate with a "clean" album field, but conflicting title evidence.
	                        new EditionFtsMatch
	                        {
	                            EditionId = 100,
	                            BookId = 100,
	                            EditionTitle = "It",
	                            BookTitle = "It",
	                            AuthorId = 77,
	                            AuthorName = "Colleen Hoover",
	                        },
	                        // More specific candidate should beat the generic "It" evidence from ALBUM.
	                        new EditionFtsMatch
	                        {
	                            EditionId = 101,
	                            BookId = 101,
	                            EditionTitle = "It Ends With Us",
	                            BookTitle = "It Ends With Us",
	                            AuthorId = 77,
	                            AuthorName = "Colleen Hoover",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 77, Name = "Colleen Hoover" });
	                    var bookService = new StubBookService(new[]
	                    {
	                        new Book { Id = 100, AuthorId = 77, Title = "It", BaseBookId = "hc:it" },
	                        new Book { Id = 101, AuthorId = 77, Title = "It Ends With Us", BaseBookId = "hc:it-ends-with-us" },
	                    });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: bookService,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/Colleen Hoover/It/It.mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "It Ends With Us" } },
	                            { "ALBUM", new List<string> { "It" } },
	                            { "ARTIST", new List<string> { "Colleen Hoover" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 77);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(101));
	                    Assert.That(match.BookId, Is.EqualTo(101));
	                }

	                [Test]
	                public void should_accept_clean_title_evidence_when_dirty_identifier_field_has_leftover_tokens()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 18415,
	                            BookId = 6042,
	                            EditionTitle = "The Village",
	                            BookTitle = "Деревня",
	                            AuthorId = 94,
	                            AuthorName = "Ivan Bunin",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 94, Name = "Ivan Bunin" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/ebooks/Ivan Bunin/The Village/ivan-bunin_the-village_isabel-f-hapgood.epub",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "title", new List<string> { "The Village" } },
	                            { "author", new List<string> { "Ivan Bunin" } },
	                            { "identifier_unknown", new List<string> { "https://standardebooks.org/ebooks/ivan-bunin/the-village/isabel-f-hapgood" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 94);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(18415));
	                    Assert.That(match.BookId, Is.EqualTo(6042));
	                }

	                [Test]
	                public void should_accept_dirty_only_title_evidence_when_no_local_candidate_explains_leftovers()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 18415,
	                            BookId = 6042,
	                            EditionTitle = "The Village",
	                            BookTitle = "Деревня",
	                            AuthorId = 94,
	                            AuthorName = "Ivan Bunin",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 94, Name = "Ivan Bunin" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/ebooks/Ivan Bunin/The Village/ivan-bunin_the-village_isabel-f-hapgood.epub",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "author", new List<string> { "Ivan Bunin" } },
	                            { "identifier_unknown", new List<string> { "https://standardebooks.org/ebooks/ivan-bunin/the-village/isabel-f-hapgood" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 94);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(18415));
	                    Assert.That(match.BookId, Is.EqualTo(6042));
	                }

	                [Test]
	                public void should_not_reject_short_title_when_other_candidate_only_shares_leftover_token()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "It",
                            BookTitle = "It",
                            AuthorId = 77,
                            AuthorName = "Test Author",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "A Game of Thrones",
                            BookTitle = "A Game of Thrones",
                            AuthorId = 77,
                            AuthorName = "Test Author",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 77, Name = "Test Author" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Aggressive, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Test Author/It/It.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "It Game" } },
                            { "ALBUM", new List<string> { "It" } },
                            { "ARTIST", new List<string> { "Test Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 77);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(100));
                    Assert.That(match.BookId, Is.EqualTo(100));
	                }

	                [Test]
	                public void should_reject_short_title_when_other_candidate_title_is_same_field_superset()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "Dune",
                            BookTitle = "Dune",
                            AuthorId = 11,
                            AuthorName = "Frank Herbert",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "Dune Messiah",
                            BookTitle = "Dune Messiah",
                            AuthorId = 11,
                            AuthorName = "Frank Herbert",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 11, Name = "Frank Herbert" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 100, AuthorId = 11, Title = "Dune", BaseBookId = "hc:dune" },
                        new Book { Id = 101, AuthorId = 11, Title = "Dune Messiah", BaseBookId = "hc:dune-messiah" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Aggressive, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Frank Herbert/Dune/Dune Messiah.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Dune Messiah" } },
                            { "ALBUM", new List<string> { "Dune" } },
                            { "ARTIST", new List<string> { "Frank Herbert" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 11);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(101));
                    Assert.That(match.BookId, Is.EqualTo(101));
	                }

	                [Test]
		                public void should_prefer_specific_embedded_title_over_generic_path_fallback()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "It",
                            BookTitle = "It",
                            AuthorId = 77,
                            AuthorName = "Colleen Hoover",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "It Ends With Us",
                            BookTitle = "It Ends With Us",
                            AuthorId = 77,
                            AuthorName = "Colleen Hoover",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 77, Name = "Colleen Hoover" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Colleen Hoover/It/It.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "It Ends With Us" } },
                            { "ARTIST", new List<string> { "Colleen Hoover" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 77);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(101));
	                    Assert.That(match.BookId, Is.EqualTo(101));
		                }

	                [Test]
	                public void should_allow_book_token_when_series_position_metadata_exists()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 9001,
                            BookId = 9001,
                            EditionTitle = "Perelandra",
                            BookTitle = "Perelandra",
                            AuthorId = 701,
                            AuthorName = "C. S. Lewis",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 701, Name = "C. S. Lewis" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book
                        {
                            Id = 9001,
                            SeriesName = "Space Trilogy",
                            SeriesPosition = "2"
                        }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/C. S. Lewis/Perelandra/Perelandra.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Perelandra: Space Trilogy, Book 2" } },
                            { "ALBUM", new List<string> { "Perelandra: Space Trilogy, Book 2" } },
                            { "ARTIST", new List<string> { "C. S. Lewis" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 701);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(9001));
                }

                [TestCase(BookMatchingStrictness.Strict, false)]
                [TestCase(BookMatchingStrictness.Balanced, true)]
                [TestCase(BookMatchingStrictness.Aggressive, true)]
                public void should_tolerate_recognized_series_decoration_without_catalog_position_outside_strict(
                    BookMatchingStrictness strictness,
                    bool expectedMatch)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 9001,
                            BookId = 9001,
                            EditionTitle = "Perelandra",
                            BookTitle = "Perelandra",
                            AuthorId = 701,
                            AuthorName = "C. S. Lewis",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 701, Name = "C. S. Lewis" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book
                        {
                            Id = 9001,
                            SeriesName = "Space Trilogy",
                            SeriesPosition = null
                        }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/C. S. Lewis/Perelandra/Perelandra.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Perelandra: Space Trilogy, Book 2" } },
                            { "ALBUM", new List<string> { "Perelandra: Space Trilogy, Book 2" } },
                            { "ARTIST", new List<string> { "C. S. Lewis" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 701);

                    if (expectedMatch)
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.EditionId, Is.EqualTo(9001));
                        Assert.That(match.BookId, Is.EqualTo(9001));
                    }
                    else
                    {
                        Assert.That(match, Is.Null);
                    }
                }

                [TestCase(BookMatchingStrictness.Strict, false)]
                [TestCase(BookMatchingStrictness.Balanced, true)]
                [TestCase(BookMatchingStrictness.Aggressive, true)]
                public void should_tolerate_foreign_series_parenthetical_outside_strict_without_a_better_book_explanation(BookMatchingStrictness strictness, bool expectedMatch)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 9120,
                            BookId = 3319,
                            EditionTitle = "The Mistwalker",
                            BookTitle = "The Mistwalker",
                            AuthorId = 65,
                            AuthorName = "Regine Abel",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 9200,
                            BookId = 3352,
                            EditionTitle = "The Hunchback",
                            BookTitle = "The Hunchback",
                            AuthorId = 65,
                            AuthorName = "Regine Abel",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 65, Name = "Regine Abel" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 3319, SeriesName = "The Mist", SeriesPosition = "1" },
                        new Book { Id = 3352, SeriesName = "Dark Tales", SeriesPosition = "2" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Regine Abel/The Mistwalker/Regine Abel - The Mistwalker.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "The Mistwalker (Dark Tales Book 2)" } },
                            { "AUTHOR", new List<string> { "Regine Abel" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 65);

                    if (expectedMatch)
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.EditionId, Is.EqualTo(9120));
                        Assert.That(match.BookId, Is.EqualTo(3319));
                    }
                    else
                    {
                        Assert.That(match, Is.Null);
                    }
                }

                [TestCase(BookMatchingStrictness.Strict)]
                [TestCase(BookMatchingStrictness.Balanced)]
                [TestCase(BookMatchingStrictness.Aggressive)]
                public void should_explain_own_series_spelled_position_parenthetical(BookMatchingStrictness strictness)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 8816,
                            BookId = 3165,
                            EditionTitle = "A Little Hatred",
                            BookTitle = "A Little Hatred",
                            AuthorId = 501,
                            AuthorName = "Joe Abercrombie",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 501, Name = "Joe Abercrombie" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 3165, SeriesName = "The Age of Madness", SeriesPosition = "1" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Joe Abercrombie/A Little Hatred/A Little Hatred by Joe Abercrombie.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "A Little Hatred: Book One (The Age of Madness)" } },
                            { "AUTHOR", new List<string> { "Joe Abercrombie" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 501);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(8816));
                    Assert.That(match.BookId, Is.EqualTo(3165));
                }

                [TestCase(BookMatchingStrictness.Strict, false)]
                [TestCase(BookMatchingStrictness.Balanced, true)]
                [TestCase(BookMatchingStrictness.Aggressive, true)]
                public void should_allow_exact_title_to_survive_same_series_position_drift_outside_strict(BookMatchingStrictness strictness, bool expectedMatch)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1001,
                            BookId = 1001,
                            EditionTitle = "Book Title",
                            BookTitle = "Book Title",
                            AuthorId = 502,
                            AuthorName = "Author Name",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 1002,
                            BookId = 1002,
                            EditionTitle = "Sibling Title",
                            BookTitle = "Sibling Title",
                            AuthorId = 502,
                            AuthorName = "Author Name",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 502, Name = "Author Name" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1001, SeriesName = "Series Name", SeriesPosition = "1" },
                        new Book { Id = 1002, SeriesName = "Series Name", SeriesPosition = "2" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Author Name/Book Title/Book Title.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Book Title (Series Name Book 2)" } },
                            { "AUTHOR", new List<string> { "Author Name" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 502);

                    if (expectedMatch)
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.EditionId, Is.EqualTo(1001));
                        Assert.That(match.BookId, Is.EqualTo(1001));
                    }
                    else
                    {
                        Assert.That(match, Is.Null);
                    }
                }

                [Test]
                public void strict_position_rejection_should_log_candidate_reason_and_truthful_stage_counts()
                {
                    var priorConfiguration = LogManager.Configuration;
                    var memoryTarget = new MemoryTarget("m03-memory")
                    {
                        Layout = "${message}"
                    };
                    var loggingConfiguration = new LoggingConfiguration();
                    loggingConfiguration.AddRule(LogLevel.Debug, LogLevel.Fatal, memoryTarget, "m03-observability");
                    LogManager.Configuration = loggingConfiguration;
                    LogManager.ReconfigExistingLoggers();

                    try
                    {
                        var logger = LogManager.GetLogger("m03-observability");
                        var containment = new ContainmentValidator(new TagNormalizer(), logger);
                        var candidate = new EditionFtsMatch
                        {
                            EditionId = 1001,
                            BookId = 1001,
                            EditionTitle = "Book Title",
                            BookTitle = "Book Title",
                            AuthorId = 502,
                            AuthorName = "Author Name",
                        };
                        var svc = new FileMatchingService(
                            matchingLogger: null,
                            v5MatchingService: null,
                            containmentValidator: containment,
                            pendingAuthorImportService: null,
                            commandQueue: null,
                            authorFolderMatchingService: null,
                            rootFolderService: null,
                            configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
                            authorService: new StubAuthorService(new Author { Id = 502, Name = "Author Name" }),
                            eventAggregator: null,
                            authorLibraryService: null,
                            editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch> { candidate }),
                            bookService: new StubBookService(new[]
                            {
                                new Book { Id = 1001, AuthorId = 502, Title = "Book Title", SeriesName = "Series Name", SeriesPosition = "1" },
                            }),
                            editionService: null,
                            editionRepository: null,
                            mediaInfoExtractor: null,
                            logger: logger);
                        var file = new DiscoveredFileWithMetadata
                        {
                            Path = "/ebooks/Author Name/Book Title/Book Title.epub",
                            AllTags = new Dictionary<string, List<string>>
                            {
                                { "TITLE", new List<string> { "Book Title (Series Name Book 2)" } },
                                { "AUTHOR", new List<string> { "Author Name" } },
                            }
                        };

                        var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 502);

                        Assert.That(match, Is.Null);
                        Assert.That(memoryTarget.Logs.Any(log =>
                            log.Contains("SERIES_POSITION_MISMATCH rejecting candidate", StringComparison.Ordinal) &&
                            log.Contains("fields=[TITLE]", StringComparison.Ordinal)), Is.True);
                        Assert.That(memoryTarget.Logs.Any(log =>
                            log.Contains("Title-evidenced=1", StringComparison.Ordinal) &&
                            log.Contains("strict series-position field rejections=1", StringComparison.Ordinal) &&
                            log.Contains("leftover rejections=0", StringComparison.Ordinal)), Is.True);
                    }
                    finally
                    {
                        LogManager.Configuration = priorConfiguration;
                        LogManager.ReconfigExistingLoggers();
                    }
                }

                [TestCase(BookMatchingStrictness.Strict)]
                [TestCase(BookMatchingStrictness.Balanced)]
                [TestCase(BookMatchingStrictness.Aggressive)]
                public void should_not_let_cross_field_series_position_junk_veto_changes(
                    BookMatchingStrictness strictness)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1700,
                            BookId = 1700,
                            EditionTitle = "Battle Ground",
                            BookTitle = "Battle Ground",
                            AuthorId = 50,
                            AuthorName = "Jim Butcher",
                            ReleaseDate = new DateTime(2020, 9, 29),
                            MatchScore = 20,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 1200,
                            BookId = 1200,
                            EditionTitle = "Changes",
                            BookTitle = "Changes",
                            AuthorId = 50,
                            AuthorName = "Jim Butcher",
                            ReleaseDate = new DateTime(2010, 4, 8),
                            MatchScore = 10,
                        }
                    });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1700, AuthorId = 50, Title = "Battle Ground", SeriesName = "Dresden Files", SeriesPosition = "17" },
                        new Book { Id = 1200, AuthorId = 50, Title = "Changes", SeriesName = "Dresden Files", SeriesPosition = "12" },
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 50, Name = "Jim Butcher" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Jim Butcher/Changes/Jim Butcher - Changes.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Changes (2010)" } },
                            { "AUTHOR", new List<string> { "Butcher, Jim - Dresden Files 17" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 50);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(1200));
                    Assert.That(match.BookId, Is.EqualTo(1200));
                }

                [TestCase("TITLE", "ALBUM")]
                [TestCase("ALBUM", "TITLE")]
                public void strict_should_keep_clean_title_field_when_another_title_field_has_position_drift(
                    string cleanField,
                    string dirtyField)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1720,
                            BookId = 1720,
                            EditionTitle = "The Law",
                            BookTitle = "The Law",
                            AuthorId = 50,
                            AuthorName = "Jim Butcher",
                        }
                    });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1720, AuthorId = 50, Title = "The Law", SeriesName = "Dresden Files", SeriesPosition = "17.2" },
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 50, Name = "Jim Butcher" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Jim Butcher/The Law/The Law.epub",
                        AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { cleanField, new List<string> { "The Law" } },
                            { dirtyField, new List<string> { "The Law: A Dresden Files Novella (Dresden Files, Book 17.5)" } },
                            { "AUTHOR", new List<string> { "Jim Butcher" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 50);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(1720));
                }

                [TestCase(BookMatchingStrictness.Strict)]
                [TestCase(BookMatchingStrictness.Balanced)]
                [TestCase(BookMatchingStrictness.Aggressive)]
                public void matching_position_should_beat_no_signal_only_between_same_field_title_rivals(
                    BookMatchingStrictness strictness)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Shared Title",
                            BookTitle = "Shared Title",
                            AuthorId = 77,
                            AuthorName = "Example Author",
                            MatchScore = 20,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Shared Title",
                            BookTitle = "Shared Title",
                            AuthorId = 77,
                            AuthorName = "Example Author",
                            MatchScore = 10,
                        }
                    });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1, AuthorId = 77, Title = "Shared Title", SeriesName = "Neutral Saga", SeriesPosition = "1" },
                        new Book { Id = 2, AuthorId = 77, Title = "Shared Title", SeriesName = "Positive Saga", SeriesPosition = "2" },
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 77, Name = "Example Author" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Example Author/Shared Title/Shared Title.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Shared Title" } },
                            { "GROUPING", new List<string> { "Neutral Saga" } },
                            { "CUSTOM", new List<string> { "Positive Saga", "2" } },
                            { "AUTHOR", new List<string> { "Example Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 77);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(2));
                }

                [Test]
                public void position_should_not_tiebreak_candidates_proven_by_different_title_fields()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Alpha Title",
                            BookTitle = "Alpha Title",
                            AuthorId = 78,
                            AuthorName = "Example Author",
                            MatchScore = 20,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Beta Title",
                            BookTitle = "Beta Title",
                            AuthorId = 78,
                            AuthorName = "Example Author",
                            MatchScore = 10,
                        }
                    });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1, AuthorId = 78, Title = "Alpha Title", SeriesName = "Alpha Saga", SeriesPosition = "1" },
                        new Book { Id = 2, AuthorId = 78, Title = "Beta Title", SeriesName = "Beta Saga", SeriesPosition = "2" },
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 78, Name = "Example Author" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Example Author/Mixed Evidence/Mixed.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Alpha Title" } },
                            { "ALBUM", new List<string> { "Beta Title (Beta Saga Book 2)" } },
                            { "GROUPING", new List<string> { "Alpha Saga" } },
                            { "AUTHOR", new List<string> { "Example Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 78);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(1));
                }

                [TestCase("Alpha", "5", false)]
                [TestCase("Beta", "5", true)]
                public void strict_should_bind_each_series_name_to_its_own_position(
                    string taggedSeries,
                    string taggedPosition,
                    bool expectedMatch)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 5,
                            BookId = 5,
                            EditionTitle = "Target",
                            BookTitle = "Target",
                            AuthorId = 88,
                            AuthorName = "Example Author",
                        }
                    });
                    var book = new Book
                    {
                        Id = 5,
                        AuthorId = 88,
                        Title = "Target",
                        SeriesName = "Alpha",
                        SeriesPosition = "1",
                        SeriesLinks = new List<SeriesBookLink>
                        {
                            new SeriesBookLink
                            {
                                BookId = 5,
                                Series = new LazyLoaded<Series>(new Series { Title = "Alpha" }),
                                Position = "1",
                                SeriesPosition = 1
                            },
                            new SeriesBookLink
                            {
                                BookId = 5,
                                Series = new LazyLoaded<Series>(new Series { Title = "Beta" }),
                                Position = "5",
                                SeriesPosition = 5
                            }
                        }
                    };
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 88, Name = "Example Author" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(new[] { book }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Example Author/Target/Target.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { $"Target ({taggedSeries} Book {taggedPosition})" } },
                            { "AUTHOR", new List<string> { "Example Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 88);

                    if (expectedMatch)
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.EditionId, Is.EqualTo(5));
                    }
                    else
                    {
                        Assert.That(match, Is.Null);
                    }
                }

                [Test]
                public void observed_numeric_position_should_not_explain_an_unrelated_spelled_position_token()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Target",
                            BookTitle = "Target",
                            AuthorId = 99,
                            AuthorName = "Example Author",
                            MatchScore = 20,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Target Second",
                            BookTitle = "Target Second",
                            AuthorId = 99,
                            AuthorName = "Example Author",
                            MatchScore = 10,
                        }
                    });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1, AuthorId = 99, Title = "Target", BaseBookId = "hc:target", SeriesName = "Alpha", SeriesPosition = "2" },
                        new Book { Id = 2, AuthorId = 99, Title = "Target Second", BaseBookId = "hc:target-second" },
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 99, Name = "Example Author" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/Example Author/Target/Target.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Target second (Alpha Book 2)" } },
                            { "AUTHOR", new List<string> { "Example Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 99);

                    Assert.That(match?.EditionId, Is.Not.EqualTo(1));
                }

                [Test]
                public void strict_should_explain_roman_position_after_candidate_series_name()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 2733,
                        BookId = 1038,
                        EditionTitle = "Jokers Wild",
                        BookTitle = "Jokers Wild",
                        AuthorId = 38,
                        AuthorName = "George R.R. Martin",
                    };
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 38, Name = "George R.R. Martin" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch> { candidate }),
                        bookService: new StubBookService(new[]
                        {
                            new Book { Id = 1038, AuthorId = 38, Title = "Jokers Wild", SeriesName = "Wild Cards", SeriesPosition = "3" },
                        }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/George R. R. Martin/Jokers Wild/Jokers Wild.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Wild Cards III: Jokers Wild" } },
                            { "AUTHOR", new List<string> { "George R. R. Martin" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 38);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(2733));
                }

                [Test]
                public void should_allow_relaxed_containment_when_missing_token_does_not_change_book_choice()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Harry Potter and the Philosopher's Stone",
                            BookTitle = "Harry Potter and the Philosopher's Stone",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Harry Potter and the Chamber of Secrets",
                            BookTitle = "Harry Potter and the Chamber of Secrets",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1, SeriesName = "Harry Potter", SeriesPosition = "1" },
                        new Book { Id = 2, SeriesName = "Harry Potter", SeriesPosition = "2" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/J.K. Rowling/Harry Potter 1/Harry Potter 1.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "ALBUM", new List<string> { "Harry Potter 1 - The Philosophers Stone" } },
                            { "ARTIST", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.BookId, Is.EqualTo(1));
                }

                [Test]
                public void should_allow_relaxed_containment_when_longer_overlap_candidate_does_not_explain_same_field()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Harry Potter and the Philosopher's Stone",
                            BookTitle = "Harry Potter and the Philosopher's Stone",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Harry Potter at Home: Readings - Harry Potter and the Philosopher's Stone",
                            BookTitle = "Harry Potter at Home: Readings - Harry Potter and the Philosopher's Stone",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 3,
                            BookId = 3,
                            EditionTitle = "Harry Potter and the Chamber of Secrets",
                            BookTitle = "Harry Potter and the Chamber of Secrets",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1, SeriesName = "Harry Potter", SeriesPosition = "1" },
                        new Book { Id = 2, SeriesName = "Harry Potter", SeriesPosition = "0" },
                        new Book { Id = 3, SeriesName = "Harry Potter", SeriesPosition = "2" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/J.K. Rowling/Unknown/01.m4b",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "The Philosophers Stone Track 1" } },
                            { "ALBUM", new List<string> { "Harry Potter 1 - The Philosophers Stone" } },
                            { "ALBUMARTIST", new List<string> { "J.K. Rowling" } },
                            { "ARTIST", new List<string> { "Jim Dale" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.BookId, Is.EqualTo(1));
                    Assert.That(match.EditionId, Is.EqualTo(1));
                }

                [Test]
                public void should_use_book_title_proof_to_select_regional_narrator_edition_without_falling_into_pocket_potters()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    const string bookTitle = "Harry Potter and the Philosopher's Stone";

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 10,
                            BookId = 1,
                            EditionTitle = "Harry Potter and the Sorcerer's Stone, Book 1",
                            BookTitle = bookTitle,
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                            NarratorNames = "Jim Dale",
                            DurationSeconds = 29880,
                            ReadingFormatId = 2,
                            MatchScore = 26.5
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 11,
                            BookId = 1,
                            EditionTitle = bookTitle,
                            BookTitle = bookTitle,
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                            ReadingFormatId = 3,
                            MatchScore = 26.5
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 12,
                            BookId = 1,
                            EditionTitle = bookTitle,
                            BookTitle = bookTitle,
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                            NarratorNames = "Stephen Fry",
                            DurationSeconds = 29880,
                            ReadingFormatId = 2,
                            MatchScore = 26.5
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 20,
                            BookId = 2,
                            EditionTitle = "Harry Potter",
                            BookTitle = "Pocket Potters",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                            ReadingFormatId = 1,
                            MatchScore = 30.0
                        }
                    });

                    var books = new[]
                    {
                        new Book
                        {
                            Id = 1,
                            AuthorId = 25,
                            Title = bookTitle,
                            BaseBookId = "hc:328491",
                            SeriesName = "Harry Potter",
                            SeriesPosition = "1"
                        },
                        new Book
                        {
                            Id = 2,
                            AuthorId = 25,
                            Title = "Pocket Potters",
                            BaseBookId = "gr:268681105"
                        }
                    };

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(books),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/J.K. Rowling/Harry Potter/Harry Potter and the Philosopher's Stone.m4b",
                        DurationSeconds = 29804,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "The Philosophers Stone Track 1" } },
                            { "ALBUM", new List<string> { "Harry Potter 1 - The Philosophers Stone" } },
                            { "ALBUMARTIST", new List<string> { "J.K. Rowling" } },
                            { "ARTIST", new List<string> { "Jim Dale" } }
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.BookId, Is.EqualTo(1));
                        Assert.That(match.EditionId, Is.EqualTo(10));
                        Assert.That(match.Provenance.SupportingSignals.Any(signal =>
                            signal.Type == "title" &&
                            signal.Scope == "book" &&
                            signal.Field == "ALBUM" &&
                            signal.Expected == bookTitle), Is.True);
                        Assert.That(match.Provenance.SupportingSignals.Any(signal =>
                            signal.Type == "narrator" &&
                            signal.Scope == "edition" &&
                            signal.Field == "ARTIST" &&
                            signal.Observed == "Jim Dale"), Is.True);
                    });
                }

                [Test]
                public void should_not_use_series_name_book_proof_to_replace_a_specific_edition_title()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 10,
                            EditionTitle = "Wild Cards I",
                            BookTitle = "Wild Cards",
                            AuthorId = 38,
                            AuthorName = "George R. R. Martin",
                            NarratorNames = "Luke Daniels",
                            DurationSeconds = 68340,
                            ReadingFormatId = 2,
                            MatchScore = 30.0
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 200,
                            BookId = 20,
                            EditionTitle = "Knaves Over Queens",
                            BookTitle = "Knaves Over Queens",
                            AuthorId = 38,
                            AuthorName = "George R. R. Martin",
                            ReadingFormatId = 3,
                            MatchScore = 25.0
                        }
                    });

                    var books = new[]
                    {
                        new Book
                        {
                            Id = 10,
                            AuthorId = 38,
                            Title = "Wild Cards",
                            BaseBookId = "hc:1175234",
                            SeriesName = "Wild Cards",
                            SeriesPosition = "1"
                        },
                        new Book
                        {
                            Id = 20,
                            AuthorId = 38,
                            Title = "Knaves Over Queens",
                            BaseBookId = "hc:1136534",
                            SeriesName = "Wild Cards",
                            SeriesPosition = "27"
                        }
                    };

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 38, Name = "George R. R. Martin" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(books),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/George R. R. Martin/Knaves Over Queens/Knaves Over Queens.m4b",
                        DurationSeconds = 67916,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Knaves over Queens : Wild Cards (Unabridged)" } },
                            { "ALBUM", new List<string> { "Knaves over Queens (Unabridged)" } },
                            { "ARTIST", new List<string> { "George R. R. Martin" } },
                            { "COMPOSER", new List<string> { "Peter Noble" } }
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 38);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.BookId, Is.EqualTo(20));
                        Assert.That(match.EditionId, Is.EqualTo(200));
                        Assert.That(match.MatchedVia, Is.EqualTo("escape_hatch"));
                    });
                }

                [Test]
                public void should_prefer_direct_edition_title_when_book_title_candidate_only_has_equivalent_duration_proof()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 10,
                            EditionTitle = "Red Country",
                            BookTitle = "Red Country",
                            AuthorId = 25,
                            AuthorName = "Joe Abercrombie",
                            NarratorNames = "Steven Pacey",
                            DurationSeconds = 71520,
                            ReadingFormatId = 2,
                            MatchScore = 25.0
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 10,
                            EditionTitle = "Red Country: Booktrack Edition",
                            BookTitle = "Red Country",
                            AuthorId = 25,
                            AuthorName = "Joe Abercrombie",
                            NarratorNames = "Steven Pacey",
                            DurationSeconds = 71580,
                            ReadingFormatId = 2,
                            MatchScore = 30.0
                        }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 25, Name = "Joe Abercrombie" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(new[]
                        {
                            new Book { Id = 10, AuthorId = 25, Title = "Red Country", BaseBookId = "hc:297006" }
                        }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Joe Abercrombie/Red Country/Red Country.m4b",
                        DurationSeconds = 71556,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Red Country 1" } },
                            { "ALBUM", new List<string> { "Red Country" } },
                            { "ARTIST", new List<string> { "Joe Abercrombie" } }
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.BookId, Is.EqualTo(10));
                        Assert.That(match.EditionId, Is.EqualTo(100));
                    });
                }

                [Test]
                public void should_not_use_partial_narrator_overlap_to_open_a_different_edition_title()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 200,
                            BookId = 20,
                            EditionTitle = "Suicide Med",
                            BookTitle = "Suicide Med",
                            AuthorId = 26,
                            AuthorName = "Freida McFadden",
                            ReadingFormatId = 2,
                            MatchScore = 25.0
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 201,
                            BookId = 20,
                            EditionTitle = "Dead Med",
                            BookTitle = "Suicide Med",
                            AuthorId = 26,
                            AuthorName = "Freida McFadden",
                            NarratorNames = "Patricia Santomasso, Scott Merriman",
                            DurationSeconds = 36180,
                            ReadingFormatId = 2,
                            MatchScore = 30.0
                        }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 26, Name = "Freida McFadden" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(new[]
                        {
                            new Book { Id = 20, AuthorId = 26, Title = "Suicide Med", BaseBookId = "hc:563300" }
                        }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Freida McFadden/Suicide Med/Suicide Med.m4b",
                        DurationSeconds = 46445,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Suicide Med" } },
                            { "ALBUM", new List<string> { "Suicide Med" } },
                            { "ARTIST", new List<string> { "Freida McFadden" } },
                            { "COMPOSER", new List<string> { "Megan Tusing, Scott Merriman" } }
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 26);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.BookId, Is.EqualTo(20));
                        Assert.That(match.EditionId, Is.EqualTo(200));
                    });
                }

                [Test]
                public void should_allow_balanced_short_title_when_no_better_candidate_explains_leftover()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "It",
                            BookTitle = "It",
                            AuthorId = 77,
                            AuthorName = "Test Author",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "A Game of Thrones",
                            BookTitle = "A Game of Thrones",
                            AuthorId = 77,
                            AuthorName = "Test Author",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 77, Name = "Test Author" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Test Author/It/It.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "It Game" } },
                            { "ARTIST", new List<string> { "Test Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 77);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(100));
                    Assert.That(match.BookId, Is.EqualTo(100));
                }

                [Test]
                public void should_allow_balanced_short_title_from_custom_tag_when_no_better_candidate_explains_leftover()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "It",
                            BookTitle = "It",
                            AuthorId = 77,
                            AuthorName = "Test Author",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "A Game of Thrones",
                            BookTitle = "A Game of Thrones",
                            AuthorId = 77,
                            AuthorName = "Test Author",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 77, Name = "Test Author" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Test Author/It/It.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "It Game" } },
                            { "ARTIST", new List<string> { "Test Author" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 77);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(100));
                    Assert.That(match.BookId, Is.EqualTo(100));
                }

                [TestCase(BookMatchingStrictness.Balanced)]
                [TestCase(BookMatchingStrictness.Aggressive)]
                public void should_prefer_the_book_whose_edition_explains_more_of_the_same_title_value(
                    BookMatchingStrictness strictness)
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "Harry Potter and the Goblet of Fire",
                            BookTitle = "Harry Potter and the Goblet of Fire",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                            ReadingFormatId = 3,
                            MatchScore = 100.0,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "Harry Potter and the Goblet of Fire Unabridged",
                            BookTitle = "Harry Potter and the Goblet of Fire Unabridged",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                            ReadingFormatId = 3,
                            MatchScore = 1.0,
                        }
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(new[]
                        {
                            new Book { Id = 100, AuthorId = 25, Title = "Harry Potter and the Goblet of Fire", BaseBookId = "hc:goblet-of-fire" },
                            new Book { Id = 101, AuthorId = 25, Title = "Harry Potter and the Goblet of Fire Unabridged", BaseBookId = "hc:goblet-of-fire-unabridged" },
                        }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/J.K. Rowling/Harry Potter and the Goblet of Fire/Harry Potter and the Goblet of Fire.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "Harry Potter and the Goblet of Fire - Unabridged" } },
                            { "CONTRIBUTOR", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(101));
                    Assert.That(match.BookId, Is.EqualTo(101));
                }

                [Test]
                public void balanced_should_tolerate_unexplained_edition_word_when_no_book_explains_it_better()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 100,
                        BookId = 100,
                        EditionTitle = "Harry Potter and the Goblet of Fire",
                        BookTitle = "Harry Potter and the Goblet of Fire",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                        ReadingFormatId = 3,
                    };

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch> { candidate }),
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/J.K. Rowling/Harry Potter and the Goblet of Fire/Harry Potter and the Goblet of Fire.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "Harry Potter and the Goblet of Fire - Unabridged" } },
                            { "CONTRIBUTOR", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.EditionId, Is.EqualTo(100));
                }

                [Test]
                public void strict_should_reject_unexplained_edition_word_near_an_otherwise_exact_title()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var candidate = new EditionFtsMatch
                    {
                        EditionId = 100,
                        BookId = 100,
                        EditionTitle = "Harry Potter and the Goblet of Fire",
                        BookTitle = "Harry Potter and the Goblet of Fire",
                        AuthorId = 25,
                        AuthorName = "J.K. Rowling",
                        ReadingFormatId = 3,
                    };

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Strict, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: new StubEditionFtsRepository(new List<EditionFtsMatch> { candidate }),
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/ebooks/J.K. Rowling/Harry Potter and the Goblet of Fire/Harry Potter and the Goblet of Fire.epub",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "Harry Potter and the Goblet of Fire - Unabridged" } },
                            { "CONTRIBUTOR", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_compare_books_by_each_books_maximal_sibling_edition_explanation()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 100,
                            EditionTitle = "Chapterhouse",
                            BookTitle = "Chapterhouse Dune",
                            AuthorId = 25,
                            AuthorName = "Frank Herbert",
                            NarratorNames = "Euan Morton",
                            DurationSeconds = 10000,
                            ReadingFormatId = 2,
                            MatchScore = 1.0,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 102,
                            BookId = 100,
                            EditionTitle = "Chapterhouse Dune",
                            BookTitle = "Chapterhouse Dune",
                            AuthorId = 25,
                            AuthorName = "Frank Herbert",
                            NarratorNames = "Euan Morton",
                            DurationSeconds = 10000,
                            ReadingFormatId = 2,
                            MatchScore = 1.0,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 101,
                            EditionTitle = "Chapterhouse Dune",
                            BookTitle = "Dune: The Gateway Collection",
                            AuthorId = 25,
                            AuthorName = "Frank Herbert",
                            NarratorNames = "Euan Morton",
                            ReadingFormatId = 2,
                            MatchScore = 100.0,
                        }
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 25, Name = "Frank Herbert" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(new[]
                        {
                            new Book { Id = 100, AuthorId = 25, Title = "Chapterhouse Dune", BaseBookId = "hc:439821" },
                            new Book { Id = 101, AuthorId = 25, Title = "Dune: The Gateway Collection", BaseBookId = "hc:490427" },
                        }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/Frank Herbert/Chapterhouse Dune/Chapterhouse Dune.m4b",
                        DurationSeconds = 10000,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "Chapterhouse Dune" } },
                            { "CONTRIBUTOR", new List<string> { "Frank Herbert" } },
                            { "PERFORMER", new List<string> { "Euan Morton" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Not.Null);
                    Assert.That(match.BookId, Is.EqualTo(100));
                }

                [Test]
                public void should_not_treat_a_bound_series_position_as_book_title_occurrence_identity()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 10,
                            EditionTitle = "Wild Cards I",
                            BookTitle = "Wild Cards",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            NarratorNames = "Example Reader",
                            ReadingFormatId = 2,
                            DurationSeconds = 10000,
                            MatchScore = 100.0,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 200,
                            BookId = 20,
                            EditionTitle = "Mississippi Roll Wild Cards",
                            BookTitle = "Mississippi Roll",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            NarratorNames = "Example Reader",
                            ReadingFormatId = 2,
                            DurationSeconds = 10000,
                            MatchScore = 1.0,
                        }
                    });
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 38, Name = "George R.R. Martin" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(new[]
                        {
                            new Book { Id = 10, AuthorId = 38, Title = "Wild Cards", BaseBookId = "hc:1175234", SeriesName = "Wild Cards", SeriesPosition = "1" },
                            new Book { Id = 20, AuthorId = 38, Title = "Mississippi Roll", BaseBookId = "gr:54861755" },
                        }),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/George R. R. Martin/Mississippi Roll/Mississippi Roll.m4b",
                        DurationSeconds = 10000,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "Mississippi Roll Wild Cards I" } },
                            { "CONTRIBUTOR", new List<string> { "George R.R. Martin" } },
                            { "PERFORMER", new List<string> { "Example Reader" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 38);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.BookId, Is.EqualTo(20));
                        Assert.That(match.EditionId, Is.EqualTo(200));
                    });
                }

                [Test]
                public void should_use_an_ineligible_sibling_edition_title_to_prove_the_specific_book()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);
                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 100,
                            BookId = 10,
                            EditionTitle = "Wild Cards",
                            BookTitle = "Wild Cards",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            ReadingFormatId = 3,
                            DurationSeconds = 0,
                            MatchScore = 100.0,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 101,
                            BookId = 10,
                            EditionTitle = "Wild Cards I",
                            BookTitle = "Wild Cards",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            ReadingFormatId = 2,
                            DurationSeconds = 0,
                            MatchScore = 100.0,
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 200,
                            BookId = 20,
                            EditionTitle = "Mississippi Roll",
                            BookTitle = "Mississippi Roll",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            ReadingFormatId = 2,
                            DurationSeconds = 0,
                            MatchScore = 1.0,
                        },
                        // This product cannot be the native audiobook winner, but its title still
                        // belongs to the same provider Book and explains the complete observed phrase.
                        new EditionFtsMatch
                        {
                            EditionId = 201,
                            BookId = 20,
                            EditionTitle = "Mississippi Roll: A Wild Cards Novel",
                            BookTitle = "Mississippi Roll",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            ReadingFormatId = 3,
                            DurationSeconds = 0,
                            MatchScore = 1.0,
                        },
                        // This direct generic/specific edge must not globally suppress the recalled
                        // sibling expansion still required to compare Wild Cards with Mississippi Roll.
                        new EditionFtsMatch
                        {
                            EditionId = 300,
                            BookId = 30,
                            EditionTitle = "Mississippi",
                            BookTitle = "Mississippi",
                            AuthorId = 38,
                            AuthorName = "George R.R. Martin",
                            ReadingFormatId = 3,
                            DurationSeconds = 0,
                            MatchScore = 200.0,
                        }
                    });
                    var books = new[]
                    {
                        new Book
                        {
                            Id = 10,
                            AuthorId = 38,
                            Title = "Wild Cards",
                            BaseBookId = "hc:1175234",
                            SeriesName = "Wild Cards",
                            SeriesPosition = "1"
                        },
                        new Book
                        {
                            Id = 20,
                            AuthorId = 38,
                            Title = "Mississippi Roll",
                            BaseBookId = "gr:54861755",
                            SeriesName = "Wild Cards",
                            SeriesPosition = "24"
                        },
                        new Book
                        {
                            Id = 30,
                            AuthorId = 38,
                            Title = "Mississippi",
                            BaseBookId = "hc:mississippi"
                        }
                    };
                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: new StubAuthorService(new Author { Id = 38, Name = "George R.R. Martin" }),
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: new StubBookService(books),
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/George R. R. Martin/Mississippi Roll/Mississippi Roll 01.mp3",
                        DurationSeconds = 21,
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "BOOKIDENTITY", new List<string> { "Mississippi Roll: Wild Cards Book 1" } },
                            { "CONTRIBUTOR", new List<string> { "Wild Cards Trust, George R. R. Martin" } },
                            { "PERFORMER", new List<string> { "William Hope" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 38);

                    Assert.Multiple(() =>
                    {
                        Assert.That(match, Is.Not.Null);
                        Assert.That(match.BookId, Is.EqualTo(20));
                        Assert.That(match.EditionId, Is.EqualTo(200));
                    });
                }

                [Test]
                public void should_reject_balanced_relaxed_containment_below_threshold()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Harry Potter and the Goblet of Fire",
                            BookTitle = "Harry Potter and the Goblet of Fire",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Harry Potter and the Chamber of Secrets",
                            BookTitle = "Harry Potter and the Chamber of Secrets",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/J.K. Rowling/Harry Potter Goblet/Harry Potter Goblet.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "ALBUM", new List<string> { "Harry Potter Goblet" } },
                            { "ARTIST", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_not_allow_loose_mode_to_drop_meaningful_title_tokens()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Harry Potter and the Goblet of Fire",
                            BookTitle = "Harry Potter and the Goblet of Fire",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Harry Potter and the Chamber of Secrets",
                            BookTitle = "Harry Potter and the Chamber of Secrets",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Aggressive, usePathAsTagsFallback: false),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: null,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/J.K. Rowling/Harry Potter Goblet/Harry Potter Goblet.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "ALBUM", new List<string> { "Harry Potter Goblet" } },
                            { "ARTIST", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_reject_incomplete_title_when_present_tokens_are_ambiguous_across_books()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 1,
                            EditionTitle = "Harry Potter and the Philosopher's Stone",
                            BookTitle = "Harry Potter and the Philosopher's Stone",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 2,
                            EditionTitle = "Harry Potter and the Chamber of Secrets",
                            BookTitle = "Harry Potter and the Chamber of Secrets",
                            AuthorId = 25,
                            AuthorName = "J.K. Rowling",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 1, SeriesName = "Harry Potter", SeriesPosition = "1" },
                        new Book { Id = 2, SeriesName = "Harry Potter", SeriesPosition = "2" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/J.K. Rowling/Harry Potter/Harry Potter.mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "ALBUM", new List<string> { "Harry Potter" } },
                            { "ARTIST", new List<string> { "J.K. Rowling" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

                    Assert.That(match, Is.Null);
                }

                [Test]
                public void should_prefer_specific_wild_cards_vii_over_generic_wild_cards()
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
                    {
                        // Generic candidate first (wrong)
                        new EditionFtsMatch
                        {
                            EditionId = 3268,
                            BookId = 911,
                            EditionTitle = "Wild Cards",
                            BookTitle = "Wild Cards",
                            AuthorId = 25,
                            AuthorName = "George R.R. Martin",
                        },
                        // Specific candidate second (correct)
                        new EditionFtsMatch
                        {
                            EditionId = 3458,
                            BookId = 922,
                            EditionTitle = "Wild Cards VII: Dead Man's Hand",
                            BookTitle = "Dead Man's Hand",
                            AuthorId = 25,
                            AuthorName = "George R.R. Martin",
                            NarratorNames = "Adrian Paul",
                        }
                    });

                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "George R.R. Martin" });
                    var bookService = new StubBookService(new[]
                    {
                        new Book { Id = 911, AuthorId = 25, Title = "Wild Cards", SeriesName = "Wild Cards", SeriesPosition = "1" },
                        new Book { Id = 922, AuthorId = 25, Title = "Dead Man's Hand", SeriesName = "Wild Cards", SeriesPosition = "7" },
                    });

                    var svc = new FileMatchingService(
                        matchingLogger: null,
                        v5MatchingService: null,
                        containmentValidator: containment,
                        pendingAuthorImportService: null,
                        commandQueue: null,
                        authorFolderMatchingService: null,
                        rootFolderService: null,
                        configService: ConfigServiceTestProxy.Create(),
                        authorService: authorService,
                        eventAggregator: null,
                        authorLibraryService: null,
                        editionFtsRepository: fts,
                        bookService: bookService,
                        editionService: null,
                        editionRepository: null,
                        mediaInfoExtractor: null,
                        logger: logger);

                    var multiAuthor = "George R. R. Martin, Wild Cards Trust, John Jos. Miller";
                    var file = new DiscoveredFileWithMetadata
                    {
                        Path = "/audiobooks/George R. R. Martin/Wild Cards II (1).mp3",
                        AllTags = new Dictionary<string, List<string>>
                        {
                            { "TITLE", new List<string> { "Wild Cards VII: Dead Man's Hand (Unabridged) Part 1 - 001" } },
                            { "ALBUM", new List<string> { "Wild Cards VII (Unabridged)" } },
                            { "ALBUMARTIST", new List<string> { multiAuthor } },
                            { "ARTIST", new List<string> { multiAuthor } },
                            { "ID3v2:TCOP", new List<string> { "&#169;1990 George R. R. Martin and the Wild Card Trust; (P)2017 Random House Audio" } },
                            { "TRACKNUMBER", new List<string> { "1" } },
                        }
                    };

                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(3458));
	                    Assert.That(match.BookId, Is.EqualTo(922));
	                }

	                [Test]
	                public void should_match_wild_cards_vii_when_part_number_and_track_number_do_not_align()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        // Generic candidate first (wrong)
	                        new EditionFtsMatch
	                        {
	                            EditionId = 3268,
	                            BookId = 911,
	                            EditionTitle = "Wild Cards",
	                            BookTitle = "Wild Cards",
	                            AuthorId = 25,
	                            AuthorName = "George R.R. Martin",
	                        },
	                        // Specific candidate second (correct)
	                        new EditionFtsMatch
	                        {
	                            EditionId = 3458,
	                            BookId = 922,
	                            EditionTitle = "Wild Cards VII: Dead Man's Hand",
	                            BookTitle = "Dead Man's Hand",
	                            AuthorId = 25,
	                            AuthorName = "George R.R. Martin",
	                            NarratorNames = "Adrian Paul",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 25, Name = "George R.R. Martin" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var multiAuthor = "George R. R. Martin, Wild Cards Trust, John Jos. Miller";
	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/George R. R. Martin/Wild Cards VII Dead Man's Hand (Unabridged) - 50.mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "Wild Cards VII: Dead Man's Hand (Unabridged) Part 2 - 019" } },
	                            { "ALBUM", new List<string> { "Wild Cards VII (Unabridged)" } },
	                            { "ALBUMARTIST", new List<string> { multiAuthor } },
	                            { "ARTIST", new List<string> { multiAuthor } },
	                            { "TRACKNUMBER", new List<string> { "50" } },
	                        }
	                    };

	                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

	                    Assert.That(match, Is.Not.Null);
	                    Assert.That(match.EditionId, Is.EqualTo(3458));
	                    Assert.That(match.BookId, Is.EqualTo(922));
	                }

	                [Test]
	                public void should_match_track_packaging_suffix_T01_19()
	                {
	                    var logger = LogManager.GetCurrentClassLogger();
	                    var containment = new ContainmentValidator(new TagNormalizer(), logger);

	                    var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
	                    {
	                        new EditionFtsMatch
	                        {
	                            EditionId = 2571,
	                            BookId = 1191,
	                            EditionTitle = "The Majors",
	                            BookTitle = "The Majors",
	                            AuthorId = 34,
	                            AuthorName = "W.E.B. Griffin",
	                        }
	                    });

	                    var authorService = new StubAuthorService(new Author { Id = 34, Name = "W.E.B. Griffin" });

	                    var svc = new FileMatchingService(
	                        matchingLogger: null,
	                        v5MatchingService: null,
	                        containmentValidator: containment,
	                        pendingAuthorImportService: null,
	                        commandQueue: null,
	                        authorFolderMatchingService: null,
	                        rootFolderService: null,
	                        configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
	                        authorService: authorService,
	                        eventAggregator: null,
	                        authorLibraryService: null,
	                        editionFtsRepository: fts,
	                        bookService: null,
	                        editionService: null,
	                        editionRepository: null,
	                        mediaInfoExtractor: null,
	                        logger: logger);

	                    var file = new DiscoveredFileWithMetadata
	                    {
	                        Path = "/audiobooks/W.E.B. Griffin/The Majors/W.E.B. Griffin - The Majors (1).mp3",
	                        AllTags = new Dictionary<string, List<string>>
	                        {
	                            { "TITLE", new List<string> { "The Majors T01-19" } },
	                            { "ALBUM", new List<string> { "The Majors" } },
	                            { "ARTIST", new List<string> { "W.E.B. Griffin" } },
	                            { "TRACKNUMBER", new List<string> { "1" } },
	                        }
	                    };

		                    var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 34);

		                    Assert.That(match, Is.Not.Null);
		                    Assert.That(match.EditionId, Is.EqualTo(2571));
		                    Assert.That(match.BookId, Is.EqualTo(1191));
		                }

        [Test]
        public void should_choose_matching_narrator_even_when_wrong_narrator_has_better_duration()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 70,
                    BookId = 1,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Jim Dale",
                    DurationSeconds = 36000,
                    MatchScore = 20.0
                },
                new EditionFtsMatch
                {
                    EditionId = 71,
                    BookId = 1,
                    EditionTitle = "Harry Potter and the Order of the Phoenix",
                    BookTitle = "Harry Potter and the Order of the Phoenix",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = "Stephen Fry",
                    DurationSeconds = 37200,
                    MatchScore = 9.0
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/Harry Potter/Harry Potter and the Order of the Phoenix.m4b",
                DurationSeconds = 36120,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Harry Potter and the Order of the Phoenix" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } },
                    { "COMPOSER", new List<string> { "Stephen Fry" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(71));
        }

        [Test]
        public void should_trust_exact_asin_over_conflicting_contributor_tags()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var author = new Author { Id = 72, Name = "Freida McFadden" };
            var book = new Book
            {
                Id = 1978,
                AuthorId = 72,
                Author = author,
                Title = "The Boyfriend",
                MediaType = BookMediaType.Audiobook
            };

            var asinEdition = new Edition
            {
                Id = 4865,
                BookId = book.Id,
                Book = book,
                Title = "The Boyfriend",
                Asin = "B0D3QV4S65",
                AudibleASIN = "B0D3QV4S65",
                Narrator = "Robb Moreira",
                NarratorNames = new List<string> { "Robb Moreira", "Victoria Connolly" },
                DurationSeconds = 33600
            };

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 4865,
                    BookId = book.Id,
                    EditionTitle = "The Boyfriend",
                    BookTitle = "The Boyfriend",
                    AuthorId = author.Id,
                    AuthorName = author.Name,
                    NarratorNames = "Robb Moreira, Victoria Connolly",
                    DurationSeconds = 33600,
                    MatchScore = 20.0
                },
                new EditionFtsMatch
                {
                    EditionId = 4866,
                    BookId = book.Id,
                    EditionTitle = "The Boyfriend",
                    BookTitle = "The Boyfriend",
                    AuthorId = author.Id,
                    AuthorName = author.Name,
                    NarratorNames = "Adam Blanford, Victoria Connolly",
                    DurationSeconds = 33180,
                    MatchScore = 9.0
                }
            });

            var svc = new FileMatchingService(
                matchingLogger: null,
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
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: new StubEditionRepository(new[] { asinEdition }),
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Freida McFadden/The Boyfriend/The Boyfriend (1).mp3",
                DurationSeconds = 33202,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "ASIN", new List<string> { "B0D3QV4S65" } },
                    { "TITLE", new List<string> { "The Boyfriend" } },
                    { "ALBUM", new List<string> { "The Boyfriend" } },
                    { "ALBUMARTIST", new List<string> { "Freida McFadden" } },
                    { "ARTIST", new List<string> { "Freida McFadden, Victoria Connolly, Adam Blanford" } },
                    { "COMPOSER", new List<string> { "Victoria Connolly, Adam Blanford" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: author.Id);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(4865));
        }

        [Test]
        public void should_choose_near_exact_duration_candidate_when_narrator_is_missing()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 80,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = null,
                    DurationSeconds = 36000,
                    MatchScore = 9.0
                },
                new EditionFtsMatch
                {
                    EditionId = 81,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = null,
                    DurationSeconds = 39000,
                    MatchScore = 10.0
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/The Casual Vacancy/The Casual Vacancy.m4b",
                DurationSeconds = 36120,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "The Casual Vacancy" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(80));
        }

        [Test]
        public void should_not_choose_loose_duration_candidate_when_a_nearer_sibling_exists()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);

            var fts = new StubEditionFtsRepository(new List<EditionFtsMatch>
            {
                new EditionFtsMatch
                {
                    EditionId = 90,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = null,
                    DurationSeconds = 36000,
                    MatchScore = 9.0
                },
                new EditionFtsMatch
                {
                    EditionId = 91,
                    BookId = 1,
                    EditionTitle = "The Casual Vacancy",
                    BookTitle = "The Casual Vacancy",
                    AuthorId = 25,
                    AuthorName = "J.K. Rowling",
                    NarratorNames = null,
                    DurationSeconds = 42000,
                    MatchScore = 10.0
                }
            });

            var authorService = new StubAuthorService(new Author { Id = 25, Name = "J.K. Rowling" });

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.K. Rowling/The Casual Vacancy/The Casual Vacancy.m4b",
                DurationSeconds = 36090,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "The Casual Vacancy" } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } }
                }
            };

            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 25);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(90));
        }
		            }
			        }
