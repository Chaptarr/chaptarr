using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Download.Clients
{
    public static class TorrentClientPathHelper
    {
        public static OsPath CombineClientPath(OsPath basePath, string memberPath)
        {
            return basePath + new OsPath(memberPath, OsPathKind.Unknown);
        }
    }
}
