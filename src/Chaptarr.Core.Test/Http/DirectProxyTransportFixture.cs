using System;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class DirectProxyTransportFixture
    {
        private class CertificateValidationProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.ReturnType == typeof(bool))
                {
                    return false;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        [Test]
        public void explicit_direct_connection_should_disable_system_proxy_resolution()
        {
            var factory = new ManagedSocketsHttpHandlerFactory(
                new ManagedWebProxyFactory(new CacheManager()),
                DispatchProxy.Create<ICertificateValidationService, CertificateValidationProxy>(),
                LogManager.GetCurrentClassLogger());

            using var handler = factory.CreateHandler(HttpProxySettings.DirectConnection, false, false, null, false, 12);

            Assert.That(handler.UseProxy, Is.False);
            Assert.That(handler.Proxy, Is.Null);
        }
    }
}
