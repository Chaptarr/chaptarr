using System;
using System.Net;
using System.Net.Http;
using Chaptarr.Http.Authentication;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class OidcBackchannelHandlerFactoryFixture
    {
        [Test]
        public void should_keep_the_legacy_connection_limits_and_disable_redirects()
        {
            var socketsFactory = new RecordingSocketsHandlerFactory();

            using var handler = OidcBackchannelHandlerFactory.Create(
                socketsFactory,
                HttpProxySettings.DirectConnection,
                allowPrivateIssuer: false);

            Assert.That(socketsFactory.AllowAutoRedirect, Is.False);
            Assert.That(socketsFactory.UseCookies, Is.False);
            Assert.That(socketsFactory.MaxConnectionsPerServer, Is.EqualTo(4));
            Assert.That(socketsFactory.AllowPrivateNetworkTargets, Is.False);
            Assert.That(socketsFactory.Handler.PooledConnectionLifetime, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(handler, Is.TypeOf<OidcBackchannelTargetPolicyHandler>());
            Assert.That(((DelegatingHandler)handler).InnerHandler, Is.TypeOf<OidcResponseLimitHandler>());
        }

        private sealed class RecordingSocketsHandlerFactory : ICreateManagedSocketsHttpHandler
        {
            public bool AllowAutoRedirect { get; private set; }
            public bool UseCookies { get; private set; }
            public int MaxConnectionsPerServer { get; private set; }
            public bool AllowPrivateNetworkTargets { get; private set; }
            public SocketsHttpHandler Handler { get; private set; }

            public SocketsHttpHandler CreateHandler(
                HttpProxySettings proxySettings,
                bool allowAutoRedirect,
                bool useCookies,
                ICredentials credentials,
                bool preAuthenticate,
                int maxConnectionsPerServer,
                bool allowPrivateNetworkTargets = true)
            {
                AllowAutoRedirect = allowAutoRedirect;
                UseCookies = useCookies;
                MaxConnectionsPerServer = maxConnectionsPerServer;
                AllowPrivateNetworkTargets = allowPrivateNetworkTargets;
                Handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = allowAutoRedirect,
                    UseCookies = useCookies,
                    MaxConnectionsPerServer = maxConnectionsPerServer
                };
                return Handler;
            }
        }
    }
}
