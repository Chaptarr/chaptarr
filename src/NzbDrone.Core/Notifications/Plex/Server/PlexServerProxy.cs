using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Notifications.Plex.Server
{
    public interface IPlexServerProxy
    {
        List<PlexSection> GetTvSections(PlexServerSettings settings);
        string Version(PlexServerSettings settings);
        bool CanConnect(PlexServerSettings settings, TimeSpan timeout, out string message);
        void Update(int sectionId, string path, PlexServerSettings settings);
    }

    public class PlexServerProxy : IPlexServerProxy
    {
        private static readonly Regex PlexTokenRegex = new Regex(@"([?&]X-Plex-Token=)[^&]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public PlexServerProxy(IHttpClient httpClient, IConfigService configService, Logger logger)
        {
            _httpClient = httpClient;
            _configService = configService;
            _logger = logger;
        }

        public List<PlexSection> GetTvSections(PlexServerSettings settings)
        {
            var request = BuildRequest("library/sections", HttpMethod.Get, settings);
            var response = ProcessRequest(request);

            CheckForError(response);

            if (response.Contains("_children"))
            {
                return Json.Deserialize<PlexMediaContainerLegacy>(response)
                    .Sections
                    .Where(d => d.Type == "artist")
                    .Select(s => new PlexSection
                    {
                        Id = s.Id,
                        Title = s.Title,
                        Language = s.Language,
                        Locations = s.Locations,
                        Type = s.Type
                    })
                    .ToList();
            }

            return Json.Deserialize<PlexResponse<PlexSectionsContainer>>(response)
                       .MediaContainer
                       .Sections
                       .Where(d => d.Type == "artist")
                       .ToList();
        }

        public void Update(int sectionId, string path, PlexServerSettings settings)
        {
            var resource = $"library/sections/{sectionId}/refresh";
            var request = BuildRequest(resource, HttpMethod.Get, settings);

            if (path.IsNotNullOrWhiteSpace())
            {
                request.AddQueryParam("path", path);
            }

            var response = ProcessRequest(request);

            CheckForError(response);
        }

        public string Version(PlexServerSettings settings)
        {
            var request = BuildRequest("identity", HttpMethod.Get, settings);
            var response = ProcessRequest(request);

            CheckForError(response);

            if (response.Contains("_children"))
            {
                return Json.Deserialize<PlexIdentity>(response)
                           .Version;
            }

            return Json.Deserialize<PlexResponse<PlexIdentity>>(response)
                       .MediaContainer
                       .Version;
        }

        public bool CanConnect(PlexServerSettings settings, TimeSpan timeout, out string message)
        {
            try
            {
                var request = BuildRequest("identity", HttpMethod.Get, settings);
                var response = ProcessRequest(request, timeout);

                CheckForError(response);

                message = "Reachable";
                return true;
            }
            catch (Exception ex)
            {
                message = SanitizeUrl(ex.Message);
                return false;
            }
        }

        private HttpRequestBuilder BuildRequest(string resource, HttpMethod method, PlexServerSettings settings)
        {
            var scheme = settings.UseSsl ? "https" : "http";

            var host = settings.Host;

            // Plex servers present a TLS certificate for *.{suffix}.plex.direct.
            // When the user configures an IP address, using the derived plex.direct hostname
            // preserves TLS validation without requiring global cert validation bypass.
            if (settings.UseSsl &&
                settings.PlexDirectSuffix.IsNotNullOrWhiteSpace() &&
                IPAddress.TryParse(settings.Host, out var ipAddress) &&
                ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                var dashedIp = settings.Host.Replace(".", "-");
                host = $"{dashedIp}.{settings.PlexDirectSuffix}.plex.direct";
            }

            var requestBuilder = new HttpRequestBuilder($"{scheme}://{host.ToUrlHost()}:{settings.Port}{settings.UrlBase}")
                                 .Accept(HttpAccept.Json)
                                 .AddQueryParam("X-Plex-Client-Identifier", _configService.PlexClientIdentifier)
                                 .AddQueryParam("X-Plex-Product", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Platform", "Windows")
                                 .AddQueryParam("X-Plex-Platform-Version", "7")
                                 .AddQueryParam("X-Plex-Device-Name", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Version", BuildInfo.Version.ToString());

            if (settings.AuthToken.IsNotNullOrWhiteSpace())
            {
                requestBuilder.AddQueryParam("X-Plex-Token", settings.AuthToken);
            }

            requestBuilder.ResourceUrl = resource;
            requestBuilder.Method = method;

            return requestBuilder;
        }

        private string ProcessRequest(HttpRequestBuilder requestBuilder, TimeSpan? timeout = null)
        {
            var httpRequest = requestBuilder.Build();

            if (timeout.HasValue)
            {
                httpRequest.RequestTimeout = timeout.Value;
            }

            HttpResponse response;

            _logger.Debug("Url: {0}", SanitizeUrl(httpRequest.Url));

            try
            {
                response = _httpClient.Execute(httpRequest);
            }
            catch (HttpException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new PlexAuthenticationException("Unauthorized - AuthToken is invalid");
                }

                throw new PlexException("Unable to connect to Plex Media Server. Status Code: {0}", ex.Response.StatusCode);
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.TrustFailure)
                {
                    throw new PlexException("Unable to connect to Plex Media Server, certificate validation failed.", ex);
                }

                throw new PlexException($"Unable to connect to Plex Media Server, {ex.Message}", ex);
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
            return SanitizeUrl(raw);
        }

        private static string SanitizeUrl(string raw)
        {
            if (raw.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return PlexTokenRegex.Replace(raw, "$1<redacted>");
        }

        private void CheckForError(string response)
        {
            _logger.Trace("Checking for error");

            if (response.IsNullOrWhiteSpace())
            {
                _logger.Trace("No response body returned, no error detected");
                return;
            }

            var error = response.Contains("_children") ?
                        Json.Deserialize<PlexError>(response) :
                        Json.Deserialize<PlexResponse<PlexError>>(response).MediaContainer;

            if (error != null && !error.Error.IsNullOrWhiteSpace())
            {
                throw new PlexException(error.Error);
            }

            _logger.Trace("No error detected");
        }
    }
}
