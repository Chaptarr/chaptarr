using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface IDownloadService
    {
        Task DownloadReport(RemoteBook remoteBook, int? downloadClientId);
        Task AdoptExistingTorrent(RemoteBook remoteBook, int? downloadClientId);
    }

    public class DownloadService : IDownloadService
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly IRateLimitService _rateLimitService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ISeedConfigProvider _seedConfigProvider;
        private readonly IMamUnsatisfiedSlotGuard _mamUnsatisfiedSlotGuard;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DownloadService(IProvideDownloadClient downloadClientProvider,
                               IDownloadClientStatusService downloadClientStatusService,
                               IIndexerFactory indexerFactory,
                               IIndexerStatusService indexerStatusService,
                               IRateLimitService rateLimitService,
                               IEventAggregator eventAggregator,
                               ISeedConfigProvider seedConfigProvider,
                               IMamUnsatisfiedSlotGuard mamUnsatisfiedSlotGuard,
                               IConfigService configService,
                               Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _downloadClientStatusService = downloadClientStatusService;
            _indexerFactory = indexerFactory;
            _indexerStatusService = indexerStatusService;
            _rateLimitService = rateLimitService;
            _eventAggregator = eventAggregator;
            _seedConfigProvider = seedConfigProvider;
            _mamUnsatisfiedSlotGuard = mamUnsatisfiedSlotGuard;
            _configService = configService;
            _logger = logger;
        }

        public async Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
        {
            var filterBlockedClients = remoteBook.Release.PendingReleaseReason == PendingReleaseReason.DownloadClientUnavailable;

            var mediaType = remoteBook.GetPreferredMediaType();
            var tags = GetDownloadClientTags(remoteBook, mediaType);

            var downloadClient = downloadClientId.HasValue
                ? _downloadClientProvider.Get(downloadClientId.Value)
                : _downloadClientProvider.GetDownloadClient(remoteBook.Release.DownloadProtocol, mediaType, remoteBook.Release.IndexerId, filterBlockedClients, tags);

            await DownloadReport(remoteBook, downloadClient, false);
        }

        public async Task AdoptExistingTorrent(RemoteBook remoteBook, int? downloadClientId)
        {
            var filterBlockedClients = remoteBook.Release.PendingReleaseReason == PendingReleaseReason.DownloadClientUnavailable;

            var mediaType = remoteBook.GetPreferredMediaType();
            var tags = GetDownloadClientTags(remoteBook, mediaType);

            var downloadClient = downloadClientId.HasValue
                ? _downloadClientProvider.Get(downloadClientId.Value)
                : _downloadClientProvider.GetDownloadClient(remoteBook.Release.DownloadProtocol, mediaType, remoteBook.Release.IndexerId, filterBlockedClients, tags);

            await DownloadReport(remoteBook, downloadClient, true);
        }

        private HashSet<int> GetDownloadClientTags(RemoteBook remoteBook, BookMediaType mediaType)
        {
            // BookshelfNG used one author tag set to select the download client for
            // every release. Keep that behavior for imported profiles while native
            // ChaptarrNG authors continue to use per-format tags.
            return _configService?.BookshelfMatchCompatibility == true
                ? remoteBook.Author?.Tags
                : remoteBook.Author?.GetTagsForMediaType(mediaType);
        }

        private async Task DownloadReport(RemoteBook remoteBook, IDownloadClient downloadClient, bool adoptExistingTorrent)
        {
            Ensure.That(remoteBook.Author, () => remoteBook.Author).IsNotNull();
            Ensure.That(remoteBook.Books, () => remoteBook.Books).HasItems();

            var downloadTitle = remoteBook.Release.Title;

            if (downloadClient == null)
            {
                var requestedProtocol = remoteBook.Release.DownloadProtocol.ToString().ToLowerInvariant();
                throw new DownloadClientUnavailableException($"No enabled {requestedProtocol} download client is configured. Add and enable a {requestedProtocol}-capable download client, then retry.");
            }

            var existingTorrent = FindExistingTorrent(remoteBook, downloadClient, adoptExistingTorrent);

            if (existingTorrent != null && !adoptExistingTorrent)
            {
                throw new ExistingTorrentFoundException(downloadClient.Definition.Name, existingTorrent);
            }

            if (existingTorrent == null && adoptExistingTorrent)
            {
                throw new ExistingTorrentNotFoundException("The matching torrent is no longer available in the selected download client. Search again before grabbing it.");
            }

            remoteBook.SeedConfiguration = _seedConfigProvider.GetSeedConfiguration(remoteBook);

            // Limit grabs to 2 per second.
            if (existingTorrent == null && remoteBook.Release.DownloadUrl.IsNotNullOrWhiteSpace() && !remoteBook.Release.DownloadUrl.StartsWith("magnet:"))
            {
                var url = new HttpUri(remoteBook.Release.DownloadUrl);
                await _rateLimitService.WaitAndPulseAsync(url.Host, TimeSpan.FromSeconds(2));
            }

            IIndexer indexer = null;

            if (!adoptExistingTorrent && remoteBook.Release.IndexerId > 0)
            {
                indexer = _indexerFactory.GetInstance(_indexerFactory.Get(remoteBook.Release.IndexerId));
            }

            var mamAvailability = adoptExistingTorrent ? null : _mamUnsatisfiedSlotGuard.TryReserve(remoteBook);
            if (mamAvailability != null && !mamAvailability.Accepted)
            {
                throw new MamUnsatisfiedSlotsUnavailableException(mamAvailability.Reason);
            }

            string downloadClientId;
            try
            {
                if (existingTorrent != null)
                {
                    downloadClientId = existingTorrent.DownloadId;
                    _logger.Info("Adopting existing torrent {0} from {1} for {2}", existingTorrent.DownloadId, downloadClient.Definition.Name, remoteBook);
                }
                else
                {
                    downloadClientId = await downloadClient.Download(remoteBook, indexer);
                }

                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
                if (existingTorrent == null)
                {
                    _indexerStatusService.RecordSuccess(remoteBook.Release.IndexerId);
                }
            }
            catch (ReleaseUnavailableException)
            {
                _logger.Trace("Release {0} no longer available on indexer.", remoteBook);
                throw;
            }
            catch (ReleaseBlockedException)
            {
                _logger.Trace("Release {0} previously added to blocklist, not sending to download client again.", remoteBook);
                throw;
            }
            catch (DownloadClientRejectedReleaseException)
            {
                _logger.Trace("Release {0} rejected by download client, possible duplicate.", remoteBook);
                throw;
            }
            catch (ReleaseDownloadException ex)
            {
                if (ex.InnerException is TooManyRequestsException http429)
                {
                    _indexerStatusService.RecordFailure(remoteBook.Release.IndexerId, http429.RetryAfter);
                }
                else
                {
                    _indexerStatusService.RecordFailure(remoteBook.Release.IndexerId);
                }

                throw;
            }

            var bookGrabbedEvent = new BookGrabbedEvent(remoteBook);
            bookGrabbedEvent.DownloadClient = downloadClient.Name;
            bookGrabbedEvent.DownloadClientId = downloadClient.Definition.Id;
            bookGrabbedEvent.DownloadClientName = downloadClient.Definition.Name;

            if (downloadClientId.IsNotNullOrWhiteSpace())
            {
                bookGrabbedEvent.DownloadId = downloadClientId;
            }

            if (existingTorrent != null)
            {
                _logger.ProgressInfo("Adopted existing torrent from {0}. {1}", downloadClient.Definition.Name, downloadTitle);
            }
            else
            {
                _logger.ProgressInfo("Report sent to {0} from indexer {1}. {2}", downloadClient.Definition.Name, remoteBook.Release.Indexer, downloadTitle);
            }

            _eventAggregator.PublishEvent(bookGrabbedEvent);
        }

        private DownloadClientItem FindExistingTorrent(RemoteBook remoteBook, IDownloadClient downloadClient, bool required)
        {
            if (downloadClient.Protocol != DownloadProtocol.Torrent ||
                remoteBook.Release is not TorrentInfo torrentInfo ||
                torrentInfo.InfoHash.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (required)
            {
                return FindTorrent(downloadClient, torrentInfo.InfoHash);
            }

            try
            {
                return FindTorrent(downloadClient, torrentInfo.InfoHash);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to check {0} for an existing torrent before grabbing {1}", downloadClient.Definition.Name, remoteBook);
                return null;
            }
        }

        private static DownloadClientItem FindTorrent(IDownloadClient downloadClient, string infoHash)
        {
            return downloadClient.GetItems()?.FirstOrDefault(item =>
                item != null && string.Equals(item.DownloadId, infoHash, StringComparison.OrdinalIgnoreCase));
        }
    }
}
