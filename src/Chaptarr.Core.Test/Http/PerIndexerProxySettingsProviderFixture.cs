using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.Indexers;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class PerIndexerProxySettingsProviderFixture
    {
        private class ConfigServiceProxy : DispatchProxy
        {
            public ProxyMode ProxyMode { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_ProxyMode" => ProxyMode,
                    "get_ProxyBypassFilter" => string.Empty,
                    "get_ProxyBypassLocalAddresses" => true,
                    "get_GlobalProxyId" => null,
                    "get_ProxyHostname" => string.Empty,
                    "get_ProxyPort" => 0,
                    "get_ProxyType" => ProxyType.Http,
                    "get_ProxyUsername" => string.Empty,
                    "get_ProxyPassword" => string.Empty,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }

            public static IConfigService Create(ProxyMode proxyMode)
            {
                var proxy = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
                ((ConfigServiceProxy)(object)proxy).ProxyMode = proxyMode;
                return proxy;
            }
        }

        private class ProxyServiceProxy : DispatchProxy
        {
            public ProxyDefinition Proxy { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "Find" => Proxy?.Id == (int)args[0] ? Proxy : null,
                    "All" => new List<ProxyDefinition>(),
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }

            public static IProxyService Create(ProxyDefinition proxy)
            {
                var service = DispatchProxy.Create<IProxyService, ProxyServiceProxy>();
                ((ProxyServiceProxy)(object)service).Proxy = proxy;
                return service;
            }
        }

        [Test]
        public void should_honor_explicit_indexer_proxy_when_global_proxy_mode_is_disabled()
        {
            var configService = ConfigServiceProxy.Create(ProxyMode.Disabled);
            var proxyService = ProxyServiceProxy.Create(new ProxyDefinition
            {
                Id = 12,
                Name = "Indexer Proxy",
                ProxyType = ProxyType.Http,
                Hostname = "proxy.example.com",
                Port = 8080
            });
            var provider = new PerIndexerProxySettingsProvider(
                configService,
                proxyService,
                new GlobalProxySettingsResolver(configService, proxyService, LogManager.GetCurrentClassLogger()),
                12);

            var settings = provider.GetProxySettings();

            Assert.That(settings, Is.Not.Null);
            Assert.That(settings.Host, Is.EqualTo("proxy.example.com"));
        }

        [Test]
        public void should_still_allow_explicit_no_proxy_override()
        {
            var configService = ConfigServiceProxy.Create(ProxyMode.ProxyEverything);
            var proxyService = ProxyServiceProxy.Create(null);
            var provider = new PerIndexerProxySettingsProvider(
                configService,
                proxyService,
                new GlobalProxySettingsResolver(configService, proxyService, LogManager.GetCurrentClassLogger()),
                IndexerDefinition.NoProxyOverride);

            Assert.That(provider.GetProxySettings().IsDirectConnection, Is.True);
        }

        [Test]
        public void should_fail_closed_when_explicit_indexer_proxy_is_missing()
        {
            var configService = ConfigServiceProxy.Create(ProxyMode.ProxyEverything);
            var proxyService = ProxyServiceProxy.Create(null);
            var provider = new PerIndexerProxySettingsProvider(
                configService,
                proxyService,
                new GlobalProxySettingsResolver(configService, proxyService, LogManager.GetCurrentClassLogger()),
                42);

            Assert.Throws<ProxyConfigurationException>(() => provider.GetProxySettings());
        }
    }
}
