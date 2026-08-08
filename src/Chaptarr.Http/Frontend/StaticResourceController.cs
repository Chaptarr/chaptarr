using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.Extensions;
using Chaptarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace Chaptarr.Http.Frontend
{
    [Authorize(Policy = "UI")]
    [ApiController]
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
                _logger.Warn("Multiple static resource mappers matched {0}: {1}", path, string.Join(", ", mappers.Select(m => m.GetType().Name)));
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

            _logger.Warn("Couldn't find handler for {0}", path);

            return NotFound();
        }
    }
}
