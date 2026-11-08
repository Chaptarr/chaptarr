using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class TrackedDownloadRefreshedEvent : IEvent
    {
        public List<TrackedDownload> TrackedDownloads { get; private set; }
        public IReadOnlyDictionary<int, HashSet<string>> ReportedDownloadIds { get; }
        public IReadOnlyDictionary<int, HashSet<string>> RetainedDownloadIds { get; }

        // Successful client history is bounded; retained imports remain authoritative too.
        public bool IsProvablyMissing(int clientId, string downloadId) =>
            RetainedDownloadIds.TryGetValue(clientId, out var retained) && !retained.Contains(downloadId);

        public TrackedDownloadRefreshedEvent(List<TrackedDownload> trackedDownloads,
            IReadOnlyDictionary<int, HashSet<string>> reportedDownloadIds = null)
        {
            TrackedDownloads = trackedDownloads;
            ReportedDownloadIds = reportedDownloadIds;
            var tracked = (trackedDownloads ?? new List<TrackedDownload>())
                .Where(item => item?.DownloadItem?.DownloadId != null)
                .ToLookup(item => item.DownloadClient, item => item.DownloadItem.DownloadId);
            RetainedDownloadIds = (reportedDownloadIds ?? new Dictionary<int, HashSet<string>>())
                .ToDictionary(client => client.Key,
                    client => client.Value.Concat(tracked[client.Key]).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }
}
