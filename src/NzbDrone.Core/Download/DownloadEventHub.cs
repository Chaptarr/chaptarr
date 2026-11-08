using System;
using NLog;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadEventHub : IHandle<DownloadFailedEvent>,
                                    IHandle<DownloadCompletedEvent>,
                                    IHandle<DownloadCanBeRemovedEvent>
    {
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadImportModeResolver _downloadImportModeResolver;
        private readonly Logger _logger;

        public DownloadEventHub(ICompletedDownloadService completedDownloadService,
            IProvideDownloadClient downloadClientProvider,
            IDownloadImportModeResolver downloadImportModeResolver,
            Logger logger)
        {
            _completedDownloadService = completedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _downloadImportModeResolver = downloadImportModeResolver;
            _logger = logger;
        }

        public void Handle(DownloadFailedEvent message)
        {
            var trackedDownload = message.TrackedDownload;

            if (message.SkipDownloadClientRemoval ||
                trackedDownload == null ||
                message.TrackedDownload.DownloadItem.Removed ||
                !trackedDownload.DownloadItem.CanBeRemoved)
            {
                return;
            }

            var downloadClient = _downloadClientProvider.Get(message.TrackedDownload.DownloadClient);
            var definition = downloadClient.Definition as DownloadClientDefinition;

            if (!definition.RemoveFailedDownloads)
            {
                return;
            }

            if (ShouldPreserveDownloadClientItem(trackedDownload))
            {
                return;
            }

            RemoveFromDownloadClient(trackedDownload, downloadClient);
        }

        public void Handle(DownloadCompletedEvent message)
        {
            var trackedDownload = message.TrackedDownload;
            var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);
            var importItem = BuildImportItem(trackedDownload);

            // Cleanup runs after completion handlers finish, so its warning remains the latest durable state.
            MarkItemAsImported(trackedDownload, downloadClient, importItem);
        }

        public void Handle(DownloadCanBeRemovedEvent message)
        {
            var trackedDownload = message.TrackedDownload;
            var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);
            var definition = downloadClient.Definition as DownloadClientDefinition;
            var importItem = BuildImportItem(trackedDownload);

            if (trackedDownload.DownloadItem.Removed ||
                !trackedDownload.DownloadItem.CanBeRemoved ||
                trackedDownload.DownloadItem.Status == DownloadItemStatus.Downloading ||
                !definition.RemoveCompletedDownloads)
            {
                return;
            }

            if (ShouldPreserveDownloadClientItem(trackedDownload, downloadClient, importItem))
            {
                return;
            }

            if (_completedDownloadService.CanRemoveDownload(trackedDownload, message.Cleanup))
            {
                RemoveFromDownloadClient(trackedDownload, downloadClient);
            }
        }

        private bool ShouldPreserveDownloadClientItem(TrackedDownload trackedDownload, IDownloadClient downloadClient = null, DownloadClientItem importItem = null)
        {
            if (_downloadImportModeResolver.ShouldPreserveDownloadClientItem(trackedDownload.DownloadItem,
                trackedDownload.RemoteBook?.Release?.IndexerId ?? 0))
            {
                _logger.Info("[{0}] Preserving download-client item and data due to download preservation policy",
                    trackedDownload.DownloadItem.Title);
                return true;
            }

            if (downloadClient is IPreserveDownloadClientItemAfterImport postImportPreserver &&
                importItem != null &&
                postImportPreserver.ShouldPreserveItemAfterImport(importItem))
            {
                _logger.Info("[{0}] Preserving download-client item and data because a post-import category is configured",
                    trackedDownload.DownloadItem.Title);
                return true;
            }

            return false;
        }

        private void RemoveFromDownloadClient(TrackedDownload trackedDownload, IDownloadClient downloadClient)
        {
            try
            {
                _logger.Debug("[{0}] Removing download from {1} history", trackedDownload.DownloadItem.Title, trackedDownload.DownloadItem.DownloadClientInfo.Name);
                downloadClient.RemoveItem(trackedDownload.DownloadItem, true);
                trackedDownload.DownloadItem.Removed = true;
                if (trackedDownload.CleanupDeletionRefused)
                {
                    trackedDownload.CleanupDeletionRefused = false;
                    trackedDownload.ClearStatus();
                }
            }
            catch (DownloadDataDeletionRefusedException ex)
            {
                if (!trackedDownload.CleanupDeletionRefused)
                {
                    _logger.Warn(ex.Message);
                }
                trackedDownload.CleanupDeletionRefused = true;
                trackedDownload.IsTrackable = true;
                trackedDownload.Warn("{0} Correct the download path or remove the item without deleting data.", ex.Message);
            }
            catch (NotSupportedException)
            {
                _logger.Warn("Removing item not supported by your download client ({0}).", downloadClient.Definition.Name);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't remove item {0} from client {1}", trackedDownload.DownloadItem.Title, downloadClient.Name);
            }
        }

        private DownloadClientItem BuildImportItem(TrackedDownload trackedDownload)
        {
            var downloadItem = trackedDownload.DownloadItem.Clone();

            if (trackedDownload.RemoteBook != null)
            {
                downloadItem.MediaType = trackedDownload.RemoteBook.GetPreferredMediaType();
            }

            return downloadItem;
        }

        private void MarkItemAsImported(TrackedDownload trackedDownload, IDownloadClient downloadClient, DownloadClientItem downloadItem)
        {
            try
            {
                _logger.Debug("[{0}] Marking download as imported from {1}", trackedDownload.DownloadItem.Title, trackedDownload.DownloadItem.DownloadClientInfo.Name);

                downloadClient.MarkItemAsImported(downloadItem);
            }
            catch (NotSupportedException e)
            {
                _logger.Debug(e.Message);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't mark item {0} as imported from client {1}", trackedDownload.DownloadItem.Title, downloadClient.Name);
            }
        }
    }
}
