using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.MetadataSource.Goodreads
{
    public interface IGoodreadsSearchProxy
    {
        public List<SearchJsonResource> Search(string query);
        public List<SearchJsonResource> SearchMetadataApi(string query) => Search(query);
    }

	public class GoodreadsSearchProxy : IGoodreadsSearchProxy
	{
		private readonly ICachedHttpResponseService _cachedHttpClient;
		private readonly IMetadataRequestBuilder _metadataRequestBuilder;
		private readonly Logger _logger;
		private readonly IHttpRequestBuilderFactory _searchBuilder;

        public GoodreadsSearchProxy(ICachedHttpResponseService cachedHttpClient,
            Logger logger,
            IMetadataRequestBuilder metadataRequestBuilder = null)
        {
            _cachedHttpClient = cachedHttpClient;
            _metadataRequestBuilder = metadataRequestBuilder;
            _logger = logger;

			_searchBuilder = new HttpRequestBuilder("https://www.goodreads.com/book/auto_complete")
				.AddQueryParam("format", "json")
				.SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom())
				.KeepAlive()
				.CreateFactory();
		}

        public List<SearchJsonResource> Search(string query)
        {
            try
            {
                var httpRequest = _searchBuilder.Create()
                    .AddQueryParam("q", query)
                    .Build();

                var response = _cachedHttpClient.Get<List<SearchJsonResource>>(
                    httpRequest,
                    true,
                    TimeSpan.FromDays(5),
                    candidate => Json.Deserialize<List<SearchJsonResource>>(candidate.Content)?.Any(result => result?.BookId > 0) == true);

                return response.Resource;
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with Goodreads.", ex, query);
            }
            catch (WebException ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with Goodreads.", ex, query, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Invalid response received from Goodreads.", ex, query);
            }
        }

        public List<SearchJsonResource> SearchMetadataApi(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<SearchJsonResource>();
            }

            if (_metadataRequestBuilder == null)
            {
                throw new InvalidOperationException("The metadata server search client is not configured");
            }

            try
            {
                var request = _metadataRequestBuilder.GetRequestBuilder().Create()
                    .SetSegment("route", "search")
                    .AddQueryParam("q", query)
                    .Build();

                return _cachedHttpClient.Get<List<SearchJsonResource>>(request, false, TimeSpan.FromDays(5)).Resource
                    ?? new List<SearchJsonResource>();
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with the metadata source.", ex, query);
            }
            catch (WebException ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with the metadata source.", ex, query, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Invalid response received from the metadata source.", ex, query);
            }
        }
    }
}
