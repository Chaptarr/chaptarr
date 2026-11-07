using System.Net;
using System.Net.Sockets;

namespace NzbDrone.Common.Http
{
    public static class NetworkAddressPolicy
    {
        public static bool IsPublicAddress(IPAddress address)
        {
            if (address == null)
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var first = bytes[0];
                var second = bytes[1];
                var third = bytes[2];
                return first != 0 && first != 10 && first != 127 && first < 224 &&
                       !(first == 100 && second >= 64 && second <= 127) &&
                       !(first == 169 && second == 254) &&
                       !(first == 172 && second >= 16 && second <= 31) &&
                       !(first == 192 && (second == 0 || second == 168 || (second == 88 && third == 99))) &&
                       !(first == 198 && (second == 18 || second == 19 || (second == 51 && third == 100))) &&
                       !(first == 203 && second == 0 && third == 113);
            }

            if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
                address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            return (bytes[0] & 0xE0) == 0x20 &&
                   !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
        }
    }
}
