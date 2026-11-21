using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class CloudMetadataTargetPolicyFixture
    {
        [TestCase("169.254.169.254")]
        [TestCase("169.254.170.2")]
        [TestCase("169.254.170.23")]
        [TestCase("168.63.129.16")]
        [TestCase("100.100.100.200")]
        [TestCase("fd00:ec2::254")]
        [TestCase("[fd00:ec2::254]")]
        [TestCase("fd00:ec2::23")]
        [TestCase("fd20:ce::254")]
        [TestCase("metadata.google.internal")]
        [TestCase("metadata.google.internal.")]
        public void should_block_known_cloud_metadata_targets(string host)
        {
            Assert.That(CloudMetadataTargetPolicy.IsBlocked(host), Is.True);
        }

        [TestCase("169.254.1.2")]
        [TestCase("10.0.0.1")]
        [TestCase("192.168.1.10")]
        [TestCase("172.16.0.5")]
        [TestCase("100.64.0.5")]
        [TestCase("fe80::1")]
        [TestCase("fd00::1")]
        public void should_allow_non_metadata_private_or_link_local_targets(string host)
        {
            Assert.That(CloudMetadataTargetPolicy.IsBlocked(host), Is.False);
        }

        [Test]
        public void should_block_ipv4_mapped_metadata_address()
        {
            Assert.That(CloudMetadataTargetPolicy.IsBlocked(IPAddress.Parse("::ffff:169.254.169.254")), Is.True);
        }

        [TestCase("::")]
        [TestCase("0:0:0:0:0:0:0:0")]
        [TestCase("::ffff:0.0.0.0")]
        [TestCase("10.0.0.1")]
        [TestCase("192.168.1.10")]
        [TestCase("169.254.1.2")]
        public void media_cover_policy_should_remain_strict_for_private_or_link_local_targets(string host)
        {
            Assert.That(MediaCoverUrlSafetyPolicy.IsPrivateOrLocalAddress(IPAddress.Parse(host)), Is.True);
        }

        [TestCase("http://[::]/cover.jpg")]
        [TestCase("http://[0:0:0:0:0:0:0:0]/cover.jpg")]
        public void media_cover_policy_rejects_unspecified_ipv6_urls(string url)
        {
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(url, null, out var safeUrl, out var reason), Is.False);
            Assert.That(safeUrl, Is.Null);
            Assert.That(reason, Is.EqualTo("private_or_local_address"));
        }

        [Test]
        public void media_cover_policy_keeps_public_ipv6_urls_eligible()
        {
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://[2606:4700:4700::1111]/cover.jpg", null,
                out var safeUrl, out var reason), Is.True);
            Assert.That(safeUrl, Is.Not.Null);
            Assert.That(reason, Is.Null);
        }
    }
}
