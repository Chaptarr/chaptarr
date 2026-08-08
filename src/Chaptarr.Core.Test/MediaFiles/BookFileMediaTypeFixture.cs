using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookFileMediaTypeFixture
    {
        [Test]
        public void should_classify_unknown_text_quality_as_ebook()
        {
            Assert.That(QualityMediaTypeHelper.IsEbookQuality(Quality.Unknown), Is.False);
            Assert.That(QualityMediaTypeHelper.IsEbookFileQuality(Quality.Unknown), Is.True);
            Assert.That(BookFile.DetermineMediaType(new QualityModel(Quality.Unknown)), Is.EqualTo("ebook"));
        }

        [Test]
        public void should_classify_unknown_audio_quality_as_audiobook()
        {
            Assert.That(QualityMediaTypeHelper.IsAudiobookQuality(Quality.UnknownAudio), Is.True);
            Assert.That(BookFile.DetermineMediaType(new QualityModel(Quality.UnknownAudio)), Is.EqualTo("audiobook"));
        }
    }
}
