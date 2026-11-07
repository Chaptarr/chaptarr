using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Qualities
{
    [TestFixture]
    public class QualityMediaTypeHelperFixture
    {
        [Test]
        public void should_detect_direct_ebook_media_type_from_container_when_quality_is_unknown()
        {
            var mediaType = QualityMediaTypeHelper.DetectMediaType(
                Quality.Unknown,
                new ReleaseInfo
                {
                    Title = "Example Release",
                    Container = "epub",
                    DownloadProtocol = DownloadProtocol.Direct
                });

            Assert.That(mediaType, Is.EqualTo(BookMediaType.Ebook));
        }

        [Test]
        public void should_detect_direct_audiobook_media_type_from_container_when_quality_is_unknown()
        {
            var mediaType = QualityMediaTypeHelper.DetectMediaType(
                Quality.Unknown,
                new ReleaseInfo
                {
                    Title = "Example Release",
                    Container = "m4b",
                    DownloadProtocol = DownloadProtocol.Direct
                });

            Assert.That(mediaType, Is.EqualTo(BookMediaType.Audiobook));
        }
    }
}
