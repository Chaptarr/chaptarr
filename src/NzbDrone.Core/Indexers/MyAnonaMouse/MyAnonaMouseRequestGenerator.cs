using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Profiles.Metadata;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouseRequestGenerator : IIndexerRequestGenerator
    {
        private const int RecentPageSize = 500;
        private const int SearchPageSize = 100;
        private const int SearchMaxPages = 10;
        private static readonly Regex BookNumberSuffixRegex = new Regex(@"\s*,?\s*book\s+\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MultiWhitespaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);

        public MyAnonaMouseSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetRecentJsonApiRequests());

            return pageableRequests;
        }

        public virtual IndexerPageableRequestChain GetSearchRequests(BookSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            var bookQuery = GetSafeBookQuery(searchCriteria);
            var authorQuery = GetSafeAuthorQuery(searchCriteria);

            // MAM_TRACE: Log search criteria
            Logger.Trace("MAM_SEARCH_START: Book search initiated");
            Logger.Trace("MAM_SEARCH_CRITERIA: Author='{0}', BookTitle='{1}', BookQuery='{2}'",
                searchCriteria.Author?.Name ?? "Unknown",
                searchCriteria.BookTitle ?? "Unknown",
                bookQuery ?? "Unknown");

            Logger.Trace("MAM_SEARCH_MODE: Using JSON API");
            var searchTerms = BuildBookSearchTerms(searchCriteria, bookQuery, authorQuery);
            if (searchTerms.Count == 0)
            {
                pageableRequests.Add(GetJsonApiRequests(string.Empty, searchCriteria));
                return pageableRequests;
            }

            pageableRequests.Add(GetJsonApiRequests(searchTerms[0], searchCriteria));

            foreach (var fallback in searchTerms.GetRange(1, searchTerms.Count - 1))
            {
                pageableRequests.AddTier(GetJsonApiRequests(fallback, searchCriteria));
            }

            return pageableRequests;
        }

        public virtual IndexerPageableRequestChain GetSearchRequests(AuthorSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            // Use JSON API for enhanced metadata
            pageableRequests.Add(GetJsonApiRequests(searchCriteria));

            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetRecentJsonApiRequests()
        {
            // MAM has no reliable RSS endpoint; emulate RSS sync by pulling the "recent" list from its JSON search endpoint.
            yield return new IndexerRequest(BuildJsonSearchRequest(string.Empty, startNumber: 0, perPage: RecentPageSize, includeDescription: false));
        }

        private List<string> BuildBookSearchTerms(BookSearchCriteria searchCriteria, string bookQuery, string authorQuery)
        {
            var searchTerms = new List<string>();

            void AddSearchTerm(string term, string logMessage, bool isFallback = false)
            {
                var normalized = term?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                if (searchTerms.Exists(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                // A term's position is its tier: searchTerms[0] is the primary request and every
                // later term becomes its own fallback tier, so this number lines up with the
                // "Search tier N/M returned ..." line written when the request actually runs.
                // The primary term logs at Debug alongside the fallbacks, otherwise a Debug-level
                // log shows the fallbacks without the term they were falling back from.
                var tier = searchTerms.Count + 1;

                if (isFallback)
                {
                    Logger.Debug(logMessage + " [tier {1}]", normalized, tier);
                }
                else
                {
                    Logger.Debug(logMessage + " [tier {2}]", normalized, bookQuery ?? "Empty", tier);
                }

                searchTerms.Add(normalized);
            }

            // MAM expects raw text, not URL-encoded. Keep the primary behavior of including both title and author terms.
            var decodedTitle = string.IsNullOrWhiteSpace(bookQuery) ?
                string.Empty :
                System.Web.HttpUtility.UrlDecode(bookQuery);

            // AuthorQuery is normalized via SearchCriteriaBase.GetQueryTitle (handles punctuation like "J.K." -> "J K")
            var decodedAuthor = string.IsNullOrWhiteSpace(authorQuery) ?
                string.Empty :
                System.Web.HttpUtility.UrlDecode(authorQuery);

            // MAM search can be sensitive to different apostrophe characters. Normalize early so both the primary and
            // fallback queries use the same representation without increasing request count.
            var originalDecodedTitle = decodedTitle;
            var originalDecodedAuthor = decodedAuthor;
            decodedTitle = NormalizeApostrophes(decodedTitle);
            decodedAuthor = NormalizeApostrophes(decodedAuthor);
            if (!string.Equals(decodedTitle, originalDecodedTitle, StringComparison.Ordinal) ||
                !string.Equals(decodedAuthor, originalDecodedAuthor, StringComparison.Ordinal))
            {
                Logger.Debug("MAM_SEARCH_APOSTROPHE_NORMALIZED: title '{0}' -> '{1}', author '{2}' -> '{3}'",
                    originalDecodedTitle,
                    decodedTitle,
                    originalDecodedAuthor,
                    decodedAuthor);
            }

            var decodedQuery = decodedTitle?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(decodedAuthor))
            {
                if (!string.IsNullOrWhiteSpace(decodedQuery))
                {
                    decodedQuery += " ";
                }

                decodedQuery += decodedAuthor.Trim();
            }

            AddSearchTerm(decodedQuery, "MAM_SEARCH_TERM: '{0}' (decoded from: '{1}')");

            if (!string.IsNullOrWhiteSpace(decodedTitle) &&
                !string.Equals(decodedTitle.Trim(), decodedQuery, StringComparison.OrdinalIgnoreCase))
            {
                AddSearchTerm(decodedTitle, "MAM_SEARCH_FALLBACK_TERM: '{0}' (title-only fallback)", isFallback: true);
            }

            // Interactive search: add a fallback query for verbose titles like "... , Book 3" that can hurt recall.
            if (searchCriteria.InteractiveSearch && !string.IsNullOrWhiteSpace(decodedTitle))
            {
                var fallbackTitle = BookNumberSuffixRegex.Replace(decodedTitle, string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(fallbackTitle) &&
                    !string.Equals(fallbackTitle, decodedTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AddSearchTerm(fallbackTitle, "MAM_SEARCH_FALLBACK_TERM: '{0}' (book-number fallback)", isFallback: true);
                }
            }

            return searchTerms;
        }

        private IEnumerable<IndexerRequest> GetJsonApiRequests(string searchText, SearchCriteriaBase searchCriteria = null)
        {
            for (var page = 0; page < SearchMaxPages; page++)
            {
                yield return new IndexerRequest(BuildJsonSearchRequest(
                    searchText,
                    startNumber: page * SearchPageSize,
                    perPage: SearchPageSize,
                    searchCriteria: searchCriteria));
            }
        }

        private static string NormalizeApostrophes(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            // Normalize both straight and curly apostrophes. Replace with whitespace to keep token boundaries stable
            // (e.g., "Sorcerer's" -> "Sorcerer s") across different search implementations.
            var normalized = title.Replace("’", " ").Replace("'", " ");
            normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim();
            return normalized;
        }

        private IEnumerable<IndexerRequest> GetJsonApiRequests(AuthorSearchCriteria searchCriteria)
        {
            var authorQuery = GetSafeAuthorQuery(searchCriteria);

            // Decode URL-encoded search query for JSON API (MAM expects raw text, not URL-encoded)
            var decodedQuery = string.IsNullOrWhiteSpace(authorQuery) ?
                string.Empty :
                System.Web.HttpUtility.UrlDecode(authorQuery);

            Logger.Trace("MAM_SEARCH_TERM: '{0}' (decoded from: '{1}')", decodedQuery ?? "Empty", authorQuery ?? "Empty");
            foreach (var request in GetJsonApiRequests(decodedQuery, searchCriteria))
            {
                yield return request;
            }
        }

        private static string GetSafeBookQuery(BookSearchCriteria searchCriteria)
        {
            var title = BookSearchCriteria.GetMainSearchTitle(searchCriteria?.BookTitle, searchCriteria?.Author?.Name);
            return string.IsNullOrWhiteSpace(title) ? string.Empty : SearchCriteriaBase.GetQueryTitle(title);
        }

        private static string GetSafeAuthorQuery(SearchCriteriaBase searchCriteria)
        {
            var authorName = searchCriteria?.Author?.Name;
            return string.IsNullOrWhiteSpace(authorName) ? string.Empty : SearchCriteriaBase.GetQueryTitle(authorName);
        }

        private HttpRequest BuildJsonSearchRequest(string searchText, int? startNumber = null, int? perPage = null, SearchCriteriaBase searchCriteria = null, bool includeDescription = true)
        {
            var requestUrl = string.Format("{0}/tor/js/loadSearchJSONbasic.php", Settings.BaseUrl.Trim().TrimEnd('/'));
            var searchPayload = BuildJsonSearchPayload(searchText, startNumber, perPage, searchCriteria, includeDescription);
            var jsonContent = JsonConvert.SerializeObject(searchPayload);

            // MAM_TRACE: Log exact API command
            Logger.Trace("MAM_API_REQUEST: URL='{0}'", requestUrl);
            Logger.Trace("MAM_API_PAYLOAD: {0}", jsonContent);

            var request = new HttpRequest(requestUrl)
            {
                Method = HttpMethod.Post,
                ContentData = Encoding.UTF8.GetBytes(jsonContent)
            };

            request.Headers.ContentType = "application/json";
            request.Headers.Set("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}");
            request.Headers.Accept = "application/json";

            if (!string.IsNullOrWhiteSpace(Settings.MamId))
            {
                request.Cookies["mam_id"] = Settings.MamId; // idempotent
            }

            if (Settings.MamSsl)
            {
                request.Cookies["mam_ssl"] = "1"; // idempotent
            }

            request.StoreRequestCookie = true;
            request.StoreResponseCookie = true;

            Logger.Trace("MAM_API_COOKIES: mam_id present={0}, mam_ssl={1}, persistence=on", !string.IsNullOrWhiteSpace(Settings.MamId), Settings.MamSsl);

            return request;
        }

        private object BuildJsonSearchPayload(string searchText, int? startNumber = null, int? perPage = null, SearchCriteriaBase searchCriteria = null, bool includeDescription = true)
        {
            var browseLanguages = BuildBrowseLanguages(searchCriteria);

            var torrentParams = new Dictionary<string, object>
            {
                ["text"] = searchText ?? "",
                ["srchIn"] = new[] { "title", "author", "narrator", "series", "tags" },
                ["searchType"] = Settings.MinimumSeeders > 0 ? "active" : "all",
                ["searchIn"] = "torrents",
                ["cat"] = new[] { "0" },
                ["main_cat"] = BuildMainCategories(searchCriteria),
                ["browseFlagsHideVsShow"] = "0",
                ["startDate"] = "",
                ["endDate"] = "",
                ["hash"] = "",
                ["sortType"] = "default",
                ["startNumber"] = (startNumber ?? 0).ToString()
            };

            if (browseLanguages.Count > 0)
            {
                torrentParams["browse_lang"] = browseLanguages;
            }

            var payload = new Dictionary<string, object>
            {
                ["tor"] = torrentParams,
                ["perpage"] = (perPage ?? SearchPageSize).ToString(),
                ["mediaInfo"] = "1"
            };

            if (includeDescription)
            {
                payload["description"] = "1";
            }

            return payload;
        }

        private static string[] BuildMainCategories(SearchCriteriaBase searchCriteria)
        {
            var mediaType = SearchMediaTypeHelper.GetRequestedMediaType(searchCriteria);

            return mediaType switch
            {
                BookMediaType.Audiobook => new[] { "13" },
                BookMediaType.Ebook => new[] { "14" },
                _ => new[] { "13", "14" }
            };
        }

        private List<string> BuildBrowseLanguages(SearchCriteriaBase searchCriteria)
        {
            if (!TryGetProfileLanguagePolicy(
                    searchCriteria,
                    out var profileLanguages,
                    out var allowUnknownLanguage,
                    out var profileLanguageConfigured,
                    out var unknownTokens))
            {
                return new List<string>();
            }

            if (!profileLanguageConfigured && unknownTokens.Count == 0)
            {
                return new List<string>();
            }

            if (allowUnknownLanguage)
            {
                Logger?.Debug("MAM_LANGUAGE_PAYLOAD: Metadata profile allows editions with unknown language; omitting server-side language filter");
                return new List<string>();
            }

            if (unknownTokens.Count > 0)
            {
                Logger?.Debug("MAM_LANGUAGE_PAYLOAD: Metadata profile contains unrecognized language token(s) [{0}]; omitting server-side language filter", string.Join(", ", unknownTokens));
                return new List<string>();
            }

            var mapped = new List<string>();
            var unmapped = new List<string>();

            foreach (var language in profileLanguages)
            {
                if (!MyAnonaMouseLanguageMapper.TryGetBrowseLanguageId(language, out var languageId))
                {
                    unmapped.Add(language);
                    continue;
                }

                var value = languageId.ToString();
                if (!mapped.Contains(value))
                {
                    mapped.Add(value);
                }
            }

            if (unmapped.Count > 0)
            {
                Logger?.Debug("MAM_LANGUAGE_PAYLOAD: Metadata profile language(s) [{0}] have no documented MAM browse_lang ID; omitting server-side language filter", string.Join(", ", unmapped));
                return new List<string>();
            }

            return mapped;
        }

        private bool TryGetProfileLanguagePolicy(
            SearchCriteriaBase searchCriteria,
            out IEnumerable<string> languages,
            out bool allowUnknownLanguage,
            out bool configured,
            out List<string> unknownTokens)
        {
            languages = Array.Empty<string>();
            allowUnknownLanguage = false;
            configured = false;
            unknownTokens = new List<string>();

            var mediaType = SearchMediaTypeHelper.GetRequestedMediaType(searchCriteria);
            if (!mediaType.HasValue)
            {
                return false;
            }

            var metadataProfile = GetMetadataProfile(searchCriteria?.Author, mediaType.Value);
            if (metadataProfile == null)
            {
                return false;
            }

            EditionMetadataProfileFilter.ParseAllowedLanguages(
                metadataProfile.AllowedLanguages,
                out var allowedLanguages,
                out allowUnknownLanguage,
                out configured,
                out unknownTokens);

            languages = allowedLanguages;
            return true;
        }

        private static MetadataProfile GetMetadataProfile(Author author, BookMediaType mediaType)
        {
            var mediaProfile = mediaType == BookMediaType.Ebook
                ? author?.EbookMetadataProfile?.Value
                : author?.AudiobookMetadataProfile?.Value;

            if (mediaProfile != null)
            {
                return mediaProfile;
            }

            var legacyProfile = author?.MetadataProfile?.Value;
            if (legacyProfile?.ProfileType == MetadataProfileType.General ||
                legacyProfile?.ProfileType == (mediaType == BookMediaType.Ebook ? MetadataProfileType.Ebook : MetadataProfileType.Audiobook))
            {
                return legacyProfile;
            }

            return null;
        }

    }
}
