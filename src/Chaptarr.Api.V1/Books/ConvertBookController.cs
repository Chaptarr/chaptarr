using System.Collections.Generic;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("convert")]
    public class ConvertBookController : Controller
    {
        private readonly IBookFileConversionService _bookFileConversionService;

        public ConvertBookController(IBookFileConversionService bookFileConversionService)
        {
            _bookFileConversionService = bookFileConversionService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<ConvertBookResource>), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        public ActionResult<List<ConvertBookResource>> GetConvertibleBookFiles(int authorId, int? bookId)
        {
            if (authorId <= 0)
            {
                return BadRequest(new ApiErrorResource
                {
                    Message = "authorId is required."
                });
            }

            return _bookFileConversionService.GetConversionPreviews(authorId, bookId).ToResource();
        }
    }
}
