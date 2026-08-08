using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;

namespace Chaptarr.Api.V1.System
{
    [V1ApiController("system/proxy")]
    public class ProxyDebugController : Controller
    {
        private readonly IConfigService _configService;
        private readonly IGlobalProxySettingsResolver _globalProxySettingsResolver;

        public ProxyDebugController(IConfigService configService, IGlobalProxySettingsResolver globalProxySettingsResolver)
        {
            _configService = configService;
            _globalProxySettingsResolver = globalProxySettingsResolver;
        }

        [HttpGet("debug")]
        public object GetProxyDebugInfo()
        {
            NzbDrone.Common.Http.Proxy.HttpProxySettings proxySettings = null;
            string configurationError = null;
            try
            {
                proxySettings = _configService.ProxyMode == ProxyMode.Disabled
                    ? _globalProxySettingsResolver.Resolve()
                    : _globalProxySettingsResolver.ResolveRequired();
            }
            catch (NzbDrone.Common.Http.Proxy.ProxyConfigurationException ex)
            {
                configurationError = ex.Message;
            }

            return new
            {
                ProxyMode = _configService.ProxyMode.ToString(),
                ProxyModeValue = (int)_configService.ProxyMode,
                ProxyEnabled = _configService.ProxyMode != ProxyMode.Disabled,
                ProxyType = proxySettings?.Type.ToString(),
                ProxyHostname = proxySettings?.Host,
                ProxyPort = proxySettings?.Port,
                ProxyUsername = string.IsNullOrEmpty(proxySettings?.Username) ? "Not Set" : "Set",
                ProxyBypassFilter = _configService.ProxyBypassFilter,
                ProxyBypassLocalAddresses = _configService.ProxyBypassLocalAddresses,
                ConfigurationError = configurationError,
                Recommendation = GetRecommendation(proxySettings, configurationError)
            };
        }

        private string GetRecommendation(NzbDrone.Common.Http.Proxy.HttpProxySettings proxySettings, string configurationError)
        {
            if (!string.IsNullOrEmpty(configurationError))
            {
                return configurationError;
            }

            if (_configService.ProxyMode == ProxyMode.Disabled)
            {
                return "Proxy is disabled. For MyAnonaMouse, set ProxyMode to 'IndexerOnly' and configure your proxy settings to match your torrent client.";
            }
            else if (_configService.ProxyMode == ProxyMode.IndexerOnly)
            {
                if (!string.IsNullOrEmpty(configurationError) || proxySettings == null)
                {
                    return configurationError ?? "Proxy routing is enabled but no global proxy is configured.";
                }

                return "Proxy is configured for indexers. Make sure these settings match your torrent client's proxy.";
            }
            else
            {
                return "Proxy is set to ProxyEverything. This should work but IndexerOnly is recommended for better performance.";
            }
        }
    }
}
