using NzbDrone.Core.Download;

namespace Chaptarr.Core.Test.Download
{
    internal sealed class NoopDownloadClientFileSnapshotService : IDownloadClientFileSnapshotService
    {
        public static readonly NoopDownloadClientFileSnapshotService Instance = new();

        public void CaptureClientList(DownloadClientItem item)
        {
        }

        public void CaptureCompletedOutput(DownloadClientItem item)
        {
        }

        public void ApplySnapshot(DownloadClientItem item)
        {
        }

        public void Delete(DownloadClientItem item)
        {
        }
    }
}
