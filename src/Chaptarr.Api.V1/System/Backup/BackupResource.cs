using System;
using Chaptarr.Http.REST;
using NzbDrone.Core.Backup;

namespace Chaptarr.Api.V1.System.Backup
{
    public class BackupResource : RestResource
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public BackupType Type { get; set; }
        public long Size { get; set; }
        public DateTime Time { get; set; }
    }
}
