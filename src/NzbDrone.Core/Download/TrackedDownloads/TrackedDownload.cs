using System;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class TrackedDownload
    {
        public int DownloadClient { get; set; }
        public DownloadClientItem DownloadItem { get; set; }
        public DownloadClientItem ImportItem { get; set; }
        public TrackedDownloadState State { get; set; }
        public TrackedDownloadStatus Status { get; private set; }
        public RemoteBook RemoteBook { get; set; }
        public TrackedDownloadStatusMessage[] StatusMessages { get; private set; }
        public DownloadProtocol Protocol { get; set; }
        public string Indexer { get; set; }
        public DateTime? Added { get; set; }
        public bool IsTrackable { get; set; }
        public bool CleanupCheckFailed { get; set; }
        public bool SlowCleanupProbeReported { get; set; }
        public bool CleanupDeletionRefused { get; set; }
        public bool AwaitingImportConfirmation { get; set; }

        public TrackedDownload()
        {
            StatusMessages = new TrackedDownloadStatusMessage[] { };
        }

        public void Warn(string message, params object[] args)
        {
            var statusMessage = string.Format(message, args);
            Warn(new TrackedDownloadStatusMessage(DownloadItem.Title, statusMessage));
        }

        public void Warn(params TrackedDownloadStatusMessage[] statusMessages)
        {
            Status = TrackedDownloadStatus.Warning;
            StatusMessages = statusMessages;
        }

        public void Wait(params TrackedDownloadStatusMessage[] statusMessages)
        {
            Status = TrackedDownloadStatus.Ok;
            StatusMessages = statusMessages;
        }

        public void ClearStatus()
        {
            Status = TrackedDownloadStatus.Ok;
            StatusMessages = Array.Empty<TrackedDownloadStatusMessage>();
        }
    }

    public enum TrackedDownloadState
    {
        Downloading,
        DownloadFailed,
        DownloadFailedPending,
        ImportPending,
        Importing,
        Imported,
        Ignored,
        ImportBlocked
    }

    public enum TrackedDownloadStatus
    {
        Ok,
        Warning,
        Error
    }
}
