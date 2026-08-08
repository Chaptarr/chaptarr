using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Queue
{
    [V1ApiController]
    public class QueueController : RestControllerWithSignalR<QueueResource, NzbDrone.Core.Queue.Queue>,
                               IHandle<QueueUpdatedEvent>, IHandle<PendingReleasesUpdatedEvent>
    {
        private readonly IQueueService _queueService;
        private readonly IPendingReleaseService _pendingReleaseService;

        private readonly QualityModelComparer _qualityComparer;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IIgnoredDownloadService _ignoredDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IBlocklistService _blocklistService;
        private readonly IConversionTrackingService _conversionTrackingService;
        private readonly IConversionJobService _conversionJobService;

        public QueueController(IBroadcastSignalRMessage broadcastSignalRMessage,
                           IQueueService queueService,
                           IPendingReleaseService pendingReleaseService,
                           QualityProfileService qualityProfileService,
                           ITrackedDownloadService trackedDownloadService,
                           IFailedDownloadService failedDownloadService,
                           IIgnoredDownloadService ignoredDownloadService,
                           IProvideDownloadClient downloadClientProvider,
                           IBlocklistService blocklistService,
                           IConversionTrackingService conversionTrackingService,
                           IConversionJobService conversionJobService = null)
            : base(broadcastSignalRMessage)
        {
            _queueService = queueService;
            _pendingReleaseService = pendingReleaseService;
            _trackedDownloadService = trackedDownloadService;
            _failedDownloadService = failedDownloadService;
            _ignoredDownloadService = ignoredDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _blocklistService = blocklistService;
            _conversionTrackingService = conversionTrackingService;
            _conversionJobService = conversionJobService;

            // Get the first available quality profile instead of creating an empty one
            var profiles = qualityProfileService.All();
            if (profiles.Any())
            {
                _qualityComparer = new QualityModelComparer(profiles.First());
            }
            else
            {
                // Create a basic profile with audiobook qualities if none exist
                var audiobookProfile = qualityProfileService.GetDefaultProfile("Default",
                    Quality.MP3,
                    Quality.MP3, Quality.M4B, Quality.FLAC, Quality.UnknownAudio);
                _qualityComparer = new QualityModelComparer(audiobookProfile);
            }
        }

        [NonAction]
        public override ActionResult<QueueResource> GetResourceByIdWithErrorHandler(int id)
        {
            return base.GetResourceByIdWithErrorHandler(id);
        }

        protected override QueueResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        [RestDeleteById]
        public void RemoveAction(int id, bool removeFromClient = true, bool blocklist = false, bool skipRedownload = false, bool changeCategory = false)
        {
            var pendingRelease = _pendingReleaseService.FindPendingQueueItem(id);

            if (pendingRelease != null)
            {
                Remove(pendingRelease);

                return;
            }

            var trackedDownload = GetTrackedDownload(id);

            if (trackedDownload == null)
            {
                throw new NotFoundException();
            }

            // Ignore can fail (eg unknown download) and we should not stop tracking unless the action succeeded,
            // otherwise the item will reappear on the next refresh cycle.
            if (Remove(trackedDownload, removeFromClient, blocklist, skipRedownload, changeCategory) == null)
            {
                throw new BadRequestException();
            }
            _trackedDownloadService.StopTracking(trackedDownload.DownloadItem.DownloadId);
        }

        [HttpDelete("bulk")]
        public object RemoveMany([FromBody] QueueBulkResource resource, [FromQuery] bool removeFromClient = true, [FromQuery] bool blocklist = false, [FromQuery] bool skipRedownload = false, [FromQuery] bool changeCategory = false)
        {
            var trackedDownloadIds = new List<string>();
            var pendingToRemove = new List<NzbDrone.Core.Queue.Queue>();
            var trackedToRemove = new List<TrackedDownload>();

            foreach (var id in resource.Ids)
            {
                var pendingRelease = _pendingReleaseService.FindPendingQueueItem(id);

                if (pendingRelease != null)
                {
                    pendingToRemove.Add(pendingRelease);
                    continue;
                }

                var trackedDownload = GetTrackedDownload(id);

                if (trackedDownload != null)
                {
                    trackedToRemove.Add(trackedDownload);
                }
            }

            foreach (var pendingRelease in pendingToRemove.DistinctBy(p => p.Id))
            {
                Remove(pendingRelease);
            }

            foreach (var trackedDownload in trackedToRemove.DistinctBy(t => t.DownloadItem.DownloadId))
            {
                // Ignore can fail (eg unknown download) and we should not stop tracking unless the action succeeded,
                // otherwise the item will reappear on the next refresh cycle.
                if (Remove(trackedDownload, removeFromClient, blocklist, skipRedownload, changeCategory) != null)
                {
                    trackedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
                }
            }

            _trackedDownloadService.StopTracking(trackedDownloadIds);

            return new { };
        }

        [HttpPost("cancelconversion/{id:int}")]
        public object CancelConversion(int id)
        {
            var queueItem = _queueService.Find(id);
            if (queueItem == null)
            {
                throw new NotFoundException();
            }

            var cancelled = !queueItem.DownloadId.IsNullOrWhiteSpace() &&
                            (_conversionJobService?.Cancel(queueItem.DownloadId) ?? _conversionTrackingService.Cancel(queueItem.DownloadId));
            if (!cancelled)
            {
                throw new BadRequestException("No active conversion found for this queue item");
            }

            return new { };
        }

        [HttpGet]
        [Produces("application/json")]
        public PagingResource<QueueResource> GetQueue([FromQuery] PagingRequestResource paging, bool includeUnknownAuthorItems = false, bool includeAuthor = false, bool includeBook = false, [FromQuery] string mediaType = null)
        {
            var pagingResource = new PagingResource<QueueResource>(paging);
            var pagingSpec = pagingResource.MapToPagingSpec<QueueResource, NzbDrone.Core.Queue.Queue>("timeleft", SortDirection.Ascending);

            return pagingSpec.ApplyToPage((spec) => GetQueue(spec, includeUnknownAuthorItems, mediaType), (q) => MapToResource(q, includeAuthor, includeBook));
        }

        private PagingSpec<NzbDrone.Core.Queue.Queue> GetQueue(PagingSpec<NzbDrone.Core.Queue.Queue> pagingSpec, bool includeUnknownAuthorItems, string mediaType)
        {
            var ascending = pagingSpec.SortDirection == SortDirection.Ascending;
            var orderByFunc = GetOrderByFunc(pagingSpec);

            var queue = _queueService.GetQueue();
            var scopedQueue = QueueMediaTypeFilter.FilterByMediaType(queue, mediaType);
            var filteredQueue = includeUnknownAuthorItems ? scopedQueue : scopedQueue.Where(q => q.Author != null);
            var pending = QueueMediaTypeFilter.FilterByMediaType(_pendingReleaseService.GetPendingQueue(), mediaType);
            var fullQueue = filteredQueue.Concat(pending).ToList();
            IOrderedEnumerable<NzbDrone.Core.Queue.Queue> ordered;

            if (pagingSpec.SortKey == "timeleft")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Timeleft, new TimeleftComparer())
                    : fullQueue.OrderByDescending(q => q.Timeleft, new TimeleftComparer());
            }
            else if (pagingSpec.SortKey == "estimatedCompletionTime")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.EstimatedCompletionTime, new EstimatedCompletionTimeComparer())
                    : fullQueue.OrderByDescending(q => q.EstimatedCompletionTime,
                        new EstimatedCompletionTimeComparer());
            }
            else if (pagingSpec.SortKey == "protocol")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Protocol)
                    : fullQueue.OrderByDescending(q => q.Protocol);
            }
            else if (pagingSpec.SortKey == "indexer")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Indexer, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue.OrderByDescending(q => q.Indexer, StringComparer.InvariantCultureIgnoreCase);
            }
            else if (pagingSpec.SortKey == "downloadClient")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.DownloadClient, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue.OrderByDescending(q => q.DownloadClient, StringComparer.InvariantCultureIgnoreCase);
            }
            else if (pagingSpec.SortKey == "quality")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Quality, _qualityComparer)
                    : fullQueue.OrderByDescending(q => q.Quality, _qualityComparer);
            }
            else if (pagingSpec.SortKey == "convertToQuality")
            {
                ordered = ascending
                    ? fullQueue
                        .OrderBy(q => q.ConvertToQualityId.HasValue ? 0 : 1)
                        .ThenBy(q => q.ConvertToQuality, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue
                        .OrderBy(q => q.ConvertToQualityId.HasValue ? 0 : 1)
                        .ThenByDescending(q => q.ConvertToQuality, StringComparer.InvariantCultureIgnoreCase);
            }
            else
            {
                ordered = ascending ? fullQueue.OrderBy(orderByFunc) : fullQueue.OrderByDescending(orderByFunc);
            }

            ordered = ordered.ThenByDescending(q => q.Size == 0 ? 0 : 100 - (q.Sizeleft / q.Size * 100));

            pagingSpec.Records = ordered.Skip((pagingSpec.Page - 1) * pagingSpec.PageSize).Take(pagingSpec.PageSize).ToList();
            pagingSpec.TotalRecords = fullQueue.Count;

            if (pagingSpec.Records.Empty() && pagingSpec.Page > 1)
            {
                pagingSpec.Page = (int)Math.Max(Math.Ceiling((decimal)(pagingSpec.TotalRecords / pagingSpec.PageSize)), 1);
                pagingSpec.Records = ordered.Skip((pagingSpec.Page - 1) * pagingSpec.PageSize).Take(pagingSpec.PageSize).ToList();
            }

            return pagingSpec;
        }

        private Func<NzbDrone.Core.Queue.Queue, object> GetOrderByFunc(PagingSpec<NzbDrone.Core.Queue.Queue> pagingSpec)
        {
            switch (pagingSpec.SortKey)
            {
                case "status":
                    return q => q.Status;
                case "authors.sortName":
                    return q => q.Author?.SortName ?? q.Title;
                case "authors.sortNameLastFirst":
                    return q => q.Author?.SortNameLastFirst ?? string.Empty;
                case "title":
                    return q => q.Title;
                case "book":
                    return q => q.Book;
                case "book.title":
                    return q => q.Book?.Title ?? string.Empty;
                case "book.releaseDate":
                    return q => q.Book?.ReleaseDate ?? DateTime.MinValue;
                case "added":
                    return q => q.Added ?? DateTime.MinValue;
                case "quality":
                    return q => q.Quality;
                case "convertToQuality":
                    return q => q.ConvertToQuality ?? string.Empty;
                case "size":
                    return q => q.Size;
                case "progress":
                    // Avoid exploding if a download's size is 0
                    return q => 100 - (q.Sizeleft / Math.Max(q.Size * 100, 1));
                default:
                    return q => q.Timeleft;
            }
        }

        private void Remove(NzbDrone.Core.Queue.Queue pendingRelease)
        {
            _blocklistService.Block(pendingRelease.RemoteBook, "Pending release manually blocklisted");
            _pendingReleaseService.RemovePendingQueueItems(pendingRelease.Id);
        }

        private TrackedDownload Remove(TrackedDownload trackedDownload, bool removeFromClient, bool blocklist, bool skipRedownload, bool changeCategory)
        {
            if (removeFromClient)
            {
                var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

                if (downloadClient == null)
                {
                    throw new BadRequestException();
                }

                downloadClient.RemoveItem(trackedDownload.DownloadItem, true);
            }
            else if (changeCategory)
            {
                var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

                if (downloadClient == null)
                {
                    throw new BadRequestException();
                }

                var downloadItem = trackedDownload.DownloadItem.Clone();

                if (trackedDownload.RemoteBook != null)
                {
                    downloadItem.MediaType = trackedDownload.RemoteBook.GetPreferredMediaType();
                }

                downloadClient.MarkItemAsImported(downloadItem);
            }

            if (blocklist)
            {
                _failedDownloadService.MarkAsFailed(trackedDownload.DownloadItem.DownloadId, skipRedownload);
            }

            if (!removeFromClient && !blocklist && !changeCategory)
            {
                if (!_ignoredDownloadService.IgnoreDownload(trackedDownload))
                {
                    return null;
                }
            }

            return trackedDownload;
        }

        private TrackedDownload GetTrackedDownload(int queueId)
        {
            var queueItem = _queueService.Find(queueId);

            if (queueItem == null)
            {
                throw new NotFoundException();
            }

            var trackedDownload = _trackedDownloadService.Find(queueItem.DownloadId);

            if (trackedDownload == null)
            {
                throw new NotFoundException();
            }

            return trackedDownload;
        }

        private QueueResource MapToResource(NzbDrone.Core.Queue.Queue queueItem, bool includeAuthor, bool includeBook)
        {
            return queueItem.ToResource(includeAuthor, includeBook, HttpContext.GetReadarrFacadeContext());
        }

        [NonAction]
        public void Handle(QueueUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }

        [NonAction]
        public void Handle(PendingReleasesUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }
    }
}
