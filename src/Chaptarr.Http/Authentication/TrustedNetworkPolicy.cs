using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Authentication
{
    public static class TrustedNetworkPolicy
    {
        private static readonly object DirectRequestKey = new object();
        private static readonly string[] ClientAddressHeaders =
        {
            "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP", "True-Client-IP"
        };

        public static void CaptureRequestOrigin(HttpContext context)
        {
            // Forwarding consumes headers and rewrites the peer; check the entire original chain.
            context.Items[DirectRequestKey] = IsLocalRequest(context, context.RequestServices.GetRequiredService<IConfigFileProvider>());
        }

        public static bool MaySkipLocalLogin(HttpContext context, IConfigFileProvider config)
        {
            return context != null && (context.Items.TryGetValue(DirectRequestKey, out var local)
                ? local is true
                : IsLocalRequest(context, config));
        }

        private static bool IsLocalRequest(HttpContext context, IConfigFileProvider config)
        {
            if (config == null || !IsLocalAddress(context.Connection.RemoteIpAddress, config)) return false;

            foreach (var name in ClientAddressHeaders)
            {
                if (!context.Request.Headers.TryGetValue(name, out var values)) continue;
                if (values.Count == 0 || values.Any(value => value == null ||
                    value.Split(',').Any(address => !IsLocalEndpoint(address, config)))) return false;
            }

            if (context.Request.Headers.TryGetValue("Forwarded", out var forwarded))
            {
                if (forwarded.Count == 0) return false;
                foreach (var line in forwarded)
                {
                    if (line == null) return false;
                    foreach (var hop in line.Split(','))
                    {
                        var addresses = hop.Split(';').Select(part => part.Split('=', 2))
                            .Where(part => part.Length == 2 && part[0].Trim().Equals("for", StringComparison.OrdinalIgnoreCase))
                            .Select(part => part[1]).ToList();
                        if (addresses.Count == 0 || addresses.Any(address => !IsLocalEndpoint(address, config))) return false;
                    }
                }
            }

            return true;
        }

        private static bool IsLocalEndpoint(string value, IConfigFileProvider config)
        {
            value = value.Trim();
            if (value.StartsWith('"') && value.EndsWith('"') && value.Length > 1)
                value = value.Substring(1, value.Length - 2);
            return IPEndPoint.TryParse(value, out var endpoint) && IsLocalAddress(endpoint.Address, config);
        }

        private static bool IsLocalAddress(IPAddress address, IConfigFileProvider config)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return address.IsLocalAddress() || (config.TrustCgnatIpAddresses && address.IsCgnatIpAddress());
        }
    }
}
