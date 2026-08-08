using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class PendingImport : ModelBase
    {
        public PendingImport()
        {
            CreatedAt = DateTime.UtcNow;
            NextRetryAt = DateTime.UtcNow; // Try immediately first time
            RetryCount = 0;
            Status = PendingImportStatus.Pending;
        }

        public string ImportType { get; set; } // 'book', 'series', 'author'
        public string ProviderIds { get; set; } // JSON with provider IDs
        public string MediaType { get; set; } // 'audiobook', 'ebook', 'both'
        public string MonitoringType { get; set; } // 'specific_book', 'all_books', 'series_books', 'none'
        public string MonitoringIds { get; set; } // JSON array of provider IDs to monitor
        public string Settings { get; set; } // JSON with quality profiles, metadata profiles, tags, root folders
        public DateTime CreatedAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime NextRetryAt { get; set; }
        public int RetryCount { get; set; }
        public PendingImportStatus Status { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? AuthorId { get; set; } // Local author ID once imported
    }

    // PendingImportStatus enum moved to PendingAuthorImport.cs

    public class PendingImportProviderIds
    {
        public string HardcoverAuthorId { get; set; }
        public string GoodreadsAuthorId { get; set; }
        public string OpenLibraryAuthorId { get; set; }
        public string GoogleBooksAuthorId { get; set; }
        public string HardcoverBookId { get; set; }
        public string GoodreadsBookId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string GoogleBooksId { get; set; }
        public string HardcoverSeriesId { get; set; }
        public string GoodreadsSeriesId { get; set; }
        public string AuthorName { get; set; } // Fallback for display
        public string BookTitle { get; set; } // Fallback for display
        public string SeriesTitle { get; set; } // Fallback for display
    }

    public class PendingImportSettings
    {
        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }
        public HashSet<int> AudiobookTags { get; set; }
        public HashSet<int> EbookTags { get; set; }
        public string Monitor { get; set; } // MonitorTypes value
        public bool SearchForMissingBooks { get; set; }
    }
}
