using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.RemotePathMappings
{
    public class RemotePathMapping : ModelBase
    {
        public int DownloadClientId { get; set; }
        public string Host { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
    }
}
