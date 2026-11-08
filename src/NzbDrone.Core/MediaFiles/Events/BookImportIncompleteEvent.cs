using System;
using System.Collections.Generic;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class BookImportIncompleteEvent : IEvent
    {
        public TrackedDownload TrackedDownload { get; private set; }
        public DateTime Date { get; private set; }
        public DownloadHistory PersistedHistory { get; private set; }
        public List<EntityHistory> PreviousHistory { get; private set; }

        public BookImportIncompleteEvent(TrackedDownload trackedDownload, DownloadHistory persistedHistory = null, List<EntityHistory> previousHistory = null)
        {
            TrackedDownload = trackedDownload;
            PersistedHistory = persistedHistory;
            PreviousHistory = previousHistory;
            Date = persistedHistory?.Date ?? DateTime.UtcNow;
        }
    }
}
