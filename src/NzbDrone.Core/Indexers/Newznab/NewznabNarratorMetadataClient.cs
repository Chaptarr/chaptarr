using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Newznab
{
    internal enum NewznabNfoEndpoint
    {
        Unknown = 0,
        Info = 1,
        GetNfo = 2
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
        private static readonly Regex GraphicAudioRegex = new Regex(@"\bgraphic\s*audio\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IIndexerHttpClient _httpClient;
        private readonly NewznabSettings _settings;
        private readonly Logger _logger;
        private readonly int _indexerId;
        private readonly TimeSpan _rateLimit;
        private readonly ICached<CacheEntry> _metadataCache;
        private readonly ICached<NewznabNfoEndpoint> _endpointCache;

        public NewznabNarratorMetadataClient(IIndexerHttpClient httpClient, NewznabSettings settings, int indexerId, TimeSpan rateLimit, ICacheManager cacheManager, Logger logger)
        {
            _httpClient = httpClient;
            _settings = settings;
            _indexerId = indexerId;
            _rateLimit = rateLimit;
            _metadataCache = cacheManager.GetCache<CacheEntry>(typeof(NewznabNarratorMetadataClient), "metadata");
            _endpointCache = cacheManager.GetCache<NewznabNfoEndpoint>(typeof(NewznabNarratorMetadataClient), "endpoint");
            _logger = logger;
        }

        public bool TryPopulate(ReleaseInfo release)
        {
            if (release == null)
            {
                return false;
            }

            // Avoid extra requests when the feed tells us no metadata exists.
            if (release.HasNfo.HasValue && release.HasNfo.Value == false)
            {
                return false;
            }

            // If we already have useful metadata, don't overwrite it.
            if (!release.Narrator.IsNullOrWhiteSpace() && !release.Duration.IsNullOrWhiteSpace())
            {
                return false;
            }

            var releaseId = ExtractReleaseId(release);
            if (releaseId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var baseUrl = GetEnhancedBaseUrl();
            var apiPath = _settings?.ApiPath;
            var apiKey = GetEnhancedApiKey();
            if (baseUrl.IsNullOrWhiteSpace() || apiPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var cacheKey = BuildCacheKey(baseUrl, apiPath, releaseId);

            if (TryGetCacheEntry(cacheKey, out var cached))
            {
                if (cached.Metadata == null)
                {
                    return false;
                }

                return Apply(release, cached.Metadata);
            }

            var endpointKey = BuildEndpointCacheKey(baseUrl, apiPath);
            var endpoint = DetermineEndpointHint(release, endpointKey);
            var ok = TryFetchAndParseMetadata(baseUrl, apiPath, apiKey, releaseId, endpoint, out var metadata, out var resolvedEndpoint);

            if (!ok || metadata == null)
            {
                SetCacheEntry(cacheKey, metadata: null, NegativeCacheTtl);
                return false;
            }

            if (resolvedEndpoint != NewznabNfoEndpoint.Unknown)
            {
                _endpointCache.Set(endpointKey, resolvedEndpoint, PositiveCacheTtl);
            }

            SetCacheEntry(cacheKey, metadata, PositiveCacheTtl);
            return Apply(release, metadata);
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

        private string BuildCacheKey(string baseUrl, string apiPath, string releaseId)
        {
            return $"{BuildEndpointCacheKey(baseUrl, apiPath)}|{releaseId}";
        }

        private string BuildEndpointCacheKey(string baseUrl, string apiPath)
        {
            return $"{_indexerId}|{baseUrl.TrimEnd('/').ToLowerInvariant()}|{apiPath.Trim('/').ToLowerInvariant()}";
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

        private bool TryFetchAndParseMetadata(string baseUrl, string apiPath, string apiKey, string releaseId, NewznabNfoEndpoint endpointHint, out NewznabNarratorMetadata metadata, out NewznabNfoEndpoint resolvedEndpoint)
        {
            metadata = null;

            var nfoText = FetchNfoText(baseUrl, apiPath, apiKey, releaseId, endpointHint, out var firstResolved);
            if (nfoText.IsNotNullOrWhiteSpace())
            {
                metadata = ParseNfo(nfoText);
                if (metadata != null)
                {
                    resolvedEndpoint = firstResolved;
                    return true;
                }
            }

            // Controlled fallback: if the chosen endpoint returns no usable metadata, try the other endpoint once.
            if (firstResolved == NewznabNfoEndpoint.Info)
            {
                var getNfoText = TryFetchGetNfo(baseUrl, apiPath, apiKey, releaseId, out _);
                if (getNfoText.IsNotNullOrWhiteSpace())
                {
                    var parsed = ParseNfo(getNfoText);
                    if (parsed != null)
                    {
                        metadata = parsed;
                        resolvedEndpoint = NewznabNfoEndpoint.GetNfo;
                        return true;
                    }
                }
            }
            else if (firstResolved == NewznabNfoEndpoint.GetNfo)
            {
                var infoText = TryFetchInfo(baseUrl, apiPath, apiKey, releaseId, out _);
                if (infoText.IsNotNullOrWhiteSpace())
                {
                    var parsed = ParseNfo(infoText);
                    if (parsed != null)
                    {
                        metadata = parsed;
                        resolvedEndpoint = NewznabNfoEndpoint.Info;
                        return true;
                    }
                }
            }

            resolvedEndpoint = firstResolved;
            return false;
        }

        private string FetchNfoText(string baseUrl, string apiPath, string apiKey, string releaseId, NewznabNfoEndpoint endpointHint, out NewznabNfoEndpoint resolvedEndpoint)
        {
            resolvedEndpoint = endpointHint;

            // Unknown: probe t=info first, then fallback to t=getnfo if "No such function".
            if (endpointHint == NewznabNfoEndpoint.Unknown)
            {
                var infoText = TryFetchInfo(baseUrl, apiPath, apiKey, releaseId, out var infoErrorCode);

                if (infoText != null)
                {
                    resolvedEndpoint = NewznabNfoEndpoint.Info;
                    return infoText;
                }

                if (infoErrorCode == 202)
                {
                    var getNfoText = TryFetchGetNfo(baseUrl, apiPath, apiKey, releaseId, out var getNfoErrorCode);
                    if (getNfoText != null)
                    {
                        resolvedEndpoint = NewznabNfoEndpoint.GetNfo;
                        return getNfoText;
                    }

                    resolvedEndpoint = getNfoErrorCode == 202 ? NewznabNfoEndpoint.Unknown : NewznabNfoEndpoint.GetNfo;
                    return null;
                }

                // Any other error (including "no nfo") - stop.
                return null;
            }

            if (endpointHint == NewznabNfoEndpoint.Info)
            {
                var infoText = TryFetchInfo(baseUrl, apiPath, apiKey, releaseId, out var infoErrorCode);
                if (infoText != null)
                {
                    resolvedEndpoint = NewznabNfoEndpoint.Info;
                    return infoText;
                }

                if (infoErrorCode == 202)
                {
                    var getNfoText = TryFetchGetNfo(baseUrl, apiPath, apiKey, releaseId, out _);
                    if (getNfoText != null)
                    {
                        resolvedEndpoint = NewznabNfoEndpoint.GetNfo;
                        return getNfoText;
                    }
                }

                return null;
            }

            // t=getnfo
            var text = TryFetchGetNfo(baseUrl, apiPath, apiKey, releaseId, out var getNfoError);
            if (text != null)
            {
                resolvedEndpoint = NewznabNfoEndpoint.GetNfo;
                return text;
            }

            if (getNfoError == 202)
            {
                var infoText = TryFetchInfo(baseUrl, apiPath, apiKey, releaseId, out _);
                if (infoText != null)
                {
                    resolvedEndpoint = NewznabNfoEndpoint.Info;
                    return infoText;
                }
            }

            return null;
        }

        private string TryFetchInfo(string baseUrl, string apiPath, string apiKey, string releaseId, out int? errorCode)
        {
            errorCode = null;
            var url = BuildApiUrl(baseUrl, apiPath, "info", releaseId, apiKey, includeRaw: false);

            var response = ExecuteRequest(url);
            if (response == null)
            {
                return null;
            }

            var contentType = response.Headers?.ContentType ?? string.Empty;
            var content = response.Content ?? string.Empty;

            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith("<", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseError(content, out var code))
                {
                    errorCode = code;
                    return null;
                }

                if (TryExtractRssDescription(content, out var description))
                {
                    return description;
                }
            }

            // Plain text NFO
            return content;
        }

        private string TryFetchGetNfo(string baseUrl, string apiPath, string apiKey, string releaseId, out int? errorCode)
        {
            errorCode = null;
            var url = BuildApiUrl(baseUrl, apiPath, "getnfo", releaseId, apiKey, includeRaw: true);

            var response = ExecuteRequest(url);
            if (response == null)
            {
                return null;
            }

            var content = response.Content ?? string.Empty;
            var contentType = response.Headers?.ContentType ?? string.Empty;

            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseError(content, out var code))
                {
                    errorCode = code;
                    return null;
                }

                if (TryExtractRssDescription(content, out var description))
                {
                    return description;
                }
            }

            return content;
        }

        private HttpResponse ExecuteRequest(string url)
        {
            try
            {
                var request = new HttpRequest(url)
                {
                    RateLimitKey = _indexerId > 0 ? _indexerId.ToString(CultureInfo.InvariantCulture) : null,
                    RateLimit = _rateLimit,
                    LogHttpError = false,
                    SuppressHttpError = true
                };

                var response = _httpClient.Execute(request);
                if (response == null)
                {
                    return null;
                }

                if (response.HasHttpError)
                {
                    return null;
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "Narrator metadata: failed to fetch extra metadata from indexer");
                return null;
            }
        }

        private static bool TryParseError(string xml, out int code)
        {
            code = 0;

            try
            {
                var doc = XDocument.Parse(xml);
                var error = doc.Descendants("error").FirstOrDefault();
                if (error == null)
                {
                    return false;
                }

                var codeAttr = error.Attribute("code")?.Value;
                if (!int.TryParse(codeAttr, out code))
                {
                    code = 0;
                }

                return true;
            }
            catch
            {
                return false;
            }
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
                if (!isGraphicAudio && GraphicAudioRegex.IsMatch(line))
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
                var hours = int.Parse(colon.Groups[1].Value, CultureInfo.InvariantCulture);
                var minutes = int.Parse(colon.Groups[2].Value, CultureInfo.InvariantCulture);
                return FormatDuration(hours * 60 + minutes);
            }

            var hoursMatch = Regex.Match(normalized, @"(\d+)\s*(?:h|hr|hrs|hour|hours)");
            var minutesMatch = Regex.Match(normalized, @"(\d+)\s*(?:m|min|mins|minute|minutes)");

            var totalMinutes = 0;

            if (hoursMatch.Success)
            {
                totalMinutes += int.Parse(hoursMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 60;
            }

            if (minutesMatch.Success)
            {
                totalMinutes += int.Parse(minutesMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            if (totalMinutes > 0)
            {
                return FormatDuration(totalMinutes);
            }

            var minutesOnly = Regex.Match(normalized, @"^(\d+)\s*(?:min|mins|minute|minutes)$");
            if (minutesOnly.Success)
            {
                totalMinutes = int.Parse(minutesOnly.Groups[1].Value, CultureInfo.InvariantCulture);
                return FormatDuration(totalMinutes);
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
