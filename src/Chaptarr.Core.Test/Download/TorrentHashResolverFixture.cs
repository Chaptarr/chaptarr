using MonoTorrent;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TorrentInfo;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TorrentHashResolverFixture
    {
        private const string V1Hash = "0123456789abcdef0123456789abcdef01234567";
        private const string V2Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [Test]
        public void should_prefer_v1_hash_for_hybrid_torrents()
        {
            var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{V1Hash}&xt=urn:btmh:1220{V2Hash}");

            var result = TorrentHashResolver.GetSupportedHashOrThrow(magnet.InfoHashes, "This magnet link");

            Assert.That(result, Is.EqualTo(magnet.InfoHashes.V1.ToHex().ToUpperInvariant()));
            Assert.That(result, Is.Not.EqualTo(magnet.InfoHashes.V2.ToHex().ToUpperInvariant()));
        }

        [Test]
        public void should_return_null_for_v2_only_torrents_when_only_optional_hash_is_needed()
        {
            var magnet = MagnetLink.Parse($"magnet:?xt=urn:btmh:1220{V2Hash}");

            var result = TorrentHashResolver.GetSupportedHashOrNull(magnet.InfoHashes);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_reject_v2_only_torrents_when_download_tracking_requires_v1()
        {
            var magnet = MagnetLink.Parse($"magnet:?xt=urn:btmh:1220{V2Hash}");

            var exception = Assert.Throws<UnsupportedTorrentHashException>(() => TorrentHashResolver.GetSupportedHashOrThrow(magnet.InfoHashes, "This magnet link"));

            Assert.That(exception.Message, Does.Contain("v2-only"));
            Assert.That(exception.Message, Does.Contain("requires a v1 info hash"));
        }

        [Test]
        public void should_drop_v2_only_hex_hashes_provided_directly_by_indexers()
        {
            var result = TorrentHashResolver.NormalizeKnownHash(V2Hash);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_normalize_supported_direct_infohashes()
        {
            var result = TorrentHashResolver.NormalizeKnownHash(V1Hash);

            Assert.That(result, Is.EqualTo(V1Hash.ToUpperInvariant()));
        }
    }
}
