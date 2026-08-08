using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Configuration
{
    public interface IProxyMigrationService
    {
        void MigrateProxySettings();
    }

    public class ProxyMigrationService : IProxyMigrationService, IHandle<ApplicationStartedEvent>
    {
        private readonly IConfigService _configService;
        private readonly IProxyService _proxyService;
        private readonly Logger _logger;

        public ProxyMigrationService(IConfigService configService, IProxyService proxyService, Logger logger)
        {
            _configService = configService;
            _proxyService = proxyService;
            _logger = logger;
        }

        public void MigrateProxySettings()
        {
            try
            {
                var proxyMode = _configService.ProxyMode;
                if (proxyMode == ProxyMode.Disabled)
                {
                    return;
                }

                var existingProxies = _proxyService.All();
                if (existingProxies.Any())
                {
                    RestoreGlobalBypassSettings(existingProxies);
                    SeedSingleProxyAsDefault(existingProxies);
                    ClearLegacyProxySettingsWhenMapped();
                    return;
                }

                // Check for legacy proxy settings
                var hostname = _configService.ProxyHostname;
                var port = _configService.ProxyPort;

                if (string.IsNullOrEmpty(hostname) || port <= 0)
                {
                    return;
                }

                _logger.Info("Migrating legacy proxy settings to new Proxies table");

                // Create proxy from legacy settings
                var proxy = new ProxyDefinition
                {
                    Name = "Legacy Proxy",
                    ProxyType = _configService.ProxyType,
                    Hostname = hostname,
                    Port = port,
                    Username = _configService.ProxyUsername,
                    Password = _configService.ProxyPassword,
                    BypassLocalAddresses = _configService.ProxyBypassLocalAddresses,
                    BypassFilter = _configService.ProxyBypassFilter
                };

                var createdProxy = _proxyService.Add(proxy);

                _configService.GlobalProxyId = createdProxy.Id;
                ClearLegacyProxySettingsWhenMapped();

                _logger.Info("Successfully migrated legacy proxy settings");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to migrate proxy settings");
            }
        }

        private void SeedSingleProxyAsDefault(System.Collections.Generic.List<ProxyDefinition> proxies)
        {
            if (_configService.GlobalProxyId.HasValue || proxies.Count != 1)
            {
                return;
            }

            _logger.Info("Setting the only configured proxy '{0}' as the default proxy", proxies[0].Name);
            _configService.GlobalProxyId = proxies[0].Id;
        }

        private void ClearLegacyProxySettingsWhenMapped()
        {
            if (!_configService.GlobalProxyId.HasValue || _proxyService.Find(_configService.GlobalProxyId.Value) == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_configService.ProxyHostname) &&
                _configService.ProxyPort <= 0 &&
                string.IsNullOrWhiteSpace(_configService.ProxyUsername) &&
                string.IsNullOrWhiteSpace(_configService.ProxyPassword))
            {
                return;
            }

            _logger.Info("Clearing migrated legacy proxy settings");
            _configService.SaveConfigDictionary(new Dictionary<string, object>
            {
                { nameof(IConfigService.ProxyHostname), string.Empty },
                { nameof(IConfigService.ProxyPort), 0 },
                { nameof(IConfigService.ProxyUsername), string.Empty },
                { nameof(IConfigService.ProxyPassword), string.Empty }
            });
        }

        private void RestoreGlobalBypassSettings(List<ProxyDefinition> proxies)
        {
            var mergedBypassLocalAddresses = _configService.ProxyBypassLocalAddresses || proxies.Any((proxy) => proxy.BypassLocalAddresses);
            var mergedBypassFilter = MergeBypassFilter(_configService.ProxyBypassFilter, proxies.Select((proxy) => proxy.BypassFilter));

            if (mergedBypassLocalAddresses != _configService.ProxyBypassLocalAddresses ||
                !string.Equals(mergedBypassFilter, _configService.ProxyBypassFilter, StringComparison.Ordinal))
            {
                _logger.Info("Consolidating per-proxy bypass settings back into the global proxy configuration");
                _configService.SaveConfigDictionary(new Dictionary<string, object>
                {
                    { nameof(IConfigService.ProxyBypassLocalAddresses), mergedBypassLocalAddresses },
                    { nameof(IConfigService.ProxyBypassFilter), mergedBypassFilter }
                });
            }

            foreach (var proxy in proxies)
            {
                if (proxy.BypassLocalAddresses == mergedBypassLocalAddresses &&
                    string.Equals(proxy.BypassFilter ?? string.Empty, mergedBypassFilter, StringComparison.Ordinal))
                {
                    continue;
                }

                proxy.BypassLocalAddresses = mergedBypassLocalAddresses;
                proxy.BypassFilter = mergedBypassFilter;
                _proxyService.Update(proxy);
            }
        }

        private static string MergeBypassFilter(string currentFilter, IEnumerable<string> proxyFilters)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddTokens(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        tokens.Add(token);
                    }
                }
            }

            AddTokens(currentFilter);

            foreach (var proxyFilter in proxyFilters)
            {
                AddTokens(proxyFilter);
            }

            return string.Join(",", tokens);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            MigrateProxySettings();
        }
    }
}
