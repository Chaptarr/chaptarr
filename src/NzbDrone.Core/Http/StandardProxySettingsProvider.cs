using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Http
{
    /// <summary>
    /// Standard proxy settings provider that uses the global proxy configuration
    /// This is used for non-indexer HTTP requests (updates, metadata, etc.)
    /// </summary>
    public class StandardProxySettingsProvider : IHttpProxySettingsProvider
    {
        private readonly IConfigService _configService;
        private readonly IGlobalProxySettingsResolver _globalProxySettingsResolver;

        public StandardProxySettingsProvider(IConfigService configService, IGlobalProxySettingsResolver globalProxySettingsResolver)
        {
            _configService = configService;
            _globalProxySettingsResolver = globalProxySettingsResolver;
        }

        public HttpProxySettings GetProxySettings(HttpUri uri)
        {
            var proxyMode = _configService.ProxyMode;

            if (proxyMode == ProxyMode.Disabled || proxyMode == ProxyMode.IndexerOnly)
            {
                return null;
            }

            if (ProxyBypassEvaluator.ShouldBypass(
                    (System.Uri)uri,
                    _configService.ProxyBypassLocalAddresses,
                    ProxyBypassEvaluator.ParseBypassList(_configService.ProxyBypassFilter)))
            {
                return HttpProxySettings.DirectConnection;
            }

            return _globalProxySettingsResolver.ResolveRequired();
        }

        public HttpProxySettings GetProxySettings()
        {
            var proxyMode = _configService.ProxyMode;

            // If proxy mode is disabled, no proxy
            if (proxyMode == ProxyMode.Disabled)
            {
                return null;
            }

            // If proxy mode is indexer only, non-indexer requests don't use proxy
            if (proxyMode == ProxyMode.IndexerOnly)
            {
                return null;
            }

            return _globalProxySettingsResolver.ResolveRequired();
        }
    }
}
