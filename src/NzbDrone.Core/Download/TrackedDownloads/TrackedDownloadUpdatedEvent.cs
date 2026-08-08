using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class TrackedDownloadUpdatedEvent : IEvent
    {
        public TrackedDownload TrackedDownload { get; }

        public TrackedDownloadUpdatedEvent(TrackedDownload trackedDownload)
        {
            TrackedDownload = trackedDownload;
        }
    }
}
