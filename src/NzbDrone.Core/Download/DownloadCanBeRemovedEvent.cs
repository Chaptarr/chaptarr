using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.Download
{
    public class DownloadCanBeRemovedEvent : IEvent
    {
        public TrackedDownload TrackedDownload { get; private set; }

        public DownloadCleanupContext Cleanup { get; }

        public DownloadCanBeRemovedEvent(TrackedDownload trackedDownload, DownloadCleanupContext cleanup = null)
        {
            TrackedDownload = trackedDownload;
            Cleanup = cleanup;
        }
    }
}
