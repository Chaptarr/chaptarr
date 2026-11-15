using System;
using System.Net;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverUrlSafetyPolicyFixture
    {
        [TestCase("1.1.1.1", true)]
        [TestCase("127.0.0.1", false)]
        [TestCase("192.168.1.1", false)]
        [TestCase("::1", false)]
        public void resolved_addresses_keep_the_existing_private_address_boundary(string address, bool allowed)
        {
            var policy = MediaCoverUrlSafetyPolicy.CreateTargetPolicy(null);
            TestDelegate validate = () => policy.ValidateAddress(new Uri("https://cover.example/image"), IPAddress.Parse(address));
            if (allowed)
            {
                Assert.DoesNotThrow(validate);
            }
            else
            {
                var error = Assert.Throws<UnsafeImageUrlException>(validate);
                Assert.That(error.Status, Is.EqualTo(WebExceptionStatus.ProtocolError));
                Assert.That(error.Message, Does.Contain("private_or_local_address"));
            }
        }

        [Test]
        public void URL_checks_do_not_require_DNS_before_cache_reuse_or_proxy_routing()
        {
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://unresolvable-cover.invalid/image", null,
                out var url, out var reason), Is.True);
            Assert.That(url, Is.EqualTo("https://unresolvable-cover.invalid/image"));
            Assert.That(reason, Is.Null);
        }
    }
}
