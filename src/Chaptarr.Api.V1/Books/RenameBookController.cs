using System.Collections.Generic;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("rename")]
    public class RenameBookController : Controller
    {
        private readonly IRenameBookFileService _renameBookFileService;

        public RenameBookController(IRenameBookFileService renameBookFileService)
        {
            _renameBookFileService = renameBookFileService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<RenameBookResource>), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        public ActionResult<List<RenameBookResource>> GetBookFiles(int authorId, int? bookId, string mediaType = null, bool moveToCanonicalAuthorFolder = false)
        {
            if (bookId.HasValue)
            {
                if (moveToCanonicalAuthorFolder)
                {
                    return BadRequest(new ApiErrorResource
                    {
                        Message = "moveToCanonicalAuthorFolder is only supported for author-level previews; omit bookId."
                    });
                }

                return _renameBookFileService.GetRenamePreviews(authorId, bookId.Value).ToResource();
            }

            return _renameBookFileService.GetRenamePreviews(authorId, MediaTypeParameterParser.NormalizeOptional(mediaType), moveToCanonicalAuthorFolder).ToResource();
        }
    }
}
