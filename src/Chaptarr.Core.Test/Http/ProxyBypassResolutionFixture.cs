using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class ProxyBypassResolutionFixture
    {
        private class ConfigServiceProxy : DispatchProxy
        {
            public ProxyMode ProxyMode { get; set; } = ProxyMode.ProxyEverything;
            public bool BypassLocalAddresses { get; set; } = true;
            public string BypassFilter { get; set; } = string.Empty;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_ProxyMode" => ProxyMode,
                    "get_ProxyBypassLocalAddresses" => BypassLocalAddresses,
                    "get_ProxyBypassFilter" => BypassFilter,
                    "get_GlobalProxyId" => null,
                    "get_ProxyHostname" => string.Empty,
                    "get_ProxyPort" => 0,
                    "get_ProxyType" => ProxyType.Http,
                    "get_ProxyUsername" => string.Empty,
                    "get_ProxyPassword" => string.Empty,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            public static IConfigService Create()
            {
                return DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            }
        }

        private class EmptyProxyServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "Find" => null,
                    "All" => new List<ProxyDefinition>(),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        [Test]
        public void standard_provider_should_bypass_missing_proxy_for_local_destination()
        {
            var config = ConfigServiceProxy.Create();
            var proxyService = DispatchProxy.Create<IProxyService, EmptyProxyServiceProxy>();
            var resolver = new GlobalProxySettingsResolver(config, proxyService, LogManager.GetCurrentClassLogger());
            var provider = new StandardProxySettingsProvider(config, resolver);

            var settings = provider.GetProxySettings(new HttpUri("http://192.168.86.100:5000/api"));

            Assert.That(settings.IsDirectConnection, Is.True);
        }

        [Test]
        public void standard_provider_should_fail_closed_for_non_bypassed_destination()
        {
            var config = ConfigServiceProxy.Create();
            var proxyService = DispatchProxy.Create<IProxyService, EmptyProxyServiceProxy>();
            var resolver = new GlobalProxySettingsResolver(config, proxyService, LogManager.GetCurrentClassLogger());
            var provider = new StandardProxySettingsProvider(config, resolver);

            Assert.Throws<ProxyConfigurationException>(() =>
                provider.GetProxySettings(new HttpUri("https://api.example.com/resource")));
        }

        [Test]
        public void per_indexer_provider_should_bypass_missing_explicit_proxy_for_local_destination()
        {
            var config = ConfigServiceProxy.Create();
            var proxyService = DispatchProxy.Create<IProxyService, EmptyProxyServiceProxy>();
            var resolver = new GlobalProxySettingsResolver(config, proxyService, LogManager.GetCurrentClassLogger());
            var provider = new PerIndexerProxySettingsProvider(config, proxyService, resolver, 42);

            var settings = provider.GetProxySettings(new HttpUri("http://10.0.0.10:9696/api"));

            Assert.That(settings.IsDirectConnection, Is.True);
        }

        [Test]
        public void configured_bypass_filter_should_not_depend_on_proxy_row_resolution()
        {
            var config = ConfigServiceProxy.Create();
            ((ConfigServiceProxy)(object)config).BypassLocalAddresses = false;
            ((ConfigServiceProxy)(object)config).BypassFilter = "*.internal.example";
            var proxyService = DispatchProxy.Create<IProxyService, EmptyProxyServiceProxy>();
            var resolver = new GlobalProxySettingsResolver(config, proxyService, LogManager.GetCurrentClassLogger());
            var provider = new StandardProxySettingsProvider(config, resolver);

            var settings = provider.GetProxySettings(new HttpUri("https://metadata.internal.example/api"));

            Assert.That(settings.IsDirectConnection, Is.True);
        }
    }
}
