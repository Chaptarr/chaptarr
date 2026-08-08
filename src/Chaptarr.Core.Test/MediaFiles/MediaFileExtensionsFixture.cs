using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileExtensionsFixture
    {
        [Test]
        public void should_treat_matroska_audio_as_supported_audio_with_unknown_fallback_quality()
        {
            Assert.That(MediaFileExtensions.AudioExtensions, Does.Contain(".mka"));
            Assert.That(MediaFileExtensions.AllExtensions, Does.Contain(".mka"));
            Assert.That(MediaFileExtensions.GetQualityForExtension(".mka"), Is.EqualTo(Quality.UnknownAudio));
        }

        [Test]
        public void should_treat_opus_as_supported_mp3_family_audio()
        {
            Assert.That(MediaFileExtensions.AudioExtensions, Does.Contain(".opus"));
            Assert.That(MediaFileExtensions.AllExtensions, Does.Contain(".opus"));
            Assert.That(MediaFileExtensions.GetQualityForExtension(".opus"), Is.EqualTo(Quality.MP3));
        }

        [Test]
        public void should_detect_matroska_audio_quality_from_codec_when_available()
        {
            var cases = new[]
            {
                ("flac", Quality.FLAC),
                ("ALAC", Quality.FLAC),
                ("wavpack", Quality.FLAC),
                ("pcm_s24le", Quality.FLAC),
                ("truehd", Quality.FLAC),
                ("opus", Quality.MP3),
                ("aac", Quality.MP3),
                (string.Empty, Quality.UnknownAudio)
            };

            foreach (var (audioFormat, expectedQuality) in cases)
            {
                var quality = MediaFileExtensions.GetQualityForExtension(
                    ".mka",
                    new MediaInfoModel { AudioFormat = audioFormat });

                Assert.That(quality, Is.EqualTo(expectedQuality), audioFormat);
            }
        }

        [Test]
        public void should_not_allow_audio_tag_writes_for_matroska_audio()
        {
            Assert.That(MediaFileExtensions.CanWriteAudioTags(".mka"), Is.False);
            Assert.That(MediaFileExtensions.CanWriteAudioTags(".mp3"), Is.True);
        }

        [Test]
        public void should_only_treat_text_formats_as_single_file_book_containers()
        {
            Assert.That(MediaFileExtensions.IsSingleFileBookContainer(".epub"), Is.True);
            Assert.That(MediaFileExtensions.IsSingleFileBookContainer(".pdf"), Is.True);
            Assert.That(MediaFileExtensions.IsSingleFileBookContainer(".mp3"), Is.False);
            Assert.That(MediaFileExtensions.IsSingleFileBookContainer(".m4a"), Is.False);
            Assert.That(MediaFileExtensions.IsSingleFileBookContainer(".m4b"), Is.False);
            Assert.That(MediaFileExtensions.IsSingleFileBookContainer(".mka"), Is.False);
        }
    }
}
