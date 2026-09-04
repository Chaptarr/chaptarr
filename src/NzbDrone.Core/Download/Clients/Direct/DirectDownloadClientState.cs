using System;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public enum DirectDownloadFallbackMode
    {
        /// <summary>No browser fallback needed — use the resolved or original URL directly.</summary>
        None = 0,

        /// <summary>
        /// API resolution failed or was unavailable; the next download attempt should
        /// use Playwright browser fallback to obtain a slow-download URL.
        /// Survives restart so the deferred attempt is not lost.
        /// </summary>
        DeferredPlaywright = 1
    }

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

        /// <summary>
        /// The URL resolved by the API fast-download endpoint. When set, the download
        /// can skip re-resolution on restart and use this URL directly.
        /// Null means resolution has not been attempted or the original <see cref="DownloadUrl"/>
        /// should be used as-is.
        /// </summary>
        public string ResolvedUrl { get; set; }

        /// <summary>
        /// Tracks whether a Playwright browser fallback has been deferred for the next attempt.
        /// On restart, if this is <see cref="DirectDownloadFallbackMode.DeferredPlaywright"/>,
        /// the download system should attempt browser-based URL resolution before falling back to
        /// the original <see cref="DownloadUrl"/>.
        /// </summary>
        public DirectDownloadFallbackMode FallbackMode { get; set; }

        public string OutputDirectory => System.IO.Path.GetDirectoryName(OutputFilePath);

        public string ActivePath => Status == DownloadItemStatus.Completed ? OutputFilePath : PartFilePath;
    }
}
