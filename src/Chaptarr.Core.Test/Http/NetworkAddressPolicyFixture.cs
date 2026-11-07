using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class NetworkAddressPolicyFixture
    {
        [TestCase("8.8.8.8", true)]
        [TestCase("10.0.0.1", false)]
        [TestCase("100.64.0.1", false)]
        [TestCase("169.254.1.1", false)]
        [TestCase("172.31.255.255", false)]
        [TestCase("192.0.2.1", false)]
        [TestCase("192.168.1.1", false)]
        [TestCase("198.18.0.1", false)]
        [TestCase("198.51.100.1", false)]
        [TestCase("203.0.113.1", false)]
        [TestCase("224.0.0.1", false)]
        [TestCase("240.0.0.1", false)]
        [TestCase("0.1.2.3", false)]
        [TestCase("2606:4700:4700::1111", true)]
        [TestCase("fc00::1", false)]
        [TestCase("::", false)]
        [TestCase("fe80::1", false)]
        [TestCase("2001:db8::1", false)]
        [TestCase("::ffff:127.0.0.1", false)]
        public void should_classify_public_and_reserved_addresses(string value, bool isPublic)
        {
            Assert.That(NetworkAddressPolicy.IsPublicAddress(IPAddress.Parse(value)), Is.EqualTo(isPublic));
        }
    }
}
