using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Download
{
    public class RetryFailedImportCommand : Command
    {
        public string DownloadId { get; set; }
        public List<string> DownloadIds { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
        public override string DiskAccessGroup => "downloadImport";
    }
}
