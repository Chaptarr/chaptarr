using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.Notifications.Plex.PlexTv
{
    public interface IPlexTvProxy
    {
        PlexTvPinResponse CreatePin(string clientIdentifier);
        string GetAuthToken(string clientIdentifier, int pinId);
        PlexTvUserResponse GetUser(string clientIdentifier, string authToken);
        List<PlexTvResourceResponse> GetResources(string clientIdentifier, string authToken);
        bool Ping(string clientIdentifier, string authToken);
    }

    public class PlexTvProxy : IPlexTvProxy
    {
        private static readonly Regex PlexTokenRegex = new Regex(@"([?&]X-Plex-Token=)[^&]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public PlexTvProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public PlexTvPinResponse CreatePin(string clientIdentifier)
        {
            var request = BuildRequest(clientIdentifier);
            request.ResourceUrl = "/api/v2/pins";
            request.AddQueryParam("strong", true);
            request.Method = HttpMethod.Post;

            if (!Json.TryDeserialize<PlexTvPinResponse>(ProcessRequest(request), out var response))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Unable to create Plex OAuth PIN");
            }

            return response;
        }

        public string GetAuthToken(string clientIdentifier, int pinId)
        {
            var request = BuildRequest(clientIdentifier);
            request.ResourceUrl = $"/api/v2/pins/{pinId}";

            if (!Json.TryDeserialize<PlexTvPinResponse>(ProcessRequest(request), out var response))
            {
                response = new PlexTvPinResponse();
            }

            return response.AuthToken;
        }

        public PlexTvUserResponse GetUser(string clientIdentifier, string authToken)
        {
            var request = BuildRequest(clientIdentifier);
            request.ResourceUrl = "/api/v2/user";
            request.AddQueryParam("X-Plex-Token", authToken);

            if (!Json.TryDeserialize<PlexTvUserResponse>(ProcessRequest(request), out var response))
            {
                response = new PlexTvUserResponse();
            }

            return response;
        }

        public List<PlexTvResourceResponse> GetResources(string clientIdentifier, string authToken)
        {
            var request = BuildRequest(clientIdentifier);
            request.ResourceUrl = "/api/v2/resources";
            request.AddQueryParam("includeHttps", 1);
            request.AddQueryParam("includeRelay", 1);
            request.AddQueryParam("X-Plex-Token", authToken);

            if (!Json.TryDeserialize<List<PlexTvResourceResponse>>(ProcessRequest(request), out var response))
            {
                return new List<PlexTvResourceResponse>();
            }

            return response ?? new List<PlexTvResourceResponse>();
        }

        public bool Ping(string clientIdentifier, string authToken)
        {
            try
            {
                // Allows us to tell plex.tv that we're still active and tokens should not be expired.
                var request = BuildRequest(clientIdentifier);

                request.ResourceUrl = "/api/v2/ping";
                request.AddQueryParam("X-Plex-Token", authToken);

                ProcessRequest(request);

                return true;
            }
            catch (Exception e)
            {
                // Catch all exceptions and log at trace, this information could be interesting in debugging, but expired tokens will be handled elsewhere.
                _logger.Trace(e, "Unable to ping plex.tv");
            }

            return false;
        }

        private HttpRequestBuilder BuildRequest(string clientIdentifier)
        {
            var requestBuilder = new HttpRequestBuilder("https://plex.tv")
                                 .Accept(HttpAccept.Json)
                                 .AddQueryParam("X-Plex-Client-Identifier", clientIdentifier)
                                 .AddQueryParam("X-Plex-Product", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Platform", "Windows")
                                 .AddQueryParam("X-Plex-Platform-Version", "7")
                                 .AddQueryParam("X-Plex-Device-Name", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Version", BuildInfo.Version.ToString());

            return requestBuilder;
        }

        private string ProcessRequest(HttpRequestBuilder requestBuilder)
        {
            var httpRequest = requestBuilder.Build();

            HttpResponse response;

            _logger.Debug("Url: {0}", SanitizeUrl(httpRequest.Url));

            try
            {
                response = _httpClient.Execute(httpRequest);
            }
            catch (HttpException ex)
            {
                throw new NzbDroneClientException(ex.Response.StatusCode, "Unable to connect to plex.tv");
            }
            catch (WebException)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Unable to connect to plex.tv");
            }

            return response.Content;
        }

        private static string SanitizeUrl(HttpUri url)
        {
            if (url == null)
            {
                return string.Empty;
            }

            var raw = url.FullUri ?? url.ToString();
            return PlexTokenRegex.Replace(raw, "$1<redacted>");
        }
    }
}
