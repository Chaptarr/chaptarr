using NzbDrone.Common.Cache;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Common.Http.Dispatchers
{
    /// <summary>
    /// HTTP dispatcher for indexer requests that always uses proxy when configured.
    /// </summary>
    public class IndexerHttpDispatcher : ManagedHttpDispatcher
    {
        public IndexerHttpDispatcher(
            IIndexerProxySettingsProvider indexerProxySettingsProvider,
            ICreateManagedSocketsHttpHandler socketsHttpHandlerFactory,
            IUserAgentBuilder userAgentBuilder,
            ICacheManager cacheManager,
            NLog.Logger logger)
            : base(indexerProxySettingsProvider, socketsHttpHandlerFactory, userAgentBuilder, cacheManager, logger)
        {
            // no-op
        }
    }
}
