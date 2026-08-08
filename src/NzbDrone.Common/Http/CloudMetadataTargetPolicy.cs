using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Http
{
    public static class CloudMetadataTargetPolicy
    {
        private static readonly HashSet<string> BlockedHostnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "metadata.google.internal",
            "metadata.google.internal."
        };

        private static readonly IPAddress[] BlockedAddresses =
        {
            IPAddress.Parse("169.254.169.254"),
            IPAddress.Parse("169.254.170.2"),
            IPAddress.Parse("169.254.170.23"),
            IPAddress.Parse("168.63.129.16"),
            IPAddress.Parse("100.100.100.200"),
            IPAddress.Parse("fd00:ec2::254"),
            IPAddress.Parse("fd00:ec2::23"),
            IPAddress.Parse("fd20:ce::254")
        };

        public static bool IsBlocked(string host)
        {
            if (host.IsNullOrWhiteSpace())
            {
                return false;
            }

            host = host.Trim().Trim('[', ']');

            if (BlockedHostnames.Contains(host))
            {
                return true;
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                return IsBlocked(ip);
            }

            try
            {
                return Dns.GetHostAddresses(host).Any(IsBlocked);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsBlocked(IPAddress ip)
        {
            if (ip == null)
            {
                return false;
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            return BlockedAddresses.Any(blocked => blocked.Equals(ip));
        }
    }
}
