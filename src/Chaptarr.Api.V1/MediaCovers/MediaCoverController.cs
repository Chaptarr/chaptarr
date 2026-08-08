using System.IO;
using Chaptarr.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.MediaCovers
{
    [V1ApiController]
    public class MediaCoverController : Controller
    {
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly IContentTypeProvider _mimeTypeProvider;

        public MediaCoverController(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
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
                // Author original images are deleted after resize to save space
                // No fallback available - return 404 if sized variant is missing
                return NotFound();
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
