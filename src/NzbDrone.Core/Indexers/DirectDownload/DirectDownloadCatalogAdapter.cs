using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public sealed class DirectDownloadCatalogAdapter
    {
        private const int ResultContextPrefixLength = 1600;
        private const int ResultContextSuffixLength = 5000;
        private static readonly Regex ResultRegex = new Regex(@"<a(?=[^>]*js-vim-focus)(?=[^>]*href=""/md5/(?<md5>[0-9a-f]{32})"")[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex MetaRegex = new Regex(@"text-gray-800[^""]*font-semibold[^""]*"">(?<meta>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex FilenameRegex = new Regex(@"font-mono"">(?<filename>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex DetailCodeRegex = new Regex(@"<span>(?<label>[^<]+)</span><span>(?<value>[^<]+)</span>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SizeRegex = new Regex(@"(?<value>\d+(?:\.\d+)?)\s*(?<unit>KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YearRegex = new Regex(@"\b(?<year>19\d{2}|20\d{2})\b", RegexOptions.Compiled);

        private readonly DirectDownloadResponseReader _reader;

        public DirectDownloadCatalogAdapter(DirectDownloadResponseReader reader)
        {
            _reader = reader;
        }

        internal async Task<DirectDownloadAdapterResult> SearchAsync(Uri baseUri, DirectDownloadProbeRequest request)
        {
            var supported = false;
            foreach (var searchTerm in DirectDownloadSearchTerms.Build(request))
            {
                var searchUri = new Uri(baseUri, $"search?q={HttpUtility.UrlEncode(searchTerm)}&ext=epub,pdf&lang=en,de");
                var response = await _reader.GetAsync(searchUri, request);
                var unwrapped = UnwrapHtmlComments(response.Content);
                var matches = ResultRegex.Matches(unwrapped);

                if (matches.Count == 0)
                {
                    continue;
                }

                supported = true;
                var releases = new List<ReleaseInfo>();
                var candidateCount = 0;
                foreach (Match match in matches)
                {
                    if (candidateCount >= DirectDownloadSearchTerms.MaxSearchCandidates)
                    {
                        break;
                    }

                    candidateCount++;
                    var md5 = match.Groups["md5"].Value;
                    var title = HttpUtility.HtmlDecode(StripTags(match.Groups["title"].Value)).Trim();
                    var htmlContext = GetResultContext(unwrapped, match.Index);
                    var meta = HttpUtility.HtmlDecode(MetaRegex.Match(htmlContext).Groups["meta"].Value).Trim();
                    var filename = HttpUtility.HtmlDecode(FilenameRegex.Match(htmlContext).Groups["filename"].Value).Trim();
                    var extension = ResolveExtension(meta, filename);
                    var infoUri = new Uri(baseUri, $"md5/{md5}");

                    releases.Add(DirectDownloadReleaseFactory.Create(
                        request.Author,
                        title,
                        extension,
                        ParseSizeBytes(meta),
                        ResolvePublishDate(meta),
                        request.Isbn,
                        infoUri.AbsoluteUri,
                        infoUri.AbsoluteUri,
                        DirectDownloadSourceFamily.CatalogPage));
                }

                if (releases.Count > 0)
                {
                    return new DirectDownloadAdapterResult(true, releases);
                }
            }

            return new DirectDownloadAdapterResult(supported, Array.Empty<ReleaseInfo>());
        }

        private async Task<string> FetchDetailIsbnAsync(Uri infoUri, DirectDownloadProbeRequest request)
        {
            var response = await _reader.GetAsync(infoUri, request);
            foreach (Match match in DetailCodeRegex.Matches(response.Content))
            {
                var label = HttpUtility.HtmlDecode(match.Groups["label"].Value).Trim();
                if (!label.Equals("ISBN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return HttpUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            }

            return null;
        }

        private async Task<string> TryFetchDetailIsbnAsync(Uri infoUri, DirectDownloadProbeRequest request)
        {
            try
            {
                return await FetchDetailIsbnAsync(infoUri, request);
            }
            catch (DirectDownloadProbeException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<string> ResolveDownloadUrlAsync(Uri baseUri, string md5, DirectDownloadProbeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return null;
            }

            var apiUri = new Uri(baseUri, $"dyn/api/fast_download.json?md5={md5}&key={HttpUtility.UrlEncode(request.ApiKey)}&path_index=0&domain_index=0");
            string rawResponse;
            try
            {
                var response = await _reader.GetAsync(apiUri, request);
                rawResponse = response.Content;
            }
            catch (DirectDownloadProbeException)
            {
                return null;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(rawResponse);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("download_url", out var downloadUrl) || downloadUrl.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var value = downloadUrl.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                if (!Uri.TryCreate(value, UriKind.Absolute, out var resolvedUri))
                {
                    throw new DirectDownloadProbeException($"Resolved download URL '{CleanseLogMessage.Cleanse(value)}' is invalid.");
                }

                return DirectDownloadUrlSafety.ValidateAbsoluteHttpOrHttpsUri(resolvedUri, value).AbsoluteUri;
            }
        }

        private static string ResolveExtension(string meta, string filename)
        {
            var lowerFilename = filename?.ToLowerInvariant() ?? string.Empty;
            foreach (var extension in DirectDownloadReleaseFactory.AllowedExtensions)
            {
                if (meta.IndexOf(extension, StringComparison.OrdinalIgnoreCase) >= 0 || lowerFilename.EndsWith('.' + extension, StringComparison.OrdinalIgnoreCase))
                {
                    return extension;
                }
            }

            return "epub";
        }

        private static string GetResultContext(string content, int matchIndex)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            var start = Math.Max(0, matchIndex - ResultContextPrefixLength);
            var length = Math.Min(content.Length - start, ResultContextPrefixLength + ResultContextSuffixLength);
            return content.Substring(start, length);
        }

        private static long ParseSizeBytes(string meta)
        {
            var match = SizeRegex.Match(meta ?? string.Empty);
            if (!match.Success)
            {
                return 0;
            }

            var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            var factor = match.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "KB" => 1024L,
                "MB" => 1024L * 1024L,
                "GB" => 1024L * 1024L * 1024L,
                "TB" => 1024L * 1024L * 1024L * 1024L,
                _ => 1L
            };

            return (long)(value * factor);
        }

        private static DateTime ResolvePublishDate(string meta)
        {
            var match = YearRegex.Match(meta ?? string.Empty);
            return match.Success && int.TryParse(match.Groups["year"].Value, out var year)
                ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                : DateTime.UtcNow;
        }

        private static string StripTags(string value)
        {
            return Regex.Replace(value ?? string.Empty, "<.*?>", string.Empty);
        }

        private static string UnwrapHtmlComments(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            return html.Replace("<!--", string.Empty).Replace("-->", string.Empty);
        }
    }
}
