using Microsoft.AspNetCore.Http;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Authentication
{
    internal static class TrustedNetworkPolicy
    {
        public static bool IsLocalOrTrusted(HttpContext context, IConfigFileProvider configFileProvider)
        {
            var ipAddress = context?.Connection?.RemoteIpAddress;
            if (ipAddress == null || configFileProvider == null)
            {
                return false;
            }

            return ipAddress.IsLocalAddress() ||
                   (configFileProvider.TrustCgnatIpAddresses && ipAddress.IsCgnatIpAddress());
        }

        public static bool IsLocalOrTrusted(HttpRequest request, IConfigFileProvider configFileProvider)
        {
            return IsLocalOrTrusted(request?.HttpContext, configFileProvider);
        }
    }
}
