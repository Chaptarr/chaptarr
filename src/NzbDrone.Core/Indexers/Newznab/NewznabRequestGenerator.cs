using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers.Newznab
{
    public class NewznabRequestGenerator : IIndexerRequestGenerator
    {
        private readonly INewznabCapabilitiesProvider _capabilitiesProvider;
        private readonly Logger _logger;
        private readonly string _indexerName;
        public int MaxPages { get; set; }
        public int PageSize { get; set; }
        public NewznabSettings Settings { get; set; }
        public int? ProxyId { get; set; }

        public NewznabRequestGenerator(INewznabCapabilitiesProvider capabilitiesProvider, Logger logger, string indexerName)
        {
            _capabilitiesProvider = capabilitiesProvider;
            _logger = logger;
            _indexerName = indexerName;

            MaxPages = 30;
            PageSize = 100;
        }

        private bool SupportsSearch
        {
            get
            {
                var capabilities = _capabilitiesProvider.GetCapabilities(Settings, ProxyId);

                return capabilities.SupportedSearchParameters != null &&
                       capabilities.SupportedSearchParameters.Contains("q");
            }
        }

        private bool SupportsBookSearch
        {
            get
            {
                var capabilities = _capabilitiesProvider.GetCapabilities(Settings, ProxyId);

                return capabilities.SupportedBookSearchParameters != null &&
                       capabilities.SupportedBookSearchParameters.Contains("author") &&
                       capabilities.SupportedBookSearchParameters.Contains("title");
            }
        }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            var capabilities = _capabilitiesProvider.GetCapabilities(Settings, ProxyId);

            if (capabilities.SupportedBookSearchParameters != null)
            {
                pageableRequests.Add(GetPagedRequests(MaxPages, Settings.Categories, "book", ""));
            }
            else if (capabilities.SupportedSearchParameters != null)
            {
                pageableRequests.Add(GetPagedRequests(MaxPages, Settings.Categories, "search", ""));
            }

            return pageableRequests;
        }

        public virtual IndexerPageableRequestChain GetSearchRequests(BookSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            var mediaType = SearchMediaTypeHelper.GetRequestedMediaType(searchCriteria);
            var searchCategories = SearchMediaTypeHelper.FilterCategoriesForMediaType(Settings.Categories, mediaType);

            // Single-format indexers are valid; distinguish a skipped search from an empty response.
            if (mediaType.HasValue && searchCategories.Count == 0)
            {
                _logger.Log(searchCriteria.InteractiveSearch ? LogLevel.Warn : LogLevel.Debug,
                    "Skipping {0} search on indexer '{1}': configured categories [{2}] contain no categories for this format. No request was sent.",
                    mediaType.Value.ToString().ToLowerInvariant(), _indexerName, string.Join(", ", Settings.Categories ?? Enumerable.Empty<int>()));
                return pageableRequests;
            }

            var supportsSearch = SupportsSearch;
            var narratorQuery = supportsSearch ? searchCriteria.NarratorQuery : string.Empty;
            if (!string.IsNullOrWhiteSpace(narratorQuery))
            {
                pageableRequests.Add(GetPagedRequests(MaxPages,
                    searchCategories,
                    "search",
                    $"&q={NewsnabifyTitle(searchCriteria.BookQuery)}+{NewsnabifyTitle(searchCriteria.AuthorQuery)}+{NewsnabifyTitle(narratorQuery)}"));
            }

            if (SupportsBookSearch)
            {
                AddBookPageableRequests(pageableRequests,
                    searchCategories,
                    $"&author={NewsnabifyTitle(searchCriteria.AuthorQuery)}&title={NewsnabifyTitle(searchCriteria.BookQuery)}");

                AddBookPageableRequests(pageableRequests,
                    searchCategories,
                    $"&title={NewsnabifyTitle(searchCriteria.BookQuery)}");
            }

            if (supportsSearch)
            {
                pageableRequests.AddTier();

                pageableRequests.Add(GetPagedRequests(MaxPages,
                    searchCategories,
                    "search",
                    $"&q={NewsnabifyTitle(searchCriteria.BookQuery)}+{NewsnabifyTitle(searchCriteria.AuthorQuery)}"));

                pageableRequests.Add(GetPagedRequests(MaxPages,
                    searchCategories,
                    "search",
                    $"&q={NewsnabifyTitle(searchCriteria.AuthorQuery)}+{NewsnabifyTitle(searchCriteria.BookQuery)}"));

                pageableRequests.AddTier();

                pageableRequests.Add(GetPagedRequests(MaxPages,
                    searchCategories,
                    "search",
                    $"&q={NewsnabifyTitle(searchCriteria.BookQuery)}"));
            }

            return pageableRequests;
        }

        private void AddBookPageableRequests(IndexerPageableRequestChain chain, IEnumerable<int> categories, string parameters)
        {
            chain.AddTier();

            chain.Add(GetPagedRequests(MaxPages, categories, "book", $"{parameters}"));
        }

        private IEnumerable<IndexerRequest> GetPagedRequests(int maxPages, IEnumerable<int> categories, string searchType, string parameters)
        {
            if (categories.Empty())
            {
                yield break;
            }

            var categoriesQuery = string.Join(",", categories.Distinct());

            var baseUrl =
                $"{Settings.BaseUrl.TrimEnd('/')}{Settings.ApiPath.TrimEnd('/')}?t={searchType}&cat={categoriesQuery}&extended=1{Settings.AdditionalParameters}";

            if (Settings.ApiKey.IsNotNullOrWhiteSpace())
            {
                baseUrl += "&apikey=" + Settings.ApiKey;
            }

            if (PageSize == 0)
            {
                yield return LogResponses(new IndexerRequest($"{baseUrl}{parameters}", HttpAccept.Rss));
            }
            else
            {
                for (var page = 0; page < maxPages; page++)
                {
                    yield return LogResponses(new IndexerRequest($"{baseUrl}&offset={page * PageSize}&limit={PageSize}{parameters}",
                        HttpAccept.Rss));
                }
            }
        }

        // HttpClient only emits the body when this flag is set, and only at Trace.
        private static IndexerRequest LogResponses(IndexerRequest request)
        {
            request.HttpRequest.LogResponseContent = true;
            return request;
        }

        private static string NewsnabifyTitle(string title)
        {
            title = title.Replace("+", " ");
            return Uri.EscapeDataString(title);
        }
    }
}
