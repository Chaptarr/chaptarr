using NLog;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Http
{
    public interface IGlobalProxySettingsResolver
    {
        HttpProxySettings Resolve();
        HttpProxySettings ResolveRequired();
    }

    public class GlobalProxySettingsResolver : IGlobalProxySettingsResolver
    {
        private readonly IConfigService _configService;
        private readonly IProxyService _proxyService;
        private readonly Logger _logger;

        public GlobalProxySettingsResolver(IConfigService configService, IProxyService proxyService, Logger logger)
        {
            _configService = configService;
            _proxyService = proxyService;
            _logger = logger;
        }

        public HttpProxySettings Resolve()
        {
            var globalProxyId = _configService.GlobalProxyId;
            if (globalProxyId.HasValue)
            {
                var proxy = _proxyService.Find(globalProxyId.Value);
                if (proxy == null)
                {
                    throw new ProxyConfigurationException($"The selected global proxy (ID {globalProxyId.Value}) no longer exists.");
                }

                _logger.Debug("Using global proxy '{0}' ({1}:{2})", proxy.Name, proxy.Hostname, proxy.Port);
                return ToSettings(proxy);
            }

            if (_proxyService.All().Count > 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(_configService.ProxyHostname) && _configService.ProxyPort > 0)
            {
                _logger.Debug("Using legacy global proxy {0}:{1}", _configService.ProxyHostname, _configService.ProxyPort);
                return new HttpProxySettings(
                    _configService.ProxyType,
                    _configService.ProxyHostname,
                    _configService.ProxyPort,
                    _configService.ProxyBypassFilter,
                    _configService.ProxyBypassLocalAddresses,
                    _configService.ProxyUsername,
                    _configService.ProxyPassword);
            }

            return null;
        }

        public HttpProxySettings ResolveRequired()
        {
            return Resolve() ?? throw new ProxyConfigurationException("Proxy routing is enabled, but no global proxy is configured.");
        }

        private HttpProxySettings ToSettings(ProxyDefinition proxy)
        {
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
}
