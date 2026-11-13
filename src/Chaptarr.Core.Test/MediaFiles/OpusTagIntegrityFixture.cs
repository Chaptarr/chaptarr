using System.IO;
using System.Linq;
using NUnit.Framework;
using TagLib;
using TagLib.Ogg;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class OpusTagIntegrityFixture
    {
        [Test]
        public void tagging_must_not_rewrite_the_first_audio_page_or_its_granule_position()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".opus");
            try
            {
                System.IO.File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "silence.opus"), path);
                byte[] audio;
                using (var file = TagLib.File.Create(path))
                {
                    var first = file.Find("OggS", 0);
                    var second = file.Find("OggS", first + 1);
                    var third = file.Find("OggS", second + 1);
                    Assert.That(file.InvariantStartPosition, Is.EqualTo(third), "Upstream ac03b7bade: reading tags must stop before the first media page.");
                    audio = System.IO.File.ReadAllBytes(path).Skip((int)third).ToArray();
                    file.Tag.Title = "Updated title";
                    file.Save();
                }

                using var updated = TagLib.File.Create(path);
                Assert.That(System.IO.File.ReadAllBytes(path).Skip((int)updated.InvariantStartPosition), Is.EqualTo(audio));
            }
            finally { System.IO.File.Delete(path); }
        }

        [TestCase(10, false, 1, 10)]
        [TestCase(260, false, 2, 5)]
        [TestCase(255, false, 1, 255)]
        [TestCase(510, false, 2, 255)]
        [TestCase(510, true, 3, 0)]
        public void packet_lacing_must_mark_a_complete_multiple_of_255(int length, bool complete, int laces, byte last)
        {
            // Upstream 15d9f9276e: a complete packet needs its terminating zero lace.
            var packets = new ByteVectorCollection { new ByteVector(new byte[length]) };
            var page = new Page(packets, new PageHeader(0, 0, PageFlags.None, complete));
            page.Render();
            Assert.That(page.Header.LacingValues, Is.EqualTo(new ByteVector(Enumerable.Repeat((byte)255, laces - 1).Concat(new[] { last }).ToArray())));
        }
    }
}
