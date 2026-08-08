using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Http;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ProviderAddedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderDeletedEvent<IDownloadClient>))]
    public class DownloadClientHttpSecurityCheck : HealthCheckBase
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IConfigService _configService;
        private readonly IGlobalProxySettingsResolver _globalProxySettingsResolver;

        public DownloadClientHttpSecurityCheck(IProvideDownloadClient downloadClientProvider,
                                               IConfigService configService,
                                               IGlobalProxySettingsResolver globalProxySettingsResolver,
                                               ILocalizationService localizationService)
            : base(localizationService)
        {
            _downloadClientProvider = downloadClientProvider;
            _configService = configService;
            _globalProxySettingsResolver = globalProxySettingsResolver;
        }

        public override HealthCheck Check()
        {
            if (IsTrustedProxyEverything())
            {
                return new HealthCheck(GetType());
            }

            foreach (var downloadClient in _downloadClientProvider.GetDownloadClients())
            {
                var settings = downloadClient.Definition?.Settings;
                if (settings == null)
                {
                    continue;
                }

                var host = GetStringSetting(settings, "Host");
                var useSsl = GetBooleanSetting(settings, "UseSsl");

                if (useSsl == false && HostResolvesToPublicAddress(host))
                {
                    return new HealthCheck(
                        GetType(),
                        HealthCheckResult.Warning,
                        $"Download client {downloadClient.Definition.Name} uses HTTP to a public host. API credentials are sent unencrypted. Use HTTPS, a VPN, or an SSH tunnel. If this client is covered by a VPN or tunnel, you can ignore this warning.",
                        "#download-client-http-public-host");
                }
            }

            return new HealthCheck(GetType());
        }

        private bool IsTrustedProxyEverything()
        {
            if (_configService.ProxyMode != ProxyMode.ProxyEverything)
            {
                return false;
            }

            try
            {
                return HostResolvesToNonPublicAddress(_globalProxySettingsResolver.ResolveRequired().Host);
            }
            catch
            {
                return false;
            }
        }

        private static string GetStringSetting(object settings, string name)
        {
            return settings.GetType().GetProperty(name)?.GetValue(settings)?.ToString();
        }

        private static bool? GetBooleanSetting(object settings, string name)
        {
            var value = settings.GetType().GetProperty(name)?.GetValue(settings);
            return value is bool boolValue ? boolValue : null;
        }

        private static bool HostResolvesToPublicAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            return TryGetAddresses(host, out var addresses) && addresses.Any(address => !IsNonPublicAddress(address));
        }

        private static bool HostResolvesToNonPublicAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            return TryGetAddresses(host, out var addresses) && addresses.All(IsNonPublicAddress);
        }

        private static bool TryGetAddresses(string host, out IPAddress[] addresses)
        {
            host = host.Trim().Trim('[', ']');

            if (IPAddress.TryParse(host, out var ip))
            {
                addresses = new[] { ip };
                return true;
            }

            try
            {
                addresses = Dns.GetHostAddresses(host);
                return addresses.Length > 0;
            }
            catch
            {
                addresses = Array.Empty<IPAddress>();
                return false;
            }
        }

        private static bool IsNonPublicAddress(IPAddress ip)
        {
            if (ip == null || IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();

                return bytes[0] == 0 ||
                       bytes[0] == 10 ||
                       bytes[0] == 127 ||
                       (bytes[0] == 169 && bytes[1] == 254) ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                {
                    return true;
                }

                var bytes = ip.GetAddressBytes();
                return (bytes[0] & 0xFE) == 0xFC;
            }

            return false;
        }
    }
}
