using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Download
{
    public class ProcessMonitoredDownloadsCommand : Command
    {
        public override bool RequiresDiskAccess => true;

        public override bool IsLongRunning => true;

        // The handler observes the cancellation token between downloads, so a started sweep can be cancelled.
        public override bool SupportsCancellation => true;
    }
}
