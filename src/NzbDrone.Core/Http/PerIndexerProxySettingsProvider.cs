using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.Http
{
    /// <summary>
    /// Proxy settings provider for a specific indexer's proxy configuration
    /// This is created on-demand by IndexerHttpClientFactory
    /// </summary>
    internal class PerIndexerProxySettingsProvider : IIndexerProxySettingsProvider
    {
        private readonly IConfigService _configService;
        private readonly IProxyService _proxyService;
        private readonly IGlobalProxySettingsResolver _globalProxySettingsResolver;
        private readonly int? _proxyId;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public PerIndexerProxySettingsProvider(IConfigService configService,
                                               IProxyService proxyService,
                                               IGlobalProxySettingsResolver globalProxySettingsResolver,
                                               int? proxyId)
        {
            _configService = configService;
            _proxyService = proxyService;
            _globalProxySettingsResolver = globalProxySettingsResolver;
            _proxyId = proxyId;
        }

        public HttpProxySettings GetProxySettings(HttpUri uri)
        {
            if (_proxyId == IndexerDefinition.NoProxyOverride)
            {
                return HttpProxySettings.DirectConnection;
            }

            if (ProxyBypassEvaluator.ShouldBypass(
                    (System.Uri)uri,
                    _configService.ProxyBypassLocalAddresses,
                    ProxyBypassEvaluator.ParseBypassList(_configService.ProxyBypassFilter)))
            {
                return HttpProxySettings.DirectConnection;
            }

            return GetProxySettings();
        }

        public HttpProxySettings GetProxySettings()
        {
            var proxyMode = _configService.ProxyMode;

            if (_proxyId == IndexerDefinition.NoProxyOverride)
            {
                _logger.Debug("Indexer is configured to bypass proxies");
                return HttpProxySettings.DirectConnection;
            }

            // Prefer per-indexer proxy if explicitly set
            if (_proxyId.HasValue)
            {
                var proxy = _proxyService.Find(_proxyId.Value);
                if (proxy == null)
                {
                    throw new ProxyConfigurationException($"The proxy selected for this indexer (ID {_proxyId.Value}) no longer exists.");
                }
                else
                {
                    _logger.Debug("Using proxy '{0}' ({1}:{2}) for this indexer", proxy.Name, proxy.Hostname, proxy.Port);
                    return new HttpProxySettings(
                        proxy.ProxyType,
                        proxy.Hostname,
                        proxy.Port,
                        _configService.ProxyBypassFilter,
                        _configService.ProxyBypassLocalAddresses,
                        proxy.Username,
                        proxy.Password);
                }
            }

            // Fallback to global proxy when ProxyMode is IndexerOnly or ProxyEverything.
            // Explicit per-indexer proxy assignments above are honored even when global proxy mode is Disabled.
            // This preserves legacy behavior where indexers used the global proxy if none was set per-indexer
            if (proxyMode == ProxyMode.IndexerOnly || proxyMode == ProxyMode.ProxyEverything)
            {
                return _globalProxySettingsResolver.ResolveRequired();
            }

            _logger.Debug("No proxy configured for this indexer and no usable global proxy in current mode ({0})", proxyMode);
            return null;
        }
    }
}
