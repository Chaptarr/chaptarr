using System.Collections.Concurrent;
using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers
{
    public class IndexerHttpClientFactory : IIndexerHttpClientFactory,
                                            IHandle<ProxyUpdatedEvent>,
                                            IHandle<ProxyDeletedEvent>
    {
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;
        private readonly ICacheManager _cacheManager;
        private readonly IRateLimitService _rateLimitService;
        private readonly ICreateManagedWebProxy _createManagedWebProxy;
        private readonly ICertificateValidationService _certificateValidationService;
        private readonly IUserAgentBuilder _userAgentBuilder;
        private readonly IProxyService _proxyService;
        private readonly IConfigService _configService;
        private readonly IGlobalProxySettingsResolver _globalProxySettingsResolver;
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<string, IIndexerHttpClient> _httpClients = new ConcurrentDictionary<string, IIndexerHttpClient>();

        public IndexerHttpClientFactory(
            IEnumerable<IHttpRequestInterceptor> requestInterceptors,
            ICacheManager cacheManager,
            IRateLimitService rateLimitService,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            IProxyService proxyService,
            IConfigService configService,
            IGlobalProxySettingsResolver globalProxySettingsResolver,
            Logger logger)
        {
            _requestInterceptors = requestInterceptors;
            _cacheManager = cacheManager;
            _rateLimitService = rateLimitService;
            _createManagedWebProxy = createManagedWebProxy;
            _certificateValidationService = certificateValidationService;
            _userAgentBuilder = userAgentBuilder;
            _proxyService = proxyService;
            _configService = configService;
            _globalProxySettingsResolver = globalProxySettingsResolver;
            _logger = logger;
        }

        public IIndexerHttpClient GetClient(int? proxyId)
        {
            // Use "none" as key for null proxy IDs to avoid null key issues
            var cacheKey = proxyId?.ToString() ?? "none";
            return _httpClients.GetOrAdd(cacheKey, _ => CreateHttpClient(proxyId));
        }

        private IIndexerHttpClient CreateHttpClient(int? proxyId)
        {
            _logger.Debug("Creating new IndexerHttpClient for proxy ID: {0}", proxyId?.ToString() ?? "None");

            var proxySettingsProvider = new PerIndexerProxySettingsProvider(_configService, _proxyService, _globalProxySettingsResolver, proxyId);

            return new IndexerHttpClient(
                _requestInterceptors,
                _cacheManager,
                _rateLimitService,
                proxySettingsProvider,
                _createManagedWebProxy,
                _certificateValidationService,
                _userAgentBuilder,
                LogManager.GetLogger($"IndexerHttpClient-{proxyId?.ToString() ?? "None"}"));
        }

        public void Handle(ProxyUpdatedEvent message)
        {
            ClearCachedClients($"proxy ID {message.ModelId} was updated");
        }

        public void Handle(ProxyDeletedEvent message)
        {
            ClearCachedClients($"proxy ID {message.ModelId} was deleted");
        }

        private void ClearCachedClients(string reason)
        {
            if (_httpClients.IsEmpty)
            {
                return;
            }

            _logger.Debug("Clearing cached indexer HTTP clients because {0}", reason);
            _httpClients.Clear();
        }
    }
}
