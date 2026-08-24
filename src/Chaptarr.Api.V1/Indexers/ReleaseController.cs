using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Chaptarr.Http;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Chaptarr.Api.V1.Indexers
{
    [V1ApiController]
    public class ReleaseController : ReleaseControllerBase
    {
        private readonly IFetchAndParseRss _rssFetcherAndParser;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IMakeDownloadDecision _downloadDecisionMaker;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IDownloadService _downloadService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IParsingService _parsingService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        private readonly ICached<DownloadDecision> _downloadDecisionCache;

        public ReleaseController(IFetchAndParseRss rssFetcherAndParser,
                             ISearchForReleases releaseSearchService,
                             IMakeDownloadDecision downloadDecisionMaker,
                             IPrioritizeDownloadDecision prioritizeDownloadDecision,
                             IDownloadService downloadService,
                             IAuthorService authorService,
                             IBookService bookService,
                             IParsingService parsingService,
                             IIndexerFactory indexerFactory,
                             ICacheManager cacheManager,
                             Logger logger)
        {
            _rssFetcherAndParser = rssFetcherAndParser;
            _releaseSearchService = releaseSearchService;
            _downloadDecisionMaker = downloadDecisionMaker;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _downloadService = downloadService;
            _authorService = authorService;
            _bookService = bookService;
            _parsingService = parsingService;
            _indexerFactory = indexerFactory;
            _logger = logger;

            PostValidator.RuleFor(s => s.IndexerId).ValidId();
            PostValidator.RuleFor(s => s.Guid).NotEmpty();

            _downloadDecisionCache = cacheManager.GetCache<DownloadDecision>(GetType(), "downloadDecisions");
        }

        [HttpPost]
        public async Task<ActionResult<ReleaseResource>> DownloadRelease([FromBody] ReleaseResource release)
        {
            ValidateResource(release);

            var decision = _downloadDecisionCache.Find(GetCacheKey(release));

            if (decision?.RemoteBook == null)
            {
                _logger.Debug("Couldn't find requested release in cache, cache timeout probably expired.");

                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Couldn't find requested release in cache, try searching again");
            }

            var remoteBook = decision.RemoteBook;

            try
            {
                if (remoteBook.Author == null)
                {
                    if (release.BookId.HasValue)
                    {
                        var book = _bookService.GetBook(release.BookId.Value);

                        remoteBook.Author = _authorService.GetAuthor(book.AuthorId);
                        remoteBook.Books = new List<Book> { book };
                    }
                    else if (release.AuthorId.HasValue)
                    {
                        var author = _authorService.GetAuthor(release.AuthorId.Value);
                        var books = _parsingService.GetBooks(remoteBook.ParsedBookInfo, author);

                        if (books.Empty())
                        {
                            throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse books in the release");
                        }

                        remoteBook.Author = author;
                        remoteBook.Books = books;
                    }
                    else
                    {
                        throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to find matching author and books");
                    }
                }
                else if (remoteBook.Books.Empty())
                {
                    var books = _parsingService.GetBooks(remoteBook.ParsedBookInfo, remoteBook.Author);

                    if (books.Empty() && release.BookId.HasValue)
                    {
                        var book = _bookService.GetBook(release.BookId.Value);

                        books = new List<Book> { book };
                    }

                    remoteBook.Books = books;
                }

                if (remoteBook.Books.Empty())
                {
                    throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse books in the release");
                }

                if (ShouldBlockInteractiveDownload(decision))
                {
                    throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse books in the release");
                }

                await _downloadService.DownloadReport(remoteBook, release.DownloadClientId);
            }
            catch (DownloadClientRejectedReleaseException ex)
            {
                _logger.Warn(ex, "Download client rejected release");

                var detail = ex.InnerException?.Message;
                var message = string.IsNullOrWhiteSpace(detail)
                    ? ex.Message
                    : $"{ex.Message}: {detail}";

                throw new NzbDroneClientException(HttpStatusCode.Conflict, $"Download client rejected release: {message}");
            }
            catch (ReleaseDownloadException ex)
            {
                _logger.Error(ex, "Getting release from indexer failed");
                throw new NzbDroneClientException(HttpStatusCode.Conflict, $"Getting release from indexer failed: {ex.Message}");
            }
            catch (DownloadClientAuthenticationException ex)
            {
                _logger.Warn(ex, "Download client authentication failed");
                throw new NzbDroneClientException(HttpStatusCode.Unauthorized, ex.Message);
            }
            catch (DownloadClientUnavailableException ex)
            {
                _logger.Warn(ex, "Download client is unavailable");
                throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable, ex.Message);
            }
            catch (DownloadClientException ex)
            {
                _logger.Warn(ex, "Failed to add to download client");
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
            }

            return Ok(release);
        }

        [HttpGet]
        public async Task<object> GetReleases(int? bookId, int? authorId, bool bypassFilters = false)
        {
            if (bookId.HasValue)
            {
                return await GetBookReleases(int.Parse(Request.Query["bookId"]), bypassFilters);
            }

            if (authorId.HasValue)
            {
                return await GetAuthorReleases(int.Parse(Request.Query["authorId"]), bypassFilters);
            }

            return await GetRss();
        }

        private async Task<object> GetBookReleases(int bookId, bool bypassFilters = false)
        {
            try
            {
                var book = _bookService.GetBook(bookId);
                var author = _authorService.GetAuthor(book.AuthorId);

                // Start indexer search
                var indexerSearchTask = _releaseSearchService.BookSearch(bookId, true, true, true);

                // Wait for indexer search to complete
                var decisions = await indexerSearchTask;

                var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);
                var siblingToggleInfo = BuildSiblingToggleInfo(book, author);

                var (results, hiddenResults, filterSummary) = MapDecisionsForInteractiveSearchWithSummary(prioritizedDecisions, bypassFilters);

                _logger.Debug("FILTER_DEBUG: TotalResults={0}, FilteredCount={1}, DisplayedCount={2}, HasSoftFilters={3}, SoftFilteredCount={4}, BypassMode={5}", filterSummary.TotalResults, filterSummary.FilteredCount, filterSummary.DisplayedCount, filterSummary.HasSoftFilters, filterSummary.SoftFilteredCount, bypassFilters);

                // Log filter summary for monitoring and debugging
                if (filterSummary.FilteredCount > 0)
                {
                    _logger.Debug("BOOK_SEARCH_FILTER_SUMMARY: {0}", filterSummary.SummaryText);
                    if (filterSummary.FilterWarnings.Any())
                    {
                        _logger.Debug("FILTER_WARNINGS: [{0}]", string.Join(", ", filterSummary.FilterWarnings));
                    }
                }

                // Return structured response with FilterSummary for enhanced UI
                return new ReleaseSearchResponse
                {
                    Releases = results,
                    HiddenReleases = hiddenResults,
                    FilterSummary = filterSummary,
                    SiblingBookId = siblingToggleInfo.SiblingBookId,
                    SiblingMediaType = siblingToggleInfo.SiblingMediaType,
                    SiblingToggleEnabled = siblingToggleInfo.SiblingToggleEnabled,
                    SiblingToggleDisabledReason = siblingToggleInfo.SiblingToggleDisabledReason
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Book search failed");
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, ex.Message);
            }
        }

        private async Task<object> GetAuthorReleases(int authorId, bool bypassFilters = false)
        {
            try
            {
                var decisions = await _releaseSearchService.AuthorSearch(authorId, false, true, true);
                var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

                var (results, hiddenResults, filterSummary) = MapDecisionsForInteractiveSearchWithSummary(prioritizedDecisions, bypassFilters);

                // Log filter summary for monitoring and debugging
                if (filterSummary.FilteredCount > 0)
                {
                    _logger.Debug("AUTHOR_SEARCH_FILTER_SUMMARY: {0}", filterSummary.SummaryText);
                    if (filterSummary.FilterWarnings.Any())
                    {
                        _logger.Debug("FILTER_WARNINGS: [{0}]", string.Join(", ", filterSummary.FilterWarnings));
                    }
                }

                // Return structured response with FilterSummary for enhanced UI
                return new ReleaseSearchResponse
                {
                    Releases = results,
                    HiddenReleases = hiddenResults,
                    FilterSummary = filterSummary
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Author search failed");
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, ex.Message);
            }
        }

        private async Task<List<ReleaseResource>> GetRss()
        {
            var reports = await _rssFetcherAndParser.Fetch();
            var decisions = _downloadDecisionMaker.GetRssDecision(reports);
            var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

            return MapDecisions(prioritizedDecisions);
        }

        protected override ReleaseResource MapDecision(DownloadDecision decision, int initialWeight)
        {
            var resource = base.MapDecision(decision, initialWeight);
            _downloadDecisionCache.Set(GetCacheKey(resource), decision, TimeSpan.FromMinutes(30));

            return resource;
        }

        private List<ReleaseResource> MapDecisionsForInteractiveSearch(IEnumerable<DownloadDecision> decisions, bool bypassFilters = false)
        {
            var result = new List<ReleaseResource>();
            var totalDecisions = decisions.Count();
            var filteredCount = 0;
            var hardFilteredCount = 0;
            var softFilteredCount = 0;
            var durationsFound = 0;
            var narratorsFound = 0;
            var graphicAudioFound = 0;

            foreach (var downloadDecision in decisions)
            {
                // Count MAM metadata for trace logging
                var releaseInfo = downloadDecision.RemoteBook.Release;
                if (releaseInfo != null)
                {
                    if (!string.IsNullOrWhiteSpace(releaseInfo.Duration))
                    {
                        durationsFound++;
                    }

                    if (!string.IsNullOrWhiteSpace(releaseInfo.Narrator))
                    {
                        narratorsFound++;
                    }

                    if (releaseInfo.IsGraphicAudio)
                    {
                        graphicAudioFound++;
                    }
                }

                // Enhanced filtering logic with bypass support
                var shouldFilter = ShouldFilterFromInteractiveSearch(downloadDecision, bypassFilters);
                if (shouldFilter.shouldFilter)
                {
                    filteredCount++;
                    if (shouldFilter.isHardFilter)
                    {
                        hardFilteredCount++;
                    }
                    else
                    {
                        softFilteredCount++;
                    }

                    _logger.Debug("Filtering out rejected release for interactive search: {0} - {1} (Hard: {2}, Bypass: {3})",
                        downloadDecision.RemoteBook.Release.Title,
                        string.Join(", ", downloadDecision.Rejections.Select(r => r.Reason)),
                        shouldFilter.isHardFilter,
                        bypassFilters);
                    continue;
                }

                var release = MapDecision(downloadDecision, result.Count);
                result.Add(release);
            }

            // Enhanced logging with filter breakdown
            _logger.Trace("MAM_SEARCH_SUMMARY: TOTAL={0}, FILTERED={1} (HARD={2}, SOFT={3}), DISPLAYED={4}, DURATIONS_FOUND={5}, NARRATORS_FOUND={6}, GRAPHIC_AUDIO={7}, BYPASS_MODE={8}", totalDecisions, filteredCount, hardFilteredCount, softFilteredCount, result.Count, durationsFound, narratorsFound, graphicAudioFound, bypassFilters);

            if (bypassFilters && softFilteredCount > 0)
            {
                _logger.Debug("FILTER_BYPASS: Showing {0} additional results due to soft filter bypass (total: {1})", softFilteredCount, result.Count);
            }

            if (durationsFound == 0 && totalDecisions > 0)
            {
                _logger.Trace("MAM_DURATION_WARNING: No durations found in {0} results - check pattern matching", totalDecisions);
            }

            return result;
        }

        private (List<ReleaseResource> results, List<ReleaseResource> hiddenResults, FilterSummary filterSummary) MapDecisionsForInteractiveSearchWithSummary(IEnumerable<DownloadDecision> decisions, bool bypassFilters = false)
        {
            var result = new List<ReleaseResource>();
            var hiddenResults = new List<ReleaseResource>();
            var allDecisions = decisions.ToList(); // Convert to list for multiple enumeration
            var totalDecisions = allDecisions.Count;
            var filteredCount = 0;
            var hardFilteredCount = 0;
            var softFilteredCount = 0;
            var durationsFound = 0;
            var narratorsFound = 0;
            var graphicAudioFound = 0;

            foreach (var downloadDecision in allDecisions)
            {
                // Count MAM metadata for trace logging
                var releaseInfo = downloadDecision.RemoteBook.Release;
                if (releaseInfo != null)
                {
                    if (!string.IsNullOrWhiteSpace(releaseInfo.Duration))
                    {
                        durationsFound++;
                    }

                    if (!string.IsNullOrWhiteSpace(releaseInfo.Narrator))
                    {
                        narratorsFound++;
                    }

                    if (releaseInfo.IsGraphicAudio)
                    {
                        graphicAudioFound++;
                    }
                }

                // Enhanced filtering logic with bypass support
                var shouldFilter = ShouldFilterFromInteractiveSearch(downloadDecision, bypassFilters);
                if (shouldFilter.shouldFilter)
                {
                    filteredCount++;
                    if (shouldFilter.isHardFilter)
                    {
                        hardFilteredCount++;
                    }
                    else
                    {
                        softFilteredCount++;
                    }

                    _logger.Debug("FILTER_DEBUG_REJECTION: Title='{0}', Rejections=[{1}], Hard={2}, Soft={3}, HasIsHardFilter={4}, HasIsSoftFilter={5}, BypassMode={6}",
                        downloadDecision.RemoteBook.Release.Title,
                        string.Join(", ", downloadDecision.Rejections.Select(r => $"'{r.Reason}'")),
                        shouldFilter.isHardFilter,
                        downloadDecision.Rejections.All(r => r.IsSoftFilter),
                        downloadDecision.Rejections.Any(r => r.IsHardFilter),
                        downloadDecision.Rejections.Any(r => r.IsSoftFilter),
                        bypassFilters);

                    hiddenResults.Add(MapDecision(downloadDecision, hiddenResults.Count));
                    continue;
                }

                var release = MapDecision(downloadDecision, result.Count);

                result.Add(release);
            }

            // Create filter summary
            var filterSummary = CreateFilterSummary(allDecisions, result, hardFilteredCount, softFilteredCount, bypassFilters);

            // Enhanced logging with filter breakdown
            _logger.Trace("MAM_SEARCH_SUMMARY: TOTAL={0}, FILTERED={1} (HARD={2}, SOFT={3}), DISPLAYED={4}, DURATIONS_FOUND={5}, NARRATORS_FOUND={6}, GRAPHIC_AUDIO={7}, BYPASS_MODE={8}", totalDecisions, filteredCount, hardFilteredCount, softFilteredCount, result.Count, durationsFound, narratorsFound, graphicAudioFound, bypassFilters);

            if (bypassFilters && softFilteredCount > 0)
            {
                _logger.Debug("FILTER_BYPASS: Showing {0} additional results due to soft filter bypass (total: {1})", softFilteredCount, result.Count);
            }

            if (durationsFound == 0 && totalDecisions > 0)
            {
                _logger.Trace("MAM_DURATION_WARNING: No durations found in {0} results - check pattern matching", totalDecisions);
            }

            return (result, hiddenResults, filterSummary);
        }

        internal static FilterSummary CreateFilterSummary(IEnumerable<DownloadDecision> allDecisions, List<ReleaseResource> displayedResults, int hardFilteredCount, int softFilteredCount, bool bypassFilters)
        {
            var decisions = allDecisions.ToList();
            var hiddenDecisions = decisions
                .Where(decision => ShouldFilterFromInteractiveSearch(decision, bypassFilters).shouldFilter)
                .ToList();
            var totalDecisions = decisions.Count;
            var filteredCount = hardFilteredCount + softFilteredCount;

            // Analyze filter categories and create breakdown
            var filterBreakdown = new Dictionary<string, int>();
            var filteredCategories = new List<string>();
            var filterWarnings = new List<string>();

            foreach (var decision in hiddenDecisions)
            {
                foreach (var rejection in decision.Rejections)
                {
                    var category = rejection.Category ?? "General";
                    if (!filterBreakdown.ContainsKey(category))
                    {
                        filterBreakdown[category] = 0;
                        filteredCategories.Add(category);
                    }

                    filterBreakdown[category]++;
                }
            }

            if (filteredCount > 0)
            {
                filterWarnings.Add($"{filteredCount} results hidden because author/book identity or release contents could not be safely resolved");
            }

            // Create summary text
            var summaryText = "";
            if (filteredCount > 0)
            {
                if (bypassFilters)
                {
                    summaryText = $"Showing {displayedResults.Count} of {totalDecisions} results (bypass mode enabled, {hardFilteredCount} hard filtered)";
                }
                else
                {
                    summaryText = $"Showing {displayedResults.Count} of {totalDecisions} results ({filteredCount} filtered)";
                }
            }
            else
            {
                summaryText = $"Showing all {displayedResults.Count} results";
            }

            return new FilterSummary
            {
                TotalResults = totalDecisions,
                FilteredCount = filteredCount,
                DisplayedCount = displayedResults.Count,
                HardFilteredCount = hardFilteredCount,
                SoftFilteredCount = softFilteredCount,
                HasSoftFilters = softFilteredCount > 0,
                HasHardFilters = hardFilteredCount > 0,
                BypassMode = bypassFilters,
                FilteredCategories = filteredCategories.Distinct().ToList(),
                FilterBreakdown = filterBreakdown,
                SummaryText = summaryText,
                FilterWarnings = filterWarnings
            };
        }

        // Enhanced filter method with bypass support and hard/soft filter detection
        internal static (bool shouldFilter, bool isHardFilter) ShouldFilterFromInteractiveSearch(DownloadDecision decision, bool bypassFilters = false)
        {
            if (!decision.Rejections.Any())
            {
                return (false, false); // No rejections, don't filter
            }

            var mismatchCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Matching",
                "Author",
                "Title",
                "Parsing",
                "Pack"
            };

            var hasMismatchRejection = decision.Rejections.Any(r => mismatchCategories.Contains(r.Category ?? "General"));
            if (hasMismatchRejection)
            {
                return (true, decision.Rejections.Any(r => r.IsHardFilter));
            }

            // Interactive search is the user override surface. Once author/book identity has
            // passed, profile and preference rejections remain visible with their score/reasons.
            return (false, false);
        }

        internal static bool ShouldBlockInteractiveDownload(DownloadDecision decision)
        {
            if (decision == null || decision.RemoteBook?.Books == null || !decision.RemoteBook.Books.Any())
            {
                return true;
            }

            return false;
        }

        internal static bool IsProfileEnforcementRejection(Rejection rejection)
        {
            if (rejection == null)
            {
                return false;
            }

            return string.Equals(rejection.Category, "Format", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rejection.Category, "Quality", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsReleaseProfileRejection(Rejection rejection)
        {
            return string.Equals(rejection?.Category, "Release Profile", StringComparison.OrdinalIgnoreCase);
        }

        private string GetCacheKey(ReleaseResource resource)
        {
            return string.Concat(resource.IndexerId, "_", resource.Guid);
        }

        private SiblingToggleInfo BuildSiblingToggleInfo(Book book, NzbDrone.Core.Books.Author author)
        {
            var siblingMediaType = book?.MediaType == BookMediaType.Ebook
                ? BookMediaType.Audiobook
                : BookMediaType.Ebook;

            var siblingMediaTypeName = ToMediaTypeName(siblingMediaType);

            if (book == null || author == null)
            {
                return new SiblingToggleInfo
                {
                    SiblingMediaType = siblingMediaTypeName,
                    SiblingToggleEnabled = false
                };
            }

            if (!HasRootFolderForMediaType(author, siblingMediaType))
            {
                return new SiblingToggleInfo
                {
                    SiblingMediaType = siblingMediaTypeName,
                    SiblingToggleEnabled = false,
                    SiblingToggleDisabledReason = $"No {siblingMediaTypeName} root folder configured for {author.Name}"
                };
            }

            var siblingBook = _bookService.GetBooksByAuthor(book.AuthorId)
                .Where(candidate => candidate != null && candidate.Id != book.Id)
                .Where(candidate => candidate.MediaType == siblingMediaType)
                .Where(candidate => WorkIdMatcher.WorkProviderIdMatches(book, candidate))
                .OrderByDescending(candidate => candidate.IsMonitored())
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefault();

            if (siblingBook == null)
            {
                return new SiblingToggleInfo
                {
                    SiblingMediaType = siblingMediaTypeName,
                    SiblingToggleEnabled = false,
                    SiblingToggleDisabledReason = $"No {siblingMediaTypeName} version of this book in your library"
                };
            }

            return new SiblingToggleInfo
            {
                SiblingBookId = siblingBook.Id,
                SiblingMediaType = siblingMediaTypeName,
                SiblingToggleEnabled = true
            };
        }

        private static bool HasRootFolderForMediaType(NzbDrone.Core.Books.Author author, BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook
                ? author.EbookRootFolderPath.IsNotNullOrWhiteSpace()
                : author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace();
        }

        private static string ToMediaTypeName(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
        }

        private sealed class SiblingToggleInfo
        {
            public int? SiblingBookId { get; set; }
            public string SiblingMediaType { get; set; }
            public bool SiblingToggleEnabled { get; set; }
            public string SiblingToggleDisabledReason { get; set; }
        }

    }
}
