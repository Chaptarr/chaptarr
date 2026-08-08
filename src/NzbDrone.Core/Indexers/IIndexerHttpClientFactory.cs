using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers
{
    public interface IIndexerHttpClientFactory
    {
        IIndexerHttpClient GetClient(int? proxyId);
    }
}
