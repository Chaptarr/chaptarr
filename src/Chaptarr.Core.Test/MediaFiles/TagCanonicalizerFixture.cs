using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TagCanonicalizerFixture
    {
        [Test]
        public void should_expose_legacy_title_and_author_sort_fields_as_canonical_tags()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ID3v2:TSOT"] = new List<string> { "title sort" },
                ["MP4:soal"] = new List<string> { "album sort" },
                ["XIPH:ARTISTSORT"] = new List<string> { "artist sort" },
                ["ASF:WM/AlbumArtistSort"] = new List<string> { "album artist sort" }
            };

            TagCanonicalizer.AddCanonicalKeys(tags);

            Assert.Multiple(() =>
            {
                Assert.That(tags["TITLESORT"], Does.Contain("title sort"));
                Assert.That(tags["ALBUMSORT"], Does.Contain("album sort"));
                Assert.That(tags["ARTISTSORT"], Does.Contain("artist sort"));
                Assert.That(tags["ALBUMARTISTSORT"], Does.Contain("album artist sort"));
            });
        }

        [Test]
        public void should_expose_audible_and_apple_freeform_narrator_tags()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["AUDIBLE:NARRATOR"] = new List<string> { "Audible Narrator" },
                ["MP4:----:com.apple.iTunes:NARRATOR"] = new List<string> { "Apple Narrator" }
            };

            TagCanonicalizer.AddCanonicalKeys(tags);

            Assert.That(tags["NARRATOR"], Is.EqualTo(new[] { "Audible Narrator", "Apple Narrator" }));
        }

        [Test]
        public void should_expose_bookshelf_xiph_and_ape_publisher_aliases_and_asf_year()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["XIPH:LABEL"] = new List<string> { "Xiph Label" },
                ["APE:Label"] = new List<string> { "Ape Label" },
                ["ASF:WM/Year"] = new List<string> { "2024" }
            };

            TagCanonicalizer.AddCanonicalKeys(tags);

            Assert.Multiple(() =>
            {
                Assert.That(tags["PUBLISHER"], Is.EqualTo(new[] { "Xiph Label", "Ape Label" }));
                Assert.That(tags["DATE"], Does.Contain("2024"));
            });
        }
    }
}
