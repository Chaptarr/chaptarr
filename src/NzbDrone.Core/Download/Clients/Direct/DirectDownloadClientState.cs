using System;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public class DirectDownloadClientState
    {
        public string DownloadId { get; set; }
        public string Title { get; set; }
        public string DownloadUrl { get; set; }
        public DownloadItemStatus Status { get; set; }
        public string OutputFilePath { get; set; }
        public string PartFilePath { get; set; }
        public string Message { get; set; }
        public long TotalSize { get; set; }
        public long DownloadedBytes { get; set; }
        public int AttemptCount { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? ImportedAtUtc { get; set; }

        public string OutputDirectory => System.IO.Path.GetDirectoryName(OutputFilePath);

        public string ActivePath => Status == DownloadItemStatus.Completed ? OutputFilePath : PartFilePath;
    }
}
