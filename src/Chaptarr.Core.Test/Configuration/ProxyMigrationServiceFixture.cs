using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class ProxyMigrationServiceFixture
    {
        private class ConfigServiceProxy : DispatchProxy
        {
            public static ProxyMode ProxyModeValue { get; set; } = ProxyMode.ProxyEverything;
            public static int? GlobalProxyIdValue { get; set; }
            public static ProxyType ProxyTypeValue { get; set; } = ProxyType.Http;
            public static string ProxyHostnameValue { get; set; } = string.Empty;
            public static int ProxyPortValue { get; set; }
            public static string ProxyUsernameValue { get; set; } = string.Empty;
            public static string ProxyPasswordValue { get; set; } = string.Empty;
            public static string ProxyBypassFilterValue { get; set; } = string.Empty;
            public static bool ProxyBypassLocalAddressesValue { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case "get_ProxyMode":
                        return ProxyModeValue;
                    case "get_GlobalProxyId":
                        return GlobalProxyIdValue;
                    case "set_GlobalProxyId":
                        GlobalProxyIdValue = (int?)args[0];
                        return null;
                    case "get_ProxyType":
                        return ProxyTypeValue;
                    case "get_ProxyHostname":
                        return ProxyHostnameValue;
                    case "get_ProxyPort":
                        return ProxyPortValue;
                    case "get_ProxyUsername":
                        return ProxyUsernameValue;
                    case "get_ProxyPassword":
                        return ProxyPasswordValue;
                    case "get_ProxyBypassFilter":
                        return ProxyBypassFilterValue;
                    case "get_ProxyBypassLocalAddresses":
                        return ProxyBypassLocalAddressesValue;
                    case "SaveConfigDictionary":
                        var values = (Dictionary<string, object>)args[0];
                        if (values.TryGetValue(nameof(IConfigService.ProxyBypassFilter), out var filterValue))
                        {
                            ProxyBypassFilterValue = filterValue?.ToString() ?? string.Empty;
                        }

                        if (values.TryGetValue(nameof(IConfigService.ProxyBypassLocalAddresses), out var bypassLocalValue))
                        {
                            ProxyBypassLocalAddressesValue = Convert.ToBoolean(bypassLocalValue);
                        }

                        if (values.TryGetValue(nameof(IConfigService.ProxyHostname), out var hostnameValue))
                        {
                            ProxyHostnameValue = hostnameValue?.ToString() ?? string.Empty;
                        }

                        if (values.TryGetValue(nameof(IConfigService.ProxyPort), out var portValue))
                        {
                            ProxyPortValue = Convert.ToInt32(portValue);
                        }

                        if (values.TryGetValue(nameof(IConfigService.ProxyUsername), out var usernameValue))
                        {
                            ProxyUsernameValue = usernameValue?.ToString() ?? string.Empty;
                        }

                        if (values.TryGetValue(nameof(IConfigService.ProxyPassword), out var passwordValue))
                        {
                            ProxyPasswordValue = passwordValue?.ToString() ?? string.Empty;
                        }

                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
                }
            }
        }

        private sealed class FakeProxyService : IProxyService
        {
            private readonly Dictionary<int, ProxyDefinition> _proxies;

            public FakeProxyService(params ProxyDefinition[] proxies)
            {
                _proxies = proxies.ToDictionary((proxy) => proxy.Id);
            }

            public List<ProxyDefinition> All()
            {
                return _proxies.Values.OrderBy((proxy) => proxy.Id).ToList();
            }

            public ProxyDefinition Find(int id)
            {
                return _proxies.TryGetValue(id, out var proxy) ? proxy : null;
            }

            public ProxyDefinition Get(int id)
            {
                return _proxies[id];
            }

            public ProxyDefinition Add(ProxyDefinition proxy)
            {
                throw new NotImplementedException();
            }

            public ProxyDefinition Update(ProxyDefinition proxy)
            {
                _proxies[proxy.Id] = proxy;
                return proxy;
            }

            public void Delete(int id)
            {
                throw new NotImplementedException();
            }
        }

        [Test]
        public void should_restore_global_bypass_settings_from_existing_proxies()
        {
            ConfigServiceProxy.ProxyModeValue = ProxyMode.ProxyEverything;
            ConfigServiceProxy.GlobalProxyIdValue = null;
            ConfigServiceProxy.ProxyTypeValue = ProxyType.Http;
            ConfigServiceProxy.ProxyHostnameValue = string.Empty;
            ConfigServiceProxy.ProxyPortValue = 0;
            ConfigServiceProxy.ProxyUsernameValue = string.Empty;
            ConfigServiceProxy.ProxyPasswordValue = string.Empty;
            ConfigServiceProxy.ProxyBypassLocalAddressesValue = false;
            ConfigServiceProxy.ProxyBypassFilterValue = "existing.local";

            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();

            var proxyService = new FakeProxyService(
                new ProxyDefinition
                {
                    Id = 1,
                    Name = "One",
                    ProxyType = ProxyType.Http,
                    Hostname = "one.invalid",
                    Port = 8080,
                    BypassLocalAddresses = false,
                    BypassFilter = "a.local"
                },
                new ProxyDefinition
                {
                    Id = 2,
                    Name = "Two",
                    ProxyType = ProxyType.Socks5,
                    Hostname = "two.invalid",
                    Port = 1080,
                    BypassLocalAddresses = true,
                    BypassFilter = "b.local,a.local"
                });

            var subject = new ProxyMigrationService(configService, proxyService, LogManager.GetCurrentClassLogger());

            subject.MigrateProxySettings();

            Assert.That(ConfigServiceProxy.ProxyBypassLocalAddressesValue, Is.True);
            Assert.That(ConfigServiceProxy.ProxyBypassFilterValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Is.EquivalentTo(new[] { "existing.local", "a.local", "b.local" }));

            foreach (var proxy in proxyService.All())
            {
                Assert.That(proxy.BypassLocalAddresses, Is.True);
                Assert.That(proxy.BypassFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Is.EquivalentTo(new[] { "existing.local", "a.local", "b.local" }));
            }
        }

        [Test]
        public void should_clear_legacy_connection_fields_after_the_selected_proxy_is_mapped()
        {
            ConfigServiceProxy.ProxyModeValue = ProxyMode.ProxyEverything;
            ConfigServiceProxy.GlobalProxyIdValue = 1;
            ConfigServiceProxy.ProxyHostnameValue = "legacy.example";
            ConfigServiceProxy.ProxyPortValue = 8080;
            ConfigServiceProxy.ProxyUsernameValue = "user";
            ConfigServiceProxy.ProxyPasswordValue = "secret";
            ConfigServiceProxy.ProxyBypassLocalAddressesValue = false;
            ConfigServiceProxy.ProxyBypassFilterValue = string.Empty;

            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var proxyService = new FakeProxyService(new ProxyDefinition
            {
                Id = 1,
                Name = "Selected",
                ProxyType = ProxyType.Http,
                Hostname = "proxy.example",
                Port = 3128
            });

            new ProxyMigrationService(configService, proxyService, LogManager.GetCurrentClassLogger()).MigrateProxySettings();

            Assert.That(ConfigServiceProxy.ProxyHostnameValue, Is.Empty);
            Assert.That(ConfigServiceProxy.ProxyPortValue, Is.Zero);
            Assert.That(ConfigServiceProxy.ProxyUsernameValue, Is.Empty);
            Assert.That(ConfigServiceProxy.ProxyPasswordValue, Is.Empty);
            Assert.That(proxyService.Get(1).Hostname, Is.EqualTo("proxy.example"));
        }
    }
}
