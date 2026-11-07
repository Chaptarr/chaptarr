using System;

namespace NzbDrone.Core.Download
{
    public class ExistingTorrentFoundException : Exception
    {
        public string DownloadClientName { get; }
        public string DownloadTitle { get; }

        public ExistingTorrentFoundException(string downloadClientName, DownloadClientItem item)
            : base($"This torrent is already present in {downloadClientName} as '{item.Title}'.")
        {
            DownloadClientName = downloadClientName;
            DownloadTitle = item.Title;
        }
    }

    public class ExistingTorrentNotFoundException : Exception
    {
        public ExistingTorrentNotFoundException(string message)
            : base(message)
        {
        }
    }
}
