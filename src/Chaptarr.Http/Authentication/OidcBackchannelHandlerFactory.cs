using System;
using System.Net.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;

namespace Chaptarr.Http.Authentication
{
    internal static class OidcBackchannelHandlerFactory
    {
        private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        private const int MaxConnectionsPerServer = 4;

        public static HttpMessageHandler Create(
            ICreateManagedSocketsHttpHandler socketsHttpHandlerFactory,
            HttpProxySettings proxySettings,
            bool allowPrivateIssuer)
        {
            var socketsHandler = socketsHttpHandlerFactory.CreateHandler(
                proxySettings,
                allowAutoRedirect: false,
                useCookies: false,
                credentials: null,
                preAuthenticate: false,
                maxConnectionsPerServer: MaxConnectionsPerServer,
                allowPrivateNetworkTargets: allowPrivateIssuer || proxySettings?.IsDirectConnection == false);

            socketsHandler.PooledConnectionLifetime = PooledConnectionLifetime;

            return new OidcBackchannelTargetPolicyHandler(
                allowPrivateIssuer,
                new OidcResponseLimitHandler(socketsHandler));
        }
    }
}
