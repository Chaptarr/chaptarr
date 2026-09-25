using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
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
using NzbDrone.SignalR;
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
        private readonly IBroadcastSignalRMessage _signalRBroadcaster;

        private readonly ICached<DownloadDecision> _downloadDecisionCache;
        private readonly ICached<CancellationTokenSource> _authorSearchCancellations;

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
                             IBroadcastSignalRMessage signalRBroadcaster)
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
            _signalRBroadcaster = signalRBroadcaster;

            PostValidator.RuleFor(s => s.IndexerId).ValidId();
            PostValidator.RuleFor(s => s.Guid).NotEmpty();

            _downloadDecisionCache = cacheManager.GetCache<DownloadDecision>(GetType(), "downloadDecisions");
            _authorSearchCancellations = cacheManager.GetCache<CancellationTokenSource>(GetType(), "authorSearchCancellations");
        }

        [HttpDelete]
        [ProducesResponseType(typeof(AuthorReleaseSearchStopResponse), 200)]
        [ProducesResponseType(400)]
        [SwaggerOperation(Description = "Requests a stop for an active grouped author search identified by its UUID searchId. The current book finishes; remaining books are not searched. isStopping confirms the request, not completion. The original GET and SignalR stream deliver the terminal result. An inactive searchId returns isStopping=false; malformed or missing IDs return 400.")]
        public AuthorReleaseSearchStopResponse StopAuthorSearch(string searchId)
        {
            if (!Guid.TryParse(searchId, out var id))
            {
                throw new BadRequestException("Stopping an author search requires a UUID searchId");
            }

            // Serialize cancellation with removal so a completed request cannot dispose a handle in use.
            lock (_authorSearchCancellations)
            {
                var cancellation = _authorSearchCancellations.Find(id.ToString());
                cancellation?.Cancel();
                return new AuthorReleaseSearchStopResponse { IsStopping = cancellation != null };
            }
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

            var decision = _downloadDecisionCache.Find(GetCacheKey(release));

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
        [ProducesResponseType(400)]
        [ProducesResponseType(409)]
        [SwaggerOperationFilter(typeof(ReleaseSearchResponseOperationFilter))]
        [SwaggerOperation(Description = "GET /api/v1/release returns ReleaseResource[] when bookId, authorId, and searchId are absent; bookId or authorId without searchId returns the flat ReleaseSearchResponse; a UUID searchId with authorId and explicit audiobook/ebook mediaType returns grouped AuthorReleaseSearchResponse results. Other searchId combinations return 400. Per-book AuthorSearchResults SignalR messages use the same grouped envelope. Scoped grabs must echo searchId and searchedBookId alongside bookId, authorId, indexerId and guid.")]
        public async Task<object> GetReleases(int? bookId, int? authorId, bool bypassFilters = false, string mediaType = null, string searchId = null)
        {
            if (searchId != null)
            {
                var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                if (bookId.HasValue || !authorId.HasValue || !parsedMediaType.HasValue || !Guid.TryParse(searchId, out _))
                {
                    throw new BadRequestException("Grouped search requires authorId, audiobook or ebook mediaType, and a UUID searchId");
                }

                return await GetGroupedAuthorReleases(authorId.Value, parsedMediaType.Value, searchId);
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

        private async Task<object> GetAuthorReleases(int authorId, bool bypassFilters, BookMediaType? mediaType)
        {
            try
            {
                var decisions = await _releaseSearchService.AuthorSearch(authorId, false, true, true, mediaType, HttpContext.RequestAborted);
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

        private async Task<AuthorReleaseSearchResponse> GetGroupedAuthorReleases(int authorId, BookMediaType mediaType, string searchId)
        {
            var response = new AuthorReleaseSearchResponse
            {
                SearchId = searchId,
                AuthorId = authorId,
                MediaType = ToMediaTypeName(mediaType)
            };
            var cancellationKey = Guid.Parse(searchId).ToString();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            lock (_authorSearchCancellations)
            {
                if (_authorSearchCancellations.Find(cancellationKey) != null)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "This searchId is already active");
                }

                _authorSearchCancellations.Set(cancellationKey, cancellation);
            }

            var cancellationToken = cancellation.Token;

            try
            {
                await _releaseSearchService.AuthorSearch(authorId, false, true, true, mediaType,
                    async books =>
                    {
                        response.BookResults = books.Select(book => new BookReleaseSearchResult
                        {
                            BookId = book.Id,
                            Title = book.Editions?.Where(edition => edition.Monitored).OrderBy(edition => edition.Id).FirstOrDefault()?.Title ?? book.Title,
                            MediaType = ToMediaTypeName(book.MediaType),
                            Status = "pending"
                        }).ToList();
                        await PublishAuthorSearchResults(response, response.BookResults);
                    },
                    async (book, decisions, error) =>
                    {
                        var group = MapBookSearchResults(book, decisions, error, response.SearchId);
                        var index = response.BookResults.FindIndex(result => result.BookId == book.Id);
                        response.BookResults[index] = group;
                        await PublishAuthorSearchResults(response, new[] { group });
                    },
                    cancellationToken);

                response.IsComplete = true;
                await PublishAuthorSearchResults(response, Array.Empty<BookReleaseSearchResult>());
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response.IsComplete = true;
                response.IsCancelled = true;
                _logger.Info("Stopped author search {0} after {1} of {2} books; completed results retained", searchId,
                    response.BookResults.Count(result => result.Status != "pending"), response.BookResults.Count);
                await PublishAuthorSearchResults(response, Array.Empty<BookReleaseSearchResult>());
                if (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    throw;
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Grouped author search could not publish a complete book result");
                var failedGroups = response.BookResults.Where(result => result.Status == "pending").ToList();
                foreach (var group in failedGroups)
                {
                    group.Status = "failed";
                }

                response.IsComplete = true;
                await PublishAuthorSearchResults(response, failedGroups);
                throw;
            }
            finally
            {
                lock (_authorSearchCancellations)
                {
                    _authorSearchCancellations.Remove(cancellationKey);
                }
            }
        }

        private BookReleaseSearchResult MapBookSearchResults(Book book, List<DownloadDecision> decisions, Exception error, string searchId)
        {
            var prioritized = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);
            var (visible, hidden, _) = MapDecisionsForInteractiveSearchWithSummary(prioritized, false, (decision, weight) =>
            {
                var resource = base.MapDecision(decision, weight);
                resource.SearchId = searchId;
                resource.SearchedBookId = book.Id;
                resource.DownloadUrl = null;
                resource.MagnetUrl = null;

                // The decision must be available before a streamed row exposes its grab action.
                _downloadDecisionCache.Set(GetCacheKey(resource), decision, TimeSpan.FromMinutes(30));
                return resource;
            });

            var rank = 0;
            foreach (var resource in visible.Concat(hidden))
            {
                resource.ReleaseWeight = rank;
                resource.Rank = ++rank;
            }

            return new BookReleaseSearchResult
            {
                BookId = book.Id,
                Title = book.Editions?.Where(edition => edition.Monitored).OrderBy(edition => edition.Id).FirstOrDefault()?.Title ?? book.Title,
                MediaType = ToMediaTypeName(book.MediaType),
                Status = error == null ? "complete" : "failed",
                Releases = visible,
                HiddenReleases = hidden
            };
        }

        private async Task PublishAuthorSearchResults(AuthorReleaseSearchResponse response, IEnumerable<BookReleaseSearchResult> groups)
        {
            try
            {
                await _signalRBroadcaster.BroadcastMessage(new SignalRMessage
                {
                    Name = "AuthorSearchResults",
                    Body = new AuthorReleaseSearchResponse
                    {
                        SearchId = response.SearchId,
                        AuthorId = response.AuthorId,
                        MediaType = response.MediaType,
                        BookResults = groups.ToList(),
                        IsComplete = response.IsComplete,
                        IsCancelled = response.IsCancelled
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to deliver author search update");
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

        private (List<ReleaseResource> results, List<ReleaseResource> hiddenResults, FilterSummary filterSummary) MapDecisionsForInteractiveSearchWithSummary(IEnumerable<DownloadDecision> decisions, bool bypassFilters = false, Func<DownloadDecision, int, ReleaseResource> mapDecision = null)
        {
            mapDecision ??= MapDecision;
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
                var shouldFilter = ShouldFilterFromInteractiveSearch(downloadDecision);
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

                    hiddenResults.Add(mapDecision(downloadDecision, hiddenResults.Count));
                    continue;
                }

                var release = mapDecision(downloadDecision, result.Count);

                result.Add(release);
            }

            foreach (var release in result.Concat(hiddenResults))
            {
                release.IsPreferredChoice = false;
            }

            var preferred = result.FirstOrDefault(release => release.Approved && release.DownloadAllowed &&
                (!release.SearchedBookId.HasValue || release.BookId == release.SearchedBookId));
            if (preferred != null)
            {
                preferred.IsPreferredChoice = true;
            }

            // Create filter summary
            var filterSummary = CreateFilterSummary(allDecisions, result, hardFilteredCount, softFilteredCount, bypassFilters);

            // Enhanced logging with filter breakdown
            _logger.Trace("MAM_SEARCH_SUMMARY: TOTAL={0}, FILTERED={1} (HARD={2}, SOFT={3}), DISPLAYED={4}, DURATIONS_FOUND={5}, NARRATORS_FOUND={6}, GRAPHIC_AUDIO={7}, BYPASS_MODE={8}", totalDecisions, filteredCount, hardFilteredCount, softFilteredCount, result.Count, durationsFound, narratorsFound, graphicAudioFound, bypassFilters);

            if (bypassFilters && softFilteredCount > 0)
            {
                _logger.Debug("FILTER_BYPASS: {0} soft-filtered release(s) are available in the hidden results and can be grabbed deliberately (visible list: {1})", softFilteredCount, result.Count);
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
                .Where(decision => ShouldFilterFromInteractiveSearch(decision).shouldFilter)
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

        internal static (bool shouldFilter, bool isHardFilter) ShouldFilterFromInteractiveSearch(DownloadDecision decision)
        {
            return InteractiveSearchVisibility.Evaluate(decision);
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
            return resource.SearchId == null
                ? string.Concat(resource.IndexerId, "_", resource.Guid)
                : $"search:{Guid.Parse(resource.SearchId):N}:book:{resource.SearchedBookId}:indexer:{resource.IndexerId}:{resource.Guid}";
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
