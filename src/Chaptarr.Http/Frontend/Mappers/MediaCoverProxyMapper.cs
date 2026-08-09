using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class MediaCoverProxyMapper : IMapHttpRequestsToDisk
    {
        private readonly Regex _regex = new Regex(@"/MediaCoverProxy/(?<hash>\w+)/(?<filename>[^/?]+)", RegexOptions.IgnoreCase);

        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IContentTypeProvider _mimeTypeProvider;
        private readonly Logger _logger;

        public MediaCoverProxyMapper(IMediaCoverProxy mediaCoverProxy, Logger logger)
        {
            _mediaCoverProxy = mediaCoverProxy;
            _mimeTypeProvider = new FileExtensionContentTypeProvider();
            _logger = logger;
        }

        public string Map(string resourceUrl)
        {
            return null;
        }

        public bool CanHandle(string resourceUrl)
        {
            return resourceUrl.StartsWith("/MediaCoverProxy/", StringComparison.InvariantCultureIgnoreCase);
        }

        public IActionResult GetResponse(string resourceUrl)
        {
            var match = _regex.Match(resourceUrl);

            if (!match.Success)
            {
                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }

            var hash = match.Groups["hash"].Value;
            var filename = match.Groups["filename"].Value;

            byte[] imageData;
            try
            {
                imageData = _mediaCoverProxy.GetImage(hash);
            }
            catch (KeyNotFoundException)
            {
                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }
            catch (PlaceholderImageException)
            {
                // Provider placeholder bytes are deliberately indistinguishable by URL.
                // Treat a content-policy rejection as a missing image so the UI uses its
                // normal author placeholder and never receives the mascot bytes.
                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }
            catch (HttpException e)
            {
                LogUnavailableImage(e.Request?.Method?.ToString() ?? "GET",
                    e.Request?.Url?.ToString() ?? _mediaCoverProxy.GetUrl(hash),
                    e.Response?.StatusCode);

                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }
            catch (WebException e)
            {
                LogUnavailableImage("GET",
                    _mediaCoverProxy.GetUrl(hash),
                    (e.Response as HttpWebResponse)?.StatusCode);

                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }

            if (!_mimeTypeProvider.TryGetContentType(filename, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return new PrivateCacheFileContentResult(imageData, contentType);
        }

        private void LogUnavailableImage(string method, string url, HttpStatusCode? statusCode)
        {
            var status = statusCode.HasValue ? ((int)statusCode.Value).ToString() : "none";
            _logger.Debug("Remote image unavailable: {0} {1} [{2}]", method, CleanseLogMessage.Cleanse(url), status);
        }

        private sealed class PrivateCacheFileContentResult : FileContentResult
        {
            private const int BrowserCacheSeconds = 24 * 60 * 60;

            public PrivateCacheFileContentResult(byte[] fileContents, string contentType)
                : base(fileContents, contentType)
            {
            }

            public override Task ExecuteResultAsync(ActionContext context)
            {
                context.HttpContext.Response.Headers["Cache-Control"] = $"private, max-age={BrowserCacheSeconds}";
                return base.ExecuteResultAsync(context);
            }
        }
    }
}
