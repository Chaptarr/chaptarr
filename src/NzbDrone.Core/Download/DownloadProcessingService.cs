using System;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IManageCommandQueue _commandQueueManager;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         Logger logger,
                                         IManageCommandQueue commandQueueManager = null)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _commandQueueManager = commandQueueManager;
        }

        // A full sweep can run for an hour on a large backlog and holds the single "default" disk slot the
        // whole time, so queued ManualImport/RetryUnmappedMatch commands (which need that slot) cannot start.
        // The sweep is periodic and idempotent, so stop between downloads when one of them is waiting and
        // resume on the next scheduled run.
        private bool DiskCommandIsWaitingForSameSlot(ProcessMonitoredDownloadsCommand message)
        {
            if (_commandQueueManager == null)
            {
                return false;
            }

            try
            {
                return _commandQueueManager.All().ToList().Any(c =>
                    c.Status == CommandStatus.Queued &&
                    c.Body.RequiresDiskAccess &&
                    c.Body.DiskAccessGroup == message.DiskAccessGroup &&
                    c.Name != message.Name);
            }
            catch (InvalidOperationException)
            {
                // The queue list was modified while copying it; treat as nothing waiting and re-check next download.
                return false;
            }
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        // CommandExecutor looks for this overload by reflection. Without it a cancelled sweep is only
        // relabelled Cancelled while its worker thread keeps importing for as long as the sweep takes,
        // so the command disappears from the started list yet still occupies one of the few workers.
        public void Execute(ProcessMonitoredDownloadsCommand message, CancellationToken cancellationToken)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => t.IsTrackable)
                                                          .ToList();

            var worked = 0;

            foreach (var trackedDownload in trackedDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var needsFailedProcessing = trackedDownload.State == TrackedDownloadState.DownloadFailedPending;
                var needsImport = enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending;

                if (!needsFailedProcessing && !needsImport)
                {
                    continue;
                }

                // Only downloads that need real work count. Each run does at least one before yielding, so a
                // steady stream of waiting commands cannot starve the sweep, and no-op entries at the front
                // of the list cannot use up that guarantee.
                if (worked > 0 && DiskCommandIsWaitingForSameSlot(message))
                {
                    _logger.Debug("ProcessMonitoredDownloads yielding the disk slot to a waiting command after {0} downloads", worked);
                    break;
                }

                worked++;

                try
                {
                    if (needsFailedProcessing)
                    {
                        _failedDownloadService.ProcessFailed(trackedDownload);
                    }
                    else
                    {
                        _completedDownloadService.Import(trackedDownload);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }

    }
}
