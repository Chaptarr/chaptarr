using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.TPL;

namespace NzbDrone.Common.Http
{
    public class IndexerHttpClient : HttpClient, IIndexerHttpClient
    {
        public IndexerHttpClient(
            IEnumerable<IHttpRequestInterceptor> requestInterceptors,
            ICacheManager cacheManager,
            IRateLimitService rateLimitService,
            IIndexerProxySettingsProvider indexerProxySettingsProvider,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            Logger logger)
            : base(
                requestInterceptors,
                cacheManager,
                rateLimitService,
                CreateIndexerDispatcher(indexerProxySettingsProvider, createManagedWebProxy, certificateValidationService, userAgentBuilder, cacheManager),
                logger)
        {
        }

        private static IndexerHttpDispatcher CreateIndexerDispatcher(
            IIndexerProxySettingsProvider indexerProxySettingsProvider,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            ICacheManager cacheManager)
        {
            var socketsHttpHandlerFactory = new ManagedSocketsHttpHandlerFactory(
                createManagedWebProxy,
                certificateValidationService,
                LogManager.GetLogger("ManagedSocketsHttpHandlerFactory"));

            return new IndexerHttpDispatcher(
                indexerProxySettingsProvider,
                socketsHttpHandlerFactory,
                userAgentBuilder,
                cacheManager,
                LogManager.GetLogger("IndexerHttpDispatcher"));
        }
    }
}
