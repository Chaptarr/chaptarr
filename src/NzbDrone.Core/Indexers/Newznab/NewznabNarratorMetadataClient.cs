using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Indexers.Newznab
{
    internal enum NewznabNfoEndpoint
    {
        Unknown = 0,
        Info = 1,
        GetNfo = 2,
        Unsupported = 3
    }

    internal sealed class NewznabNarratorMetadata
    {
        public string Narrator { get; init; }
        public string Duration { get; init; }
        public bool IsGraphicAudio { get; init; }
    }

    internal sealed class NewznabNarratorMetadataClient
    {
        private sealed class CacheEntry
        {
            public CacheEntry(NewznabNarratorMetadata metadata)
            {
                Metadata = metadata;
            }

            public NewznabNarratorMetadata Metadata { get; }
        }

        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromHours(12);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(1);
        private const int MaxCacheEntries = 5000;

        private static readonly Regex IdFromQueryRegex = new Regex(@"[?&]id=([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IdFromGetNzbRegex = new Regex(@"/getnzb/([^/?#]+?)\.nzb\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IdFromDetailsRegex = new Regex(@"/details/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IdFromNfoPathRegex = new Regex(@"/nfo/([^/?#&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NarratorLineRegex = new Regex(@"^(?:narration|narrator|narrated\s+by|read\s+by)\s*[:>]+\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LengthLineRegex = new Regex(@"^(?:length|duration|runtime)\s*[:>]+\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IIndexerHttpClient _httpClient;
        private readonly NewznabSettings _settings;
        private readonly int _indexerId;
        private readonly TimeSpan _rateLimit;
        private readonly int? _proxyId;
        private readonly ICached<CacheEntry> _metadataCache;
        private readonly ICached<NewznabNfoEndpoint> _endpointCache;

        public NewznabNarratorMetadataClient(IIndexerHttpClient httpClient, NewznabSettings settings, int indexerId, TimeSpan rateLimit, ICacheManager cacheManager, int? proxyId = null)
        {
            _httpClient = httpClient;
            _settings = settings;
            _indexerId = indexerId;
            _rateLimit = rateLimit;
            _proxyId = proxyId;
            _metadataCache = cacheManager.GetCache<CacheEntry>(typeof(NewznabNarratorMetadataClient), "metadata");
            _endpointCache = cacheManager.GetCache<NewznabNfoEndpoint>(typeof(NewznabNarratorMetadataClient), "endpoint");
        }

        public bool? SupportsMetadata => _endpointCache.Find(BuildEndpointCacheKey()) switch
        {
            NewznabNfoEndpoint.Info or NewznabNfoEndpoint.GetNfo => true,
            NewznabNfoEndpoint.Unsupported => false,
            _ => null
        };

        public void Test(IEnumerable<ReleaseInfo> releases)
        {
            var release = releases
                .Where(r => r.HasNfo != false && ExtractReleaseId(r).IsNotNullOrWhiteSpace())
                .OrderByDescending(r => r.HasNfo == true)
                .FirstOrDefault();

            if (release != null)
            {
                TryPopulate(release, force: true);
            }
        }

        public bool TryPopulate(ReleaseInfo release, bool force = false)
        {
            if (release == null || release.HasNfo == false)
            {
                return false;
            }

            var releaseId = ExtractReleaseId(release);
            var baseUrl = GetEnhancedBaseUrl();
            var apiPath = _settings?.ApiPath;
            if (releaseId.IsNullOrWhiteSpace() || baseUrl.IsNullOrWhiteSpace() || apiPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var endpointKey = BuildEndpointCacheKey();
            var cacheKey = $"{endpointKey}|{releaseId}";
            if (!force)
            {
                if (SupportsMetadata == false)
                {
                    return false;
                }

                if (TryGetCacheEntry(cacheKey, out var cached))
                {
                    return cached.Metadata != null && Apply(release, cached.Metadata);
                }
            }

            var first = DetermineEndpointHint(release, endpointKey) == NewznabNfoEndpoint.GetNfo
                ? NewznabNfoEndpoint.GetNfo : NewznabNfoEndpoint.Info;
            var second = first == NewznabNfoEndpoint.Info ? NewznabNfoEndpoint.GetNfo : NewznabNfoEndpoint.Info;
            var unsupported = 0;
            NewznabNarratorMetadata metadata = null;

            foreach (var endpoint in new[] { first, second })
            {
                var text = FetchNfoText(baseUrl, apiPath, GetEnhancedApiKey(), releaseId, endpoint, out var errorCode);
                if (errorCode is 202 or 203)
                {
                    unsupported++;
                    continue;
                }

                if (errorCode == 300)
                {
                    break;
                }

                if (text.IsNotNullOrWhiteSpace())
                {
                    _endpointCache.Set(endpointKey, endpoint, PositiveCacheTtl);
                    metadata = ParseNfo(text);
                    if (metadata != null || force)
                    {
                        break;
                    }
                }
            }

            if (unsupported == 2)
            {
                _endpointCache.Set(endpointKey, NewznabNfoEndpoint.Unsupported, NegativeCacheTtl);
            }

            if (!force || metadata != null)
            {
                SetCacheEntry(cacheKey, metadata, metadata == null ? NegativeCacheTtl : PositiveCacheTtl);
            }

            return metadata != null && Apply(release, metadata);
        }

        private bool Apply(ReleaseInfo release, NewznabNarratorMetadata metadata)
        {
            var changed = false;

            if (release.Narrator.IsNullOrWhiteSpace() && !metadata.Narrator.IsNullOrWhiteSpace())
            {
                release.Narrator = metadata.Narrator;
                changed = true;
            }

            if (release.Duration.IsNullOrWhiteSpace() && !metadata.Duration.IsNullOrWhiteSpace())
            {
                release.Duration = metadata.Duration;
                changed = true;
            }

            if (!release.IsGraphicAudio && metadata.IsGraphicAudio)
            {
                release.IsGraphicAudio = true;
                changed = true;
            }

            return changed;
        }

        private bool TryGetCacheEntry(string cacheKey, out CacheEntry entry)
        {
            entry = cacheKey.IsNullOrWhiteSpace() ? null : _metadataCache.Find(cacheKey);
            return entry != null;
        }

        private void SetCacheEntry(string cacheKey, NewznabNarratorMetadata metadata, TimeSpan ttl)
        {
            if (cacheKey.IsNullOrWhiteSpace())
            {
                return;
            }

            _metadataCache.Set(cacheKey, new CacheEntry(metadata), ttl);
            TrimCacheIfNeeded();
        }

        private void TrimCacheIfNeeded()
        {
            if (_metadataCache.Count <= MaxCacheEntries)
            {
                return;
            }

            _metadataCache.ClearExpired();

            if (_metadataCache.Count > MaxCacheEntries)
            {
                _metadataCache.Clear();
            }
        }

        private string BuildEndpointCacheKey()
        {
            var identity = $"{GetEnhancedBaseUrl()?.TrimEnd('/')}|{_settings?.ApiPath}|{GetEnhancedApiKey()}|{_proxyId}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        private string GetEnhancedBaseUrl()
        {
            return _settings?.NarratorMetadataBaseUrl.IsNotNullOrWhiteSpace() == true
                ? _settings.NarratorMetadataBaseUrl
                : _settings?.BaseUrl;
        }

        private string GetEnhancedApiKey()
        {
            return _settings?.NarratorMetadataApiKey.IsNotNullOrWhiteSpace() == true
                ? _settings.NarratorMetadataApiKey
                : _settings?.ApiKey;
        }

        private NewznabNfoEndpoint DetermineEndpointHint(ReleaseInfo release, string endpointKey)
        {
            var cachedEndpoint = _endpointCache.Find(endpointKey);
            if (cachedEndpoint != NewznabNfoEndpoint.Unknown)
            {
                return cachedEndpoint;
            }

            if (release?.NfoUrl.IsNullOrWhiteSpace() != false)
            {
                return NewznabNfoEndpoint.Unknown;
            }

            if (release.NfoUrl.IndexOf("t=getnfo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NewznabNfoEndpoint.GetNfo;
            }

            if (release.NfoUrl.IndexOf("t=info", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NewznabNfoEndpoint.Info;
            }

            return NewznabNfoEndpoint.Unknown;
        }

        private string FetchNfoText(string baseUrl, string apiPath, string apiKey, string releaseId, NewznabNfoEndpoint endpoint, out int? errorCode)
        {
            errorCode = null;
            var getNfo = endpoint == NewznabNfoEndpoint.GetNfo;
            var url = BuildApiUrl(baseUrl, apiPath, getNfo ? "getnfo" : "info", releaseId, apiKey, includeRaw: getNfo);
            var request = new HttpRequest(url)
            {
                RateLimitKey = _indexerId > 0 ? _indexerId.ToString(CultureInfo.InvariantCulture) : null,
                RateLimit = _rateLimit,
                LogHttpError = false,
                SuppressHttpError = true
            };
            var response = _httpClient.Execute(request) ?? throw new InvalidOperationException("Indexer returned no NFO response");

            // Newznab error documents may use either HTTP 200 or HTTP 400/404.
            if ((!response.HasHttpError || response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound) &&
                TryParseError(response, out var code))
            {
                errorCode = code;
                return null;
            }

            if (response.HasHttpError)
            {
                throw new HttpException(response);
            }

            var contentType = response.Headers?.ContentType ?? string.Empty;
            var content = response.Content ?? string.Empty;
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                content.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Indexer returned an unexpected NFO response format");
            }

            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                return TryExtractRssDescription(content, out var description) ? description : null;
            }

            return content;
        }

        private static bool TryParseError(HttpResponse response, out int code)
        {
            code = 0;
            XDocument doc;
            try
            {
                doc = XDocument.Parse(response.Content ?? string.Empty);
            }
            catch
            {
                return false;
            }

            var error = doc.Root?.Name.LocalName == "error" ? doc.Root : null;
            if (error == null)
            {
                return false;
            }

            int.TryParse(error.Attribute("code")?.Value, out code);
            if (code is not (202 or 203 or 300))
            {
                NewznabRssParser.CheckError(doc, new IndexerResponse(new IndexerRequest(response.Request), response));
            }

            return true;
        }

        private static bool TryExtractRssDescription(string xml, out string description)
        {
            description = null;

            try
            {
                var doc = XDocument.Parse(xml);
                var desc = doc.Descendants("item").Elements("description").FirstOrDefault()
                           ?? doc.Descendants("description").FirstOrDefault();
                if (desc == null)
                {
                    return false;
                }

                description = desc.Value;
                return !description.IsNullOrWhiteSpace();
            }
            catch
            {
                return false;
            }
        }

        private static string BuildApiUrl(string baseUrl, string apiPath, string function, string releaseId, string apiKey, bool includeRaw)
        {
            var url = $"{baseUrl.TrimEnd('/')}/{apiPath.TrimStart('/')}";

            url += $"?t={Uri.EscapeDataString(function)}&id={Uri.EscapeDataString(releaseId)}";

            if (!apiKey.IsNullOrWhiteSpace())
            {
                url += $"&apikey={Uri.EscapeDataString(apiKey)}";
            }

            if (includeRaw)
            {
                url += "&raw=1";
            }

            return url;
        }

        internal static string ExtractReleaseId(ReleaseInfo release)
        {
            var fromQuery = TryExtractFirstGroup(IdFromQueryRegex, release?.DownloadUrl)
                            ?? TryExtractFirstGroup(IdFromQueryRegex, release?.Guid)
                            ?? TryExtractFirstGroup(IdFromQueryRegex, release?.NfoUrl);

            if (!fromQuery.IsNullOrWhiteSpace())
            {
                return fromQuery;
            }

            var fromGetNzb = TryExtractFirstGroup(IdFromGetNzbRegex, release?.DownloadUrl);
            if (!fromGetNzb.IsNullOrWhiteSpace())
            {
                return fromGetNzb;
            }

            var fromDetails = TryExtractFirstGroup(IdFromDetailsRegex, release?.Guid)
                              ?? TryExtractFirstGroup(IdFromDetailsRegex, release?.InfoUrl);
            if (!fromDetails.IsNullOrWhiteSpace())
            {
                return fromDetails;
            }

            var fromNfoPath = TryExtractFirstGroup(IdFromNfoPathRegex, release?.NfoUrl);
            if (!fromNfoPath.IsNullOrWhiteSpace())
            {
                return fromNfoPath;
            }

            return null;
        }

        private static string TryExtractFirstGroup(Regex regex, string input)
        {
            if (input.IsNullOrWhiteSpace())
            {
                return null;
            }

            var match = regex.Match(input);
            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : null;
        }

        internal static NewznabNarratorMetadata ParseNfo(string nfoText)
        {
            if (nfoText.IsNullOrWhiteSpace())
            {
                return null;
            }

            string narrator = null;
            string duration = null;
            var isGraphicAudio = false;

            var lines = nfoText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(l => l?.Trim())
                .Where(l => !l.IsNullOrWhiteSpace())
                .ToList();

            foreach (var line in lines)
            {
                if (!isGraphicAudio && AudioProductionDetector.ContainsGraphicAudioBrand(line))
                {
                    isGraphicAudio = true;
                }

                if (narrator.IsNullOrWhiteSpace())
                {
                    var match = NarratorLineRegex.Match(line);
                    if (match.Success)
                    {
                        narrator = match.Groups[1].Value.Trim();
                    }
                }

                if (duration.IsNullOrWhiteSpace())
                {
                    var match = LengthLineRegex.Match(line);
                    if (match.Success)
                    {
                        duration = NormalizeDuration(match.Groups[1].Value.Trim());
                    }
                }

                if (!narrator.IsNullOrWhiteSpace() && !duration.IsNullOrWhiteSpace() && isGraphicAudio)
                {
                    break;
                }
            }

            if (narrator.IsNullOrWhiteSpace() && duration.IsNullOrWhiteSpace() && !isGraphicAudio)
            {
                return null;
            }

            return new NewznabNarratorMetadata
            {
                Narrator = narrator,
                Duration = duration,
                IsGraphicAudio = isGraphicAudio
            };
        }

        internal static string NormalizeDuration(string raw)
        {
            if (raw.IsNullOrWhiteSpace())
            {
                return null;
            }

            // Accept "22 hrs and 37 mins", "22h 37m", "22:37:00", "1357 minutes"
            var normalized = raw.Trim().ToLowerInvariant();

            // HH:MM[:SS]
            var colon = Regex.Match(normalized, @"^(\d{1,3}):(\d{1,2})(?::(\d{1,2}))?$");
            if (colon.Success)
            {
                return int.TryParse(colon.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
                       int.TryParse(colon.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                    ? FormatDuration(hours * 60 + minutes)
                    : raw.Trim();
            }

            var hoursMatch = Regex.Match(normalized, @"(\d+)\s*(?:h|hr|hrs|hour|hours)");
            var minutesMatch = Regex.Match(normalized, @"(\d+)\s*(?:m|min|mins|minute|minutes)");

            long totalMinutes = 0;

            if (hoursMatch.Success)
            {
                if (!int.TryParse(hoursMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
                {
                    return raw.Trim();
                }

                totalMinutes += (long)hours * 60;
            }

            if (minutesMatch.Success)
            {
                if (!int.TryParse(minutesMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                {
                    return raw.Trim();
                }

                totalMinutes += minutes;
            }

            if (totalMinutes > 0)
            {
                return totalMinutes <= int.MaxValue ? FormatDuration((int)totalMinutes) : raw.Trim();
            }

            var minutesOnly = Regex.Match(normalized, @"^(\d+)\s*(?:min|mins|minute|minutes)$");
            if (minutesOnly.Success)
            {
                return null;
            }

            return raw.Trim();
        }

        private static string FormatDuration(int totalMinutes)
        {
            if (totalMinutes <= 0)
            {
                return null;
            }

            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;

            if (hours <= 0)
            {
                return $"{minutes}m";
            }

            if (minutes <= 0)
            {
                return $"{hours}h";
            }

            return $"{hours}h {minutes}m";
        }
    }
}
