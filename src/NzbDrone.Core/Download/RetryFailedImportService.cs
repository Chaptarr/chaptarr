using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download
{
    public class RetryFailedImportService : IExecute<RetryFailedImportCommand>
    {
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IRemotePathMappingService _remotePathMappingService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IDownloadClientFileSnapshotService _downloadClientFileSnapshotService;
        private readonly IConversionTrackingService _conversionTrackingService;
        private readonly IConversionJobService _conversionJobService;
        private readonly ICommandResultReporter _commandResultReporter;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public RetryFailedImportService(ITrackedDownloadService trackedDownloadService,
                                        IProvideDownloadClient downloadClientProvider,
                                        IRemotePathMappingService remotePathMappingService,
                                        ICompletedDownloadService completedDownloadService,
                                        IDownloadClientFileSnapshotService downloadClientFileSnapshotService,
                                        IConversionTrackingService conversionTrackingService,
                                        ICommandResultReporter commandResultReporter,
                                        IEventAggregator eventAggregator,
                                        Logger logger,
                                        IConversionJobService conversionJobService = null)
        {
            _trackedDownloadService = trackedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _remotePathMappingService = remotePathMappingService;
            _completedDownloadService = completedDownloadService;
            _downloadClientFileSnapshotService = downloadClientFileSnapshotService;
            _conversionTrackingService = conversionTrackingService;
            _conversionJobService = conversionJobService;
            _commandResultReporter = commandResultReporter;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Execute(RetryFailedImportCommand message)
        {
            var downloadIds = GetDownloadIds(message);
            if (downloadIds.Empty())
            {
                throw new ArgumentException("At least one downloadId must be provided", nameof(message));
            }

            var failures = new List<string>();
            foreach (var downloadId in downloadIds)
            {
                try
                {
                    Retry(downloadId);
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                    _logger.Warn(ex, "Failed to retry import for download '{0}'", downloadId);
                }
            }

            if (failures.Any())
            {
                if (downloadIds.Count == 1)
                {
                    throw new InvalidOperationException(failures[0]);
                }

                _commandResultReporter.Report(CommandResult.Unsuccessful);
            }
        }

        private void Retry(string downloadId)
        {
            var trackedDownload = _trackedDownloadService.Find(downloadId);
            if (trackedDownload == null)
            {
                throw new InvalidOperationException($"Unable to retry import because download '{downloadId}' is not currently tracked.");
            }

            var previousImportItem = trackedDownload.ImportItem;
            var refreshedFromClient = TryRefreshFromDownloadClient(ref trackedDownload, out var downloadClient);
            if (!refreshedFromClient)
            {
                ReResolveCachedOutputPath(trackedDownload, downloadClient);
            }

            if (trackedDownload.DownloadItem?.Status != DownloadItemStatus.Completed)
            {
                throw new InvalidOperationException($"Unable to retry import for '{trackedDownload.DownloadItem?.Title ?? downloadId}' because the download is not completed.");
            }

            if (!CanRetryImport(trackedDownload))
            {
                throw new InvalidOperationException($"Unable to retry import for '{trackedDownload.DownloadItem.Title}' because it is not in a blocked import state.");
            }

            _logger.Debug("[IMPORT-RETRY] Retrying import for completed download: {0} ({1})",
                trackedDownload.DownloadItem.Title,
                trackedDownload.DownloadItem.DownloadId);

            _conversionJobService?.Reset(trackedDownload.DownloadItem.DownloadId);
            _conversionTrackingService?.Clear(trackedDownload.DownloadItem.DownloadId);
            _downloadClientFileSnapshotService.Delete(trackedDownload.DownloadItem);
            _downloadClientFileSnapshotService.Delete(previousImportItem);
            _downloadClientFileSnapshotService.Delete(trackedDownload.ImportItem);
            trackedDownload.EmptyImportAttempts = 0;
            trackedDownload.State = TrackedDownloadState.ImportPending;
            trackedDownload.ClearStatus();
            PublishQueueUpdate();

            _completedDownloadService.Import(trackedDownload);
            PublishQueueUpdate();

            if (trackedDownload.State != TrackedDownloadState.Imported &&
                !(_conversionJobService?.IsActive(trackedDownload.DownloadItem.DownloadId) ?? false))
            {
                _commandResultReporter.Report(CommandResult.Unsuccessful);
            }
        }

        private static bool CanRetryImport(TrackedDownload trackedDownload)
        {
            return trackedDownload.State == TrackedDownloadState.ImportBlocked;
        }

        private bool TryRefreshFromDownloadClient(ref TrackedDownload trackedDownload, out IDownloadClient downloadClient)
        {
            downloadClient = null;

            var downloadId = trackedDownload?.DownloadItem?.DownloadId;
            if (downloadId.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to load download client for retrying failed import '{0}'. Using cached download item.", downloadId);
                return false;
            }

            if (downloadClient == null)
            {
                return false;
            }

            DownloadClientItem currentItem;
            try
            {
                currentItem = downloadClient.GetItems()
                    .FirstOrDefault(item => string.Equals(item.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to refresh failed import '{0}' from download client. Using cached download item.", downloadId);
                return false;
            }

            if (currentItem == null)
            {
                _logger.Debug("Download '{0}' is no longer reported by the download client. Using cached download item.", downloadId);
                return false;
            }

            if (downloadClient.Definition is not DownloadClientDefinition definition)
            {
                _logger.Debug("Download client for '{0}' does not expose a download client definition. Using cached download item.", downloadId);
                return false;
            }

            var refreshed = _trackedDownloadService.TrackDownload(definition, currentItem);
            if (refreshed == null)
            {
                return false;
            }

            trackedDownload = refreshed;
            return true;
        }

        private void ReResolveCachedOutputPath(TrackedDownload trackedDownload, IDownloadClient downloadClient)
        {
            var item = trackedDownload?.DownloadItem;
            if (item == null)
            {
                return;
            }

            item.FilePaths = null;
            item.FileListConfidence = null;
            trackedDownload.ImportItem = null;

            if (item.OutputPath.IsEmpty)
            {
                return;
            }

            var downloadClientId = item.DownloadClientInfo != null && item.DownloadClientInfo.Id > 0
                ? item.DownloadClientInfo.Id
                : trackedDownload.DownloadClient;
            var host = GetDownloadClientHost(downloadClient);
            var remappedPath = _remotePathMappingService.RemapRemoteToLocal(downloadClientId, host, item.OutputPath);

            if (!remappedPath.IsEmpty)
            {
                item.OutputPath = remappedPath;
            }
        }

        private static string GetDownloadClientHost(IDownloadClient downloadClient)
        {
            var settings = downloadClient?.Definition?.Settings;
            return settings?.GetType().GetProperty("Host")?.GetValue(settings)?.ToString();
        }

        private static List<string> GetDownloadIds(RetryFailedImportCommand message)
        {
            if (message == null)
            {
                return new List<string>();
            }

            var downloadIds = new List<string>();
            if (message.DownloadId.IsNotNullOrWhiteSpace())
            {
                downloadIds.Add(message.DownloadId);
            }

            if (message.DownloadIds != null)
            {
                downloadIds.AddRange(message.DownloadIds.Where(id => id.IsNotNullOrWhiteSpace()));
            }

            return downloadIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void PublishQueueUpdate()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                .Where(t => t.IsTrackable)
                .ToList();

            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads));
        }
    }
}
