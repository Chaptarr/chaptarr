using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.Authentication;
using Chaptarr.Http.Extensions;
using Chaptarr.Http.Frontend.Mappers;
using NzbDrone.Core.MediaCover;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Instrumentation;

namespace Chaptarr.Http.Frontend
{
    [Authorize(Policy = "UI")]
    [ApiController]
    [AllowCookieRedirect]
    public class StaticResourceController : Controller
    {
        private readonly IEnumerable<IMapHttpRequestsToDisk> _requestMappers;
        private readonly Logger _logger;

        public StaticResourceController(IEnumerable<IMapHttpRequestsToDisk> requestMappers,
            Logger logger)
        {
            _requestMappers = requestMappers;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpGet("login")]
        public IActionResult LoginPage()
        {
            return MapResource("login");
        }

        [EnableCors("AllowGet")]
        [AllowAnonymous]
        [HttpGet("content/{**path:regex(^(?!api(/|$)).*)}")]
        public IActionResult IndexContent([FromRoute] string path)
        {
            return MapResource("Content/" + path);
        }

        [Authorize(AuthenticationSchemes = "API")]
        [HttpGet("MediaCover/{authorId:int}/{filename}")]
        [HttpGet("MediaCover/author/{authorId:int}/{filename}")]
        public IActionResult AuthorCover(int authorId, string filename)
        {
            return MapCachedCover(authorId, filename, isBook: false);
        }

        [Authorize(AuthenticationSchemes = "API")]
        [HttpGet("MediaCover/Books/{bookId:int}/{filename}")]
        [HttpGet("MediaCover/book/{bookId:int}/{filename}")]
        public IActionResult BookCover(int bookId, string filename)
        {
            return MapCachedCover(bookId, filename, isBook: true);
        }

        [Authorize(AuthenticationSchemes = "API")]
        [HttpGet("MediaCoverProxy/{hash}/{filename}")]
        public IActionResult RegisteredProxyCover(string hash, string filename)
        {
            var path = "/MediaCoverProxy/" + hash + "/" + filename;
            return SeerrApiKeyAccessPolicy.IsMediaCoverProxyPath(path)
                ? MapResource(path.TrimStart('/'))
                : NotFound();
        }

        private IActionResult MapCachedCover(int id, string filename, bool isBook)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(filename) ||
                filename.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0 ||
                !MediaCoverRendition.IsSupportedImagePath(filename))
            {
                return NotFound();
            }

            return MapResource("MediaCover/" + (isBook ? "Books/" : string.Empty) + id + "/" + filename);
        }

        [HttpGet("")]
        [HttpGet("/{**path:regex(^(?!(api|feed)(/|$)).*)}")]
        public IActionResult Index([FromRoute] string path)
        {
            return MapResource(path);
        }

        private IActionResult MapResource(string path)
        {
            path = "/" + (path ?? "");

            var mappers = _requestMappers.Where(m => m.CanHandle(path)).ToList();

            if (mappers.Count > 1)
            {
                _logger.WarnSafe("Multiple static resource mappers matched {0}: {1}", path, string.Join(", ", mappers.Select(m => m.GetType().Name)));
            }

            var mapper = mappers.FirstOrDefault();

            if (mapper != null)
            {
                var result = mapper.GetResponse(path);

                if (result != null)
                {
                    if ((result as FileResult)?.ContentType?.StartsWith("text/html", System.StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Response.Headers.DisableCache();
                    }

                    return result;
                }

                return NotFound();
            }

            _logger.WarnSafe("Couldn't find handler for {0}", path);

            return NotFound();
        }
    }
}
