using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Download.Clients.Transmission
{
    public interface ITransmissionProxy
    {
        List<TransmissionTorrent> GetTorrents(TransmissionSettings settings);
        TransmissionTorrent GetTorrentDetails(string hashString, TransmissionSettings settings);
        void AddTorrentFromUrl(string torrentUrl, string downloadDirectory, TransmissionSettings settings);
        void AddTorrentFromData(byte[] torrentData, string downloadDirectory, TransmissionSettings settings);
        void SetTorrentSeedingConfiguration(string hash, TorrentSeedConfiguration seedConfiguration, TransmissionSettings settings);
        TransmissionConfig GetConfig(TransmissionSettings settings);
        string GetProtocolVersion(TransmissionSettings settings);
        string GetClientVersion(TransmissionSettings settings);
        void RemoveTorrent(string hash, bool removeData, TransmissionSettings settings);
        void MoveTorrentToTopInQueue(string hashString, TransmissionSettings settings);
    }

    public class TransmissionProxy : ITransmissionProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        private ICached<string> _authSessionIDCache;

        public TransmissionProxy(ICacheManager cacheManager, IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            _authSessionIDCache = cacheManager.GetCache<string>(GetType(), "authSessionID");
        }

        public List<TransmissionTorrent> GetTorrents(TransmissionSettings settings)
        {
            var result = GetTorrentStatus(settings);

            var torrents = ((JArray)result.Arguments["torrents"]).ToObject<List<TransmissionTorrent>>();

            return torrents;
        }

        public TransmissionTorrent GetTorrentDetails(string hashString, TransmissionSettings settings)
        {
            var result = GetTorrentStatus(new[] { hashString }, settings, new[]
            {
                "hashString",
                "name",
                "downloadDir",
                "files",
                "fileStats"
            });

            var torrents = ((JArray)result.Arguments["torrents"]).ToObject<List<TransmissionTorrent>>();

            return torrents.FirstOrDefault();
        }

        public void AddTorrentFromUrl(string torrentUrl, string downloadDirectory, TransmissionSettings settings)
        {
            var arguments = new Dictionary<string, object>();
            arguments.Add("filename", torrentUrl);
            arguments.Add("paused", settings.AddPaused);

            if (!downloadDirectory.IsNullOrWhiteSpace())
            {
                arguments.Add("download-dir", downloadDirectory);
            }

            ProcessRequest("torrent-add", arguments, settings);
        }

        public void AddTorrentFromData(byte[] torrentData, string downloadDirectory, TransmissionSettings settings)
        {
            var arguments = new Dictionary<string, object>();
            arguments.Add("metainfo", Convert.ToBase64String(torrentData));
            arguments.Add("paused", settings.AddPaused);

            if (!downloadDirectory.IsNullOrWhiteSpace())
            {
                arguments.Add("download-dir", downloadDirectory);
            }

            ProcessRequest("torrent-add", arguments, settings);
        }

        public void SetTorrentSeedingConfiguration(string hash, TorrentSeedConfiguration seedConfiguration, TransmissionSettings settings)
        {
            if (seedConfiguration == null)
            {
                return;
            }

            var arguments = new Dictionary<string, object>();
            arguments.Add("ids", new[] { hash });

            if (seedConfiguration.Ratio != null)
            {
                arguments.Add("seedRatioLimit", seedConfiguration.Ratio.Value);
                arguments.Add("seedRatioMode", 1);
            }

            if (seedConfiguration.SeedTime != null)
            {
                arguments.Add("seedIdleLimit", Convert.ToInt32(seedConfiguration.SeedTime.Value.TotalMinutes));
                arguments.Add("seedIdleMode", 1);
            }

            ProcessRequest("torrent-set", arguments, settings);
        }

        public string GetProtocolVersion(TransmissionSettings settings)
        {
            var config = GetConfig(settings);

            return config.RpcVersion;
        }

        public string GetClientVersion(TransmissionSettings settings)
        {
            var config = GetConfig(settings);

            return config.Version;
        }

        public TransmissionConfig GetConfig(TransmissionSettings settings)
        {
            // Gets the transmission version.
            var result = GetSessionVariables(settings);

            return Json.Deserialize<TransmissionConfig>(result.Arguments.ToJson());
        }

        public void RemoveTorrent(string hashString, bool removeData, TransmissionSettings settings)
        {
            var arguments = new Dictionary<string, object>();
            arguments.Add("ids", new string[] { hashString });
            arguments.Add("delete-local-data", removeData);

            ProcessRequest("torrent-remove", arguments, settings);
        }

        public void MoveTorrentToTopInQueue(string hashString, TransmissionSettings settings)
        {
            var arguments = new Dictionary<string, object>();
            arguments.Add("ids", new string[] { hashString });

            ProcessRequest("queue-move-top", arguments, settings);
        }

        private TransmissionResponse GetSessionVariables(TransmissionSettings settings)
        {
            // Retrieve transmission information such as the default download directory, bandwith throttling and seed ratio.
            return ProcessRequest("session-get", null, settings);
        }

        private TransmissionResponse GetSessionStatistics(TransmissionSettings settings)
        {
            return ProcessRequest("session-stats", null, settings);
        }

        private TransmissionResponse GetTorrentStatus(TransmissionSettings settings)
        {
            return GetTorrentStatus(null, settings);
        }

        private TransmissionResponse GetTorrentStatus(IEnumerable<string> hashStrings, TransmissionSettings settings, string[] fields = null)
        {
            fields ??= new string[]
            {
                "id",
                "hashString", // Unique torrent ID. Use this instead of the client id?
                "name",
                "downloadDir",
                "totalSize",
                "leftUntilDone",
                "isFinished",
                "eta",
                "status",
                "secondsDownloading",
                "secondsSeeding",
                "errorString",
                "uploadedEver",
                "downloadedEver",
                "seedRatioLimit",
                "seedRatioMode",
                "seedIdleLimit",
                "seedIdleMode",
                "fileCount"
            };

            var arguments = new Dictionary<string, object>();
            arguments.Add("fields", fields);

            if (hashStrings != null)
            {
                arguments.Add("ids", hashStrings);
            }

            var result = ProcessRequest("torrent-get", arguments, settings);

            return result;
        }

        private HttpRequestBuilder BuildRequest(TransmissionSettings settings)
        {
            var requestBuilder = new HttpRequestBuilder(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase)
                .Resource("rpc")
                .Accept(HttpAccept.Json);

            requestBuilder.LogResponseContent = true;
            requestBuilder.NetworkCredential = new BasicNetworkCredential(settings.Username, settings.Password);
            requestBuilder.AllowAutoRedirect = false;

            return requestBuilder;
        }

        private void AuthenticateClient(HttpRequestBuilder requestBuilder, TransmissionSettings settings, bool reauthenticate = false)
        {
            var authKey = $"{requestBuilder.BaseUrl}:{settings.Password}";

            var sessionId = _authSessionIDCache.Find(authKey);

            if (sessionId == null || reauthenticate)
            {
                _authSessionIDCache.Remove(authKey);

                var authLoginRequest = BuildRequest(settings).Build();
                authLoginRequest.SuppressHttpError = true;

                var response = _httpClient.Execute(authLoginRequest);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.MovedPermanently:
                        var url = response.Headers.GetSingleValue("Location");

                        throw new DownloadClientUnavailableException("Remote site redirected to " + url);
                    case HttpStatusCode.Forbidden:
                        throw new DownloadClientUnavailableException($"Failed to authenticate with Transmission. It may be necessary to add {BuildInfo.AppName}'s IP address to RPC whitelist.");
                    case HttpStatusCode.Conflict:
                        sessionId = SanitizeSessionId(response.Headers.GetSingleValue("X-Transmission-Session-Id"));

                        if (sessionId == null)
                        {
                            throw new DownloadClientUnavailableException("Remote host did not return a Session Id.");
                        }

                        break;
                    default:
                        throw new DownloadClientAuthenticationException("Failed to authenticate with Transmission.");
                }

                _logger.Debug("Transmission authentication succeeded. Session id: {0}", FormatSessionIdForLog(sessionId));

                _authSessionIDCache.Set(authKey, sessionId);
            }

            requestBuilder.SetHeader("X-Transmission-Session-Id", sessionId);
        }

        private static string SanitizeSessionId(string sessionId)
        {
            if (sessionId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var sanitized = sessionId.Trim().Trim('"');

            // Some HTTP stacks may coalesce multiple header values using ';'
            var separatorIndex = sanitized.IndexOf(';');
            if (separatorIndex >= 0)
            {
                sanitized = sanitized.Substring(0, separatorIndex).Trim();
            }

            return sanitized.IsNullOrWhiteSpace() ? null : sanitized;
        }

        private static string FormatSessionIdForLog(string sessionId)
        {
            if (sessionId.IsNullOrWhiteSpace())
            {
                return "<none>";
            }

            if (sessionId.Length <= 8)
            {
                return $"{sessionId} (len={sessionId.Length})";
            }

            return $"{sessionId.Substring(0, 8)}… (len={sessionId.Length})";
        }

        public TransmissionResponse ProcessRequest(string action, object arguments, TransmissionSettings settings)
        {
            try
            {
                var requestBuilder = BuildRequest(settings);
                var authKey = $"{requestBuilder.BaseUrl}:{settings.Password}";
                requestBuilder.Headers.ContentType = "application/json";
                requestBuilder.SuppressHttpError = true;

                AuthenticateClient(requestBuilder, settings);

                var data = new Dictionary<string, object>();
                data.Add("method", action);

                if (arguments != null)
                {
                    data.Add("arguments", arguments);
                }

                var requestBody = data.ToJson();
                HttpResponse response = null;

                // Transmission RPC requires a valid X-Transmission-Session-Id header.
                // When missing/invalid, it responds with 409 Conflict and includes a fresh session id
                // in the X-Transmission-Session-Id response header. Retry a few times to handle
                // session id rotation and to provide clearer errors when proxies strip headers.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var sentSessionId = SanitizeSessionId(requestBuilder.Headers.GetSingleValue("X-Transmission-Session-Id"));
                    var request = requestBuilder.Post().Build();
                    request.SetContent(requestBody);
                    request.ContentSummary = string.Format("{0}(...)", action);

                    response = _httpClient.Execute(request);

                    // Handle redirects explicitly to avoid parsing HTML redirect pages as JSON
                    if (response.HasHttpRedirect)
                    {
                        var url = response.Headers.GetSingleValue("Location");
                        throw new DownloadClientUnavailableException("Remote site redirected to " + url);
                    }

                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        var sessionId = SanitizeSessionId(response.Headers.GetSingleValue("X-Transmission-Session-Id"));

                        _logger.Debug(
                            "Transmission RPC 409 for '{0}' (attempt {1}). Sent session id {2}, received {3}",
                            action,
                            attempt + 1,
                            FormatSessionIdForLog(sentSessionId),
                            FormatSessionIdForLog(sessionId));

                        if (sessionId.IsNullOrWhiteSpace())
                        {
                            // Fall back to the legacy auth flow (GET -> 409 -> session id) if needed
                            AuthenticateClient(requestBuilder, settings, true);
                        }
                        else
                        {
                            _authSessionIDCache.Set(authKey, sessionId);
                            requestBuilder.SetHeader("X-Transmission-Session-Id", sessionId);
                        }

                        continue;
                    }

                    break;
                }

                if (response == null)
                {
                    throw new DownloadClientUnavailableException("No response received from Transmission.");
                }

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var attemptedUrl = response.Request?.Url?.FullUri ?? "(unknown)";
                    throw new DownloadClientUnavailableException(
                        $"Transmission RPC at {attemptedUrl} repeatedly returned 409 Conflict even after session id negotiation. " +
                        "If you are using a reverse proxy, ensure it forwards the 'X-Transmission-Session-Id' header. " +
                        "Also verify the Url Base (commonly '/transmission/').");
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new DownloadClientAuthenticationException("User authentication failed.");
                }

                // Some reverse proxies or incorrect Url Base settings may return HTML (e.g. a login page or 404)
                // which leads to JSON parsing errors like: "Unexpected character encountered while parsing value: <"
                var content = response.Content ?? string.Empty;
                var trimmed = content.TrimStart();
                var looksLikeJson = trimmed.StartsWith("{") || trimmed.StartsWith("[");

                if (!looksLikeJson)
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new DownloadClientUnavailableException($"Access forbidden by Transmission. It may be necessary to add {BuildInfo.AppName}'s IP address to the RPC whitelist.");
                    }

                    var attemptedUrl = response.Request?.Url?.FullUri ?? "(unknown)";
                    var contentType = response.Headers.ContentType ?? "unknown";
                    throw new DownloadClientUnavailableException(
                        $"Unexpected non-JSON response from Transmission RPC at {attemptedUrl} (status {(int)response.StatusCode}). " +
                        "Verify the Url Base is correct (typically '/transmission/') and that the RPC endpoint '/rpc' is reachable. " +
                        $"Received Content-Type: {contentType}");
                }

                TransmissionResponse transmissionResponse;
                try
                {
                    transmissionResponse = Json.Deserialize<TransmissionResponse>(content);
                }
                catch (Exception ex)
                {
                    var attemptedUrl = response.Request?.Url?.FullUri ?? "(unknown)";
                    throw new DownloadClientUnavailableException($"Failed to parse Transmission RPC response from {attemptedUrl}: {ex.Message}");
                }

                if (transmissionResponse == null)
                {
                    throw new TransmissionException("Unexpected response");
                }
                else if (transmissionResponse.Result != "success")
                {
                    throw new TransmissionException(transmissionResponse.Result);
                }

                return transmissionResponse;
            }
            catch (HttpException ex)
            {
                throw new DownloadClientException("Unable to connect to Transmission, please check your settings", ex);
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.TrustFailure)
                {
                    throw new DownloadClientUnavailableException("Unable to connect to Transmission, certificate validation failed.", ex);
                }

                throw new DownloadClientUnavailableException("Unable to connect to Transmission, please check your settings", ex);
            }
        }
    }
}
