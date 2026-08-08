using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class GlobalProxySettingsResolverFixture
    {
        private class ConfigProxy : DispatchProxy
        {
            public int? GlobalProxyId { get; set; }
            public string ProxyHostname { get; set; } = string.Empty;
            public int ProxyPort { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_GlobalProxyId" => GlobalProxyId,
                    "get_ProxyHostname" => ProxyHostname,
                    "get_ProxyPort" => ProxyPort,
                    "get_ProxyType" => ProxyType.Http,
                    "get_ProxyUsername" => string.Empty,
                    "get_ProxyPassword" => string.Empty,
                    "get_ProxyBypassFilter" => string.Empty,
                    "get_ProxyBypassLocalAddresses" => false,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        private class ProxyServiceProxy : DispatchProxy
        {
            public ProxyDefinition Proxy { get; set; }
            public List<ProxyDefinition> Proxies { get; } = new List<ProxyDefinition>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "Find" => Proxy?.Id == (int)args[0] ? Proxy : null,
                    "All" => Proxies,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        [Test]
        public void required_resolution_should_fail_when_no_proxy_is_configured()
        {
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            var proxies = DispatchProxy.Create<IProxyService, ProxyServiceProxy>();
            var resolver = new GlobalProxySettingsResolver(config, proxies, LogManager.GetCurrentClassLogger());

            Assert.Throws<ProxyConfigurationException>(() => resolver.ResolveRequired());
        }

        [Test]
        public void selected_missing_proxy_should_not_fall_back()
        {
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            ((ConfigProxy)(object)config).GlobalProxyId = 99;
            var proxies = DispatchProxy.Create<IProxyService, ProxyServiceProxy>();
            var resolver = new GlobalProxySettingsResolver(config, proxies, LogManager.GetCurrentClassLogger());

            Assert.Throws<ProxyConfigurationException>(() => resolver.Resolve());
        }

        [Test]
        public void legacy_settings_should_not_be_used_when_proxy_rows_exist_without_a_selection()
        {
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            ((ConfigProxy)(object)config).ProxyHostname = "legacy.example";
            ((ConfigProxy)(object)config).ProxyPort = 8080;
            var proxies = DispatchProxy.Create<IProxyService, ProxyServiceProxy>();
            ((ProxyServiceProxy)(object)proxies).Proxies.Add(new ProxyDefinition
            {
                Id = 2,
                Name = "Current Proxy",
                Hostname = "current.example",
                Port = 8081
            });
            var resolver = new GlobalProxySettingsResolver(config, proxies, LogManager.GetCurrentClassLogger());

            Assert.Throws<ProxyConfigurationException>(() => resolver.ResolveRequired());
        }
    }
}
