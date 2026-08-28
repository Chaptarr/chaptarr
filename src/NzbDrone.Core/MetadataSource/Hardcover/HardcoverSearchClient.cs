using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.MetadataSource.Hardcover
{
    public interface IHardcoverSearchClient
    {
        List<object> Search(string query);
    }

    public class HardcoverSearchClient : IHardcoverSearchClient
    {
        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        
        private const string HARDCOVER_ENDPOINT = "https://api.hardcover.app/v1/graphql";
        private const int TIMEOUT_SECONDS = 8;
        private const int MAX_RESULTS_PER_TYPE = 10;

        // Hardcover rejects GraphQL operations containing more than five top-level fields.
        private const int MAX_AUTHOR_BOOK_FIELDS_PER_OPERATION = 5;

        // Hardcover allows only one top-level search field per GraphQL operation.
        private const string SEARCH_QUERY = @"
            query ChaptarrSearch($q: String!, $query_type: String!, $limit: Int!, $page: Int!) {
                search(query: $q, query_type: $query_type, per_page: $limit, page: $page) {
                    results
                }
            }";

        private static readonly (string QueryType, string ResponseProperty)[] SearchTargets =
        {
            ("Author", "authors"),
            ("Book", "books"),
            ("Series", "series")
        };

        private const string ENRICHMENT_QUERY = @"
            query SearchEnrichment($author_ids: [Int!]!, $series_ids: [Int!]!) {
                authors(where: { id: { _in: $author_ids } }) {
                    id
                    bio
                    born_date
                    death_date
                    image { url }
                }
                series(where: { id: { _in: $series_ids } }) {
                    id
                    primary_book_series: book_series(where: { featured: { _eq: true } }, limit: 3, order_by: { position: asc }) {
                        book { image { url } }
                    }
                    book_series(limit: 3, order_by: { position: asc }) {
                        book { image { url } }
                    }
                }
            }";

        private const string AUTHOR_BOOKS_SELECTION = @"
                    id
                    title
                    subtitle
                    description
                    rating
                    ratings_count
                    pages
                    release_date
                    image { url }
                    contributions(limit: 30) {
                        author_id
                        contribution
                        author { id name }
                    }";

        public HardcoverSearchClient(IHttpClient httpClient, IConfigService configService)
        {
            _httpClient = httpClient;
            _configService = configService;
            _logger = LogManager.GetCurrentClassLogger();
        }

        public List<object> Search(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return new List<object>();
            }

            if (!_configService.HardcoverEnabled || string.IsNullOrEmpty(_configService.HardcoverApiToken))
            {
                _logger.Debug("Hardcover search skipped - not enabled or no token configured");
                return null;
            }

            try
            {
                // Trim and limit search term length
                var cleanedTerm = searchTerm.Trim();
                if (cleanedTerm.Length > 200)
                {
                    cleanedTerm = cleanedTerm.Substring(0, 200);
                }

                var results = ExecuteSearch(cleanedTerm);

                if (results == null)
                {
                    _logger.Warn($"Hardcover search failed for '{cleanedTerm}'");
                    return null;
                }

                _logger.Debug($"Hardcover search successful: {results.Count} total results for '{cleanedTerm}'");
                return results;
            }
            catch (NzbDroneClientException ex)
            {
                _logger.Error($"Hardcover search failed for '{searchTerm}': {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Hardcover search failed for '{searchTerm}'");
                return null;
            }
        }

        private List<object> ExecuteSearch(string query)
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // Hardcover allows at most one `search` root per request, so each
            // target must be its own single-operation request.
            var searchPayloads = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in SearchTargets)
            {
                var jsonContent = JsonSerializer.Serialize(new
                {
                    query = SEARCH_QUERY,
                    variables = new
                    {
                        q = query,
                        query_type = target.QueryType,
                        limit = MAX_RESULTS_PER_TYPE,
                        page = 1
                    }
                }, jsonOptions);

                var request = new HttpRequestBuilder(HARDCOVER_ENDPOINT)
                    .SetHeader("Content-Type", "application/json")
                    .SetHeader("Accept", "application/json")
                    .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                    .Build();

                request.Method = HttpMethod.Post;
                request.SetContent(jsonContent);

                // Add Authorization AFTER building to avoid duplication
                request.Headers.Add("Authorization", $"Bearer {_configService.HardcoverApiToken}");

                var response = ExecuteWithRetry(request, out var failureDetail);
                if (response == null)
                {
                    throw new NzbDroneClientException(
                        HttpStatusCode.ServiceUnavailable,
                        $"Hardcover {target.QueryType} search failed: {failureDetail ?? "no response"}");
                }

                try
                {
                    using var document = JsonDocument.Parse(response.Content);
                    if (!TryExtractSearchPayload(target.QueryType, document.RootElement, out var searchPayload))
                    {
                        throw new NzbDroneClientException(
                            HttpStatusCode.ServiceUnavailable,
                            $"Hardcover {target.QueryType} search returned an unusable response: {TruncateForError(response.Content)}");
                    }

                    searchPayloads[target.ResponseProperty] = searchPayload;
                }
                catch (JsonException ex)
                {
                    throw new NzbDroneClientException(
                        HttpStatusCode.ServiceUnavailable,
                        $"Hardcover {target.QueryType} search returned unparseable JSON ({ex.Message}): {TruncateForError(response.Content)}");
                }
            }

            var combinedContent = JsonSerializer.Serialize(new { data = searchPayloads }, jsonOptions);
            return ParseSearchResponse(query, combinedContent, jsonOptions);
        }

        private static string TruncateForError(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return "(empty body)";
            }

            return content.Length <= 500 ? content : content.Substring(0, 500) + "…";
        }

        private bool TryExtractSearchPayload(string queryType, JsonElement root, out JsonElement searchPayload)
        {
            searchPayload = default;

            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
            {
                var hasAuthError = false;
                foreach (var error in errorsElement.EnumerateArray())
                {
                    var message = error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString()
                            : error.GetRawText();

                    _logger.Error($"Hardcover {queryType} search GraphQL error: {message}");

                    if (error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("extensions", out var extensions) &&
                        extensions.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.String &&
                        code.GetString() == "UNAUTHENTICATED")
                    {
                        hasAuthError = true;
                    }
                }

                if (hasAuthError)
                {
                    _logger.Warn("Hardcover GraphQL authentication failed - token invalid");
                }

                // Do not silently merge partial operation data with successful result types.
                return false;
            }

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("search", out var search) ||
                search.ValueKind != JsonValueKind.Object)
            {
                _logger.Error($"Hardcover {queryType} search response missing data.search");
                return false;
            }

            searchPayload = search.Clone();
            return true;
        }

        private HttpResponse ExecuteWithRetry(HttpRequest request)
        {
            return ExecuteWithRetry(request, out _);
        }

        private HttpResponse ExecuteWithRetry(HttpRequest request, out string failureDetail)
        {
            failureDetail = null;
            var timeout = TimeSpan.FromSeconds(TIMEOUT_SECONDS);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                HttpResponse response;

                try
                {
                    request.RequestTimeout = timeout;
                    response = _httpClient.Execute(request);
                }
                catch (HttpException ex)
                {
                    response = ex.Response;
                    if (response == null)
                    {
                        _logger.Warn($"Hardcover API request failed without a response (attempt {attempt}/2): {ex.Message}");
                        failureDetail = $"request failed without a response: {ex.Message}";
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Hardcover API request failed (attempt {attempt}/2): {ex.Message}");
                    failureDetail = $"request failed: {ex.Message}";
                    if (attempt == 1 && IsRetryableException(ex))
                    {
                        continue;
                    }

                    return null;
                }

                if (!response.HasHttpError)
                {
                    return response;
                }

                failureDetail = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {TruncateForError(response.Content)}";

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.Warn("Hardcover API returned 401 - invalid or expired token");
                    return null;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.Warn($"Hardcover API rate limit hit (429) - attempt {attempt}/2");
                    if (attempt == 1)
                    {
                        continue;
                    }

                    return null;
                }

                if ((int)response.StatusCode >= 500)
                {
                    _logger.Warn($"Hardcover API server error ({response.StatusCode}) - attempt {attempt}/2");
                    if (attempt == 1)
                    {
                        continue;
                    }

                    return null;
                }

                // Contract, authorization, and other client errors are not transient.
                _logger.Error($"Hardcover API error: {failureDetail}");
                return null;
            }

            return null;
        }

        private bool IsRetryableException(Exception ex)
        {
            // Retry on network timeouts and temporary connection issues
            return ex is TimeoutException || 
                   ex is HttpRequestException ||
                   ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
        }

        private List<object> ParseSearchResponse(string query, string responseContent, JsonSerializerOptions jsonOptions)
        {
            try
            {
                using var document = JsonDocument.Parse(responseContent);
                var root = document.RootElement;
                
                // Check for GraphQL errors
                if (root.TryGetProperty("errors", out var errorsElement))
                {
                    bool hasAuthError = false;
                    foreach (var error in errorsElement.EnumerateArray())
                    {
                        var message = error.GetProperty("message").GetString();
                        _logger.Error($"Hardcover GraphQL error: {message}");
                        
                        // Check for authentication errors in extensions
                        if (error.TryGetProperty("extensions", out var extensions) &&
                            extensions.TryGetProperty("code", out var code) &&
                            code.GetString() == "UNAUTHENTICATED")
                        {
                            _logger.Warn("Hardcover GraphQL authentication failed - token invalid");
                            hasAuthError = true;
                        }
                    }
                    
                    // Only fail completely on auth errors; otherwise try to parse data
                    if (hasAuthError)
                    {
                        return null;
                    }
                }

                if (!root.TryGetProperty("data", out var data))
                {
                    _logger.Error("Hardcover response missing data field");
                    return null;
                }

                var authors = new List<HardcoverAuthorResult>();
                var books = new List<HardcoverBookResult>();
                var seriesResults = new List<HardcoverSeriesResult>();

                // Parse authors
                if (data.TryGetProperty("authors", out var authorsElement) &&
                    authorsElement.TryGetProperty("results", out var authorResultsObj) &&
                    authorResultsObj.TryGetProperty("hits", out var authorHits))
                {
                    foreach (var hit in authorHits.EnumerateArray())
                    {
                        if (hit.TryGetProperty("document", out var documentElement))
                        {
                            var author = ParseAuthor(documentElement);
                            if (author != null)
                            {
                                author.SearchScore = GetUnsignedLongValue(hit, "text_match");
                                authors.Add(author);
                            }
                        }
                    }
                }

                // Parse books
                if (data.TryGetProperty("books", out var booksElement) &&
                    booksElement.TryGetProperty("results", out var bookResultsObj) &&
                    bookResultsObj.TryGetProperty("hits", out var bookHits))
                {
                    foreach (var hit in bookHits.EnumerateArray())
                    {
                        if (hit.TryGetProperty("document", out var documentElement))
                        {
                            var book = ParseBook(documentElement);
                            if (book != null)
                            {
                                book.SearchScore = GetUnsignedLongValue(hit, "text_match");
                                books.Add(book);
                            }
                        }
                    }
                }

                // Parse series
                if (data.TryGetProperty("series", out var seriesElement) &&
                    seriesElement.TryGetProperty("results", out var seriesResultsObj) &&
                    seriesResultsObj.TryGetProperty("hits", out var seriesHits))
                {
                    foreach (var hit in seriesHits.EnumerateArray())
                    {
                        if (hit.TryGetProperty("document", out var documentElement))
                        {
                            var series = ParseSeries(documentElement);
                            if (series != null)
                            {
                                series.SearchScore = GetUnsignedLongValue(hit, "text_match");
                                // Hardcover search can return placeholder series with no books attached.
                                // These show up as "Loading..." in the UI and aren't useful to add.
                                if (series.BooksCount > 0 || series.PrimaryBooksCount > 0)
                                {
                                    seriesResults.Add(series);
                                }
                            }
                        }
                    }
                }

                var primaryBooksByAuthorId = new Dictionary<int, List<HardcoverBookResult>>();
                authors = FilterAuthorsWithPrimaryBooks(authors, jsonOptions, primaryBooksByAuthorId);

                // Pick one cross-type anchor before replacing any raw book hits. An exact, verified
                // author name wins the "books about this person" ambiguity; otherwise exact primary
                // labels and Hardcover's own text_match score decide the closest entity.
                var anchor = FindSearchAnchor(query, authors, books, seriesResults);

                // Hardcover's book search returns biographies for person-name queries. Reuse the
                // already-fetched primary-author books only when that author is the selected anchor.
                if (anchor is HardcoverAuthorResult anchorAuthor &&
                    int.TryParse(anchorAuthor.Id, out var anchorAuthorId) &&
                    primaryBooksByAuthorId.TryGetValue(anchorAuthorId, out var byAuthor) &&
                    byAuthor.Count > 0)
                {
                    books = byAuthor;
                }

                EnrichAuthorsAndSeries(authors, seriesResults, jsonOptions);

                var orderedResults = OrderSearchResultsAroundAnchor(query, anchor, authors, books, seriesResults);
                _logger.Debug("Hardcover search anchor for '{0}': {1}; ordered {2} results",
                    query,
                    DescribeSearchResult(anchor),
                    orderedResults.Count);
                return orderedResults;
            }
            catch (JsonException ex)
            {
                _logger.Error(ex, $"Failed to parse Hardcover response: {ex.Message}");
                return null;
            }
        }

        private List<HardcoverAuthorResult> FilterAuthorsWithPrimaryBooks(
            List<HardcoverAuthorResult> authors,
            JsonSerializerOptions jsonOptions,
            Dictionary<int, List<HardcoverBookResult>> primaryBooksByAuthorId)
        {
            if (authors == null || authors.Count == 0)
            {
                return authors ?? new List<HardcoverAuthorResult>();
            }

            var candidates = new List<(HardcoverAuthorResult Author, int AuthorId)>();
            foreach (var author in authors)
            {
                if (author == null || !int.TryParse(author.Id, out var authorId))
                {
                    continue;
                }

                if (HardcoverAuthorIdentity.IsLikelyMultiPerson(author.Name))
                {
                    _logger.Debug("Dropping Hardcover author search hit {0} ({1}) because it looks like a combined multi-person identity", author.Name, author.Id);
                    continue;
                }

                candidates.Add((author, authorId));
            }

            var booksByAuthorId = FetchBooksByAuthors(candidates.Select(candidate => candidate.AuthorId), jsonOptions);
            if (booksByAuthorId == null)
            {
                _logger.Debug("Keeping Hardcover author search hits because the primary-book batch query failed");
                return candidates.Select(candidate => candidate.Author).ToList();
            }

            var filtered = new List<HardcoverAuthorResult>();
            foreach (var candidate in candidates)
            {
                if (booksByAuthorId.TryGetValue(candidate.AuthorId, out var books) && books.Count > 0)
                {
                    primaryBooksByAuthorId[candidate.AuthorId] = books;
                    filtered.Add(candidate.Author);
                    continue;
                }

                _logger.Debug("Dropping Hardcover author search hit {0} ({1}) because it has no primary-author books", candidate.Author.Name, candidate.Author.Id);
            }

            return filtered;
        }

        private object FindSearchAnchor(
            string query,
            List<HardcoverAuthorResult> authors,
            List<HardcoverBookResult> books,
            List<HardcoverSeriesResult> series)
        {
            var candidates = BuildSearchCandidates(query, authors, books, series);

            // Person-name searches are the one cross-type ambiguity Hardcover cannot resolve by
            // text_match alone: biographies titled with the person's exact name receive the same
            // score. Authors reaching this point have primary-author books, so an exact name is
            // authoritative enough to anchor the result graph.
            var exactAuthor = candidates
                .Where(candidate => HasMultipleQueryTokens(query) &&
                                    candidate.Type == SearchEntityType.Author &&
                                    candidate.ExactPrimaryLabel)
                .OrderByDescending(candidate => candidate.ProviderScore)
                .ThenBy(candidate => candidate.ProviderRank)
                .FirstOrDefault();

            if (exactAuthor != null)
            {
                return exactAuthor.Result;
            }

            return candidates
                .OrderByDescending(candidate => candidate.ExactPrimaryLabel)
                .ThenByDescending(candidate => candidate.ProviderScore)
                .ThenByDescending(candidate => GetAnchorTypePriority(candidate.Type))
                .ThenBy(candidate => candidate.ProviderRank)
                .Select(candidate => candidate.Result)
                .FirstOrDefault();
        }

        private List<object> OrderSearchResultsAroundAnchor(
            string query,
            object anchor,
            List<HardcoverAuthorResult> authors,
            List<HardcoverBookResult> books,
            List<HardcoverSeriesResult> series)
        {
            var ordered = new List<object>();
            var providerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unkeyedResults = new HashSet<object>();

            void Add(object result)
            {
                if (result == null)
                {
                    return;
                }

                var key = GetSearchResultKey(result);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (!providerKeys.Add(key))
                    {
                        return;
                    }
                }
                else if (!unkeyedResults.Add(result))
                {
                    return;
                }

                ordered.Add(result);
            }

            switch (anchor)
            {
                case HardcoverAuthorResult anchorAuthor:
                    Add(anchorAuthor);
                    foreach (var book in books.Where(book => ContainsProviderId(book?.AuthorIds, anchorAuthor.Id)))
                    {
                        Add(book);
                    }

                    foreach (var seriesResult in series.Where(seriesResult => ProviderIdEquals(seriesResult?.AuthorId, anchorAuthor.Id)))
                    {
                        Add(seriesResult);
                    }
                    break;

                case HardcoverBookResult anchorBook:
                    Add(anchorBook);
                    foreach (var authorId in anchorBook.AuthorIds ?? Array.Empty<string>())
                    {
                        foreach (var author in authors.Where(author => ProviderIdEquals(author?.Id, authorId)))
                        {
                            Add(author);
                        }
                    }

                    foreach (var seriesId in anchorBook.SeriesIds ?? Array.Empty<string>())
                    {
                        foreach (var seriesResult in series.Where(seriesResult => ProviderIdEquals(seriesResult?.Id, seriesId)))
                        {
                            Add(seriesResult);
                        }
                    }

                    foreach (var relatedBook in books.Where(book =>
                                 !ReferenceEquals(book, anchorBook) &&
                                 (SharesProviderId(book?.AuthorIds, anchorBook.AuthorIds) ||
                                  SharesProviderId(book?.SeriesIds, anchorBook.SeriesIds))))
                    {
                        Add(relatedBook);
                    }
                    break;

                case HardcoverSeriesResult anchorSeries:
                    Add(anchorSeries);
                    foreach (var book in books.Where(book => ContainsProviderId(book?.SeriesIds, anchorSeries.Id)))
                    {
                        Add(book);
                    }

                    foreach (var author in authors.Where(author => ProviderIdEquals(author?.Id, anchorSeries.AuthorId)))
                    {
                        Add(author);
                    }
                    break;
            }

            // Everything not directly connected to the anchor remains available in exact-label,
            // then Hardcover cross-type text_match order instead of Authors -> Books -> Series.
            foreach (var candidate in BuildSearchCandidates(query, authors, books, series)
                         .OrderByDescending(candidate => candidate.ExactPrimaryLabel)
                         .ThenByDescending(candidate => candidate.ProviderScore)
                         .ThenByDescending(candidate => GetAnchorTypePriority(candidate.Type))
                         .ThenBy(candidate => candidate.ProviderRank))
            {
                Add(candidate.Result);
            }

            return ordered;
        }

        private List<SearchCandidate> BuildSearchCandidates(
            string query,
            List<HardcoverAuthorResult> authors,
            List<HardcoverBookResult> books,
            List<HardcoverSeriesResult> series)
        {
            var candidates = new List<SearchCandidate>();

            foreach (var pair in (authors ?? new List<HardcoverAuthorResult>()).Select((author, index) => (author, index)))
            {
                var labels = new[] { pair.author?.Name }
                    .Concat(pair.author?.AlternateNames ?? Array.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .ToList();
                candidates.Add(new SearchCandidate(
                    pair.author,
                    SearchEntityType.Author,
                    pair.author?.SearchScore ?? 0,
                    pair.index,
                    labels.Any(label => IsExactCompactMatch(query, label))));
            }

            foreach (var pair in (books ?? new List<HardcoverBookResult>()).Select((book, index) => (book, index)))
            {
                candidates.Add(new SearchCandidate(
                    pair.book,
                    SearchEntityType.Book,
                    pair.book?.SearchScore ?? 0,
                    pair.index,
                    IsExactCompactMatch(query, pair.book?.Title)));
            }

            foreach (var pair in (series ?? new List<HardcoverSeriesResult>()).Select((seriesResult, index) => (seriesResult, index)))
            {
                candidates.Add(new SearchCandidate(
                    pair.seriesResult,
                    SearchEntityType.Series,
                    pair.seriesResult?.SearchScore ?? 0,
                    pair.index,
                    IsExactCompactMatch(query, pair.seriesResult?.Name)));
            }

            return candidates.Where(candidate => candidate.Result != null).ToList();
        }

        private bool HasMultipleQueryTokens(string query)
        {
            return !string.IsNullOrWhiteSpace(query) &&
                   query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        }

        private bool IsExactCompactMatch(string query, string candidate)
        {
            var normalizedQuery = NormalizeCompact(query);
            return normalizedQuery.Length > 0 && normalizedQuery == NormalizeCompact(candidate);
        }

        private string NormalizeCompact(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private int GetAnchorTypePriority(SearchEntityType type)
        {
            return type switch
            {
                SearchEntityType.Book => 3,
                SearchEntityType.Series => 2,
                _ => 1
            };
        }

        private bool ContainsProviderId(IEnumerable<string> ids, string expectedId)
        {
            return ids?.Any(id => ProviderIdEquals(id, expectedId)) == true;
        }

        private bool SharesProviderId(IEnumerable<string> left, IEnumerable<string> right)
        {
            var rightIds = (right ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return rightIds.Count > 0 &&
                   (left ?? Array.Empty<string>()).Any(id => !string.IsNullOrWhiteSpace(id) && rightIds.Contains(id.Trim()));
        }

        private bool ProviderIdEquals(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private string GetSearchResultKey(object result)
        {
            return result switch
            {
                HardcoverAuthorResult author when !string.IsNullOrWhiteSpace(author.Id) => $"author:{author.Id.Trim()}",
                HardcoverBookResult book when !string.IsNullOrWhiteSpace(book.Id) => $"book:{book.Id.Trim()}",
                HardcoverSeriesResult series when !string.IsNullOrWhiteSpace(series.Id) => $"series:{series.Id.Trim()}",
                _ => null
            };
        }

        private string DescribeSearchResult(object result)
        {
            return result switch
            {
                HardcoverAuthorResult author => $"author {author.Id} '{author.Name}'",
                HardcoverBookResult book => $"book {book.Id} '{book.Title}'",
                HardcoverSeriesResult series => $"series {series.Id} '{series.Name}'",
                _ => "none"
            };
        }

        private enum SearchEntityType
        {
            Author,
            Book,
            Series
        }

        private sealed class SearchCandidate
        {
            public SearchCandidate(
                object result,
                SearchEntityType type,
                ulong providerScore,
                int providerRank,
                bool exactPrimaryLabel)
            {
                Result = result;
                Type = type;
                ProviderScore = providerScore;
                ProviderRank = providerRank;
                ExactPrimaryLabel = exactPrimaryLabel;
            }

            public object Result { get; }
            public SearchEntityType Type { get; }
            public ulong ProviderScore { get; }
            public int ProviderRank { get; }
            public bool ExactPrimaryLabel { get; }
        }

        private Dictionary<int, List<HardcoverBookResult>> FetchBooksByAuthors(IEnumerable<int> authorIds, JsonSerializerOptions jsonOptions)
        {
            var ids = authorIds?
                .Where(id => id > 0)
                .Distinct()
                .Take(MAX_RESULTS_PER_TYPE)
                .ToList() ?? new List<int>();

            if (ids.Count == 0)
            {
                return new Dictionary<int, List<HardcoverBookResult>>();
            }

            try
            {
                // Hardcover counts top-level roots across every operation in one
                // request, so each <=5-root chunk must be its own request.
                var authorIdChunks = ids
                    .Chunk(MAX_AUTHOR_BOOK_FIELDS_PER_OPERATION)
                    .Select(chunk => chunk.ToList())
                    .ToList();

                var results = ids.ToDictionary(id => id, _ => new List<HardcoverBookResult>());
                foreach (var chunk in authorIdChunks)
                {
                    var jsonContent = JsonSerializer.Serialize(new
                    {
                        query = BuildAuthorBooksBatchQuery(chunk),
                        variables = new
                        {
                            limit = MAX_RESULTS_PER_TYPE
                        }
                    }, jsonOptions);

                    var request = new HttpRequestBuilder(HARDCOVER_ENDPOINT)
                        .SetHeader("Content-Type", "application/json")
                        .SetHeader("Accept", "application/json")
                        .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                        .Build();

                    request.Method = HttpMethod.Post;
                    request.SetContent(jsonContent);
                    request.Headers.Add("Authorization", $"Bearer {_configService.HardcoverApiToken}");

                    var response = ExecuteWithRetry(request);
                    if (response == null || response.HasHttpError || string.IsNullOrWhiteSpace(response.Content))
                    {
                        return null;
                    }

                    using var document = JsonDocument.Parse(response.Content);
                    var operation = document.RootElement;
                    if (operation.TryGetProperty("errors", out var errors))
                    {
                        _logger.Debug("Hardcover author-books operation failed: {0}", errors.GetRawText());
                        return null;
                    }

                    if (!operation.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    for (var authorIndex = 0; authorIndex < chunk.Count; authorIndex++)
                    {
                        var authorId = chunk[authorIndex];
                        if (!data.TryGetProperty(GetAuthorBooksAlias(authorIndex), out var booksArray) || booksArray.ValueKind != JsonValueKind.Array)
                        {
                            return null;
                        }

                        foreach (var bookEl in booksArray.EnumerateArray())
                        {
                            var primary = GetBestPrimaryAuthorContributorFromGraphQL(bookEl);
                            if (!string.Equals(primary.Id, authorId.ToString(), StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var book = ParseGraphQLBook(bookEl);
                            if (book != null)
                            {
                                results[authorId].Add(book);
                            }
                        }
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Hardcover author-books query failed");
                return null;
            }
        }

        private static string BuildAuthorBooksBatchQuery(List<int> authorIds)
        {
            var selections = authorIds.Select((authorId, index) => $@"
                {GetAuthorBooksAlias(index)}: books(
                    where: {{
                        book_status_id: {{ _eq: 1 }}
                        is_partial_book: {{ _neq: true }}
                        contributions: {{
                            author_id: {{ _eq: {authorId} }}
                        }}
                    }}
                    order_by: {{ ratings_count: desc }}
                    limit: $limit
                ) {{
{AUTHOR_BOOKS_SELECTION}
                }}");

            return $@"
            query BooksByAuthors($limit: Int!) {{
{string.Join(Environment.NewLine, selections)}
            }}";
        }

        private static string GetAuthorBooksAlias(int index)
        {
            return $"author{index}";
        }

        private HardcoverBookResult ParseGraphQLBook(JsonElement element)
        {
            try
            {
                var id = GetStringOrNumberValue(element, "id");
                var title = GetStringValue(element, "title");
                var authorArrays = BuildAuthorArrays(GetPrimaryAuthorContributorsFromGraphQL(element));

                return new HardcoverBookResult
                {
                    Id = id,
                    Title = title,
                    Subtitle = GetStringValue(element, "subtitle"),
                    Description = GetStringValue(element, "description"),
                    AuthorNames = authorArrays.AuthorNames,
                    AuthorIds = authorArrays.AuthorIds,
                    SeriesIds = Array.Empty<string>(),
                    SeriesNames = new string[0],
                    Isbns = new string[0],
                    Rating = GetFloatValue(element, "rating"),
                    Pages = GetIntValue(element, "pages"),
                    ReleaseDate = GetStringValue(element, "release_date"),
                    ImageUrl = GetImageUrl(element)
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to parse Hardcover GraphQL book");
                return null;
            }
        }

        // Returns (id, name) pairs for primary-author contributors, in original
        // contribution order. Pairing keeps id+name from the SAME contribution row so role-filtering
        // can't silently swap them. Used by both the GraphQL-by-author path (inner contributions(...))
        // and the typesense search-doc path (document.contributions[]).
        internal static List<(string Id, string Name)> GetPrimaryAuthorContributorsFromGraphQL(JsonElement element)
        {
            var pairs = new List<(string Id, string Name)>();
            var seen = new HashSet<string>();

            if (!element.TryGetProperty("contributions", out var contributions) ||
                contributions.ValueKind != JsonValueKind.Array)
            {
                return pairs;
            }

            foreach (var contribution in contributions.EnumerateArray())
            {
                string role = null;
                if (contribution.TryGetProperty("contribution", out var roleEl) &&
                    roleEl.ValueKind == JsonValueKind.String)
                {
                    role = roleEl.GetString();
                }

                if (!HardcoverContributionRoles.IsPrimaryAuthor(role))
                {
                    continue;
                }

                string id = null;
                if (contribution.TryGetProperty("author", out var author) &&
                    author.ValueKind == JsonValueKind.Object &&
                    author.TryGetProperty("id", out var authorIdEl))
                {
                    if (authorIdEl.ValueKind == JsonValueKind.Number && authorIdEl.TryGetInt64(out var i64))
                    {
                        id = i64.ToString();
                    }
                    else if (authorIdEl.ValueKind == JsonValueKind.String)
                    {
                        id = authorIdEl.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(id) &&
                    contribution.TryGetProperty("author_id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var i64))
                    {
                        id = i64.ToString();
                    }
                    else if (idEl.ValueKind == JsonValueKind.String)
                    {
                        id = idEl.GetString();
                    }
                }

                string name = null;
                if (contribution.TryGetProperty("author", out var authorObj) &&
                    authorObj.ValueKind == JsonValueKind.Object &&
                    authorObj.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    name = nameEl.GetString();
                }

                var dedupKey = id ?? ("name:" + (name ?? string.Empty));
                if (string.IsNullOrWhiteSpace(dedupKey) || !seen.Add(dedupKey))
                {
                    continue;
                }

                pairs.Add((id, name));
            }

            return pairs;
        }

        internal static (string Id, string Name) GetBestPrimaryAuthorContributorFromGraphQL(JsonElement element)
        {
            if (!element.TryGetProperty("contributions", out var contributions) ||
                contributions.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            (int Priority, int Index, string Id, string Name)? best = null;
            var idx = 0;
            foreach (var contribution in contributions.EnumerateArray())
            {
                string role = null;
                if (contribution.TryGetProperty("contribution", out var roleEl) &&
                    roleEl.ValueKind == JsonValueKind.String)
                {
                    role = roleEl.GetString();
                }

                if (!HardcoverContributionRoles.TryGetPrimaryPriority(role, out var priority))
                {
                    idx++;
                    continue;
                }

                var id = GetContributorAuthorId(contribution);
                var name = GetContributorAuthorName(contribution);
                if (string.IsNullOrWhiteSpace(id))
                {
                    idx++;
                    continue;
                }

                if (best == null ||
                    priority < best.Value.Priority ||
                    (priority == best.Value.Priority && idx < best.Value.Index))
                {
                    best = (priority, idx, id, name);
                }

                idx++;
            }

            return best == null ? (null, null) : (best.Value.Id, best.Value.Name);
        }

        private static string GetContributorAuthorId(JsonElement contribution)
        {
            if (contribution.TryGetProperty("author", out var author) &&
                author.ValueKind == JsonValueKind.Object &&
                author.TryGetProperty("id", out var authorIdEl))
            {
                if (authorIdEl.ValueKind == JsonValueKind.Number && authorIdEl.TryGetInt64(out var i64))
                {
                    return i64.ToString();
                }
                if (authorIdEl.ValueKind == JsonValueKind.String)
                {
                    return authorIdEl.GetString();
                }
            }

            if (contribution.TryGetProperty("author_id", out var idEl))
            {
                if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var i64))
                {
                    return i64.ToString();
                }
                if (idEl.ValueKind == JsonValueKind.String)
                {
                    return idEl.GetString();
                }
            }

            return null;
        }

        private static string GetContributorAuthorName(JsonElement contribution)
        {
            if (contribution.TryGetProperty("author", out var authorObj) &&
                authorObj.ValueKind == JsonValueKind.Object &&
                authorObj.TryGetProperty("name", out var nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
            {
                return nameEl.GetString();
            }

            return null;
        }

        internal static (string[] AuthorNames, string[] AuthorIds) BuildAuthorArrays(IEnumerable<(string Id, string Name)> pairs)
        {
            var validPairs = pairs
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            return (
                validPairs.Select(p => p.Name).ToArray(),
                validPairs.Select(p => p.Id).ToArray());
        }

        private string GetStringOrNumberValue(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt64(out var i64))
                {
                    return i64.ToString();
                }
                if (prop.TryGetDouble(out var dbl))
                {
                    return dbl.ToString();
                }
            }

            return null;
        }

        private void EnrichAuthorsAndSeries(List<HardcoverAuthorResult> authors, List<HardcoverSeriesResult> series, JsonSerializerOptions jsonOptions)
        {
            if ((authors == null || authors.Count == 0) && (series == null || series.Count == 0))
            {
                return;
            }

            // Search results don't include author bios or series cover art; enrich via a single batched GraphQL query.
            var authorIds = (authors ?? new List<HardcoverAuthorResult>())
                .Select(a => a?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id) && int.TryParse(id, out _))
                .Select(int.Parse)
                .Distinct()
                .Take(MAX_RESULTS_PER_TYPE)
                .ToList();

            var seriesIds = (series ?? new List<HardcoverSeriesResult>())
                .Select(s => s?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id) && int.TryParse(id, out _))
                .Select(int.Parse)
                .Distinct()
                .Take(MAX_RESULTS_PER_TYPE)
                .ToList();

            if (authorIds.Count == 0 && seriesIds.Count == 0)
            {
                return;
            }

            try
            {
                var graphqlRequest = new
                {
                    query = ENRICHMENT_QUERY,
                    variables = new
                    {
                        author_ids = authorIds,
                        series_ids = seriesIds
                    }
                };

                var jsonContent = JsonSerializer.Serialize(graphqlRequest, jsonOptions);

	                var request = new HttpRequestBuilder(HARDCOVER_ENDPOINT)
	                    .SetHeader("Content-Type", "application/json")
	                    .SetHeader("Accept", "application/json")
	                    .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
	                    .Build();

                request.Method = HttpMethod.Post;
                request.SetContent(jsonContent);
                request.Headers.Add("Authorization", $"Bearer {_configService.HardcoverApiToken}");

                var response = ExecuteWithRetry(request);
                if (response == null || response.HasHttpError || string.IsNullOrWhiteSpace(response.Content))
                {
                    return;
                }

                using var document = JsonDocument.Parse(response.Content);
                if (!document.RootElement.TryGetProperty("data", out var data))
                {
                    return;
                }

                if (authors?.Count > 0 &&
                    data.TryGetProperty("authors", out var authorsArray) &&
                    authorsArray.ValueKind == JsonValueKind.Array)
                {
                    var byId = new Dictionary<string, (string bio, string imageUrl, string bornDate, string deathDate)>();
                    foreach (var authorElement in authorsArray.EnumerateArray())
                    {
                        if (!authorElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        var id = idElement.GetInt32().ToString();
                        var bio = GetStringValue(authorElement, "bio");
                        var imageUrl = GetImageUrl(authorElement);
                        var bornDate = GetStringValue(authorElement, "born_date");
                        var deathDate = GetStringValue(authorElement, "death_date");
                        byId[id] = (bio, imageUrl, bornDate, deathDate);
                    }

                    foreach (var author in authors)
                    {
                        if (author == null || string.IsNullOrWhiteSpace(author.Id))
                        {
                            continue;
                        }

                        if (!byId.TryGetValue(author.Id, out var details))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(author.Bio) && !string.IsNullOrWhiteSpace(details.bio))
                        {
                            author.Bio = details.bio;
                        }

                        if (string.IsNullOrWhiteSpace(author.ImageUrl) && !string.IsNullOrWhiteSpace(details.imageUrl))
                        {
                            author.ImageUrl = details.imageUrl;
                        }

                        if (string.IsNullOrWhiteSpace(author.BornDate) && !string.IsNullOrWhiteSpace(details.bornDate))
                        {
                            author.BornDate = details.bornDate;
                        }

                        if (string.IsNullOrWhiteSpace(author.DeathDate) && !string.IsNullOrWhiteSpace(details.deathDate))
                        {
                            author.DeathDate = details.deathDate;
                        }
                    }
                }

                if (series?.Count > 0 &&
                    data.TryGetProperty("series", out var seriesArray) &&
                    seriesArray.ValueKind == JsonValueKind.Array)
                {
                    var coverUrlsById = new Dictionary<string, string[]>();
                    foreach (var seriesElement in seriesArray.EnumerateArray())
                    {
                        if (!seriesElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        var id = idElement.GetInt32().ToString();
                        var coverUrls = new List<string>();

                        // Prefer featured (primary) books for cover art; fall back to the first books if none are featured.
                        JsonElement bookSeries = default;
                        bool hasPrimarySeries = seriesElement.TryGetProperty("primary_book_series", out var primarySeries) &&
                                                primarySeries.ValueKind == JsonValueKind.Array &&
                                                primarySeries.GetArrayLength() > 0;

                        if (hasPrimarySeries)
                        {
                            bookSeries = primarySeries;
                        }
                        else if (seriesElement.TryGetProperty("book_series", out var fallbackSeries) && fallbackSeries.ValueKind == JsonValueKind.Array)
                        {
                            bookSeries = fallbackSeries;
                        }

                        if (bookSeries.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var bookSeriesItem in bookSeries.EnumerateArray())
                            {
                                if (!bookSeriesItem.TryGetProperty("book", out var bookElement) || bookElement.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                var coverUrl = GetImageUrl(bookElement);
                                if (!string.IsNullOrWhiteSpace(coverUrl))
                                {
                                    coverUrls.Add(coverUrl);
                                }
                            }
                        }

                        if (coverUrls.Count > 0)
                        {
                            coverUrlsById[id] = coverUrls.ToArray();
                        }
                    }

                    foreach (var s in series)
                    {
                        if (s == null || string.IsNullOrWhiteSpace(s.Id))
                        {
                            continue;
                        }

                        if (coverUrlsById.TryGetValue(s.Id, out var coverUrls))
                        {
                            s.CoverUrls = coverUrls;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Hardcover enrichment failed");
            }
        }

	        private HardcoverAuthorResult ParseAuthor(JsonElement element)
	        {
	            try
	            {
	                var bio = GetStringValue(element, "bio") ??
	                          GetStringValue(element, "biography") ??
	                          GetStringValue(element, "about") ??
	                          GetStringValue(element, "description");

	                return new HardcoverAuthorResult
	                {
	                    Id = GetStringValue(element, "id"),
	                    Name = GetStringValue(element, "name"),
	                    AlternateNames = GetStringArrayValue(element, "alternate_names"),
	                    Bio = bio,
	                    BooksCount = GetIntValue(element, "books_count"),
	                    Slug = GetStringValue(element, "slug"),
	                    BornDate = GetStringValue(element, "born_date"),
	                    DeathDate = GetStringValue(element, "death_date"),
	                    ImageUrl = GetImageUrl(element)
	                };
	            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to parse Hardcover author");
                return null;
            }
        }

        private HardcoverBookResult ParseBook(JsonElement element)
        {
            try
            {
                var title = GetStringValue(element, "title");

                // Build author list from contributions[].contribution role, not the flat author_names array.
                // The flat array mixes Editor/Narrator/etc in arbitrary order; for "Tasting and Smelling"
                // it lists Editor Gary K. Beauchamp before author Linda Bartoshuk, so picking [0] is wrong.
                var pairs = GetPrimaryAuthorContributorsFromGraphQL(element);

                // Fall back to flat author_names if no contributions[] in the doc (older/edge schema).
                // Don't fall back to flat names when contributions[] exists but had no author-role rows —
                // that's an editor-only / narrator-only / etc. book and we want it to surface that way.
                string[] authorNames;
                string[] authorIds;
                if (pairs.Count > 0)
                {
                    var authorArrays = BuildAuthorArrays(pairs);
                    authorNames = authorArrays.AuthorNames;
                    authorIds = authorArrays.AuthorIds;
                }
                else if (element.TryGetProperty("contributions", out var contribsEl) &&
                         contribsEl.ValueKind == JsonValueKind.Array)
                {
                    // Has contributions[] but no author-role rows → leave author info empty.
                    authorNames = Array.Empty<string>();
                    authorIds = Array.Empty<string>();
                }
                else
                {
                    authorNames = GetStringArrayValue(element, "author_names");
                    authorIds = Array.Empty<string>();
                }

                if (authorIds.Length > 0)
                {
                    _logger.Debug($"Hardcover book '{title}' author-role IDs: {string.Join(", ", authorIds)}");
                }
                else
                {
                    _logger.Debug($"Hardcover book '{title}' has no author-role contributors");
                }

                return new HardcoverBookResult
                {
                    Id = GetStringValue(element, "id"),
                    Title = title,
                    Subtitle = GetStringValue(element, "subtitle"),
                    Description = GetStringValue(element, "description"),
                    AuthorNames = authorNames,
                    AuthorIds = authorIds,
                    SeriesIds = GetStringOrNumberArrayValue(element, "series_ids"),
                    SeriesNames = GetStringArrayValue(element, "series_names"),
                    Isbns = GetStringArrayValue(element, "isbns"),
                    Rating = GetFloatValue(element, "rating"),
                    Pages = GetIntValue(element, "pages"),
                    ReleaseDate = GetStringValue(element, "release_date"),
                    ImageUrl = GetImageUrl(element)
                };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to parse Hardcover book");
                return null;
            }
        }

        private HardcoverSeriesResult ParseSeries(JsonElement element)
        {
            try
            {
                string authorId = null;
                if (element.TryGetProperty("author", out var authorElement) &&
                    authorElement.ValueKind == JsonValueKind.Object)
                {
                    authorId = GetStringOrNumberValue(authorElement, "id");
                }

                var coverUrls = new List<string>();
                if (element.TryGetProperty("books", out var booksElement) && booksElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bookElement in booksElement.EnumerateArray())
                    {
                        var coverUrl = GetImageUrl(bookElement);
                        if (!string.IsNullOrWhiteSpace(coverUrl))
                        {
                            coverUrls.Add(coverUrl);
                        }
                    }
                }

                return new HardcoverSeriesResult
                {
                    Id = GetStringValue(element, "id"),
                    Name = GetStringValue(element, "name"),
                    Slug = GetStringValue(element, "slug"),
                    Description = GetStringValue(element, "description"),
                    BooksCount = GetIntValue(element, "books_count"),
                    PrimaryBooksCount = GetIntValue(element, "primary_books_count"),
                    ReadersCount = GetIntValue(element, "readers_count"),
                    AuthorId = authorId,
                    AuthorName = GetStringValue(element, "author_name"),
                    CoverUrls = coverUrls.Take(3).ToArray()
                };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to parse Hardcover series");
                return null;
            }
        }

        // Helper methods for parsing JSON elements
        private string GetStringValue(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            return prop.GetString();
        }

        private string[] GetStringArrayValue(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            }
            return new string[0];
        }

        private string[] GetStringOrNumberArrayValue(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return prop.EnumerateArray()
                .Select(item => item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetRawText(),
                    _ => null
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private ulong GetUnsignedLongValue(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                return 0;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt64(out var numericValue))
            {
                return numericValue;
            }

            return prop.ValueKind == JsonValueKind.String && ulong.TryParse(prop.GetString(), out var stringValue)
                ? stringValue
                : 0;
        }

        private int GetIntValue(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt32()
                : 0;
        }

        private float GetFloatValue(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                {
                    return prop.GetSingle();
                }
            }
            return 0f;
        }

	        private string GetImageUrl(JsonElement element)
	        {
	            if (element.ValueKind != JsonValueKind.Object)
	            {
	                return null;
	            }

	            // Most Hardcover documents embed images as { "image": { "url": "..." } }
	            if (element.TryGetProperty("image", out var imageElement))
	            {
	                if (imageElement.ValueKind == JsonValueKind.Object)
	                {
	                    if (imageElement.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
	                    {
	                        return urlElement.GetString();
	                    }

	                    if (imageElement.TryGetProperty("image_url", out var imageUrlElement) && imageUrlElement.ValueKind == JsonValueKind.String)
	                    {
	                        return imageUrlElement.GetString();
	                    }

	                    if (imageElement.TryGetProperty("imageUrl", out var imageUrlElement2) && imageUrlElement2.ValueKind == JsonValueKind.String)
	                    {
	                        return imageUrlElement2.GetString();
	                    }
	                }

	                if (imageElement.ValueKind == JsonValueKind.String)
	                {
	                    return imageElement.GetString();
	                }
	            }

	            // Some documents may include flat string URLs
	            if (element.TryGetProperty("image_url", out var flatImageUrl) && flatImageUrl.ValueKind == JsonValueKind.String)
	            {
	                return flatImageUrl.GetString();
	            }
	            if (element.TryGetProperty("imageUrl", out var flatImageUrl2) && flatImageUrl2.ValueKind == JsonValueKind.String)
	            {
	                return flatImageUrl2.GetString();
	            }

	            return null;
	        }

    }

    // Result DTOs matching Hardcover schema
    public class HardcoverAuthorResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string[] AlternateNames { get; set; }
        public string Bio { get; set; }
        public int BooksCount { get; set; }
        public string Slug { get; set; }
        public string BornDate { get; set; }
        public string DeathDate { get; set; }
        public string ImageUrl { get; set; }
        public ulong SearchScore { get; set; }
    }

    public class HardcoverBookResult
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Description { get; set; }
        public string[] AuthorNames { get; set; }
        public string[] AuthorIds { get; set; }
        public string[] SeriesIds { get; set; }
        public string[] SeriesNames { get; set; }
        public string[] Isbns { get; set; }
        public float Rating { get; set; }
        public int Pages { get; set; }
        public string ReleaseDate { get; set; }
        public string ImageUrl { get; set; }
        public ulong SearchScore { get; set; }
    }

    public class HardcoverSeriesResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public string Description { get; set; }
        public int BooksCount { get; set; }
        public int PrimaryBooksCount { get; set; }
        public int ReadersCount { get; set; }
        public string AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string[] CoverUrls { get; set; } = Array.Empty<string>();
        public ulong SearchScore { get; set; }
    }
}
