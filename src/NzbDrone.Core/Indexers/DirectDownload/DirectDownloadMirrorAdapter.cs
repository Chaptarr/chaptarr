using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public sealed class DirectDownloadMirrorAdapter
    {
        private static readonly Regex RowRegex = new Regex(@"<tr>\s*<td>.*?<a href=""(?<info>[^""]+)"">(?<title>[^<]+)</a></td><td>(?<author>[^<]*)</td><td>(?<publisher>[^<]*)</td><td>(?<year>[^<]*)</td><td>(?<language>[^<]*)</td><td>(?<pages>[^<]*)</td><td>(?<size>[^<]*)</td><td>(?<extension>[^<]*)</td><td><a href=""(?<mirror>[^""]+)"">",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex GetLinkRegex = new Regex(@">GET</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HrefRegex = new Regex(@"href=""(?<href>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SizeRegex = new Regex(@"(?<value>\d+(?:\.\d+)?)\s*(?<unit>KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly DirectDownloadResponseReader _reader;

        public DirectDownloadMirrorAdapter(DirectDownloadResponseReader reader)
        {
            _reader = reader;
        }

        internal async Task<DirectDownloadAdapterResult> SearchAsync(Uri baseUri, DirectDownloadProbeRequest request)
        {
            var supported = false;
            foreach (var searchTerm in DirectDownloadSearchTerms.Build(request))
            {
                var searchUri = new Uri(baseUri, $"index.php?req={HttpUtility.UrlEncode(searchTerm)}&columns[]=t&objects[]=f&topics[]=l");
                var response = await _reader.GetAsync(searchUri, request);
                if (response.Content.IndexOf("<table", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                supported = true;
                var releases = new List<ReleaseInfo>();
                var candidateCount = 0;
                foreach (Match match in RowRegex.Matches(response.Content))
                {
                    if (candidateCount >= DirectDownloadSearchTerms.MaxSearchCandidates)
                    {
                        break;
                    }

                    var title = HttpUtility.HtmlDecode(match.Groups["title"].Value).Trim();
                    var extension = HttpUtility.HtmlDecode(match.Groups["extension"].Value).Trim().ToLowerInvariant();
                    if (!DirectDownloadReleaseFactory.AllowedExtensions.Contains(extension))
                    {
                        continue;
                    }

                    candidateCount++;
                    var mirrorUri = new Uri(baseUri, match.Groups["mirror"].Value);
                    var infoUri = new Uri(baseUri, match.Groups["info"].Value);

                    releases.Add(DirectDownloadReleaseFactory.Create(
                        request.Author,
                        title,
                        extension,
                        ParseSizeBytes(match.Groups["size"].Value),
                        ResolvePublishDate(match.Groups["year"].Value),
                        request.Isbn,
                        infoUri.AbsoluteUri,
                        mirrorUri.AbsoluteUri,
                        DirectDownloadSourceFamily.MirrorIndex));
                }

                if (releases.Count > 0)
                {
                    return new DirectDownloadAdapterResult(true, releases);
                }
            }

            return new DirectDownloadAdapterResult(supported, Array.Empty<ReleaseInfo>());
        }

        private async Task<string> ResolveDownloadUrlAsync(Uri mirrorUri, DirectDownloadProbeRequest request)
        {
            var response = await _reader.GetAsync(mirrorUri, request);
            if (GetLinkRegex.IsMatch(response.Content))
            {
                var match = HrefRegex.Match(response.Content);
                if (match.Success)
                {
                    var resolved = new Uri(mirrorUri, match.Groups["href"].Value).AbsoluteUri;
                    return DirectDownloadUrlSafety.ValidateAbsoluteHttpOrHttpsUri(new Uri(resolved), resolved).AbsoluteUri;
                }
            }

            return null;
        }

        private static long ParseSizeBytes(string sizeText)
        {
            var match = SizeRegex.Match(sizeText ?? string.Empty);
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

        private static DateTime ResolvePublishDate(string yearText)
        {
            return int.TryParse(yearText, out var year)
                ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                : DateTime.UtcNow;
        }
    }
}
