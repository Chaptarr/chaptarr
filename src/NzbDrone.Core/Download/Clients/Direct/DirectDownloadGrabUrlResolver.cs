using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers.DirectDownload;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public sealed class DirectDownloadGrabUrlResolver
    {
        private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(15);
        private const int MaxFastDownloadDomainRotations = 5;
        private static readonly Regex DetailPageDownloadLinkRegex = new(
            @"href=""(?<href>[^""]+)""[^>]*>(?:\s*)(?:GET|Download)(?:\s*)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DetailPageFileExtensionRegex = new(
            @"href=""(?<href>https?://[^""]+\.(?:pdf|epub|mobi|azw3|djvu|cbz|cbr|fb2|txt)(?:\?[^""]*)?)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DetailPageAaFastDownloadLinkRegex = new(
            @"href=""(?<href>/fast_download/[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DetailPageAaSlowDownloadLinkRegex = new(
            @"href=""(?<href>/slow_download/[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DetailPageAaDownloadLinkRegex = new(
            @"href=""(?<href>(?:/slow_download/|/fast_download/)[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] DdosChallengeMarkers = new[]
        {
            "DDoS-Guard", "DDoS Protection", "challenge-platform",
            "cf-browser-verification", "checking your browser", "Just a moment"
        };

        private readonly IHttpClient _httpClient;
        private readonly IBrowserDownloadResolver _browserResolver;

        public DirectDownloadGrabUrlResolver(IHttpClient httpClient, IBrowserDownloadResolver browserResolver = null)
        {
            _httpClient = httpClient;
            _browserResolver = browserResolver ?? new NullBrowserDownloadResolver();
        }

        public async Task<GrabResolution> TryResolveGrabAsync(string downloadUrl, string apiKey, string source)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                return GrabResolution.NotApplicable(downloadUrl);
            }

            if (string.Equals(source, "CatalogPage", StringComparison.OrdinalIgnoreCase))
            {
                return await TryResolveCatalogGrabAsync(downloadUrl, apiKey);
            }

            if (string.Equals(source, "MirrorIndex", StringComparison.OrdinalIgnoreCase))
            {
                var mirrorResolved = await TryResolveMirrorAsync(downloadUrl);
                return mirrorResolved != null
                    ? GrabResolution.Success(mirrorResolved)
                    : GrabResolution.NotApplicable(downloadUrl);
            }

            return GrabResolution.NotApplicable(downloadUrl);
        }

        public async Task<string> TryResolveAsync(string downloadUrl, string apiKey, string source, bool slowFallbackEnabled = false)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                return downloadUrl;
            }

            if (string.Equals(source, "CatalogPage", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = await TryResolveCatalogAsync(downloadUrl, apiKey);
                if (resolved != null)
                {
                    return resolved;
                }

                // API and scraping both failed — try browser fallback if enabled
                if (slowFallbackEnabled)
                {
                    var browserResolved = await _browserResolver.TryResolveSlowDownloadUrlAsync(downloadUrl);
                    if (browserResolved != null)
                    {
                        return browserResolved;
                    }

                    throw new ReleaseDownloadException(
                        null,
                        "Direct source could not resolve a download URL via API, scraping, or browser fallback. The source may be temporarily unavailable. Remove from blocklist to retry.");
                }

                if (IsCatalogInfoUrl(downloadUrl))
                {
                    var hasKey = !string.IsNullOrWhiteSpace(apiKey);
                    throw new ReleaseDownloadException(
                        null,
                        hasKey
                            ? "Direct source could not resolve a downloadable file URL. The source may be temporarily unavailable or the API key may be invalid. Enable the slow-download browser fallback or remove from blocklist to retry."
                            : "Direct source detail page has no public download link. Configure an API key for the indexer to enable fast downloads, or enable the slow-download browser fallback.");
                }

                return downloadUrl;
            }

            if (string.Equals(source, "MirrorIndex", StringComparison.OrdinalIgnoreCase))
            {
                return await TryResolveMirrorAsync(downloadUrl) ?? downloadUrl;
            }

            return downloadUrl;
        }

        public async Task<ApiKeyValidationResult> ValidateApiKeyAsync(string sourceUrl, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return ApiKeyValidationResult.Empty();
            }

            if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out var baseUri))
            {
                return ApiKeyValidationResult.TransientFailure("Source URL is not valid.");
            }

            // Use a deliberately impossible md5 for the validation probe — this verifies
            // the key is accepted without consuming a download slot or matching a real file.
            var probeMd5 = "00000000000000000000000000000000";
            var apiUri = new Uri(baseUri, $"/dyn/api/fast_download.json?md5={probeMd5}&key={HttpUtility.UrlEncode(apiKey)}&path_index=0&domain_index=0");

            try
            {
                var httpRequest = new HttpRequest(apiUri.AbsoluteUri)
                {
                    AllowAutoRedirect = true,
                    RequestTimeout = ResolveTimeout,
                    SuppressHttpError = true
                };
                httpRequest.Headers.Accept = "application/json, */*;q=0.1";
                httpRequest.Headers["User-Agent"] = "Chaptarr/1.0 (Direct Download Indexer)";
                httpRequest.Headers["Referer"] = baseUri.GetLeftPart(UriPartial.Authority) + "/";

                var response = await _httpClient.GetAsync(httpRequest);

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ApiKeyValidationResult.InvalidOrExpired($"Provider returned {(int)response.StatusCode}. The API key may be invalid.");
                }

                var rawResponse = response.Content;
                if (string.IsNullOrWhiteSpace(rawResponse))
                {
                    // Empty body with non-error status — ambiguous, treat as transient
                    return ApiKeyValidationResult.TransientFailure("Provider returned an empty response.");
                }

                using var document = JsonDocument.Parse(rawResponse);
                if (document.RootElement.TryGetProperty("error", out var errorProp) &&
                    errorProp.ValueKind == JsonValueKind.String)
                {
                    var errorText = errorProp.GetString() ?? string.Empty;

                    if (errorText.Contains("No downloads left", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("no downloads remaining", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("limit reached", StringComparison.OrdinalIgnoreCase))
                    {
                        return ApiKeyValidationResult.NoDownloadsRemaining($"API key accepted but: {errorText}. Wait for quota reset or configure another source URL.");
                    }

                    if (errorText.Contains("invalid key", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("bad key", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("wrong key", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("secret key", StringComparison.OrdinalIgnoreCase))
                    {
                        return ApiKeyValidationResult.InvalidOrExpired($"Provider rejected the API key: {errorText}");
                    }

                    // Other API error — key was accepted, but request failed for another reason
                    return ApiKeyValidationResult.Valid();
                }

                if (document.RootElement.TryGetProperty("download_url", out var downloadUrl) &&
                    downloadUrl.ValueKind == JsonValueKind.String)
                {
                    var value = downloadUrl.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return ApiKeyValidationResult.Valid();
                    }
                }

                // JSON parsed, has no error, download_url is null/empty — key accepted but no result for probe md5
                return ApiKeyValidationResult.Valid();
            }
            catch (JsonException)
            {
                // Non-JSON response — likely HTML challenge page
                return ApiKeyValidationResult.InvalidOrExpired("Provider returned a non-JSON response. The API key or endpoint may be incorrect.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiKeyValidationResult.TransientFailure($"Could not validate API key: {RedactApiKeyFromUrl(ex.Message)}");
            }
        }

        private static bool IsCatalogInfoUrl(string url)
        {
            return url != null && url.Contains("/md5/", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> TryResolveCatalogAsync(string infoUrl, string apiKey)
        {
            if (!Uri.TryCreate(infoUrl, UriKind.Absolute, out var infoUri))
            {
                return null;
            }

            var md5 = ExtractMd5FromPath(infoUri.AbsolutePath);
            if (md5 == null)
            {
                return null;
            }

            // Fast path: use the API key to get a direct download URL
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var apiResult = await TryResolveViaFastDownloadApiAsync(infoUri, md5, apiKey);
                if (apiResult != null)
                {
                    return apiResult;
                }
            }

            // Fallback: scrape the detail page HTML for a public download link
            return await TryScrapeDetailPageForDownloadLinkAsync(infoUri);
        }

        private async Task<GrabResolution> TryResolveCatalogGrabAsync(string infoUrl, string apiKey)
        {
            if (!Uri.TryCreate(infoUrl, UriKind.Absolute, out var infoUri))
            {
                return GrabResolution.Unavailable("Source URL is not a valid absolute URI.");
            }

            var md5 = ExtractMd5FromPath(infoUri.AbsolutePath);
            if (md5 == null)
            {
                return GrabResolution.Unavailable("Source URL does not contain a recognizable file identifier.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return GrabResolution.Unavailable("No API key configured. Configure an API key to enable fast downloads.");
            }

            var apiResult = await TryResolveViaFastDownloadApiAsync(infoUri, md5, apiKey);
            if (apiResult != null)
            {
                return GrabResolution.Success(apiResult);
            }

            return GrabResolution.Unavailable("Fast-download API did not return a valid download URL. The source may be temporarily unavailable or the API key may be invalid.");
        }

        private async Task<string> TryResolveViaFastDownloadApiAsync(Uri infoUri, string md5, string apiKey)
        {
            var baseUri = new Uri(infoUri.GetLeftPart(UriPartial.Authority));

            for (var domainIndex = 0; domainIndex < MaxFastDownloadDomainRotations; domainIndex++)
            {
                var apiUri = new Uri(baseUri, $"/dyn/api/fast_download.json?md5={md5}&key={HttpUtility.UrlEncode(apiKey)}&path_index=0&domain_index={domainIndex}");

                try
                {
                    var httpRequest = new HttpRequest(apiUri.AbsoluteUri)
                    {
                        AllowAutoRedirect = true,
                        RequestTimeout = ResolveTimeout
                    };
                    httpRequest.Headers.Accept = "application/json, */*;q=0.1";
                    httpRequest.Headers["User-Agent"] = "Chaptarr/1.0 (Direct Download Indexer)";
                    httpRequest.Headers["Referer"] = infoUri.GetLeftPart(UriPartial.Authority) + "/";

                    var response = await _httpClient.GetAsync(httpRequest);
                    var rawResponse = response.Content;

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        return null;
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized ||
                        response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(rawResponse))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(rawResponse);
                    if (!document.RootElement.TryGetProperty("download_url", out var downloadUrl) || downloadUrl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = downloadUrl.GetString();
                    if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var resolvedUri))
                    {
                        continue;
                    }

                    if (!string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return resolvedUri.AbsoluteUri;
                }
                catch
                {
                    continue;
                }
            }

            return null;
        }

        private async Task<string> TryScrapeDetailPageForDownloadLinkAsync(Uri infoUri)
        {
            try
            {
                var httpRequest = new HttpRequest(infoUri.AbsoluteUri)
                {
                    AllowAutoRedirect = true,
                    RequestTimeout = ResolveTimeout
                };
                httpRequest.Headers.Accept = "text/html, */*;q=0.1";
                httpRequest.Headers["User-Agent"] = "Chaptarr/1.0 (Direct Download Indexer)";
                httpRequest.Headers["Referer"] = infoUri.GetLeftPart(UriPartial.Authority) + "/";

                var response = await _httpClient.GetAsync(httpRequest);
                var content = response.Content;

                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                var getLinks = ExtractLinks(DetailPageDownloadLinkRegex, content, infoUri);
                var fileLinks = ExtractLinks(DetailPageFileExtensionRegex, content, infoUri);
                var aaFastLinks = ExtractLinks(DetailPageAaFastDownloadLinkRegex, content, infoUri);
                var aaSlowLinks = ExtractLinks(DetailPageAaSlowDownloadLinkRegex, content, infoUri);

                var allLinks = new List<string>();
                allLinks.AddRange(getLinks);
                allLinks.AddRange(fileLinks);
                allLinks.AddRange(aaFastLinks);
                allLinks.AddRange(aaSlowLinks);

                if (allLinks.Count == 0)
                {
                    var fallbackMatch = DetailPageAaDownloadLinkRegex.Match(content);
                    if (fallbackMatch.Success)
                    {
                        var href = fallbackMatch.Groups["href"].Value;
                        if (TryResolveAndValidateLink(infoUri, href, out var resolvedAbsoluteUri))
                        {
                            return resolvedAbsoluteUri;
                        }
                    }

                    return null;
                }

                foreach (var link in allLinks)
                {
                    if (await IsLinkAccessibleAsync(link))
                    {
                        return link;
                    }
                }

                return allLinks[0];
            }
            catch
            {
                return null;
            }
        }

        private List<string> ExtractLinks(Regex regex, string content, Uri baseUri)
        {
            var links = new List<string>();
            foreach (Match match in regex.Matches(content))
            {
                var href = match.Groups["href"].Value;
                if (TryResolveAndValidateLink(baseUri, href, out var resolvedAbsoluteUri))
                {
                    links.Add(resolvedAbsoluteUri);
                }
            }

            return links;
        }

        private async Task<bool> IsLinkAccessibleAsync(string url)
        {
            try
            {
                var httpRequest = new HttpRequest(url)
                {
                    AllowAutoRedirect = true,
                    RequestTimeout = ResolveTimeout,
                    SuppressHttpError = true
                };
                httpRequest.Headers.Accept = "*/*";
                httpRequest.Headers["User-Agent"] = "Chaptarr/1.0 (Direct Download Indexer)";

                var response = await _httpClient.GetAsync(httpRequest);
                var contentType = response.Headers.ContentType ?? string.Empty;

                if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var content = response.Content ?? string.Empty;
                foreach (var marker in DdosChallengeMarkers)
                {
                    if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveAndValidateLink(Uri baseUri, string href, out string absoluteUri)
        {
            absoluteUri = null;

            if (string.IsNullOrWhiteSpace(href))
            {
                return false;
            }

            var resolved = new Uri(baseUri, href).AbsoluteUri;

            if (!Uri.TryCreate(resolved, UriKind.Absolute, out var resolvedUri))
            {
                return false;
            }

            if (!string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            absoluteUri = resolvedUri.AbsoluteUri;
            return true;
        }

        private async Task<string> TryResolveMirrorAsync(string mirrorUrl)
        {
            if (!Uri.TryCreate(mirrorUrl, UriKind.Absolute, out var mirrorUri))
            {
                return null;
            }

            try
            {
                var httpRequest = new HttpRequest(mirrorUri.AbsoluteUri)
                {
                    AllowAutoRedirect = true,
                    RequestTimeout = ResolveTimeout
                };
                httpRequest.Headers.Accept = "text/html, */*;q=0.1";

                var response = await _httpClient.GetAsync(httpRequest);
                var content = response.Content;

                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                // Look for a GET link pattern (common in mirror pages)
                var getLinkMatch = System.Text.RegularExpressions.Regex.Match(
                    content,
                    @"href=""(?<href>[^""]+)""[^>]*>(?:\s*)GET(?:\s*)</a>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!getLinkMatch.Success)
                {
                    return null;
                }

                var href = getLinkMatch.Groups["href"].Value;
                var resolved = new Uri(mirrorUri, href).AbsoluteUri;

                if (!Uri.TryCreate(resolved, UriKind.Absolute, out var resolvedUri))
                {
                    return null;
                }

                if (!string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return resolvedUri.AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractMd5FromPath(string absolutePath)
        {
            var marker = "/md5/";
            var index = absolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var md5Start = index + marker.Length;
            var remaining = absolutePath.Substring(md5Start);

            // MD5 is 32 hex characters
            if (remaining.Length < 32)
            {
                return null;
            }

            var md5 = remaining.Substring(0, 32);
            foreach (var c in md5)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return null;
                }
            }

            return md5;
        }

        internal static string RedactApiKeyFromUrl(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var keyIndex = text.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return text;
            }

            var valueStart = keyIndex + 4;
            if (valueStart >= text.Length)
            {
                return text;
            }

            var valueEnd = text.IndexOfAny(new[] { '&', ' ', '"', '\'', '#' }, valueStart);
            if (valueEnd < 0)
            {
                valueEnd = text.Length;
            }

            return string.Concat(text.AsSpan(0, valueStart), "[redacted]", text.AsSpan(valueEnd));
        }
    }
}
