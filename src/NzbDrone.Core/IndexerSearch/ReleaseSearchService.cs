using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
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
        Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch);
        Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch);
        Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch);
        Task<List<DownloadDecision>> AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch);
    }

    public class ReleaseSearchService : ISearchForReleases
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IMakeDownloadDecision _makeDownloadDecision;
        private readonly Logger _logger;

        public ReleaseSearchService(IIndexerFactory indexerFactory,
                                IBookService bookService,
                                IAuthorService authorService,
                                IEditionSelector editionSelector,
                                IMakeDownloadDecision makeDownloadDecision,
                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _bookService = bookService;
            _authorService = authorService;
            _makeDownloadDecision = makeDownloadDecision;
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var book = _bookService.GetBook(bookId);
            return await BookSearch(book, missingOnly, userInvokedSearch, interactiveSearch);
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
            var books = authorCatalog.Where(book => book.IsMonitored()).ToList();
            books = FilterBooksByConfiguredMediaProfiles(author, books);

            if (books.Count == 0)
            {
                _logger.Debug("[RELEASE_SEARCH_SERVICE] Skipping author search for '{0}' because no monitored books have a configured quality profile for their media type", author?.Name ?? "NULL");
                return new List<DownloadDecision>();
            }

            var searchSpec = Get<AuthorSearchCriteria>(author, books, authorCatalog, userInvokedSearch, interactiveSearch);
            return await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
        }

        public Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            return BookSearch(book, null, missingOnly, userInvokedSearch, interactiveSearch);
        }

        public async Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
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
            var searchSpec = Get<BookSearchCriteria>(author, new List<Book> { book }, authorCatalog, userInvokedSearch, interactiveSearch);

            // Search the same edition the API/UI displays: the single monitored edition.
            var selectedEdition = book.Editions?
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id)
                .FirstOrDefault();

            searchSpec.BookTitle = GetSearchBookTitle(book, selectedEdition) ?? string.Empty;
            searchSpec.BookIsbn = GetSearchBookIsbn(book, selectedEdition);
            if (book.ReleaseDate.HasValue)
            {
                searchSpec.BookYear = book.ReleaseDate.Value.Year;
            }

            var decisions = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
            return DeDupeDecisions(decisions);
        }

        internal static string GetSearchBookTitle(Book book, Edition selectedEdition)
        {
            if (book == null)
            {
                return string.Empty;
            }

            // Keep searches honest: use the monitored edition title (the one displayed in the UI),
            // falling back to the book title if needed.
            var selectedTitle = selectedEdition?.Title;
            if (!string.IsNullOrWhiteSpace(selectedTitle))
            {
                return selectedTitle;
            }

            return book.Title ?? string.Empty;
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

        private TSpec Get<TSpec>(Author author, List<Book> books, List<Book> authorCatalog, bool userInvokedSearch, bool interactiveSearch)
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
                UserInvokedSearch = userInvokedSearch,
                InteractiveSearch = interactiveSearch
            };

            _logger.Debug("[RELEASE_SEARCH_SERVICE] SearchCriteria created with:");
            _logger.Debug("  - Books: {0}", books?.Count ?? 0);
            _logger.Debug("  - Author catalog: {0}", spec.AuthorCatalog?.Count ?? 0);
            _logger.Debug("  - Author: '{0}'", spec.Author?.Name ?? "NULL");
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

        private async Task<List<DownloadDecision>> Dispatch(Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction, SearchCriteriaBase criteriaBase)
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

            var tasks = indexers.Select(indexer => DispatchIndexer(searchAction, indexer, criteriaBase));

            var batch = await Task.WhenAll(tasks);

            var reports = batch.SelectMany(x => x).ToList();

            if (requestedMediaType.HasValue && criteriaBase?.Books?.Count == 1)
            {
                var originalCount = reports.Count;
                reports = FilterReportsByRequestedMediaType(reports, requestedMediaType.Value);
                var filteredOutCount = originalCount - reports.Count;

                if (filteredOutCount > 0)
                {
                    _logger.Debug("Filtered {0} cross-media releases for requested media type {1}", filteredOutCount, requestedMediaType.Value);
                }
            }

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

            return _makeDownloadDecision.GetSearchDecision(reports, criteriaBase).ToList();
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


        private List<ReleaseInfo> FilterReportsByRequestedMediaType(List<ReleaseInfo> reports, BookMediaType requestedMediaType)
        {
            return (reports ?? new List<ReleaseInfo>())
                .Where(report => IsCompatibleWithRequestedMediaType(report, requestedMediaType))
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
