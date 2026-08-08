namespace NzbDrone.Common.Http.Proxy
{
    /// <summary>
    /// Proxy settings provider for indexer requests.
    /// Always returns proxy settings when ProxyMode is IndexerOnly or ProxyEverything.
    /// </summary>
    public interface IIndexerProxySettingsProvider : IHttpProxySettingsProvider
    {
    }
}
