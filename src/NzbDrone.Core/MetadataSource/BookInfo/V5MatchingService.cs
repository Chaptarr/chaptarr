using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MetadataSource.BookInfo
{
    public interface IV5MatchingService
    {
        void ProcessSeriesLinks(List<Book> books);
        List<V5MatchedAuthor> SearchV5Matching(string query, IDictionary<string, List<string>> tags, string mediaType, string filePath);
    }

    public class V5MatchingService : IV5MatchingService
    {
        private readonly IHttpClient _httpClient;
        private readonly IProvideAuthorInfo _authorInfoProxy;
        private readonly IConfigService _configService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IMatchingUploadLogger _matchingLogger;
        private readonly IMetadataServerHealthGate _metadataServerHealthGate;
        private readonly Logger _logger;

        public V5MatchingService(
            IHttpClient httpClient,
            IProvideAuthorInfo authorInfoProxy,
            IConfigService configService,
            IAuthorService authorService,
            // IAddAuthorService addAuthorService, // DEPRECATED-IDENTIFICATION
            IBookService bookService,
            IEditionService editionService,
            ISeriesService seriesService,
            ISeriesBookLinkService seriesBookLinkService,
            IRootFolderService rootFolderService,
            IQualityProfileService qualityProfileService,
            IMatchingUploadLogger matchingLogger,
            Logger logger,
            IMetadataServerHealthGate metadataServerHealthGate)
        {
            _httpClient = httpClient;
            _authorInfoProxy = authorInfoProxy;
            _configService = configService;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _matchingLogger = matchingLogger;
            _metadataServerHealthGate = metadataServerHealthGate ?? throw new ArgumentNullException(nameof(metadataServerHealthGate));
            _logger = logger;
        }

        public List<V5MatchedAuthor> SearchV5Matching(string query, IDictionary<string, List<string>> tags, string mediaType, string filePath)
        {
            _logger.Debug("[V5-MATCHING] ENTER query='{0}', tags={1}, media={2}, file={3}",
                query, tags?.Count ?? 0, mediaType, Path.GetFileName(filePath ?? "unknown"));

            var effectiveQuery = BuildEffectiveQuery(query, tags);
            if (string.IsNullOrWhiteSpace(effectiveQuery))
            {
                _logger.Debug("[V5-MATCHING] Skipping call: empty query for file '{0}'", Path.GetFileName(filePath ?? "unknown"));
                return new List<V5MatchedAuthor>();
            }

            Dictionary<string, List<string>> logTags = null;
            string safeMetadataServerAuthority = null;

            try
            {
                var metadataServerUrl = _configService.MetadataServerUrl;
                safeMetadataServerAuthority = GetSafeMetadataServerAuthority(metadataServerUrl);
                _logger.Debug("[V5-MATCHING] Metadata server: {0}", safeMetadataServerAuthority ?? "UNCONFIGURED");

                if (string.IsNullOrWhiteSpace(metadataServerUrl))
                {
                    _logger.Error("[V5-MATCHING] Metadata server URL is not configured!");
                    return new List<V5MatchedAuthor>();
                }

                string url = null;
                try
                {
                    _logger.Debug("[V5-MATCHING] Building URL with base: {0}", metadataServerUrl);
                    url = $"{metadataServerUrl.TrimEnd('/')}/api/v5/match";
                    _logger.Debug("[V5-MATCHING] Successfully built URL: {0}", url);
                }
                catch (Exception urlEx)
                {
                    _logger.Error(urlEx, "[V5-MATCHING] Failed to build URL! Base URL: {0}", metadataServerUrl);
                    return new List<V5MatchedAuthor>();
                }

                // Policy: send every non-trash tag through the shared exclusion policy and size bounds.
                // The tag extraction pipeline is intentionally field-agnostic and can include large/freeform text.
                var tagsCopy = new Dictionary<string, List<string>>(tags ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
                var requestTags = BuildV5RequestTags(tagsCopy, filePath);
                logTags = requestTags;

                // Build request body
                var requestBody = new V5MatchRequest
                {
                    q = effectiveQuery,
                    tags = requestTags,
                    media_type = mediaType
                };

                var requestJson = JsonConvert.SerializeObject(requestBody);
                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("[V5-MATCHING] REQUEST {0} tags={1} keys=[{2}]",
                        url,
                        requestTags.Count,
                        string.Join(", ", requestTags.Keys.Take(12)));
                }

                HttpRequest httpRequest = null;
                try
                {
                    httpRequest = new HttpRequestBuilder(url)
                        .Accept(HttpAccept.Json)
                        .SetHeader("Content-Type", "application/json")
                        .Build();

                    httpRequest.Method = HttpMethod.Query;
                    httpRequest.SetContent(requestJson);
                    httpRequest.RequestTimeout = TimeSpan.FromSeconds(15);
                    _logger.Debug("[V5-MATCHING] HTTP request built successfully");
                }
                catch (Exception reqEx)
                {
                    _logger.Error(reqEx, "[V5-MATCHING] Failed to build HTTP request for URL: {0}", url);
                    return new List<V5MatchedAuthor>();
                }

                // Execute the request
                _logger.Trace("[V5-MATCHING] Executing HTTP request...");
                HttpResponse httpResponse = null;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    if (!_metadataServerHealthGate.TryBeginRequest(out var retryAfter))
                    {
                        stopwatch.Stop();
                        var retryMessage = $"METADATA_SERVER_UNAVAILABLE: retrying in {MetadataServerHealthGate.FormatRetryAfter(retryAfter)}";
                        _logger.Warn("[V5-MATCHING] Skipping metadata server request for '{0}': {1}", effectiveQuery, retryMessage);
                        _matchingLogger.LogV5Request(effectiveQuery, logTags, mediaType, retryMessage, filePath: filePath);
                        return new List<V5MatchedAuthor>();
                    }

                    httpResponse = _httpClient.Execute(httpRequest);
                    _metadataServerHealthGate.ReportResponse(httpResponse);
                    stopwatch.Stop();
                    _logger.Debug("[V5-MATCHING] HTTP request completed in {0}ms", stopwatch.ElapsedMilliseconds);
                }
                catch (Exception httpEx)
                {
                    _metadataServerHealthGate.ReportException(httpEx);
                    stopwatch.Stop();
                    _logger.Error(httpEx, "[V5-MATCHING] HTTP request failed after {0}ms! URL: {1}", stopwatch.ElapsedMilliseconds, url);
                    _logger.Error("[V5-MATCHING] Exception type: {0}", httpEx.GetType().FullName);
                    _logger.Error("[V5-MATCHING] Exception message: {0}", httpEx.Message);
                    if (httpEx.InnerException != null)
                    {
                        _logger.Error("[V5-MATCHING] Inner exception: {0}: {1}", httpEx.InnerException.GetType().FullName, httpEx.InnerException.Message);
                    }

                    // Log V5 request failure
                    _matchingLogger.LogV5Request(effectiveQuery, logTags, mediaType, $"HTTP_ERROR: {httpEx.GetType().Name}", filePath: filePath);
                    return new List<V5MatchedAuthor>();
                }

                if (httpResponse == null)
                {
                    _logger.Error("[V5-MATCHING] HTTP response is NULL!");
                    return new List<V5MatchedAuthor>();
                }

                // ROBUST LOGGING: Log response details
                _logger.Debug("[V5-MATCHING] RESPONSE {0} in {1}ms", (int)httpResponse.StatusCode, stopwatch.ElapsedMilliseconds);
                _logger.Trace("[V5-MATCHING] RESPONSE bytes={0}", httpResponse.ResponseData?.Length ?? 0);

                if ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300)
                {
                    _logger.Error("[V5-MATCHING] Request failed with status {0} from {1}", (int)httpResponse.StatusCode, safeMetadataServerAuthority ?? "UNCONFIGURED");
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.Trace("[V5-MATCHING] Error body (truncated): {0}", SanitizeV5TagValue(httpResponse.Content, 512) ?? "<empty>");
                    }
                    // Log V5 request failure
                    _matchingLogger.LogV5Request(effectiveQuery, logTags, mediaType, $"HTTP_STATUS_{(int)httpResponse.StatusCode}", filePath: filePath);
                    return new List<V5MatchedAuthor>();
                }

                // Parse the response
                V5MatchingResponse matchResponse = null;
                try
                {
                    matchResponse = JsonConvert.DeserializeObject<V5MatchingResponse>(httpResponse.Content);
                    _logger.Debug("[V5-MATCHING] Successfully parsed response");
                }
                catch (Exception parseEx)
                {
                    _logger.Error(parseEx, "[V5-MATCHING] Failed to parse response JSON from {0} (bytes={1}, first={2})",
                        safeMetadataServerAuthority ?? "UNCONFIGURED",
                        httpResponse.ResponseData?.Length ?? 0,
                        SanitizeV5TagValue(httpResponse.Content, 256) ?? "<empty>");
                    return new List<V5MatchedAuthor>();
                }

                if (matchResponse == null || matchResponse.matches == null)
                {
                    _logger.Warn("[V5-MATCHING] Response was null or contained no matches");
                    return new List<V5MatchedAuthor>();
                }

                _logger.Debug("[V5-MATCHING] Received {0} matches from metadata server", matchResponse.matches.Count);

                // Log successful V5 request
                var responseContent = matchResponse.matches.Count > 0 
                    ? $"SUCCESS: Found {matchResponse.matches.Count} matches: {string.Join(", ", matchResponse.matches.Take(3).Select(m => m.author))}" 
                    : "SUCCESS: No matches found";
                _matchingLogger.LogV5Request(effectiveQuery, logTags, mediaType, responseContent, filePath: filePath);

                // Convert V5Match objects to V5MatchedAuthor for compatibility
                var authors = matchResponse.matches.Select(m => new V5MatchedAuthor
                {
                    name = m.author,
                    id = m.author_id,
                    work_id = m.work_id,
                    work_title = m.work_title,
                    edition_title = m.edition_title,
                    edition_hardcover_id = m.edition_hardcover_id  // Include edition ID from match
                }).ToList();

                return authors;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[V5-MATCHING] Unexpected error in SearchV5Matching");
                _logger.Error("[V5-MATCHING] Exception type: {0}", ex.GetType().FullName);
                _logger.Error("[V5-MATCHING] Exception message: {0}", ex.Message);
                _logger.Error("[V5-MATCHING] Stack trace: {0}", ex.StackTrace);
                // Log unexpected error
                _matchingLogger.LogV5Request(effectiveQuery, logTags, mediaType, $"UNEXPECTED_ERROR: {ex.GetType().Name}", filePath: filePath);
                return new List<V5MatchedAuthor>();
            }
            finally { }
        }

        private const int V5MaxTagValueChars = 256;
        private const int V5MaxTotalTagBytes = 8 * 1024; // Keep request bodies small and predictable

        private static string GetSafeMetadataServerAuthority(string metadataServerUrl)
        {
            if (string.IsNullOrWhiteSpace(metadataServerUrl))
            {
                return null;
            }

            var trimmed = metadataServerUrl.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }

            // Some configs omit a scheme; avoid logging the raw value (may contain credentials).
            if (Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out var uriWithScheme))
            {
                return uriWithScheme.GetLeftPart(UriPartial.Authority);
            }

            return "INVALID_URL";
        }

        private static Dictionary<string, List<string>> BuildV5RequestTags(Dictionary<string, List<string>> tags, string filePath)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            tags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // Use the same trash/exclude filter as local FTS matching — single source of truth.
            var totalBytes = 0;
            // Tag fields are semantically uniform. Ordinal ordering is only a deterministic packing
            // order; it does not assign any field more matching authority than another.
            foreach (var kv in tags
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(kv.Key) ||
                    TagExclusionPolicy.IsExcludedFromMatching(kv.Key))
                {
                    continue;
                }

                foreach (var rawValue in kv.Value ?? new List<string>())
                {
                    var value = SanitizeV5TagValue(rawValue, V5MaxTagValueChars);
                    if (string.IsNullOrWhiteSpace(value) ||
                        string.Equals(value, "[Binary Data]", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "[Unknown Frame]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var bytes = Encoding.UTF8.GetByteCount(value);
                    if (bytes <= 0 || totalBytes + bytes > V5MaxTotalTagBytes)
                    {
                        // One large value must not hide smaller, still-useful values that
                        // follow it. Keep scanning until the byte budget is actually full.
                        continue;
                    }

                    if (!result.TryGetValue(kv.Key, out var outputValues))
                    {
                        outputValues = new List<string>();
                        result[kv.Key] = outputValues;
                    }

                    outputValues.Add(value);
                    totalBytes += bytes;
                }
            }

            // Include file_name (basename only), never the full path.
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var fileName = Path.GetFileName(filePath);
                var value = SanitizeV5TagValue(fileName, 200);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var bytes = Encoding.UTF8.GetByteCount(value);
                    if (totalBytes + bytes <= V5MaxTotalTagBytes)
                    {
                        result["file_name"] = new List<string> { value };
                    }
                }
            }

            return result;
        }

        private static string BuildEffectiveQuery(string query, IDictionary<string, List<string>> tags)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                return query;
            }

            return CanonicalMatchInputBuilder.BuildEmbeddedQuery(tags);
        }

        private static string SanitizeV5TagValue(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            maxChars = Math.Max(1, maxChars);

            // Collapse all whitespace/control characters into spaces to avoid control chars in logs/JSON.
            var sb = new StringBuilder(Math.Min(value.Length, maxChars));
            var lastWasSpace = false;
            foreach (var ch in value)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(ch);
                    lastWasSpace = false;
                }

                if (sb.Length >= maxChars)
                {
                    break;
                }
            }

            var sanitized = sb.ToString().Trim();
            return sanitized.Length == 0 ? null : sanitized;
        }


        private NewItemMonitorTypes ConvertToNewItemMonitorType(MonitorTypes monitorType)
        {
            switch (monitorType)
            {
                case MonitorTypes.All:
                    return NewItemMonitorTypes.All;
                case MonitorTypes.None:
                    return NewItemMonitorTypes.None;
                case MonitorTypes.Future:
                case MonitorTypes.Missing:
                case MonitorTypes.Latest:
                case MonitorTypes.First:
                    return NewItemMonitorTypes.New;
                default:
                    return NewItemMonitorTypes.None;
            }
        }

        public void ProcessSeriesLinks(List<Book> books)
        {
            _logger.Debug("[SERIES-LINKS] Processing series links for {0} books", books.Count);

            foreach (var book in books)
            {
                try
                {
                    if (book.SeriesLinks?.Any() == true)
                    {
                        _logger.Debug("[SERIES-LINKS] Processing {0} series links for book '{1}'",
                            book.SeriesLinks.Count, book.Title);

                        var linksToCreate = new List<SeriesBookLink>();

                        foreach (var seriesLink in book.SeriesLinks)
                        {
                            if (seriesLink.Series?.Value != null)
                            {
                                var seriesRef = seriesLink.Series.Value;

                                // Series identity is provider-ID-backed. Do not fall back to title matching since it
                                // can link books to the wrong series. If we don't have a Goodreads series ID, skip.
                                if (seriesRef.GoodreadsSeriesId.IsNullOrWhiteSpace())
                                {
                                    _logger.Warn("[SERIES-LINKS] Skipping series link for book '{0}' because series '{1}' has no GoodreadsSeriesId",
                                        book.Title, seriesRef.Title);
                                    continue;
                                }

                                var matches = _seriesService.FindById(new List<string> { seriesRef.GoodreadsSeriesId }, book.MediaType);
                                var series = matches.FirstOrDefault(s => s != null && s.IsOriginal);

                                if (series != null)
                                {
                                    var link = new SeriesBookLink
                                    {
                                        BookId = book.Id,
                                        SeriesId = series.Id,
                                        Position = seriesLink.Position,
                                        SeriesPosition = seriesLink.SeriesPosition,
                                        IsPrimary = seriesLink.IsPrimary
                                    };

                                    linksToCreate.Add(link);
                                    _logger.Debug("[SERIES-LINKS] Creating SeriesBookLink: Book '{0}' ({1}) -> Series '{2}' ({3}) at position {4}",
                                        book.Title, book.Id, series.Title, series.Id, seriesLink.Position);
                                }
                                else
                                {
                                    _logger.Warn("[SERIES-LINKS] Could not find series '{0}' for book '{1}' (mediaType: {2})",
                                        seriesRef.Title, book.Title, book.MediaType);
                                }
                            }
                        }

                        if (linksToCreate.Any())
                        {
                            _seriesBookLinkService.InsertMany(linksToCreate);
                            _logger.Debug("[SERIES-LINKS] Created {0} series-book links for '{1}'",
                                linksToCreate.Count, book.Title);

                            foreach (var link in linksToCreate)
                            {
                                _logger.Debug("[SERIES-LINKS] Created SeriesBookLink: Book='{0}' -> Series='{1}', Position='{2}', SeriesPosition={3}",
                                    book.Title, link.SeriesId, link.Position, link.SeriesPosition);
                            }
                        }
                        else
                        {
                            _logger.Warn("[SERIES-LINKS] NO SeriesBookLinks created for '{0}' - linksToCreate was empty", book.Title);
                        }
                    }
                    else
                    {
                        _logger.Debug("[SERIES-LINKS] No SeriesLinks to process for book '{0}'", book.Title);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[SERIES-LINKS] Error processing series links for book '{0}'", book.Title);
                }
            }
        }
    }

    public class V5MatchRequest
    {
        public string q { get; set; }
        public Dictionary<string, List<string>> tags { get; set; }
        public string media_type { get; set; }
    }

    public class V5MatchingResponse
    {
        public List<V5Match> matches { get; set; }
    }

    public class V5MatchResponse
    {
        public List<V5MatchedAuthor> authors { get; set; }
    }

    public class V5Match
    {
        public string author { get; set; }
        public string author_id { get; set; }
        public string work_title { get; set; }
        public string work_id { get; set; }
        public string edition_title { get; set; }
        public string edition_hardcover_id { get; set; }
    }

    public class V5MatchedAuthor
    {
        public string name { get; set; }   // Author name for ID3 tag matching
        public string id { get; set; }     // Provider-prefixed ID like "hc:123" or "gr:456"
        public string work_id { get; set; }
        public string work_title { get; set; }
        public string edition_title { get; set; }
        public string edition_hardcover_id { get; set; }
    }
}
