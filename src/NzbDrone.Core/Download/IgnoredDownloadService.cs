using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download
{
    public interface IIgnoredDownloadService
    {
        bool IgnoreDownload(TrackedDownload trackedDownload);
    }

    public class IgnoredDownloadService : IIgnoredDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public IgnoredDownloadService(IEventAggregator eventAggregator,
                                      Logger logger)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public bool IgnoreDownload(TrackedDownload trackedDownload)
        {
            if (trackedDownload?.DownloadItem == null)
            {
                _logger.Warn("Unable to ignore download: tracked download or download item was null");
                return false;
            }

            var remoteBook = trackedDownload.RemoteBook;
            var author = remoteBook?.Author;
            var books = remoteBook?.Books;
            var quality = remoteBook?.ParsedBookInfo?.Quality ?? new QualityModel(Quality.Unknown);

            var authorId = author?.Id ?? 0;
            var bookIds = books?.Select(b => b?.Id ?? 0).Where(id => id > 0).Distinct().ToList() ?? new List<int>();

            var isUnknown = authorId == 0 || bookIds.Empty();

            if (isUnknown)
            {
                _logger.Info("Ignoring unknown download: {0}", trackedDownload.DownloadItem.Title);
            }

            var downloadIgnoredEvent = new DownloadIgnoredEvent
            {
                AuthorId = authorId,
                BookIds = bookIds,
                Quality = quality,
                SourceTitle = trackedDownload.DownloadItem.Title,
                DownloadClientInfo = trackedDownload.DownloadItem.DownloadClientInfo,
                DownloadId = trackedDownload.DownloadItem.DownloadId,
                TrackedDownload = trackedDownload,
                Message = isUnknown ? "Manually ignored (unknown download)" : "Manually ignored"
            };

            _eventAggregator.PublishEvent(downloadIgnoredEvent);
            return true;
        }
    }
}
