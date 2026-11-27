using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class MediaCoverMapper : StaticResourceMapperBase
    {
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public MediaCoverMapper(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider, Logger logger)
            : base(diskProvider, logger)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public override string Map(string resourceUrl)
        {
            if (!MediaCoverRendition.IsSupportedImagePath(resourceUrl) ||
                Path.GetFileName(resourceUrl).StartsWith(DiskProviderBase.TemporaryWritePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = resourceUrl.Replace('/', Path.DirectorySeparatorChar);
            path = path.Trim(Path.DirectorySeparatorChar);

            var resourcePath = Path.Combine(GetAllowedRoot(resourceUrl), path);
            if (!_diskProvider.FileExists(resourcePath) || _diskProvider.GetFileSize(resourcePath) == 0)
            {
                var baseResourcePath = MediaCoverRendition.GetOriginalPath(resourcePath);
                if (resourceUrl.Contains("/MediaCover/Books/"))
                {
                    return baseResourcePath;
                }

                var name = Path.GetFileNameWithoutExtension(baseResourcePath);
                var extension = Path.GetExtension(baseResourcePath);
                if (Enum.TryParse<MediaCoverTypes>(name, true, out var coverType) &&
                    (resourcePath == baseResourcePath || MediaCoverRendition.GetHeights(coverType).Any(height =>
                        Path.GetFileName(resourcePath).Equals($"{name}-{height}{extension}", StringComparison.OrdinalIgnoreCase))))
                {
                    // Serve the same photo's remaining size, never another URL-keyed variant.
                    return MediaCoverRendition.FindExistingPath(
                        height => height.HasValue
                            ? Path.Combine(Path.GetDirectoryName(baseResourcePath), $"{name}-{height}{extension}")
                            : baseResourcePath,
                        _diskProvider, coverType) ?? resourcePath;
                }
            }

            return resourcePath;
        }

        protected override FileStreamResult CreateFileResult(string resourceUrl, string filePath, Stream contentStream, string contentType)
        {
            if (contentStream.Length == 0)
            {
                contentStream.Dispose();
                _logger.Debug("Not serving empty media cover: {0}", resourceUrl);
                return null;
            }

            if (!Path.GetFileName(resourceUrl).Equals(Path.GetFileName(filePath), StringComparison.Ordinal))
            {
                // Keep originals cacheable, including formats the resizer cannot decode.
                // A rendition substitute must not hide a later repair at this same URL.
                var original = MediaCoverRendition.GetOriginalPath(filePath) == filePath;
                _logger.Debug("Serving media cover substitute: {0} -> {1}; source: {2}",
                    resourceUrl, Path.GetFileName(filePath), original ? "original (default caching)" : "rendition (no-store)");
                if (!original)
                {
                    return new UncachedFileStreamResult(contentStream, contentType);
                }
            }

            return base.CreateFileResult(resourceUrl, filePath, contentStream, contentType);
        }

        private sealed class UncachedFileStreamResult : FileStreamResult
        {
            public UncachedFileStreamResult(Stream stream, string contentType)
                : base(stream, contentType)
            {
            }

            public override Task ExecuteResultAsync(ActionContext context)
            {
                context.HttpContext.Response.Headers.DisableCache();
                return base.ExecuteResultAsync(context);
            }
        }

        protected override string GetAllowedRoot(string resourceUrl)
        {
            return _appFolderInfo.GetAppDataPath();
        }

        public override bool CanHandle(string resourceUrl)
        {
            return resourceUrl.StartsWith("/MediaCover/", StringComparison.InvariantCultureIgnoreCase) &&
                   MediaCoverRendition.IsSupportedImagePath(resourceUrl);
        }
    }
}
