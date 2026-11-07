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
        protected readonly INewznabCapabilitiesProvider _capabilitiesProvider;
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
            _indexerName = indexerName.IsNullOrWhiteSpace() ? "Newznab" : indexerName;

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

        protected virtual bool SupportsBookSearch
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
            var requestedMediaType = SearchMediaTypeHelper.GetRequestedMediaType(searchCriteria);
            var configuredCategories = (Settings.Categories ?? Enumerable.Empty<int>()).Distinct().ToList();
            var searchCategories = SearchMediaTypeHelper.FilterCategoriesForMediaType(configuredCategories, requestedMediaType);

            if (requestedMediaType.HasValue && configuredCategories.Count > 0 && searchCategories.Count == 0)
            {
                _logger.Warn("Newznab indexer '{0}' has no {1} categories selected; this search will send no indexer requests",
                    _indexerName,
                    requestedMediaType.Value.ToString().ToLowerInvariant());
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

            if (SupportsSearch)
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

            // A title like "..., Book 4" rarely appears in a release name, so every tier above can come
            // back empty for a book the indexer does have. Tiers are tried in order until one returns
            // results, so this only runs when the precise queries found nothing. Automatic searches
            // (including those requested by SeerrNG) need it as much as interactive ones.
            // MyAnonaMouse has the same last-resort tier.
            var queryWithoutBookNumber = searchCriteria.BookQueryWithoutBookNumber;
            if (queryWithoutBookNumber != null)
            {
                pageableRequests.AddTier();

                if (SupportsBookSearch)
                {
                    pageableRequests.Add(GetPagedRequests(MaxPages,
                        searchCategories,
                        "book",
                        $"&title={NewsnabifyTitle(queryWithoutBookNumber)}"));
                }

                if (SupportsSearch)
                {
                    pageableRequests.Add(GetPagedRequests(MaxPages,
                        searchCategories,
                        "search",
                        $"&q={NewsnabifyTitle(queryWithoutBookNumber)}"));
                }
            }

            return pageableRequests;
        }

        public virtual IndexerPageableRequestChain GetSearchRequests(AuthorSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            if (SupportsBookSearch)
            {
                AddBookPageableRequests(pageableRequests,
                    Settings.Categories,
                    $"&author={NewsnabifyTitle(searchCriteria.AuthorQuery)}");
            }

            if (SupportsSearch)
            {
                pageableRequests.AddTier();

                pageableRequests.Add(GetPagedRequests(MaxPages,
                    Settings.Categories,
                    "search",
                    $"&q={NewsnabifyTitle(searchCriteria.AuthorQuery)}"));
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
                $"{NewznabEndpointCompatibility.Normalize(Settings.BaseUrl).TrimEnd('/')}{Settings.ApiPath.TrimEnd('/')}?t={searchType}&cat={categoriesQuery}&extended=1{Settings.AdditionalParameters}";

            if (Settings.ApiKey.IsNotNullOrWhiteSpace())
            {
                baseUrl += "&apikey=" + Settings.ApiKey;
            }

            if (PageSize == 0)
            {
                yield return new IndexerRequest($"{baseUrl}{parameters}", HttpAccept.Rss);
            }
            else
            {
                for (var page = 0; page < maxPages; page++)
                {
                    yield return new IndexerRequest($"{baseUrl}&offset={page * PageSize}&limit={PageSize}{parameters}",
                        HttpAccept.Rss);
                }
            }
        }

        private static string NewsnabifyTitle(string title)
        {
            title = title.Replace("+", " ");
            return Uri.EscapeDataString(title);
        }
    }
}
