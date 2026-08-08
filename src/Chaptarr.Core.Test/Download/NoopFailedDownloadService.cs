using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;

namespace Chaptarr.Core.Test.Download
{
    /// <summary>
    /// Shared inert IFailedDownloadService for fixtures that construct services by hand and do not
    /// exercise failure handling. Mirrors <see cref="NoopDownloadClientFileSnapshotService"/>.
    /// </summary>
    public sealed class NoopFailedDownloadService : IFailedDownloadService
    {
        public static readonly NoopFailedDownloadService Instance = new NoopFailedDownloadService();

        public void MarkAsFailed(int historyId, bool skipRedownload = false)
        {
        }

        public void MarkAsFailed(string downloadId, bool skipRedownload = false)
        {
        }

        public void MarkAsFailed(TrackedDownload trackedDownload, string reason, bool skipRedownload = false)
        {
        }

        public void Check(TrackedDownload trackedDownload)
        {
        }

        public void ProcessFailed(TrackedDownload trackedDownload)
        {
        }
    }
}
