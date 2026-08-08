using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class UpdateLogFileMapper : StaticResourceMapperBase
    {
        private readonly IAppFolderInfo _appFolderInfo;

        public UpdateLogFileMapper(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider, Logger logger)
            : base(diskProvider, logger)
        {
            _appFolderInfo = appFolderInfo;
        }

        public override string Map(string resourceUrl)
        {
            var path = resourceUrl.Replace('/', Path.DirectorySeparatorChar);
            path = Path.GetFileName(path);

            return Path.Combine(_appFolderInfo.GetUpdateLogFolder(), path);
        }

        protected override string GetAllowedRoot(string resourceUrl)
        {
            return _appFolderInfo.GetUpdateLogFolder();
        }

        public override bool CanHandle(string resourceUrl)
        {
            if (resourceUrl == null)
            {
                return false;
            }

            if (!resourceUrl.StartsWith("/updatelogfile/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = resourceUrl.Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(path);

            return LogFileNameValidator.IsSafeTxtLogFileName(fileName);
        }
    }
}
