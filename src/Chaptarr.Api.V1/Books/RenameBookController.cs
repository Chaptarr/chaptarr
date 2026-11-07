using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("rename")]
    public class RenameBookController : Controller
    {
        private readonly IRenameBookFileService _renameBookFileService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public RenameBookController(
            IRenameBookFileService renameBookFileService,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _renameBookFileService = renameBookFileService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<RenameBookResource>), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        public ActionResult<List<RenameBookResource>> GetBookFiles(int authorId, int? bookId, string mediaType = null, bool moveToCanonicalAuthorFolder = false)
        {
            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;
            if (bookId.HasValue)
            {
                if (moveToCanonicalAuthorFolder)
                {
                    return BadRequest(new ApiErrorResource
                    {
                        Message = "moveToCanonicalAuthorFolder is only supported for author-level previews; omit bookId."
                    });
                }

                var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(
                    _legacyIdentityMapService,
                    new[] { bookId.Value },
                    MediaTypeParameterParser.ParseOptional(mediaType));
                if (resolvedBookIds.Count == 0)
                {
                    return NotFound();
                }

                var previews = resolvedBookIds
                    .Distinct()
                    .SelectMany(targetBookId => _renameBookFileService.GetRenamePreviews(authorId, targetBookId))
                    .ToResource();
                ProjectLegacyIdentities(previews, bookId.Value);
                return previews;
            }

            var authorPreviews = _renameBookFileService
                .GetRenamePreviews(authorId, MediaTypeParameterParser.NormalizeOptional(mediaType), moveToCanonicalAuthorFolder)
                .ToResource();
            ProjectLegacyIdentities(authorPreviews, null);
            return authorPreviews;
        }

        private void ProjectLegacyIdentities(List<RenameBookResource> resources, int? sourceBookId)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true || _legacyIdentityMapService == null || resources == null)
            {
                return;
            }

            var editionsByTargetId = _legacyIdentityMapService
                .GetMappingsByTargetIds("Edition", resources.Select(resource => resource.EditionId))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var filesByTargetId = _legacyIdentityMapService
                .GetMappingsByTargetIds("BookFile", resources.Select(resource => resource.BookFileId))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var booksByTargetId = _legacyIdentityMapService
                .GetMappingsByTargetIds("Book", resources.Select(resource => resource.BookId))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            foreach (var resource in resources)
            {
                if (sourceBookId.HasValue)
                {
                    resource.BookId = sourceBookId.Value;
                }
                else if (booksByTargetId.TryGetValue(resource.BookId, out var bookMapping))
                {
                    resource.BookId = bookMapping.SourceId;
                }

                if (editionsByTargetId.TryGetValue(resource.EditionId, out var editionMapping))
                {
                    resource.EditionId = editionMapping.SourceId;
                }

                if (filesByTargetId.TryGetValue(resource.BookFileId, out var fileMapping))
                {
                    resource.BookFileId = fileMapping.SourceId;
                    resource.Id = fileMapping.SourceId;
                }
            }
        }
    }
}
