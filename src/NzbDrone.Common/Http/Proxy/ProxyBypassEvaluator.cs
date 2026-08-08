using System;
using System.Linq;
using System.Net;
using NetTools;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Http.Proxy
{
    public static class ProxyBypassEvaluator
    {
        private static readonly Uri EvaluationProxyUri = new Uri("http://127.0.0.1:1");

        public static string[] ParseBypassList(string bypassFilter)
        {
            if (string.IsNullOrWhiteSpace(bypassFilter))
            {
                return Array.Empty<string>();
            }

            var hostList = bypassFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = 0; i < hostList.Length; i++)
            {
                if (hostList[i].StartsWith("*", StringComparison.Ordinal))
                {
                    hostList[i] = ";" + hostList[i];
                }
            }

            return hostList;
        }

        public static bool ShouldBypass(Uri destination, bool bypassLocalAddress, string[] bypassList)
        {
            var proxy = new WebProxy(EvaluationProxyUri, bypassLocalAddress, bypassList ?? Array.Empty<string>());
            return ShouldBypass(destination, bypassLocalAddress, bypassList, proxy);
        }

        internal static bool ShouldBypass(Uri destination, bool bypassLocalAddress, string[] bypassList, IWebProxy proxy)
        {
            if (destination == null)
            {
                return true;
            }

            if (bypassLocalAddress && destination.HostNameType == UriHostNameType.IPv4 &&
                IPAddress.TryParse(destination.Host, out var localAddress) &&
                (localAddress.IsLocalAddress() || localAddress.IsCgnatIpAddress()))
            {
                return true;
            }

            if (bypassLocalAddress && destination.HostNameType == UriHostNameType.IPv6 &&
                IPAddress.TryParse(destination.Host, out localAddress) && localAddress.IsLocalAddress())
            {
                return true;
            }

            if (destination.IsLoopback || IsBypassedByIpAddressRange(bypassList, destination.Host))
            {
                return true;
            }

            return proxy?.IsBypassed(destination) == true;
        }

        private static bool IsBypassedByIpAddressRange(string[] bypassList, string host)
        {
            if (bypassList == null || bypassList.Length == 0 || !IPAddress.TryParse(host, out var ipAddress))
            {
                return false;
            }

            if (ipAddress.IsIPv4MappedToIPv6)
            {
                ipAddress = ipAddress.MapToIPv4();
            }

            return bypassList.Any(bypass =>
                IPAddressRange.TryParse(bypass, out var ipAddressRange) &&
                ipAddressRange.Contains(ipAddress));
        }
    }
}
