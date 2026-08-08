namespace NzbDrone.Common.Http
{
    /// <summary>
    /// HTTP client specifically for indexer requests.
    /// This client respects proxy settings when ProxyMode is IndexerOnly or ProxyEverything.
    /// </summary>
    public interface IIndexerHttpClient : IHttpClient
    {
    }
}
