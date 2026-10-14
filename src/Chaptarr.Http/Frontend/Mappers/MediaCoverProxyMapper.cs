using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class MediaCoverProxyMapper : IMapHttpRequestsToDisk
    {
        private readonly Regex _regex = new Regex(@"^/MediaCoverProxy/(?:book/(?<bookId>[1-9][0-9]*)|(?<hash>\w+))/(?<filename>[^/?]+)(?:\?.*)?$", RegexOptions.IgnoreCase);

        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IBookService _bookService;
        private readonly IContentTypeProvider _mimeTypeProvider;
        private readonly Logger _logger;

        public MediaCoverProxyMapper(IMediaCoverProxy mediaCoverProxy, IBookService bookService, Logger logger)
        {
            _mediaCoverProxy = mediaCoverProxy;
            _bookService = bookService;
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
            var bookIdText = match.Groups["bookId"].Value;
            var filename = match.Groups["filename"].Value;

            byte[] imageData;
            try
            {
                if (bookIdText.Length > 0)
                {
                    if (!int.TryParse(bookIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var bookId))
                    {
                        return new StatusCodeResult((int)HttpStatusCode.NotFound);
                    }

                    var book = _bookService.GetBook(bookId);
                    var candidates = MediaCoverRendition.SelectMonitoredBookCovers(book);
                    if (candidates.Count == 0)
                    {
                        return new StatusCodeResult((int)HttpStatusCode.NotFound);
                    }

                    imageData = GetFirstAvailableBookCover(candidates);
                }
                else
                {
                    var imageUrl = _mediaCoverProxy.GetUrl(hash);

                    if (string.IsNullOrWhiteSpace(imageUrl))
                    {
                        return new StatusCodeResult((int)HttpStatusCode.NotFound);
                    }

                    imageData = GetAvailableImage(imageUrl);
                }
            }
            catch (ModelNotFoundException)
            {
                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }
            catch (KeyNotFoundException)
            {
                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }

            if (imageData == null)
            {
                return new StatusCodeResult((int)HttpStatusCode.NotFound);
            }

            if (!_mimeTypeProvider.TryGetContentType(filename, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return new PrivateCacheFileContentResult(imageData, contentType);
        }

        private byte[] GetFirstAvailableBookCover(IReadOnlyList<BookCoverSelection> candidates)
        {
            foreach (var candidate in candidates)
            {
                var imageData = GetAvailableImage(candidate.Cover.Url);
                if (imageData != null)
                {
                    return imageData;
                }
            }

            return null;
        }

        private byte[] GetAvailableImage(string imageUrl)
        {
            try
            {
                return _mediaCoverProxy.GetImageFromUrl(imageUrl);
            }
            catch (PlaceholderImageException)
            {
                // Rejected provider bytes use the same missing-image outcome as unavailable covers.
                return null;
            }
            catch (Exception e)
            {
                // Each fetch failure leaves later candidates eligible and preserves the UI placeholder outcome.
                var httpException = e as HttpException;
                LogUnavailableImage(httpException?.Request?.Method?.ToString() ?? "GET",
                    httpException?.Request?.Url?.ToString() ?? imageUrl,
                    httpException?.Response?.StatusCode ?? ((e as WebException)?.Response as HttpWebResponse)?.StatusCode);
                return null;
            }
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
