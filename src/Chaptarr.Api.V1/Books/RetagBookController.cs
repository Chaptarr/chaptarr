using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
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
            IEnumerable<RetagBookFilePreview> previews;

            if (bookId.HasValue)
            {
                previews = _metadataTagService.GetRetagPreviewsByBook(bookId.Value);
            }
            else if (authorId.HasValue)
            {
                previews = _metadataTagService.GetRetagPreviewsByAuthor(authorId.Value);
            }
            else
            {
                throw new BadRequestException("One of authorId or bookId must be specified");
            }

            previews = previews.Where(x => x.Changes.Any());

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                IReadOnlySet<string> allowedExtensions;
                var parsedMediaType = MediaTypeParameterParser.ParseRequired(mediaType);

                if (parsedMediaType == BookMediaType.Audiobook)
                {
                    allowedExtensions = MediaFileExtensions.AudioExtensions;
                }
                else
                {
                    allowedExtensions = MediaFileExtensions.TextExtensions;
                }

                previews = previews.Where(p => allowedExtensions.Contains(Path.GetExtension(p.Path ?? string.Empty)));
            }

            return previews.ToResource();
        }
    }
}
