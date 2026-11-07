using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("retag")]
    public class RetagBookController : Controller
    {
        private readonly IMetadataTagService _metadataTagService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public RetagBookController(
            IMetadataTagService metadataTagService,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _metadataTagService = metadataTagService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [HttpGet]
        public List<RetagBookResource> GetBooks(int? authorId, int? bookId, string mediaType = null)
        {
            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;
            IEnumerable<RetagBookFilePreview> previews;

            if (bookId.HasValue)
            {
                var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(
                    _legacyIdentityMapService,
                    new[] { bookId.Value },
                    MediaTypeParameterParser.ParseOptional(mediaType));
                if (resolvedBookIds.Count == 0)
                {
                    return new List<RetagBookResource>();
                }

                bookId = resolvedBookIds[0];

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
