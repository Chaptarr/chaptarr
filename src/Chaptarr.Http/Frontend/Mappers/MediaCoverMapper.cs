using System;
using System.IO;
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

        public MediaCoverMapper(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider, Logger logger)
            : base(diskProvider, logger)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
        }

        public override string Map(string resourceUrl)
        {
            if (!MediaCoverRendition.IsSupportedImagePath(resourceUrl))
            {
                return null;
            }

            var path = resourceUrl.Replace('/', Path.DirectorySeparatorChar);
            path = path.Trim(Path.DirectorySeparatorChar);

            var resourcePath = Path.Combine(GetAllowedRoot(resourceUrl), path);

            if (!_diskProvider.FileExists(resourcePath) || _diskProvider.GetFileSize(resourcePath) == 0)
            {
                var baseResourcePath = MediaCoverRendition.GetOriginalPath(resourcePath);
                if (baseResourcePath != resourcePath)
                {
                    // Only fallback to originals for books; author originals are deleted after resize.
                    if (resourceUrl.Contains("/MediaCover/Books/"))
                    {
                        return baseResourcePath;
                    }
                }
            }

            return resourcePath;
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
