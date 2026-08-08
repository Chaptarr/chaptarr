using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Configuration
{
    public class ProxyReferenceCleanupService : IHandle<ProxyDeletedEvent>
    {
        private readonly IConfigService _configService;
        private readonly IProxyService _proxyService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        public ProxyReferenceCleanupService(IConfigService configService,
                                            IProxyService proxyService,
                                            IIndexerFactory indexerFactory,
                                            Logger logger)
        {
            _configService = configService;
            _proxyService = proxyService;
            _indexerFactory = indexerFactory;
            _logger = logger;
        }

        public void Handle(ProxyDeletedEvent message)
        {
            var deletedProxyId = message.ModelId;

            ClearGlobalProxyReference(deletedProxyId);
            ClearIndexerProxyReferences(deletedProxyId);
            DisableProxyModeWhenNoProxiesRemain();
        }

        private void ClearGlobalProxyReference(int deletedProxyId)
        {
            if (_configService.GlobalProxyId != deletedProxyId)
            {
                return;
            }

            _logger.Info("Clearing global proxy reference to deleted proxy ID {0}", deletedProxyId);
            _configService.GlobalProxyId = null;
        }

        private void ClearIndexerProxyReferences(int deletedProxyId)
        {
            var indexers = _indexerFactory.All()
                                         .Where(indexer => indexer.ProxyId == deletedProxyId)
                                         .ToList();

            if (!indexers.Any())
            {
                return;
            }

            foreach (var indexer in indexers)
            {
                indexer.ProxyId = null;
            }

            _logger.Info("Clearing deleted proxy ID {0} from {1} indexer(s)", deletedProxyId, indexers.Count);
            _indexerFactory.Update(indexers);
        }

        private void DisableProxyModeWhenNoProxiesRemain()
        {
            if (_configService.ProxyMode == ProxyMode.Disabled)
            {
                return;
            }

            if (_proxyService.All().Any())
            {
                if (!_configService.GlobalProxyId.HasValue)
                {
                    _logger.Error("Proxy routing remains enabled without a selected global proxy; outbound requests requiring the proxy will be blocked");
                }

                return;
            }

            _logger.Info("Disabling proxy routing because no proxies remain");
            _configService.GlobalProxyId = null;
            _configService.SaveConfigDictionary(new Dictionary<string, object>
            {
                { nameof(IConfigService.ProxyMode), ProxyMode.Disabled }
            });
        }
    }
}
