using System.Collections.Generic;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("convert")]
    public class ConvertBookController : Controller
    {
        private readonly IBookFileConversionService _bookFileConversionService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public ConvertBookController(
            IBookFileConversionService bookFileConversionService,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _bookFileConversionService = bookFileConversionService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<ConvertBookResource>), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        public ActionResult<List<ConvertBookResource>> GetConvertibleBookFiles(int authorId, int? bookId, string mediaType = null)
        {
            if (authorId <= 0)
            {
                return BadRequest(new ApiErrorResource
                {
                    Message = "authorId is required."
                });
            }

            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;
            if (bookId.HasValue)
            {
                var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(
                    _legacyIdentityMapService,
                    new[] { bookId.Value },
                    MediaTypeParameterParser.ParseOptional(mediaType));
                if (resolvedBookIds.Count == 0)
                {
                    return NotFound();
                }

                bookId = resolvedBookIds[0];
            }

            return _bookFileConversionService.GetConversionPreviews(authorId, bookId).ToResource();
        }
    }
}
