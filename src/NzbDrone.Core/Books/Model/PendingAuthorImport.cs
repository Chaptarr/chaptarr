using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    internal static class PendingAuthorImportRetryReason
    {
        public const string AuthorNotYetAvailable = "Author not yet available on metadata server";
    }

    public enum PendingImportStatus
    {
        NotRequested,
        Pending,
        InProgress,
        Retrying,
        PartialSuccess,
        Succeeded,
        Failed
    }

    public class PendingAuthorImport : ModelBase
    {
        // Core identification
        public string ProviderId { get; set; }
        public string ProviderPrefix { get; set; }
        public string AuthorName { get; set; }
        // Discovered author folder path (when known) to preserve existing folder structure
        public string DiscoveredAuthorFolderPath { get; set; }

        // Dual-instance status tracking
        public PendingImportStatus AudiobookStatus { get; set; }
        public PendingImportStatus EbookStatus { get; set; }
        public PendingImportStatus OverallStatus { get; set; }

        // Audiobook configuration
        public int? AudiobookMonitorExisting { get; set; } // 0=None, 1=All, 2=Selected
        public bool? AudiobookMonitorFuture { get; set; } // true=monitor, false=don't monitor
        public int? AudiobookQualityProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public string AudiobookRootFolderPath { get; set; }
        public string AudiobookBooksToMonitor { get; set; } // JSON serialized List<string>

        // Ebook configuration
        public int? EbookMonitorExisting { get; set; } // 0=None, 1=All, 2=Selected
        public bool? EbookMonitorFuture { get; set; } // true=monitor, false=don't monitor
        public int? EbookQualityProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }
        public string EbookRootFolderPath { get; set; }
        public string EbookBooksToMonitor { get; set; } // JSON serialized List<string>

        // Common fields
        public string Tags { get; set; } // JSON serialized HashSet<int>
        public bool SearchForMissingBooks { get; set; }

        // Tracking fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime NextAttemptAt { get; set; }
        public int AttemptCount { get; set; }
        public int MaxAttempts { get; set; }
        public string LastError { get; set; }

        // Source tracking
        public string RequestedBy { get; set; }
        public string SourceApplication { get; set; }
        public string CorrelationId { get; set; }

        // Locking fields for concurrent access control
        public string LockedBy { get; set; }
        public DateTime? LockedAt { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public long Version { get; set; }

        // Helper methods
        public bool HasAudiobook()
        {
            return AudiobookStatus != PendingImportStatus.NotRequested;
        }

        public bool HasEbook()
        {
            return EbookStatus != PendingImportStatus.NotRequested;
        }

        public bool IsActive()
        {
            return OverallStatus == PendingImportStatus.Pending ||
                   OverallStatus == PendingImportStatus.InProgress ||
                   OverallStatus == PendingImportStatus.Retrying;
        }

        public bool IsComplete()
        {
            return OverallStatus == PendingImportStatus.Succeeded ||
                   OverallStatus == PendingImportStatus.Failed;
        }

        public void UpdateOverallStatus()
        {
            var audiobookDone = AudiobookStatus == PendingImportStatus.NotRequested ||
                               AudiobookStatus == PendingImportStatus.Succeeded ||
                               AudiobookStatus == PendingImportStatus.Failed;

            var ebookDone = EbookStatus == PendingImportStatus.NotRequested ||
                           EbookStatus == PendingImportStatus.Succeeded ||
                           EbookStatus == PendingImportStatus.Failed;

            var audiobookSuccess = AudiobookStatus == PendingImportStatus.NotRequested ||
                                  AudiobookStatus == PendingImportStatus.Succeeded;

            var ebookSuccess = EbookStatus == PendingImportStatus.NotRequested ||
                              EbookStatus == PendingImportStatus.Succeeded;

            if (audiobookDone && ebookDone)
            {
                if (audiobookSuccess && ebookSuccess)
                {
                    OverallStatus = PendingImportStatus.Succeeded;
                }
                else if (!audiobookSuccess && !ebookSuccess)
                {
                    OverallStatus = PendingImportStatus.Failed;
                }
                else
                {
                    OverallStatus = PendingImportStatus.PartialSuccess;
                }
            }
            else if (AudiobookStatus == PendingImportStatus.InProgress ||
                    EbookStatus == PendingImportStatus.InProgress)
            {
                OverallStatus = PendingImportStatus.InProgress;
            }
            else
            {
                OverallStatus = PendingImportStatus.Retrying;
            }
        }
    }
}
