using System;
using System.Collections.Generic;
using System.Net;
using FluentValidation.Results;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.ImportLists.Chaptarr
{
    public interface IChaptarrV1Proxy
    {
        List<ChaptarrAuthor> GetAuthors(ChaptarrSettings settings);
        List<ChaptarrBook> GetBooks(ChaptarrSettings settings);
        List<ChaptarrProfile> GetProfiles(ChaptarrSettings settings);
        List<ChaptarrRootFolder> GetRootFolders(ChaptarrSettings settings);
        List<ChaptarrTag> GetTags(ChaptarrSettings settings);
        ValidationFailure Test(ChaptarrSettings settings);
    }

    public class ChaptarrV1Proxy : IChaptarrV1Proxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ChaptarrV1Proxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public List<ChaptarrAuthor> GetAuthors(ChaptarrSettings settings)
        {
            return Execute<ChaptarrAuthor>("/api/v1/author", settings);
        }

        public List<ChaptarrBook> GetBooks(ChaptarrSettings settings)
        {
            return Execute<ChaptarrBook>("/api/v1/book", settings);
        }

        public List<ChaptarrProfile> GetProfiles(ChaptarrSettings settings)
        {
            return Execute<ChaptarrProfile>("/api/v1/qualityprofile", settings);
        }

        public List<ChaptarrRootFolder> GetRootFolders(ChaptarrSettings settings)
        {
            return Execute<ChaptarrRootFolder>("/api/v1/rootfolder", settings);
        }

        public List<ChaptarrTag> GetTags(ChaptarrSettings settings)
        {
            return Execute<ChaptarrTag>("/api/v1/tag", settings);
        }

        public ValidationFailure Test(ChaptarrSettings settings)
        {
            try
            {
                GetAuthors(settings);
            }
            catch (HttpException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.Error(ex, "API Key is invalid");
                    return new ValidationFailure("ApiKey", "API Key is invalid");
                }

                if (ex.Response.HasHttpRedirect)
                {
                    _logger.Error(ex, "Chaptarr returned redirect and is invalid");
                    return new ValidationFailure("BaseUrl", "Chaptarr URL is invalid, are you missing a URL base?");
                }

                _logger.Error(ex, "Unable to connect to import list.");
                return new ValidationFailure(string.Empty, $"Unable to connect to import list: {ex.Message}. Check the log surrounding this error for details.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to connect to import list.");
                return new ValidationFailure(string.Empty, $"Unable to connect to import list: {ex.Message}. Check the log surrounding this error for details.");
            }

            return null;
        }

        private List<TResource> Execute<TResource>(string resource, ChaptarrSettings settings)
        {
            if (settings.BaseUrl.IsNullOrWhiteSpace() || settings.ApiKey.IsNullOrWhiteSpace())
            {
                return new List<TResource>();
            }

            var baseUrl = settings.BaseUrl.TrimEnd('/');

            var request = new HttpRequestBuilder(baseUrl).Resource(resource)
                .Accept(HttpAccept.Json)
                .SetHeader("X-Api-Key", settings.ApiKey)
                .Build();

            var response = _httpClient.Get(request);

            if ((int)response.StatusCode >= 300)
            {
                throw new HttpException(response);
            }

            var results = JsonConvert.DeserializeObject<List<TResource>>(response.Content);

            return results ?? new List<TResource>();
        }
    }
}
