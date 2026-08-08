using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Ignored
{
    [V1ApiController("ignored")]
    public class IgnoredController : Controller
    {
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IManageCommandQueue _commandQueueManager;

        public IgnoredController(IDownloadHistoryService downloadHistoryService,
                                 ITrackedDownloadService trackedDownloadService,
                                 IManageCommandQueue commandQueueManager)
        {
            _downloadHistoryService = downloadHistoryService;
            _trackedDownloadService = trackedDownloadService;
            _commandQueueManager = commandQueueManager;
        }

        [HttpGet]
        [Produces("application/json")]
        public PagingResource<IgnoredDownloadResource> GetIgnored([FromQuery] PagingRequestResource paging)
        {
            var pagingResource = new PagingResource<IgnoredDownloadResource>(paging);
            var pagingSpec = pagingResource.MapToPagingSpec<IgnoredDownloadResource, DownloadHistory>("date", SortDirection.Descending);
            var trackableDownloadIds = GetTrackableDownloadIds();

            return pagingSpec.ApplyToPage(_downloadHistoryService.CurrentlyIgnored, model =>
            {
                return model.ToResource(ContainsDownloadId(trackableDownloadIds, model.DownloadId));
            });
        }

        [RestDeleteById]
        public void DeleteIgnored(int id)
        {
            RemoveIgnored(_downloadHistoryService.RemoveIgnored(id));
        }

        [HttpDelete("bulk")]
        public object Remove([FromBody] IgnoredDownloadBulkResource resource)
        {
            RemoveIgnored(_downloadHistoryService.RemoveIgnored(resource?.Ids ?? new List<int>()));

            return new { };
        }

        private HashSet<string> GetTrackableDownloadIds()
        {
            return _trackedDownloadService.GetTrackedDownloads()
                .Where(trackedDownload => trackedDownload?.IsTrackable == true)
                .Select(trackedDownload => trackedDownload.DownloadItem?.DownloadId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private void RemoveIgnored(List<string> downloadIds)
        {
            if (downloadIds == null || downloadIds.Count == 0)
            {
                return;
            }

            var idsToStopTracking = _trackedDownloadService.GetTrackedDownloads()
                .Select(trackedDownload => trackedDownload?.DownloadItem?.DownloadId)
                .Where(id => downloadIds.Any(downloadId => SameDownloadId(id, downloadId)))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (idsToStopTracking.Any())
            {
                _trackedDownloadService.StopTracking(idsToStopTracking);
            }

            _commandQueueManager.Push(new RefreshMonitoredDownloadsCommand(), CommandPriority.High, CommandTrigger.Manual);
        }

        private static bool SameDownloadId(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsDownloadId(HashSet<string> downloadIds, string downloadId)
        {
            return !string.IsNullOrWhiteSpace(downloadId) &&
                   downloadIds.Contains(downloadId.Trim());
        }
    }
}
