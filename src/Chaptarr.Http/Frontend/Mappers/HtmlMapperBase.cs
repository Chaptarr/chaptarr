using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace Chaptarr.Http.Frontend.Mappers
{
    public abstract class HtmlMapperBase : StaticResourceMapperBase
    {
        private readonly IDiskProvider _diskProvider;
        private readonly Lazy<ICacheBreakerProvider> _cacheBreakProviderFactory;
        // Matches src/href asset urls so we can prepend UrlBase and (optionally) append cache breakers.
        // Supports existing query strings (e.g. index.html adds ?cb=...) so UrlBase rewriting works behind sub-path reverse proxies.
        private static readonly Regex ReplaceRegex = new Regex(@"(?:(?<attribute>href|src)=\"")(?<path>.*?(?<extension>css|js|png|ico|ics|svg|json)(?:\?[^""]*)?)(?:\"")(?:\s(?<nohash>data-no-hash))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        protected HtmlMapperBase(IDiskProvider diskProvider,
                                 Lazy<ICacheBreakerProvider> cacheBreakProviderFactory,
                                 Logger logger)
            : base(diskProvider, logger)
        {
            _diskProvider = diskProvider;
            _cacheBreakProviderFactory = cacheBreakProviderFactory;
        }

        protected string HtmlPath;
        protected string UrlBase;

        protected override string GetAllowedRoot(string resourceUrl)
        {
            return Path.GetDirectoryName(HtmlPath);
        }

        protected override Stream GetContentStream(string filePath)
        {
            var text = GetHtmlText();

            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(text);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        protected string GetHtmlText()
        {
            var text = _diskProvider.ReadAllText(HtmlPath);
            var cacheBreakProvider = _cacheBreakProviderFactory.Value;
            var urlBaseForJsString = JavaScriptEncoder.Default.Encode(UrlBase ?? string.Empty);

            text = ReplaceRegex.Replace(text, match =>
            {
                var path = match.Groups["path"].Value;

                string url;
                if (match.Groups["nohash"].Success || path.Contains('?'))
                {
                    url = path;
                }
                else
                {
                    url = cacheBreakProvider.AddCacheBreakerToPath(path);
                }

                var rewritten = $"{UrlBase}{url}";
                return $"{match.Groups["attribute"].Value}=\"{WebUtility.HtmlEncode(rewritten)}\"";
            });

            text = text.Replace("__URL_BASE__", urlBaseForJsString);

            return text;
        }
    }
}
