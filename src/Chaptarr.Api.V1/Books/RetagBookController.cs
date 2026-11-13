using System.Collections.Generic;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("retag")]
    public class RetagBookController : Controller
    {
        private readonly IMetadataTagService _metadataTagService;

        public RetagBookController(IMetadataTagService metadataTagService)
        {
            _metadataTagService = metadataTagService;
        }

        [HttpGet]
        public List<RetagBookResource> GetBooks(int? authorId, int? bookId, string mediaType = null)
        {
            var scope = MediaTypeParameterParser.ParseOptional(mediaType);
            IEnumerable<RetagBookFilePreview> previews;

            if (bookId.HasValue)
            {
                previews = _metadataTagService.GetRetagPreviewsByBook(bookId.Value, scope);
            }
            else if (authorId.HasValue)
            {
                previews = _metadataTagService.GetRetagPreviewsByAuthor(authorId.Value, scope);
            }
            else
            {
                throw new BadRequestException("One of authorId or bookId must be specified");
            }

            return previews.ToResource();
        }
    }
}
