using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Chaptarr.Api.V1.MediaTypes;
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
using Swashbuckle.AspNetCore.Annotations;
using BadRequestException = Chaptarr.Http.REST.BadRequestException;
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
        private readonly IGroupedAuthorSearch _groupedAuthorSearch;
        private readonly InteractiveReleaseMapper _interactiveReleaseMapper;

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
                             Logger logger,
                             IGroupedAuthorSearch groupedAuthorSearch)
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
            _groupedAuthorSearch = groupedAuthorSearch;
            _interactiveReleaseMapper = new InteractiveReleaseMapper(cacheManager, logger);

            PostValidator.RuleFor(s => s.IndexerId).ValidId();
            PostValidator.RuleFor(s => s.Guid).NotEmpty();

            _downloadDecisionCache = cacheManager.GetCache<DownloadDecision>(GetType(), "downloadDecisions");
        }

        [HttpDelete]
        [ProducesResponseType(typeof(AuthorReleaseSearchStopResponse), 200)]
        [ProducesResponseType(400)]
        [SwaggerOperation(Description = "Requests a stop for an active grouped author search identified by its UUID searchId. Queued work is removed atomically (isRemoved=true); otherwise the current book finishes and remaining books are not searched. isStopping confirms a running stop request, not completion. The SignalR stream delivers the terminal result. An inactive searchId returns isStopping=false; malformed or missing IDs return 400.")]
        public AuthorReleaseSearchStopResponse StopAuthorSearch(string searchId)
        {
            if (!Guid.TryParse(searchId, out var id))
            {
                throw new BadRequestException("Stopping an author search requires a UUID searchId");
            }

            return _groupedAuthorSearch.Stop(id);
        }

        [HttpPost]
        public async Task<ActionResult<ReleaseResource>> DownloadRelease([FromBody] ReleaseResource release)
        {
            ValidateResource(release);

            if (release.SearchId != null || release.SearchedBookId.HasValue)
            {
                if (!Guid.TryParse(release.SearchId, out _) || release.SearchedBookId.GetValueOrDefault() <= 0)
                {
                    throw new BadRequestException("A scoped release requires a valid searchId and searchedBookId");
                }
            }

            var decision = release.SearchId == null
                ? _downloadDecisionCache.Find(InteractiveReleaseMapper.GetCacheKey(release))
                : _groupedAuthorSearch.GetDecision(release);

            if (decision?.RemoteBook == null)
            {
                _logger.Debug("Couldn't find requested release in cache, cache timeout probably expired.");

                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Couldn't find requested release in cache, try searching again");
            }

            var remoteBook = decision.RemoteBook;

            // A displayed search group must never redirect a grab to another book.
            if (release.SearchId != null &&
                (remoteBook.Books?.Count != 1 || remoteBook.Books[0].Id != release.SearchedBookId ||
                 release.BookId != release.SearchedBookId ||
                 (release.AuthorId.HasValue && release.AuthorId != remoteBook.Author?.Id)))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "The release does not match the requested book");
            }

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
                if (release.SearchId != null) _groupedAuthorSearch.MarkGrabbed(release);
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
        [ProducesResponseType(200)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(409)]
        [SwaggerOperationFilter(typeof(ReleaseSearchResponseOperationFilter))]
        [SwaggerOperation(Description = "GET /api/v1/release with existing=true, authorId and mediaType returns the retained grouped search or null, without querying indexers. Otherwise it returns ReleaseResource[] when bookId, authorId, and searchId are absent; bookId or authorId without searchId returns the flat ReleaseSearchResponse; a UUID searchId with authorId and explicit audiobook/ebook mediaType queues a grouped search and immediately returns an AuthorReleaseSearchResponse acknowledgement. Book results and completion arrive through SignalR; ending the GET does not cancel the search. Other searchId combinations return 400. Per-book AuthorSearchResults SignalR messages use the same grouped envelope. Scoped grabs must echo searchId and searchedBookId alongside bookId, authorId, indexerId and guid.")]
        public async Task<object> GetReleases(int? bookId, int? authorId, bool bypassFilters = false, string mediaType = null, string searchId = null, bool existing = false)
        {
            if (existing)
            {
                var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                if (bookId.HasValue || !authorId.HasValue || !parsedMediaType.HasValue || searchId != null)
                {
                    throw new BadRequestException("Existing search lookup requires authorId and audiobook or ebook mediaType, without bookId or searchId");
                }
                return _groupedAuthorSearch.GetSnapshot(authorId.Value, parsedMediaType.Value);
            }

            if (searchId != null)
            {
                var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                if (bookId.HasValue || !authorId.HasValue || !parsedMediaType.HasValue || !Guid.TryParse(searchId, out _))
                {
                    throw new BadRequestException("Grouped search requires authorId, audiobook or ebook mediaType, and a UUID searchId");
                }

                return _groupedAuthorSearch.Start(authorId.Value, parsedMediaType.Value, searchId);
            }

            if (bookId.HasValue)
            {
                return await GetBookReleases(int.Parse(Request.Query["bookId"]), bypassFilters);
            }

            if (authorId.HasValue)
            {
                return await GetAuthorReleases(int.Parse(Request.Query["authorId"]), bypassFilters, MediaTypeParameterParser.ParseOptional(mediaType));
            }

            return await GetRss();
        }

        private async Task<object> GetBookReleases(int bookId, bool bypassFilters = false)
        {
            try
            {
                var book = _bookService.GetBook(bookId);
                var author = _authorService.GetAuthor(book.AuthorId);

                book.Author = author;
                var outcome = await _releaseSearchService.BookSearchWithOutcome(book, null, true, true, true);
                var decisions = outcome.Decisions;

                var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);
                var siblingToggleInfo = BuildSiblingToggleInfo(book, author);

                var (results, hiddenResults, filterSummary) = _interactiveReleaseMapper.MapInteractiveDecisions(prioritizedDecisions, bypassFilters);

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
                    IndexersQueried = outcome.Indexers.Queried,
                    IndexersFailed = outcome.Indexers.Failed,
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

        private async Task<object> GetAuthorReleases(int authorId, bool bypassFilters, BookMediaType? mediaType)
        {
            try
            {
                var decisions = await _releaseSearchService.AuthorSearch(authorId, false, true, true, mediaType, HttpContext.RequestAborted);
                var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

                var (results, hiddenResults, filterSummary) = _interactiveReleaseMapper.MapInteractiveDecisions(prioritizedDecisions, bypassFilters);

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
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
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
            return _interactiveReleaseMapper.MapDecision(decision, initialWeight);
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
