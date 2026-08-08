using System;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.TPL;
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
        private readonly Logger _logger;

        public DownloadService(IProvideDownloadClient downloadClientProvider,
                               IDownloadClientStatusService downloadClientStatusService,
                               IIndexerFactory indexerFactory,
                               IIndexerStatusService indexerStatusService,
                               IRateLimitService rateLimitService,
                               IEventAggregator eventAggregator,
                               ISeedConfigProvider seedConfigProvider,
                               IMamUnsatisfiedSlotGuard mamUnsatisfiedSlotGuard,
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
            _logger = logger;
        }

        public async Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
        {
            var filterBlockedClients = remoteBook.Release.PendingReleaseReason == PendingReleaseReason.DownloadClientUnavailable;

            var mediaType = remoteBook.GetPreferredMediaType();
            var tags = remoteBook.Author?.GetTagsForMediaType(mediaType);

            var downloadClient = downloadClientId.HasValue
                ? _downloadClientProvider.Get(downloadClientId.Value)
                : _downloadClientProvider.GetDownloadClient(remoteBook.Release.DownloadProtocol, mediaType, remoteBook.Release.IndexerId, filterBlockedClients, tags);

            await DownloadReport(remoteBook, downloadClient);
        }

        private async Task DownloadReport(RemoteBook remoteBook, IDownloadClient downloadClient)
        {
            Ensure.That(remoteBook.Author, () => remoteBook.Author).IsNotNull();
            Ensure.That(remoteBook.Books, () => remoteBook.Books).HasItems();

            var downloadTitle = remoteBook.Release.Title;

            if (downloadClient == null)
            {
                var requestedProtocol = remoteBook.Release.DownloadProtocol.ToString().ToLowerInvariant();
                throw new DownloadClientUnavailableException($"No enabled {requestedProtocol} download client is configured. Add and enable a {requestedProtocol}-capable download client, then retry.");
            }

            remoteBook.SeedConfiguration = _seedConfigProvider.GetSeedConfiguration(remoteBook);

            // Limit grabs to 2 per second.
            if (remoteBook.Release.DownloadUrl.IsNotNullOrWhiteSpace() && !remoteBook.Release.DownloadUrl.StartsWith("magnet:"))
            {
                var url = new HttpUri(remoteBook.Release.DownloadUrl);
                await _rateLimitService.WaitAndPulseAsync(url.Host, TimeSpan.FromSeconds(2));
            }

            IIndexer indexer = null;

            if (remoteBook.Release.IndexerId > 0)
            {
                indexer = _indexerFactory.GetInstance(_indexerFactory.Get(remoteBook.Release.IndexerId));
            }

            var mamAvailability = _mamUnsatisfiedSlotGuard.TryReserve(remoteBook);
            if (!mamAvailability.Accepted)
            {
                throw new MamUnsatisfiedSlotsUnavailableException(mamAvailability.Reason);
            }

            string downloadClientId;
            try
            {
                downloadClientId = await downloadClient.Download(remoteBook, indexer);
                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
                _indexerStatusService.RecordSuccess(remoteBook.Release.IndexerId);
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

            _logger.ProgressInfo("Report sent to {0} from indexer {1}. {2}", downloadClient.Definition.Name, remoteBook.Release.Indexer, downloadTitle);
            _eventAggregator.PublishEvent(bookGrabbedEvent);
        }
    }
}
