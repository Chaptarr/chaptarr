using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Qualities;
using CoreTrackedDownloadState = NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadState;

namespace Chaptarr.Api.V1.Queue
{
    /// <summary>
    /// BookshelfNG v1 queue-state contract. ChaptarrNG's retryable ImportBlocked state
    /// is exposed as the legacy ImportFailed value while its richer behavior stays internal.
    /// </summary>
    public enum TrackedDownloadState
    {
        Downloading,
        DownloadFailed,
        DownloadFailedPending,
        ImportPending,
        Importing,
        ImportFailed,
        Imported,
        Ignored
    }

    public class QueueResource : RestResource
    {
        public int? AuthorId { get; set; }
        public int? BookId { get; set; }
        public AuthorResource Author { get; set; }
        public BookResource Book { get; set; }
        public QualityModel Quality { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
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
        public string ErrorMessage { get; set; }
        public string DownloadId { get; set; }
        public string ConversionStatus { get; set; }
        public int? ConvertToQualityId { get; set; }
        public string ConvertToQuality { get; set; }
        public decimal? ConversionProgress { get; set; }
        public string ConversionMessage { get; set; }
        public bool CanCancelConversion { get; set; }
        public bool CanRetryImport { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public string DownloadClient { get; set; }
        public bool DownloadClientHasPostImportCategory { get; set; }
        public string Indexer { get; set; }
        public string OutputPath { get; set; }
        public bool DownloadForced { get; set; }
    }

    public static class QueueResourceMapper
    {
        public static QueueResource ToResource(this NzbDrone.Core.Queue.Queue model, bool includeAuthor, bool includeBook)
        {
            return model.ToResource(includeAuthor, includeBook, null);
        }

        public static QueueResource ToResource(this NzbDrone.Core.Queue.Queue model, bool includeAuthor, bool includeBook, ReadarrFacadeContext facadeContext)
        {
            if (model == null)
            {
                return null;
            }

            var customFormats = model.RemoteBook?.CustomFormats;
            var qualityProfile = model.RemoteBook?.Author?.GetQualityProfileForQuality(model.Quality.Quality);
            var customFormatScore = qualityProfile?.CalculateCustomFormatScore(customFormats) ?? 0;

            return new QueueResource
            {
                Id = model.Id,
                AuthorId = model.Author?.Id,
                BookId = model.Book?.Id,
                Author = includeAuthor && model.Author != null ? model.Author.ToResource(facadeContext) : null,
                Book = includeBook && model.Book != null ? model.Book.ToResource(new BookResourceMappingOptions { FacadeContext = facadeContext }) : null,
                Quality = model.Quality,
                CustomFormats = customFormats?.ToResource(false),
                CustomFormatScore = customFormatScore,
                Size = model.Size,
                Title = model.Title,
                Sizeleft = model.Sizeleft,
                Timeleft = model.Timeleft,
                EstimatedCompletionTime = model.EstimatedCompletionTime,
                Added = model.Added,
                Status = model.Status.FirstCharToLower(),
                TrackedDownloadStatus = model.TrackedDownloadStatus,
                TrackedDownloadState = ToBookshelfTrackedDownloadState(model.TrackedDownloadState),
                StatusMessages = model.StatusMessages,
                ErrorMessage = model.ErrorMessage,
                DownloadId = model.DownloadId,
                ConversionStatus = model.ConversionStatus,
                ConvertToQualityId = model.ConvertToQualityId,
                ConvertToQuality = model.ConvertToQuality,
                ConversionProgress = model.ConversionProgress,
                ConversionMessage = model.ConversionMessage,
                CanCancelConversion = model.CanCancelConversion,
                CanRetryImport = model.CanRetryImport,
                Protocol = model.Protocol,
                DownloadClient = model.DownloadClient,
                DownloadClientHasPostImportCategory = model.DownloadClientHasPostImportCategory,
                Indexer = model.Indexer,
                OutputPath = model.OutputPath,
                DownloadForced = model.DownloadForced
            };
        }

        private static TrackedDownloadState? ToBookshelfTrackedDownloadState(CoreTrackedDownloadState? state)
        {
            return state switch
            {
                CoreTrackedDownloadState.Downloading => TrackedDownloadState.Downloading,
                CoreTrackedDownloadState.DownloadFailed => TrackedDownloadState.DownloadFailed,
                CoreTrackedDownloadState.DownloadFailedPending => TrackedDownloadState.DownloadFailedPending,
                CoreTrackedDownloadState.ImportPending => TrackedDownloadState.ImportPending,
                CoreTrackedDownloadState.Importing => TrackedDownloadState.Importing,
                CoreTrackedDownloadState.ImportBlocked => TrackedDownloadState.ImportFailed,
                CoreTrackedDownloadState.Imported => TrackedDownloadState.Imported,
                CoreTrackedDownloadState.Ignored => TrackedDownloadState.Ignored,
                _ => null
            };
        }

        public static List<QueueResource> ToResource(this IEnumerable<NzbDrone.Core.Queue.Queue> models, bool includeAuthor, bool includeBook)
        {
            return models.Select((m) => ToResource(m, includeAuthor, includeBook)).ToList();
        }

        public static List<QueueResource> ToResource(this IEnumerable<NzbDrone.Core.Queue.Queue> models, bool includeAuthor, bool includeBook, ReadarrFacadeContext facadeContext)
        {
            return models.Select(m => ToResource(m, includeAuthor, includeBook, facadeContext)).ToList();
        }
    }
}
