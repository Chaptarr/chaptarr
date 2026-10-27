using System.Collections.Generic;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class TrackedDownloadRefreshedEvent : IEvent
    {
        public List<TrackedDownload> TrackedDownloads { get; private set; }
        public IReadOnlyDictionary<int, HashSet<string>> ReportedDownloadIds { get; }

        public TrackedDownloadRefreshedEvent(List<TrackedDownload> trackedDownloads,
            IReadOnlyDictionary<int, HashSet<string>> reportedDownloadIds = null)
        {
            TrackedDownloads = trackedDownloads;
            ReportedDownloadIds = reportedDownloadIds;
        }
    }
}
