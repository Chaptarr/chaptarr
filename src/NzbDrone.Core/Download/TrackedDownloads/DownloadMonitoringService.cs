using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class DownloadMonitoringService : IExecute<RefreshMonitoredDownloadsCommand>,
                                             IExecute<CheckForFinishedDownloadCommand>,
                                             IHandle<BookGrabbedEvent>,
                                             IHandle<TrackImportedEvent>,
                                             IHandle<DownloadsProcessedEvent>,
                                             IHandle<TrackedDownloadsRemovedEvent>,
                                             IHandle<ModelEvent<RemotePathMapping>>
    {
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _manageCommandQueue;
        private readonly IConfigService _configService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly Logger _logger;
        private readonly Debouncer _refreshDebounce;
        private readonly Dictionary<int, DateTime> _failedPolls = new();

        public DownloadMonitoringService(IDownloadClientStatusService downloadClientStatusService,
                                         IDownloadClientFactory downloadClientFactory,
                                         IEventAggregator eventAggregator,
                                         IManageCommandQueue manageCommandQueue,
                                         IConfigService configService,
                                         IFailedDownloadService failedDownloadService,
                                         ICompletedDownloadService completedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         Logger logger)
        {
            _downloadClientStatusService = downloadClientStatusService;
            _downloadClientFactory = downloadClientFactory;
            _eventAggregator = eventAggregator;
            _manageCommandQueue = manageCommandQueue;
            _configService = configService;
            _failedDownloadService = failedDownloadService;
            _completedDownloadService = completedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _logger = logger;

            _refreshDebounce = new Debouncer(QueueRefresh, TimeSpan.FromSeconds(5));
        }

        private void QueueRefresh()
        {
            _manageCommandQueue.Push(new RefreshMonitoredDownloadsCommand(), CommandPriority.High);
        }

        private void Refresh(CommandTrigger trigger = CommandTrigger.Unspecified)
        {
            _refreshDebounce.Pause();
            try
            {
                var downloadClients = _downloadClientFactory.DownloadHandlingEnabled(false);
                var blockedClients = _downloadClientStatusService.GetBlockedProviders().Select(client => client.ProviderId).ToHashSet();
                var previous = _trackedDownloadService.GetTrackedDownloads().Where(item => item.IsTrackable).ToLookup(item => item.DownloadClient);

                var trackedDownloads = new List<TrackedDownload>();
                var reportedDownloadIds = new Dictionary<int, HashSet<string>>();

                foreach (var downloadClient in downloadClients)
                {
                    var retained = previous[downloadClient.Definition.Id].ToList();
                    var clientTrackedDownloads = blockedClients.Contains(downloadClient.Definition.Id)
                        ? retained
                        : ProcessClientDownloads(downloadClient, reportedDownloadIds, retained);

                    trackedDownloads.AddRange(clientTrackedDownloads.Where(DownloadIsTrackable));
                }

                _trackedDownloadService.UpdateTrackable(trackedDownloads);
                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads, reportedDownloadIds));

                var processCommand = new ProcessMonitoredDownloadsCommand
                {
                    Trigger = trigger
                };

                _manageCommandQueue.Push(processCommand, CommandPriority.High, trigger);
            }
            finally
            {
                _refreshDebounce.Resume();
            }
        }

        private List<TrackedDownload> ProcessClientDownloads(IDownloadClient downloadClient,
            Dictionary<int, HashSet<string>> reportedDownloadIds, List<TrackedDownload> retained)
        {
            var downloadClientItems = new List<DownloadClientItem>();
            var trackedDownloads = new List<TrackedDownload>();

            try
            {
                (downloadClient as IRecoverDownloadClientOperations)?.RecoverPendingOperations();
                downloadClientItems = downloadClient.GetItems().ToList();
                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
                if (_failedPolls.Remove(downloadClient.Definition.Id, out var failedSince))
                {
                    _logger.Info("Download client {0} ({1}) responded after {2:F1}s of failed polls; reconciling {3} items.",
                        downloadClient.Definition.Name, downloadClient.Definition.Id,
                        (DateTime.UtcNow - failedSince).TotalSeconds, downloadClientItems.Count);
                }
            }
            catch (Exception ex)
            {
                _downloadClientStatusService.RecordFailure(downloadClient.Definition.Id);
                _failedPolls.TryAdd(downloadClient.Definition.Id, DateTime.UtcNow);
                _logger.Warn(ex, "Unable to retrieve queue and history items from {0} ({1}); retaining {2} tracked downloads. Failed polls for {3:F1}s.",
                    downloadClient.Definition.Name, downloadClient.Definition.Id, retained.Count,
                    (DateTime.UtcNow - _failedPolls[downloadClient.Definition.Id]).TotalSeconds);
                return retained;
            }

            foreach (var downloadItem in downloadClientItems)
            {
                var item = ProcessClientItem(downloadClient, downloadItem);
                trackedDownloads.AddIfNotNull(item);
            }

            reportedDownloadIds[downloadClient.Definition.Id] = downloadClientItems
                .Select(item => item.DownloadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return trackedDownloads;
        }

        private TrackedDownload ProcessClientItem(IDownloadClient downloadClient, DownloadClientItem downloadItem)
        {
            try
            {
                var trackedDownload = _trackedDownloadService.TrackDownload((DownloadClientDefinition)downloadClient.Definition, downloadItem);

                if (trackedDownload != null && trackedDownload.State == TrackedDownloadState.Downloading)
                {
                    _failedDownloadService.Check(trackedDownload);
                    _completedDownloadService.Check(trackedDownload);
                }

                return trackedDownload;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't process tracked download {0}", downloadItem.Title);
            }

            return _trackedDownloadService.Find(downloadItem.DownloadId);
        }

        private bool DownloadIsTrackable(TrackedDownload trackedDownload)
        {
            // A deliberate cleanup refusal must stay visible, including failed downloads.
            if (trackedDownload.CleanupDeletionRefused && !trackedDownload.DownloadItem.Removed &&
                trackedDownload.State != TrackedDownloadState.Ignored)
            {
                return true;
            }

            // Keep failed cleanup checks visible while the imported download awaits a retry.
            if ((trackedDownload.State == TrackedDownloadState.Imported && !trackedDownload.CleanupCheckFailed) ||
                trackedDownload.State == TrackedDownloadState.DownloadFailed ||
                trackedDownload.State == TrackedDownloadState.Ignored)
            {
                return false;
            }

            // If CDH is disabled and the download status is complete don't track it
            if (!_configService.EnableCompletedDownloadHandling && trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed)
            {
                return false;
            }

            return true;
        }

        public void Execute(RefreshMonitoredDownloadsCommand message)
        {
            Refresh(message?.Trigger ?? CommandTrigger.Unspecified);
        }

        public void Execute(CheckForFinishedDownloadCommand message)
        {
            _logger.Warn("A third party app used the deprecated CheckForFinishedDownload command, it should be updated RefreshMonitoredDownloads instead");
            Refresh(message?.Trigger ?? CommandTrigger.Unspecified);
        }

        public void Handle(BookGrabbedEvent message)
        {
            _refreshDebounce.Execute();
        }

        public void Handle(TrackImportedEvent message)
        {
            _refreshDebounce.Execute();
        }

        public void Handle(DownloadsProcessedEvent message)
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads().Where(t => t.IsTrackable && DownloadIsTrackable(t)).ToList();

            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads));
        }

        public void Handle(TrackedDownloadsRemovedEvent message)
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads().Where(t => t.IsTrackable && DownloadIsTrackable(t)).ToList();

            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads));
        }

        public void Handle(ModelEvent<RemotePathMapping> message)
        {
            QueueRefresh();
        }
    }
}
