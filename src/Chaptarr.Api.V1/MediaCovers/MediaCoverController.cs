using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.MediaCovers
{
    [V1ApiController]
    public class MediaCoverController : Controller
    {
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly IContentTypeProvider _mimeTypeProvider;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public MediaCoverController(
            IAppFolderInfo appFolderInfo,
            IDiskProvider diskProvider,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _legacyIdentityMapService = legacyIdentityMapService;
            _mimeTypeProvider = new FileExtensionContentTypeProvider();
        }

        [HttpGet(@"{authorId:int}/{filename}")]
        [HttpGet(@"author/{authorId:int}/{filename}")]
        public IActionResult GetAuthorMediaCover(int authorId, string filename)
        {
            if (!MediaCoverRendition.IsSupportedImagePath(filename))
            {
                return NotFound();
            }

            var baseDir = Path.Combine(_appFolderInfo.GetAppDataPath(), "MediaCover", authorId.ToString());
            var filePath = GetSafePath(baseDir, filename);

            if (filePath == null)
            {
                return NotFound();
            }

            if (!_diskProvider.FileExists(filePath) || _diskProvider.GetFileSize(filePath) == 0)
            {
                // FreeBSD has no native resizer, and a failed resize on any platform
                // retains the original. Serve that image rather than a broken cover.
                var originalPath = MediaCoverRendition.GetOriginalPath(filePath);
                if (originalPath == filePath || !_diskProvider.FileExists(originalPath) ||
                    _diskProvider.GetFileSize(originalPath) == 0)
                {
                    return NotFound();
                }

                filePath = originalPath;
            }

            return PhysicalFile(filePath, GetContentType(filePath));
        }

        [HttpGet(@"Books/{bookId:int}/{filename}")]
        [HttpGet(@"book/{bookId:int}/{filename}")]
        public IActionResult GetBookMediaCover(int bookId, string filename)
        {
            if (!MediaCoverRendition.IsSupportedImagePath(filename))
            {
                return NotFound();
            }

            var mediaType = MediaTypeParameterParser.ParseOptional(HttpContext?.GetReadarrFacadeContext()?.MediaType);
            var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(_legacyIdentityMapService, new[] { bookId }, mediaType);
            if (resolvedBookIds.Count == 0)
            {
                return NotFound();
            }

            bookId = resolvedBookIds[0];

            var baseDir = Path.Combine(_appFolderInfo.GetAppDataPath(), "MediaCover", "Books", bookId.ToString());
            var filePath = GetSafePath(baseDir, filename);

            if (filePath == null)
            {
                return NotFound();
            }

            if (!_diskProvider.FileExists(filePath) || _diskProvider.GetFileSize(filePath) == 0)
            {
                // Return the full sized image if someone requests a non-existing resized one.
                // TODO: This code can be removed later once everyone had the update for a while.
                var basefilePath = MediaCoverRendition.GetOriginalPath(filePath);
                if (basefilePath == filePath || !_diskProvider.FileExists(basefilePath))
                {
                    return NotFound();
                }

                filePath = basefilePath;
            }

            return PhysicalFile(filePath, GetContentType(filePath));
        }

        private static string GetSafePath(string baseDir, string filename)
        {
            if (baseDir.IsNullOrWhiteSpace() || filename.IsNullOrWhiteSpace())
            {
                return null;
            }

            var fullBaseDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
            var combined = Path.GetFullPath(Path.Combine(fullBaseDir, filename));

            // Allow the base directory itself and any descendants.
            if (combined.PathEquals(fullBaseDir) ||
                combined.StartsWith(fullBaseDir + Path.DirectorySeparatorChar, DiskProviderBase.PathStringComparison))
            {
                return combined;
            }

            return null;
        }

        private string GetContentType(string filePath)
        {
            if (!_mimeTypeProvider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return contentType;
        }
    }
}
