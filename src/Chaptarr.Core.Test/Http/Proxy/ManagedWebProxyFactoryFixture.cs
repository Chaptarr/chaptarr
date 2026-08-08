using System;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http.Proxy;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http.Proxy
{
    [TestFixture]
    public class ManagedWebProxyFactoryFixture
    {
        [TestCase("http://192.168.0.10:9696/")]
        [TestCase("http://10.0.0.42:9696/")]
        [TestCase("http://172.16.0.10:9696/")]
        [TestCase("http://100.64.0.10:9696/")]
        public void should_bypass_proxy_for_private_ip_when_bypass_local_addresses_enabled(string destinationUrl)
        {
            var factory = new ManagedWebProxyFactory(new CacheManager());
            var proxySettings = new HttpProxySettings(
                type: ProxyType.Http,
                host: "127.0.0.1",
                port: 8118,
                bypassFilter: string.Empty,
                bypassLocalAddress: true);

            var proxy = factory.GetWebProxy(proxySettings);

            Assert.That(proxy.IsBypassed(new Uri(destinationUrl)), Is.True);
        }

        [Test]
        public void should_not_bypass_proxy_for_private_ip_when_bypass_local_addresses_disabled()
        {
            var factory = new ManagedWebProxyFactory(new CacheManager());
            var proxySettings = new HttpProxySettings(
                type: ProxyType.Http,
                host: "127.0.0.1",
                port: 8118,
                bypassFilter: string.Empty,
                bypassLocalAddress: false);

            var proxy = factory.GetWebProxy(proxySettings);

            Assert.That(proxy.IsBypassed(new Uri("http://192.168.0.10:9696/")), Is.False);
        }

        [Test]
        public void should_bypass_proxy_for_ip_range_in_bypass_filter()
        {
            var factory = new ManagedWebProxyFactory(new CacheManager());
            var proxySettings = new HttpProxySettings(
                type: ProxyType.Http,
                host: "127.0.0.1",
                port: 8118,
                bypassFilter: "192.168.0.0/16",
                bypassLocalAddress: false);

            var proxy = factory.GetWebProxy(proxySettings);

            Assert.That(proxy.IsBypassed(new Uri("http://192.168.0.10:9696/")), Is.True);
        }
    }
}
