using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public enum ConversionJobStatus
    {
        Queued = 0,
        Converting = 1,
        ReadyToImport = 2,
        Completed = 3,
        Failed = 4,
        Cancelling = 5,
        Cancelled = 6
    }

    public class ConversionJob : ModelBase
    {
        public string DownloadId { get; set; }
        public ConversionJobStatus Status { get; set; }
        public string RequestJson { get; set; }
        public string WorkRoot { get; set; }
        public string WorkFolder { get; set; }
        public string OutputPath { get; set; }
        public int TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        // SQLite NUMERIC can return whole-number progress values as INTEGER (for example 0 or 100).
        public decimal? Progress { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public int AttemptCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? HeartbeatAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class ConversionJobRequest
    {
        public string DownloadId { get; set; }
        public string BookTitle { get; set; }
        public string WorkRoot { get; set; }
        public string WorkFolder { get; set; }
        public string OutputPath { get; set; }
        public List<string> ConversionInputFiles { get; set; } = new();
        public List<ConversionArtifactSource> Sources { get; set; } = new();
        public int TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public int AudioBitrate { get; set; }
        public int AudioSampleRate { get; set; }
        public int AudioChannels { get; set; }
        public bool NoConversion { get; set; }
        public long ExpectedSourceDurationTicks { get; set; }
        public string TagSignature { get; set; }
        public ConversionTagOptions TagOptions { get; set; }
    }

    public class ConversionArtifactSource
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public long ModifiedUtcTicks { get; set; }
    }

    public class ConversionArtifactManifest
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; }
        public bool Importable { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string OutputPath { get; set; }
        public long OutputSize { get; set; }
        public long OutputModifiedUtcTicks { get; set; }
        public string OutputSha256 { get; set; }
        public int TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public int AudioBitrate { get; set; }
        public int AudioSampleRate { get; set; }
        public int AudioChannels { get; set; }
        public bool NoConversion { get; set; }
        public string TagMode { get; set; }
        public string TagSignature { get; set; }
        public List<ConversionArtifactSource> Sources { get; set; } = new();
    }
}
