using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;

namespace NzbDrone.Core.Download
{
    public interface IDownloadImportModeResolver
    {
        ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem);
        DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem);
        bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem, int fallbackIndexerId = 0, bool includeUnmanaged = true);
    }

    public class DownloadImportPolicy
    {
        public DownloadImportPolicy(ImportMode importMode, bool preserveDownloadClientItem, string preserveReason = null)
        {
            ImportMode = importMode;
            PreserveDownloadClientItem = preserveDownloadClientItem;
            PreserveReason = preserveReason;
        }

        public ImportMode ImportMode { get; }
        public bool PreserveDownloadClientItem { get; }
        public string PreserveReason { get; }
    }

    public class DownloadImportModeResolver : IDownloadImportModeResolver
    {
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly Logger _logger;

        public DownloadImportModeResolver(IDownloadHistoryService downloadHistoryService,
                                          IIndexerFactory indexerFactory,
                                          IDownloadClientFactory downloadClientFactory,
                                          Logger logger)
        {
            _downloadHistoryService = downloadHistoryService;
            _indexerFactory = indexerFactory;
            _downloadClientFactory = downloadClientFactory;
            _logger = logger;
        }

        public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem)
        {
            return ResolvePolicy(requestedMode, downloadClientItem).ImportMode;
        }

        public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem)
        {
            if (requestedMode != ImportMode.Auto || downloadClientItem == null)
            {
                var explicitPreserveReason = GetPreserveReason(downloadClientItem);
                return new DownloadImportPolicy(explicitPreserveReason == null ? requestedMode : ImportMode.Copy, explicitPreserveReason != null, explicitPreserveReason);
            }

            var preserveReason = GetPreserveReason(downloadClientItem);
            if (preserveReason != null)
            {
                _logger.Debug("[IMPORT-MODE] Forcing copy/hardlink for download '{0}' ({1}) because {2}",
                    downloadClientItem.Title ?? "<unknown>",
                    downloadClientItem.DownloadId ?? "<unknown>",
                    preserveReason);

                return new DownloadImportPolicy(ImportMode.Copy, true, preserveReason);
            }

            // Match current download-import behavior: resolve Auto via CanMoveFiles.
            var effectiveMode = downloadClientItem.CanMoveFiles ? ImportMode.Move : ImportMode.Copy;

            return new DownloadImportPolicy(effectiveMode, false);
        }

        public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem, int fallbackIndexerId = 0, bool includeUnmanaged = true)
        {
            return GetPreserveReason(downloadClientItem, fallbackIndexerId, includeUnmanaged) != null;
        }

        private string GetPreserveReason(DownloadClientItem downloadClientItem, int fallbackIndexerId = 0, bool includeUnmanaged = true)
        {
            if (downloadClientItem == null ||
                downloadClientItem.DownloadClientInfo?.Protocol != DownloadProtocol.Torrent ||
                downloadClientItem.DownloadId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var downloadId = downloadClientItem.DownloadId;
            var downloadClientId = downloadClientItem.DownloadClientInfo?.Id ?? 0;
            var grab = _downloadHistoryService.GetLatestGrab(downloadId);
            if (grab?.ShouldKeepSeeding() == true)
            {
                return "Preserve torrents was enabled when this torrent was grabbed";
            }

            var indexerId = grab?.IndexerId > 0 ? grab.IndexerId : fallbackIndexerId;

            // Tier 1: per-indexer protection for downloads Chaptarr grabbed.
            if (indexerId > 0 && ShouldNeverMoveForIndexer(indexerId))
            {
                return $"Preserve torrents is enabled on the indexer (IndexerId={indexerId})";
            }

            // Tier 2: per-download-client protection for unmanaged downloads (no grab history).
            if (includeUnmanaged && grab == null && downloadClientId > 0 && ShouldCopyUnmanagedForClient(downloadClientId))
            {
                return $"the download client is configured to preserve unmanaged downloads (DownloadClientId={downloadClientId})";
            }

            return null;
        }

        private bool ShouldNeverMoveForIndexer(int indexerId)
        {
            var indexer = _indexerFactory.Find(indexerId);
            var torrentSettings = indexer?.Settings as ITorrentIndexerSettings;

            return torrentSettings?.SeedCriteria?.NeverMoveOnImport == true;
        }

        private bool ShouldCopyUnmanagedForClient(int downloadClientId)
        {
            var downloadClient = _downloadClientFactory.Find(downloadClientId);

            return downloadClient?.CopyUnmanagedDownloads == true;
        }
    }
}
