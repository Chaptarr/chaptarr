using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentValidation.Results;
using MonoTorrent;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouse : HttpIndexerBase<MyAnonaMouseSettings>
    {
        private readonly IMamUnsatisfiedSlotReservationRepository _slotReservationRepository;

        public override string Name => "MyAnonaMouse";

        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override bool SupportsRss => true; // Uses MAM JSON search endpoint for RSS-style sync
        public override bool SupportsSearch => true;
        public override int PageSize => 100;

        public MyAnonaMouse(IIndexerHttpClientFactory httpClientFactory, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService,
                            IMamUnsatisfiedSlotReservationRepository slotReservationRepository, Logger logger)
            : base(httpClientFactory, indexerStatusService, configService, parsingService, logger)
        {
            _slotReservationRepository = slotReservationRepository;
        }

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                var config = new MyAnonaMouseSettings
                {
                    SeedTimeHours = 72
                };

                yield return new IndexerDefinition
                {
                    Name = GetType().Name,
                    EnableRss = true, // Enabled by default to match synthetic RSS/recent support
                    EnableAutomaticSearch = true, // Always enabled by default
                    EnableInteractiveSearch = true, // Always enabled by default
                    Implementation = GetType().Name,
                    Settings = config
                };
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new MyAnonaMouseRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new MyAnonaMouseJsonParser(Settings);
        }

        public override HttpRequest GetDownloadRequest(string link)
        {
            var uri = new Uri(link);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            var tid = query["tid"];
            var canUseWedge = string.Equals(query["canUseToken"], "true", StringComparison.OrdinalIgnoreCase);
            var isAudiobook = string.Equals(query["isAudiobook"], "true", StringComparison.OrdinalIgnoreCase);

            query.Remove("canUseToken");
            query.Remove("isAudiobook");
            query.Remove("fl");

            var applyWedge = Settings.UseFreeleechWedge == (int)MyAnonaMouseFreeleechWedgeAction.Preferred &&
                             canUseWedge &&
                             (!Settings.UseFreeleechOnlyForAudiobooks || isAudiobook);
            var queryString = query.ToString();
            var downloadLink = uri.GetLeftPart(UriPartial.Path);
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                downloadLink += "?" + queryString;
            }

            if (applyWedge)
            {
                downloadLink += string.IsNullOrWhiteSpace(queryString) ? "?fl" : "&fl";
                _logger.Debug("Requesting a MAM personal freeleech wedge for torrent {0}", tid);
            }

            var request = new HttpRequest(downloadLink);
            request.Headers.Set("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}");
            var requestHostMatchesMam = Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var baseUri) &&
                                        string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);

            // Ensure required session cookies are present for authenticated downloads, but never send
            // MAM session cookies to an off-domain redirect target.
            if (requestHostMatchesMam && !string.IsNullOrWhiteSpace(Settings.MamId))
            {
                request.Cookies["mam_id"] = Settings.MamId; // idempotent
            }
            // Include mam_ssl if configured (many sessions require it)
            if (requestHostMatchesMam && Settings.MamSsl)
            {
                request.Cookies["mam_ssl"] = "1"; // idempotent
            }
            // Persist cookies (request + response) so updated mam_id from MAM is kept
            request.StoreRequestCookie = true;
            request.StoreResponseCookie = true;
            // Add Referer for sites that verify flow
            if (!string.IsNullOrWhiteSpace(tid))
            {
                var referer = Settings.BaseUrl.TrimEnd('/') + "/t/" + tid;
                request.Headers.Set("Referer", referer);
                _logger.Debug("MAM_DOWNLOAD: Added Referer header to {0}", referer);
            }
            _logger.Debug("MAM_DOWNLOAD: Created download request for {0}", downloadLink);
            _logger.Trace("MAM_DOWNLOAD: Cookie configured - mam_id present: {0}, length: {1}",
                !string.IsNullOrWhiteSpace(Settings.MamId),
                Settings.MamId?.Length ?? 0);
            _logger.Trace("MAM_DOWNLOAD: mam_ssl enabled: {0}", Settings.MamSsl);

            // Force the request to not follow redirects so we can see what MAM returns
            request.AllowAutoRedirect = false;

            return request;
        }

        public override async Task<HttpResponse> ExecuteDownloadRequestAsync(HttpRequest request)
        {
            var response = await base.ExecuteDownloadRequestAsync(request);
            if (!HasFreeleechParameter(request?.Url?.FullUri) || IsTorrentResponse(response))
            {
                ConfirmServedTorrent(request, response);
                return response;
            }

            _logger.Debug("MAM did not return a torrent for the preferred-wedge request; retrying the download without forcing a wedge");
            var fallbackRequest = GetDownloadRequest(RemoveFreeleechParameter(request.Url.FullUri));
            fallbackRequest.RateLimitKey = request.RateLimitKey;
            fallbackRequest.RateLimit = request.RateLimit;
            fallbackRequest.RequestTimeout = request.RequestTimeout;
            fallbackRequest.Headers.Accept = request.Headers.Accept;

            var fallbackResponse = await base.ExecuteDownloadRequestAsync(fallbackRequest);
            ConfirmServedTorrent(fallbackRequest, fallbackResponse);
            return fallbackResponse;
        }

        protected override async Task<ValidationFailure> TestConnection()
        {
            try
            {
                // MAM_TRACE: Log test attempt
                _logger.Trace("MAM_TEST_START: Testing MAM indexer connection");

                var generator = GetRequestGenerator();
                var parser = GetParser();

                var searchCriteria = new IndexerSearch.Definitions.BookSearchCriteria
                {
                    Author = new Books.Author { Name = "Test" },
                    BookTitle = "Test"
                };

                var requests = generator.GetSearchRequests(searchCriteria);
                var firstRequest = requests.GetAllTiers().FirstOrDefault()?.FirstOrDefault();

                if (firstRequest == null)
                {
                    _logger.Trace("MAM_TEST_ERROR: No request generated");
                    return new ValidationFailure(string.Empty, "Failed to generate test request. Check your settings.");
                }

                _logger.Trace("MAM_TEST_REQUEST: Testing JSON API connectivity (should use configured proxy if set)");
                var releases = await FetchPage(firstRequest, parser);

                _logger.Trace("MAM_TEST_SUCCESS: JSON API test successful, found {0} results", releases.Count);

                // After successful connection, refresh the user's account status.
                try
                {
                    await RefreshAccountStatus();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to check MAM account status, continuing without it");
                }

                // Provide a non-fatal, informational success message for the test modal
                try
                {
                    var cls = string.IsNullOrWhiteSpace(Settings.UserClass) ? "Unknown" : Settings.UserClass;
                    var membership = Settings.IsVip
                        ? $"\u2713 VIP Member - Class: {cls}"
                        : $"User Class: {cls} (Standard member)";
                    var slots = Settings.UnsatisfiedCount.HasValue && Settings.UnsatisfiedLimit.HasValue
                        ? $"; unsatisfied torrents: {Settings.UnsatisfiedCount}/{Settings.UnsatisfiedLimit}"
                        : string.Empty;
                    var message = membership + slots;

                    // Attach to the temporary definition used during Test so the controller can surface it
                    Definition.Message = new ProviderMessage(message, ProviderMessageType.Info);
                }
                catch { }

                return null; // Success
            }
            catch (global::System.Exception ex)
            {
                _logger.Trace("MAM_TEST_EXCEPTION: Test failed with exception - {0}", ex.Message);
                _logger.Warn(ex, "MAM indexer test failed");

                if (ex.Message.Contains("403") || ex.Message.Contains("Forbidden"))
                {
                    return new ValidationFailure(string.Empty, "Authentication failed. Verify your mam_id is correct (found under Preferences > Security on MAM). If using a proxy, ensure it's configured in Settings > General > Proxy.");
                }

                return new ValidationFailure(string.Empty, $"Connection test failed: {ex.Message}");
            }
        }

        public async Task<MyAnonaMouseAccountStatus> RefreshAccountStatus()
        {
            var request = new HttpRequestBuilder($"{Settings.BaseUrl.TrimEnd('/')}/jsonLoad.php?snatch_summary&pretty")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}")
                .Build();

            request.RateLimitKey = Definition?.Id > 0 ? Definition.Id.ToString() : null;
            request.Cookies["mam_id"] = Settings.MamId;
            if (Settings.MamSsl)
            {
                request.Cookies["mam_ssl"] = "1";
            }

            var response = await _httpClient.ExecuteAsync(request);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Failed to fetch MAM user data: HTTP {(int)response.StatusCode} {response.StatusCode}");
            }

            var userData = JsonConvert.DeserializeObject<MyAnonaMouseUserDataResponse>(response.Content);
            var className = userData?.ClassName;

            if (string.IsNullOrWhiteSpace(className))
            {
                throw new InvalidOperationException("MAM user data response did not include a user class");
            }

            if (userData.Unsatisfied == null || userData.Unsatisfied.Count < 0 || userData.Unsatisfied.Limit <= 0 || userData.Created <= 0)
            {
                throw new InvalidOperationException("MAM user data response did not include a valid unsatisfied-torrent summary");
            }

            DateTime snapshotCreatedUtc;
            try
            {
                snapshotCreatedUtc = DateTimeOffset.FromUnixTimeSeconds(userData.Created).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidOperationException("MAM user data response included an invalid summary timestamp", ex);
            }

            var refreshedUtc = DateTime.UtcNow;
            var isVip = IsVipClass(className);
            Settings.UserClass = className;
            Settings.IsVip = isVip;
            Settings.UnsatisfiedCount = userData.Unsatisfied.Count;
            Settings.UnsatisfiedLimit = userData.Unsatisfied.Limit;
            Settings.UnsatisfiedSnapshotUtc = snapshotCreatedUtc;
            Settings.UnsatisfiedStatusRefreshedUtc = refreshedUtc;

            _logger.Debug("MAM account status detected: {0} (VIP: {1}), unsatisfied {2}/{3}", className, isVip, userData.Unsatisfied.Count, userData.Unsatisfied.Limit);

            return new MyAnonaMouseAccountStatus
            {
                UserClass = className,
                IsVip = isVip,
                UnsatisfiedCount = userData.Unsatisfied.Count,
                UnsatisfiedLimit = userData.Unsatisfied.Limit,
                SnapshotCreatedUtc = snapshotCreatedUtc,
                RefreshedUtc = refreshedUtc
            };
        }

        private static bool IsVipClass(string className)
        {
            return className.IndexOf("VIP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasFreeleechParameter(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)[0])
                .Any(key => string.Equals(Uri.UnescapeDataString(key), "fl", StringComparison.OrdinalIgnoreCase));
        }

        private static string RemoveFreeleechParameter(string url)
        {
            var uri = new Uri(url);
            var queryString = string.Join(
                "&",
                uri.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !string.Equals(
                        Uri.UnescapeDataString(part.Split('=', 2)[0]),
                        "fl",
                        StringComparison.OrdinalIgnoreCase)));

            return uri.GetLeftPart(UriPartial.Path) +
                   (string.IsNullOrWhiteSpace(queryString) ? string.Empty : "?" + queryString);
        }

        private void ConfirmServedTorrent(HttpRequest request, HttpResponse response)
        {
            if (_slotReservationRepository == null ||
                Definition?.Id <= 0 ||
                !IsTorrentResponse(response) ||
                !Uri.TryCreate(request?.Url?.FullUri, UriKind.Absolute, out var uri))
            {
                return;
            }

            var torrentId = System.Web.HttpUtility.ParseQueryString(uri.Query)["tid"];
            if (string.IsNullOrWhiteSpace(torrentId))
            {
                return;
            }

            var reservation = _slotReservationRepository.Find(Definition.Id, torrentId);
            if (reservation == null || reservation.ConfirmedUtc.HasValue)
            {
                return;
            }

            reservation.ConfirmedUtc = DateTime.UtcNow;
            _slotReservationRepository.Update(reservation);
            _logger.Debug(
                "Confirmed MAM slot reservation for torrent {0} on indexer '{1}' when MAM returned a valid torrent payload",
                torrentId,
                Definition.Name);
        }

        private static bool IsTorrentResponse(HttpResponse response)
        {
            if (response?.StatusCode != HttpStatusCode.OK || response.ResponseData?.Length == 0)
            {
                return false;
            }

            try
            {
                Torrent.Load(response.ResponseData);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public class MyAnonaMouseAccountStatus
    {
        public string UserClass { get; set; }
        public bool IsVip { get; set; }
        public int UnsatisfiedCount { get; set; }
        public int UnsatisfiedLimit { get; set; }
        public DateTime SnapshotCreatedUtc { get; set; }
        public DateTime RefreshedUtc { get; set; }
    }

    public class MyAnonaMouseUserDataResponse
    {
        [JsonProperty("classname")]
        public string ClassName { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("unsat")]
        public MyAnonaMouseUnsatisfiedSummary Unsatisfied { get; set; }
    }

    public class MyAnonaMouseUnsatisfiedSummary
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }
    }

}
