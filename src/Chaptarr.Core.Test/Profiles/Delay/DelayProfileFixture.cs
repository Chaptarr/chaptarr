using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Profiles.Delay;

namespace Chaptarr.Core.Test.Profiles.Delay
{
    [TestFixture]
    public class DelayProfileFixture
    {
        [Test]
        public void should_return_the_configured_delay_for_torrent_and_usenet()
        {
            var subject = new DelayProfile
            {
                TorrentDelay = 30,
                UsenetDelay = 45
            };

            Assert.Multiple(() =>
            {
                Assert.That(subject.GetProtocolDelay(DownloadProtocol.Torrent), Is.EqualTo(30));
                Assert.That(subject.GetProtocolDelay(DownloadProtocol.Usenet), Is.EqualTo(45));
            });
        }

        [Test]
        public void should_not_apply_torrent_or_usenet_delay_to_direct()
        {
            var subject = new DelayProfile
            {
                TorrentDelay = 30,
                UsenetDelay = 45
            };

            Assert.That(subject.GetProtocolDelay(DownloadProtocol.Direct), Is.Zero);
        }
    }
}
