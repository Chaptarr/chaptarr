using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Qualities
{
    [TestFixture]
    public class BookshelfQualityCompatibilityFixture
    {
        [Test]
        public void should_restore_legacy_audio_quality_weights_without_changing_native_defaults()
        {
            var definitions = BookshelfQualityCompatibility.GetDefaultDefinitions();
            var legacyWeights = definitions.ToDictionary(definition => definition.Quality.Id, definition => definition.Weight);
            var nativeWeights = Quality.DefaultQualityDefinitions.ToDictionary(definition => definition.Quality.Id, definition => definition.Weight);

            Assert.Multiple(() =>
            {
                Assert.That(legacyWeights[Quality.MP3.Id], Is.EqualTo(100));
                Assert.That(legacyWeights[Quality.M4B.Id], Is.EqualTo(105));
                Assert.That(legacyWeights[Quality.FLAC.Id], Is.EqualTo(110));
                Assert.That(nativeWeights[Quality.FLAC.Id], Is.EqualTo(100));
                Assert.That(nativeWeights[Quality.MP3.Id], Is.EqualTo(105));
                Assert.That(nativeWeights[Quality.M4B.Id], Is.EqualTo(110));
            });
        }

        [Test]
        public void should_compare_audio_qualities_in_bookshelf_order()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BookshelfQualityCompatibility.Compare(Quality.MP3, Quality.M4B), Is.LessThan(0));
                Assert.That(BookshelfQualityCompatibility.Compare(Quality.M4B, Quality.FLAC), Is.LessThan(0));
                Assert.That(BookshelfQualityCompatibility.Compare(Quality.FLAC, Quality.MP3), Is.GreaterThan(0));
            });
        }
    }
}
