using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Backup;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class BackupFileMapper : StaticResourceMapperBase
    {
        private readonly IBackupService _backupService;

        public BackupFileMapper(IBackupService backupService, IDiskProvider diskProvider, Logger logger)
            : base(diskProvider, logger)
        {
            _backupService = backupService;
        }

        public override string Map(string resourceUrl)
        {
            var path = resourceUrl.Replace("/backup/", "").Replace('/', Path.DirectorySeparatorChar);

            return Path.Combine(GetAllowedRoot(resourceUrl), path);
        }

        protected override string GetAllowedRoot(string resourceUrl)
        {
            return _backupService.GetBackupFolder();
        }

        public override bool CanHandle(string resourceUrl)
        {
            return resourceUrl.StartsWith("/backup/") && BackupService.BackupFileRegex.IsMatch(resourceUrl);
        }
    }
}
