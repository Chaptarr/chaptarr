using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TagExtraction;
using TagLib;
using TagLib.Mpeg4;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class AudioTagMutationFixture
    {
        public static IEnumerable<TestCaseData> NativeIdentityCases()
        {
            foreach (var format in new[] { ".m4b", ".flac", ".wma" })
                foreach (var key in new[] { "ASIN", "ISBN" })
                    foreach (var lower in new[] { false, true })
                        foreach (var scrub in new[] { false, true })
                            yield return new TestCaseData(format, key, lower, scrub);
        }

        [TestCaseSource(nameof(NativeIdentityCases))]
        public void native_identity_changes_keep_the_existing_field(string extension, string key, bool lower, bool scrub)
        {
            var file = Add(extension);
            _scrub = true;
            _service.WriteTags(file, false);
            var name = extension == ".m4b" && key == "ASIN" ? "AUDIBLE_ASIN" : extension == ".wma" ? "WM/" + key : key;
            if (lower) name = name.ToLowerInvariant();
            var old = key == "ASIN" ? "B002V0QUOC" : "0593135202";
            var value = key == "ASIN" ? "B002V5ECMK" : "9780593135204";
            using (var audio = TagLib.File.Create(file.Path))
            {
                if (audio.GetTag(TagTypes.Apple) is AppleTag apple) apple.SetDashBox("com.pilabor.tone", name, old);
                else if (audio.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) xiph.SetField(name, old);
                else ((TagLib.Asf.Tag)audio.GetTag(TagTypes.Asf)).SetDescriptorString(old, name);
                audio.Save();
            }
            _scrub = scrub;
            file.Edition.Title = "Corrected title";
            if (key == "ASIN") file.Edition.AudibleASIN = value; else file.Edition.Isbn13 = value;
            _service.WriteTags(file, false);
            Assert.That(_warnings.Logs, Is.Empty);
            using (var audio = TagLib.File.Create(file.Path))
            {
                if (audio.GetTag(TagTypes.Apple) is AppleTag apple)
                    Assert.That(AppleTagFields.Read(apple).Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                        Is.EqualTo(new[] { ("com.pilabor.tone", name, value) }));
                else if (audio.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                    Assert.That(xiph.GetField(name), Is.EqualTo(new[] { value }));
                else
                    Assert.That(((TagLib.Asf.Tag)audio.GetTag(TagTypes.Asf)).Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.ToString()), Is.EqualTo(new[] { value }));
            }
            AssertSecondScrubIsNoop(file);
        }

        [TestCase("Publisher")]
        [TestCase("publisher")]
        public void scrub_keeps_existing_ape_publisher_spelling(string key)
        {
            var file = Add(".wv");
            _scrub = true;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var ape = (TagLib.Ape.Tag)audio.GetTag(TagTypes.Ape);
                ape.RemoveItem("PUBLISHER"); ape.SetValue(key, file.Edition.Publisher); audio.Save();
            }
            AssertSecondScrubIsNoop(file);
            file.Edition.Publisher = "Corrected publisher";
            _service.WriteTags(file, false);
            using var saved = TagLib.File.Create(file.Path);
            var fields = (TagLib.Ape.Tag)saved.GetTag(TagTypes.Ape);
            Assert.That(fields.Where(x => x.Equals("Publisher", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { key }));
            Assert.That(fields.GetItem(key).ToString(), Is.EqualTo(file.Edition.Publisher));
        }

        [Test]
        public void scrub_does_not_restore_a_secondary_ape_publisher()
        {
            var file = Add(".mp3");
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Ape.Tag)audio.GetTag(TagTypes.Ape, true)).SetValue("Publisher", "Stale publisher");
                audio.Save();
            }
            _scrub = true;
            file.Edition.Publisher = "Correct publisher";
            _service.WriteTags(file, false);
            Assert.That(_warnings.Logs, Is.Empty);
            using var saved = TagLib.File.Create(file.Path);
            Assert.That(saved.Tag.Publisher, Is.EqualTo("Correct publisher"));
            var ape = saved.GetTag(TagTypes.Ape) as TagLib.Ape.Tag;
            Assert.That(ape == null || !ape.Contains("Publisher"), Is.True);
        }

        [Test]
        public void malformed_mp4_custom_tag_warns_and_leaves_the_file_unchanged()
        {
            var file = Add(".m4b");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (AppleTag)audio.GetTag(TagTypes.Apple);
                apple.SetDashBox("com.pilabor.tone", "AUDIBLE_ASIN", "shadow");
                var shadow = apple.Single(box => box.BoxType.ToString() == "----" &&
                    (box.GetChild(new ByteVector("name")) as AppleAdditionalInfoBox)?.Text == "AUDIBLE_ASIN");
                foreach (var child in shadow.Children.OfType<AppleDataBox>().ToArray()) shadow.RemoveChild(child);
                apple.SetDashBox("com.pilabor.tone", "AUDIBLE_ASIN", "B002V0QUOC");
                audio.Save();
            }
            var before = System.IO.File.ReadAllBytes(file.Path);
            file.Edition.AudibleASIN = "B002V5ECMK";
            _service.WriteTags(file, false);
            Assert.That(_warnings.Logs.Single(line => line.Contains("malformed MP4 custom tag")), Does.StartWith("Warn:").And.Contain(file.Path));
            Assert.That(_warnings.Logs, Is.All.StartsWith("Warn:"));
            Assert.That(_events, Is.Empty);
            Assert.That(System.IO.File.ReadAllBytes(file.Path), Is.EqualTo(before));
        }

        [TestCase("AUDIBLE_ASIN")]
        [TestCase("PUBLISHER")]
        [TestCase("SERIES")]
        public void malformed_shadow_mp4_custom_box_does_not_loop(string name)
        {
            var file = Add(".m4b");
            using var audio = TagLib.File.Create(file.Path);
            var apple = (AppleTag)audio.GetTag(TagTypes.Apple);
            apple.SetDashBox("com.pilabor.tone", name, "shadow");
            var shadow = apple.Single(box => box.BoxType.ToString() == "----" &&
                (box.GetChild(new ByteVector("name")) as AppleAdditionalInfoBox)?.Text == name);
            foreach (var child in shadow.Children.OfType<AppleDataBox>().ToArray()) shadow.RemoveChild(child);
            apple.SetDashBox("com.pilabor.tone", name, "real value");
            Assert.That(AppleTagFields.Read(apple).Count(x => x.Name == name), Is.EqualTo(1));
            Assert.Throws<CorruptFileException>(() => AppleTagFields.SetText(apple, "com.pilabor.tone", name, "new value"));
        }
    }
}
