using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.IndexerSearch
{
    public interface ISearchForReleases
    {
        Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false);
        Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false);
        Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false);
        Task<List<DownloadDecision>> AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch);
        Task<List<DownloadDecision>> SeriesSearch(int seriesId, int authorId, bool userInvokedSearch, bool interactiveSearch);
    }

    public class ReleaseSearchService : ISearchForReleases
    {
        private const int MaxAlternateEditionSearchTitles = 5;

        private readonly IIndexerFactory _indexerFactory;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly IMakeDownloadDecision _makeDownloadDecision;
        private readonly Logger _logger;

        public ReleaseSearchService(IIndexerFactory indexerFactory,
                                IBookService bookService,
                                IAuthorService authorService,
                                IEditionSelector editionSelector,
                                IMakeDownloadDecision makeDownloadDecision,
                                Logger logger,
                                ISeriesService seriesService = null,
                                ISeriesBookLinkService seriesBookLinkService = null)
        {
            _indexerFactory = indexerFactory;
            _bookService = bookService;
            _authorService = authorService;
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _makeDownloadDecision = makeDownloadDecision;
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
        {
            var book = _bookService.GetBook(bookId);
            return await BookSearch(book, missingOnly, userInvokedSearch, interactiveSearch, allowUnmonitored);
        }

        public async Task<List<DownloadDecision>> AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var downloadDecisions = new List<DownloadDecision>();

            var author = _authorService.GetAuthor(authorId);

            var decisions = await AuthorSearch(author, missingOnly, userInvokedSearch, interactiveSearch);
            downloadDecisions.AddRange(decisions);

            return DeDupeDecisions(downloadDecisions);
        }

        public async Task<List<DownloadDecision>> AuthorSearch(Author author, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var authorCatalog = GetAuthorCatalog(author, null);
            var books = authorCatalog
                .Where(book => book.IsMonitoredWithAuthor())
                .ToList();
            books = FilterBooksByConfiguredMediaProfiles(author, books);

            if (books.Count == 0)
            {
                _logger.Debug("[RELEASE_SEARCH_SERVICE] Skipping author search for '{0}' because no monitored books have a configured quality profile for their media type", author?.Name ?? "NULL");
                return new List<DownloadDecision>();
            }

            var searchSpec = Get<AuthorSearchCriteria>(author, books, authorCatalog, userInvokedSearch, interactiveSearch);
            return await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
        }

        public async Task<List<DownloadDecision>> SeriesSearch(int seriesId, int authorId, bool userInvokedSearch, bool interactiveSearch)
        {
            var series = _seriesService?.GetSeries(seriesId);
            var author = _authorService.GetAuthor(authorId);

            if (series == null || author == null || _seriesBookLinkService == null)
            {
                return new List<DownloadDecision>();
            }

            // Series links are local and the legacy foreign-author filter is no longer
            // supported. Restrict the links to this author through the local catalog below.
            var links = _seriesBookLinkService.GetLinksBySeries(seriesId);
            var positions = links
                .GroupBy(link => link.BookId)
                .ToDictionary(group => group.Key, group => group.Min(link => link.SeriesPosition > 0 ? link.SeriesPosition : int.MaxValue));

            var authorCatalog = _bookService.GetBooksByAuthor(author.Id);
            var books = authorCatalog
                .Where(book => positions.ContainsKey(book.Id) && book.MediaType == series.MediaType)
                .OrderBy(book => positions[book.Id])
                .ToList();

            books = FilterBooksByConfiguredMediaProfiles(author, books);

            if (books.Count == 0)
            {
                return new List<DownloadDecision>();
            }

            var searchSpec = Get<BookSearchCriteria>(author, books, authorCatalog, userInvokedSearch, interactiveSearch, false);
            searchSpec.BookTitle = series.Title;

            var decisions = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);

            foreach (var decision in decisions)
            {
                if (decision?.RemoteBook == null)
                {
                    continue;
                }

                decision.RemoteBook.Author = author;
                decision.RemoteBook.Books = books;
                decision.RemoteBook.DownloadAllowed = true;
            }

            return DeDupeDecisions(decisions);
        }

        public Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
        {
            return BookSearch(book, null, missingOnly, userInvokedSearch, interactiveSearch, allowUnmonitored);
        }

        public async Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
        {
            _logger.Debug("[RELEASE_SEARCH_SERVICE] ===== BookSearch STARTED =====");
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Book: '{0}' (ID: {1}, AuthorId: {2})", book?.Title ?? "NULL", book?.Id ?? -1, book?.AuthorId ?? -1);
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Parameters: missingOnly={0}, userInvokedSearch={1}, interactiveSearch={2}", missingOnly, userInvokedSearch, interactiveSearch);

            var author = book.Author?.Id == book.AuthorId ? book.Author : null;
            if (author == null)
            {
                _logger.Debug("[RELEASE_SEARCH_SERVICE] Getting author with ID {0}...", book.AuthorId);
                author = _authorService.GetAuthor(book.AuthorId);
            }

            book.Author = author;

            if (!interactiveSearch && !allowUnmonitored && !book.IsMonitoredWithAuthor())
            {
                _logger.Debug("[RELEASE_SEARCH_SERVICE] Skipping book search for '{0}' because the book or its author {1} side is not monitored",
                    book?.Title ?? "NULL",
                    book?.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook");
                return new List<DownloadDecision>();
            }

            _logger.Debug("[RELEASE_SEARCH_SERVICE] Retrieved author: '{0}' (ID: {1})", author?.Name ?? "NULL", author?.Id ?? -1);
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Author profile IDs - AudiobookQualityProfileId: {0}, EbookQualityProfileId: {1}", author?.AudiobookQualityProfileId ?? -999, author?.EbookQualityProfileId ?? -999);
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Author profile objects - AudiobookQualityProfile: {0}, EbookQualityProfile: {1}",
                author?.AudiobookQualityProfileId.HasValue == true ? $"LOADED ('{author.AudiobookQualityProfile?.Value?.Name ?? "Unknown"}')" : "NULL",
                author?.EbookQualityProfileId.HasValue == true ? $"LOADED ('{author.EbookQualityProfile?.Value?.Name ?? "Unknown"}')" : "NULL");

            if (!HasConfiguredQualityProfileForMediaType(author, book?.MediaType ?? BookMediaType.Audiobook))
            {
                _logger.Debug("[RELEASE_SEARCH_SERVICE] Skipping book search for '{0}' because author '{1}' has no configured quality profile for media type {2}",
                    book?.Title ?? "NULL",
                    author?.Name ?? "NULL",
                    book?.MediaType ?? BookMediaType.Audiobook);
                return new List<DownloadDecision>();
            }

            _logger.Debug("[RELEASE_SEARCH_SERVICE] Creating search criteria...");
            var searchSpec = Get<BookSearchCriteria>(author, new List<Book> { book }, authorCatalog, userInvokedSearch, interactiveSearch, !allowUnmonitored);

            // Search the same edition the API/UI displays: the single monitored edition.
            var selectedEdition = book.Editions?
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id)
                .FirstOrDefault();

            searchSpec.BookTitle = GetSearchBookTitle(book, selectedEdition) ?? string.Empty;
            searchSpec.BookIsbn = GetSearchBookIsbn(book, selectedEdition);
            var alternateSearchTitles = GetAlternateEditionSearchTitles(book, searchSpec.BookTitle, author?.Name);
            if (book.ReleaseDate.HasValue)
            {
                searchSpec.BookYear = book.ReleaseDate.Value.Year;
            }

            var decisions = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec, alternateSearchTitles);
            return DeDupeDecisions(decisions);
        }

        internal static List<string> GetAlternateEditionSearchTitles(Book book, string primaryTitle, string authorName)
        {
            if (book == null)
            {
                return new List<string>();
            }

            var seenQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryQuery = GetSearchQueryKey(primaryTitle, authorName);
            if (!string.IsNullOrWhiteSpace(primaryQuery))
            {
                seenQueries.Add(primaryQuery);
            }

            // Search language variants already present for this work, preferring English
            // titles when available. Matching and profile checks still use the monitored
            // edition; these values only change the text sent to indexers.
            var editionCandidates = (book.Editions ?? new List<Edition>())
                .Where(edition => edition != null && !string.IsNullOrWhiteSpace(edition.Title))
                .OrderByDescending(edition => edition.Language?.CanonicalizeLanguage() == "eng")
                .ThenBy(edition => edition.Id)
                .Select(edition => (Title: GetSearchBookTitle(book, edition), edition.Language, edition.Id))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
                .ToList();

            var candidates = editionCandidates
                .Where(candidate => candidate.Language?.CanonicalizeLanguage() == "eng")
                .ToList();

            if (!string.IsNullOrWhiteSpace(book.OriginalTitle))
            {
                candidates.Add((book.OriginalTitle, null, int.MaxValue - 1));
            }

            if (!string.IsNullOrWhiteSpace(book.Title))
            {
                candidates.Add((book.Title, null, int.MaxValue));
            }

            candidates.AddRange(editionCandidates
                .Where(candidate => candidate.Language?.CanonicalizeLanguage() != "eng"));

            return candidates
                .Where(candidate =>
                {
                    var query = GetSearchQueryKey(candidate.Title, authorName);
                    return !string.IsNullOrWhiteSpace(query) && seenQueries.Add(query);
                })
                .Select(candidate => candidate.Title)
                .Take(MaxAlternateEditionSearchTitles)
                .ToList();
        }

        private static string GetSearchQueryKey(string title, string authorName)
        {
            var mainTitle = BookSearchCriteria.GetMainSearchTitle(title, authorName);
            return string.IsNullOrWhiteSpace(mainTitle) ? null : SearchCriteriaBase.GetQueryTitle(mainTitle);
        }

        internal static string GetSearchBookTitle(Book book, Edition selectedEdition)
        {
            return BookEditionTitle.GetTitleForBook(book, selectedEdition);
        }

        internal static string GetSearchBookIsbn(Book book, Edition selectedEdition)
        {
            if (!string.IsNullOrWhiteSpace(selectedEdition?.Isbn13))
            {
                return selectedEdition.Isbn13;
            }

            if (!string.IsNullOrWhiteSpace(selectedEdition?.Isbn10))
            {
                return selectedEdition.Isbn10;
            }

            var fallbackEdition = book?.Editions?
                .Where(edition => edition != null && edition != selectedEdition && IsSuitableEbookEdition(edition))
                .Where(edition => !string.IsNullOrWhiteSpace(edition.Isbn13) || !string.IsNullOrWhiteSpace(edition.Isbn10))
                .OrderBy(edition => GetEditionIsbnFallbackRank(edition, selectedEdition, book))
                .ThenBy(edition => edition.Id)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(fallbackEdition?.Isbn13))
            {
                return fallbackEdition.Isbn13;
            }

            if (!string.IsNullOrWhiteSpace(fallbackEdition?.Isbn10))
            {
                return fallbackEdition.Isbn10;
            }

            if (!string.IsNullOrWhiteSpace(book?.ISBN13))
            {
                return book.ISBN13;
            }

            return string.IsNullOrWhiteSpace(book?.ISBN10) ? null : book.ISBN10;
        }

        private static bool IsSuitableEbookEdition(Edition edition)
        {
            if (edition == null)
            {
                return false;
            }

            if (edition.ReadingFormatId == 3 || edition.IsEbook)
            {
                return true;
            }

            return false;
        }

        private static int GetEditionIsbnFallbackRank(Edition edition, Edition selectedEdition, Book book)
        {
            var selectedTitle = selectedEdition?.Title;
            if (TitlesMatch(edition?.Title, selectedTitle))
            {
                return 0;
            }

            if (TitlesMatch(edition?.Title, book?.Title))
            {
                return 1;
            }

            return 2;
        }

        private static bool TitlesMatch(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(NormalizeComparisonTitle(left), NormalizeComparisonTitle(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeComparisonTitle(string value)
        {
            return value?
                .Trim()
                .Replace('’', '\'')
                .Replace('‘', '\'');
        }

        internal static bool HasConfiguredQualityProfileForMediaType(Author author, BookMediaType mediaType)
        {
            if (author == null)
            {
                return false;
            }

            return mediaType switch
            {
                BookMediaType.Ebook => author.EbookQualityProfileId.HasValue && author.EbookQualityProfileId.Value > 0,
                _ => author.AudiobookQualityProfileId.HasValue && author.AudiobookQualityProfileId.Value > 0,
            };
        }

        internal static List<Book> FilterBooksByConfiguredMediaProfiles(Author author, IEnumerable<Book> books)
        {
            return (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null && HasConfiguredQualityProfileForMediaType(author, book.MediaType))
                .ToList();
        }

        private TSpec Get<TSpec>(Author author, List<Book> books, List<Book> authorCatalog, bool userInvokedSearch, bool interactiveSearch, bool monitoredBooksOnly = true)
            where TSpec : SearchCriteriaBase, new()
        {
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Creating SearchCriteria of type {0}", typeof(TSpec).Name);
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Input author: '{0}' (ID: {1})", author?.Name ?? "NULL", author?.Id ?? -1);
            _logger.Debug("[RELEASE_SEARCH_SERVICE] Author has profiles - Audiobook: {0}, Ebook: {1}",
                author?.AudiobookQualityProfileId.HasValue == true ? "YES" : "NO",
                author?.EbookQualityProfileId.HasValue == true ? "YES" : "NO");

            var spec = new TSpec
            {
                Books = books,
                Author = author,
                AuthorCatalog = GetAuthorCatalog(author, authorCatalog),
                MonitoredBooksOnly = monitoredBooksOnly,
                UserInvokedSearch = userInvokedSearch,
                InteractiveSearch = interactiveSearch
            };

            _logger.Debug("[RELEASE_SEARCH_SERVICE] SearchCriteria created with:");
            _logger.Debug("  - Books: {0}", books?.Count ?? 0);
            _logger.Debug("  - Author catalog: {0}", spec.AuthorCatalog?.Count ?? 0);
            _logger.Debug("  - Author: '{0}'", spec.Author?.Name ?? "NULL");
            _logger.Debug("  - Monitored books only: {0}", spec.MonitoredBooksOnly);
            _logger.Debug("  - Author.AudiobookQualityProfile: {0}",
                spec.Author?.AudiobookQualityProfileId.HasValue == true ? $"SET ('{spec.Author.AudiobookQualityProfile?.Value?.Name ?? "Unknown"}')" : "NULL");
            _logger.Debug("  - Author.EbookQualityProfile: {0}",
                spec.Author?.EbookQualityProfileId.HasValue == true ? $"SET ('{spec.Author.EbookQualityProfile?.Value?.Name ?? "Unknown"}')" : "NULL");
            _logger.Debug("  - UserInvokedSearch: {0}", spec.UserInvokedSearch);
            _logger.Debug("  - InteractiveSearch: {0}", spec.InteractiveSearch);

            return spec;
        }

        private List<Book> GetAuthorCatalog(Author author, List<Book> authorCatalog)
        {
            if (authorCatalog != null)
            {
                return authorCatalog;
            }

            if (author?.Books?.Count > 0)
            {
                return author.Books;
            }

            return author?.Id > 0 ? _bookService.GetBooksByAuthor(author.Id) : new List<Book>();
        }

        private async Task<List<DownloadDecision>> Dispatch(
            Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction,
            SearchCriteriaBase criteriaBase,
            IReadOnlyList<string> alternateSearchTitles = null)
        {
            var indexers = criteriaBase.InteractiveSearch ?
                _indexerFactory.InteractiveSearchEnabled() :
                _indexerFactory.AutomaticSearchEnabled();

            var requestedMediaType = SearchMediaTypeHelper.GetRequestedMediaType(criteriaBase);
            var mediaType = requestedMediaType ?? BookMediaType.Audiobook;

            var authorTags = criteriaBase?.Author?.GetTagsForMediaType(mediaType) ?? new HashSet<int>();

            // Filter indexers to untagged indexers and indexers with intersecting tags
            indexers = indexers.Where(i => i.Definition.Tags.Empty() || i.Definition.Tags.Intersect(authorTags).Any()).ToList();

            _logger.ProgressInfo("Searching indexers for {0}. {1} active indexers", criteriaBase, indexers.Count);

            // Log detailed search information
            _logger.Debug("[SEARCH_INITIATED] Type: {0}, Author: '{1}', Books: [{2}], Indexers: [{3}]",
                criteriaBase.InteractiveSearch ? "Interactive" : (criteriaBase.UserInvokedSearch ? "Manual" : "Automatic"),
                criteriaBase.Author?.Name ?? "Unknown",
                criteriaBase.Books != null ? string.Join(", ", criteriaBase.Books.Select(b => b.Title)) : "None",
                string.Join(", ", indexers.Select(i => i.Definition.Name)));

            var reports = await FetchReports(indexers, searchAction, criteriaBase);
            var decisions = reports.Count == 0
                ? new List<DownloadDecision>()
                : MakeSearchDecisions(reports, requestedMediaType, criteriaBase);

            var bookSearchCriteria = criteriaBase as BookSearchCriteria;
            if (bookSearchCriteria != null && indexers.Count > 0 && alternateSearchTitles?.Count > 0 && !decisions.Any(decision => decision.Approved))
            {
                foreach (var alternateTitle in alternateSearchTitles)
                {
                    _logger.Debug("No approved release found for the monitored edition title; trying alternate edition title '{0}'", alternateTitle);
                    bookSearchCriteria.SearchTitleOverride = alternateTitle;

                    try
                    {
                        var alternateReports = await FetchReports(indexers, searchAction, criteriaBase);
                        if (alternateReports.Count > 0)
                        {
                            reports.AddRange(alternateReports);
                            decisions = MakeSearchDecisions(reports, requestedMediaType, criteriaBase);
                        }
                    }
                    finally
                    {
                        bookSearchCriteria.SearchTitleOverride = null;
                    }

                    if (decisions.Any(decision => decision.Approved))
                    {
                        break;
                    }
                }
            }

            if (reports.Count == 0)
            {
                decisions = MakeSearchDecisions(reports, requestedMediaType, criteriaBase);
            }

            var filteredReports = FilterReportsByRequestedMediaType(reports, requestedMediaType, criteriaBase);
            if (filteredReports.Count < reports.Count)
            {
                _logger.Debug("Filtered {0} cross-media releases for requested media type {1}", reports.Count - filteredReports.Count, requestedMediaType.Value);
            }
            reports = filteredReports;

            _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexers", reports.Count, criteriaBase, indexers.Count);

            // Log raw results for debugging
            if (reports.Any())
            {
                _logger.Debug("[INDEXER_RAW_RESULTS] Total: {0} results", reports.Count);
                var sampleCount = Math.Min(reports.Count, 5); // Log first 5 results as sample
                for (var i = 0; i < sampleCount; i++)
                {
                    var report = reports[i];
                    _logger.Debug("  Result #{0}: Title='{1}', Indexer='{2}', Size={3}, PublishDate={4}, Author='{5}'",
                        i + 1,
                        report.Title,
                        report.Indexer,
                        report.Size,
                        report.PublishDate,
                        report.Author ?? "N/A");
                }

                if (reports.Count > sampleCount)
                {
                    _logger.Debug("  ... and {0} more results", reports.Count - sampleCount);
                }
            }

            if (indexers.Any())
            {
                var lastSearchTime = DateTime.UtcNow;
                _logger.Debug("Setting last search time to: {0}", lastSearchTime);

                criteriaBase.Books.ForEach(a => a.LastSearchTime = lastSearchTime);
                _bookService.UpdateLastSearchTime(criteriaBase.Books);
            }

            return decisions;
        }

        private async Task<List<ReleaseInfo>> FetchReports(
            IEnumerable<IIndexer> indexers,
            Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction,
            SearchCriteriaBase criteriaBase)
        {
            var tasks = indexers.Select(indexer => DispatchIndexer(searchAction, indexer, criteriaBase));
            var batch = await Task.WhenAll(tasks);
            return batch.SelectMany(results => results).ToList();
        }

        private List<DownloadDecision> MakeSearchDecisions(
            List<ReleaseInfo> reports,
            BookMediaType? requestedMediaType,
            SearchCriteriaBase criteriaBase)
        {
            var candidates = FilterReportsByRequestedMediaType(reports, requestedMediaType, criteriaBase);
            return _makeDownloadDecision.GetSearchDecision(candidates, criteriaBase).ToList();
        }

        private async Task<IList<ReleaseInfo>> DispatchIndexer(Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction, IIndexer indexer, SearchCriteriaBase criteriaBase)
        {
            try
            {
                return await searchAction(indexer);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error while searching for {0}", criteriaBase);
            }

            return Array.Empty<ReleaseInfo>();
        }

        private List<DownloadDecision> DeDupeDecisions(List<DownloadDecision> decisions)
        {
            // De-dupe reports by guid so duplicate results aren't returned. Pick the one with the least rejections and higher indexer priority.
            return decisions.GroupBy(d => d.RemoteBook.Release.Guid)
                .Select(d => d.OrderBy(v => v.Rejections.Count()).ThenBy(v => v.RemoteBook?.Release?.IndexerPriority ?? IndexerDefinition.DefaultPriority).First())
                .ToList();
        }


        private List<ReleaseInfo> FilterReportsByRequestedMediaType(List<ReleaseInfo> reports, BookMediaType? requestedMediaType, SearchCriteriaBase criteriaBase)
        {
            if (!requestedMediaType.HasValue || criteriaBase?.Books?.Count != 1)
            {
                return reports;
            }

            return (reports ?? new List<ReleaseInfo>())
                .Where(report => IsCompatibleWithRequestedMediaType(report, requestedMediaType.Value))
                .ToList();
        }

        private bool IsCompatibleWithRequestedMediaType(ReleaseInfo report, BookMediaType requestedMediaType)
        {
            var qualityModel = ParseQualityForMediaTypeDetection(report);
            var detectedMediaType = QualityMediaTypeHelper.DetectMediaType(qualityModel?.Quality, report);

            return !detectedMediaType.HasValue || detectedMediaType.Value == requestedMediaType;
        }

        private QualityModel ParseQualityForMediaTypeDetection(ReleaseInfo report)
        {
            var torrentInfo = report as TorrentInfo;
            if (torrentInfo?.FileType.IsNotNullOrWhiteSpace() == true && IsMAMIndexer(report.Indexer))
            {
                return QualityParser.ParseQualityFromFileType(torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer);
            }

            return QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags);
        }

        private static bool IsMAMIndexer(string indexerName)
        {
            return !string.IsNullOrWhiteSpace(indexerName) &&
                   (indexerName.Contains("MyAnonamouse", StringComparison.OrdinalIgnoreCase) ||
                    indexerName.Contains("MAM", StringComparison.OrdinalIgnoreCase));
        }
    }
}
