using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using DownloadState = NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadState;

namespace NzbDrone.Core.Queue
{
    public class Queue : ModelBase
    {
        public bool HasActiveDownload() => !string.IsNullOrWhiteSpace(DownloadId) &&
            TrackedDownloadState != DownloadState.Imported &&
            TrackedDownloadState != DownloadState.DownloadFailed &&
            TrackedDownloadState != DownloadState.DownloadFailedPending &&
            TrackedDownloadState != DownloadState.Ignored;

        public List<int> GetTargetBookIds()
        {
            if (this.RemoteBook?.AlternativeCopies == true && this.Book?.Id > 0)
            {
                return new List<int> { this.Book.Id };
            }

            if (this.TargetBookIds?.Any() == true)
            {
                return this.TargetBookIds
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
            }

            if (this.Book?.Id > 0)
            {
                return new List<int> { this.Book.Id };
            }

            var matchingMediaTypeBooks = this.RemoteBook?.GetBooksMatchingReleaseMediaType();
            if (matchingMediaTypeBooks?.Any() == true)
            {
                return matchingMediaTypeBooks.Where(book => book?.Id > 0).Select(book => book.Id).Distinct().ToList();
            }

            return this.RemoteBook?.Books?.Where(book => book?.Id > 0).Select(book => book.Id).Distinct().ToList() ?? new List<int>();
        }


        public int PendingReleaseId { get; set; }
        public Author Author { get; set; }
        public Book Book { get; set; }
        public QualityModel Quality { get; set; }
        public decimal Size { get; set; }
        public string Title { get; set; }
        public decimal Sizeleft { get; set; }
        public TimeSpan? Timeleft { get; set; }
        public DateTime? EstimatedCompletionTime { get; set; }
        public DateTime? Added { get; set; }
        public string Status { get; set; }
        public TrackedDownloadStatus? TrackedDownloadStatus { get; set; }
        public TrackedDownloadState? TrackedDownloadState { get; set; }
        public List<TrackedDownloadStatusMessage> StatusMessages { get; set; }
        public string DownloadId { get; set; }
        public List<int> TargetBookIds { get; set; } = new();
        public string ConversionStatus { get; set; }
        public int? ConvertToQualityId { get; set; }
        public string ConvertToQuality { get; set; }
        public decimal? ConversionProgress { get; set; }
        public string ConversionMessage { get; set; }
        public bool CanCancelConversion { get; set; }
        public bool CanRetryImport { get; set; }
        public bool AwaitingImportConfirmation { get; set; }
        public RemoteBook RemoteBook { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public string DownloadClient { get; set; }
        public bool DownloadClientHasPostImportCategory { get; set; }
        public string Indexer { get; set; }
        public string OutputPath { get; set; }
        public string ErrorMessage { get; set; }
        public bool DownloadForced { get; set; }
    }
}
