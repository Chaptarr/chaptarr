using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TagExtraction;
using TagLib;
using File = System.IO.File;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class AudioTagMutationFixture
    {
        [TestCaseSource(nameof(TagFormats))]
        public void artwork_option_off_preserves_art_even_when_scrubbing(string extension)
        {
            var file = Add(extension);
            var art = CreateCover("original-art", 19);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new Picture(art) { Type = PictureType.BackCover, Description = "Chosen artwork" } };
                audio.Tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            _coverPath = CreateCover("edition-art", 29);
            _embedCover = false;
            foreach (var scrub in new[] { false, true })
            {
                _scrub = scrub;
                file.Edition.Title = "Changed title " + scrub;
                _events.Clear();
                _service.WriteTags(file, false);
                Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
                using (var audio = TagLib.File.Create(file.Path))
                {
                    Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
                    Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(art)));
                    if (scrub) Assert.That(audio.Tag.Comment, Is.Null.Or.Empty);
                }
                AssertSecondScrubIsNoop(file);
            }
            Assert.That(_coverLookups, Is.Zero, "Embedding off must not read the edition cover");
        }

        [TestCaseSource(nameof(TagFormats))]
        public void edition_identifiers_are_incidental_and_round_trip_through_the_scanner(string extension)
        {
            var file = Add(extension);
            _scrub = true;
            _service.WriteTags(file, false);
            file.Edition.AudibleASIN = "B08G9PRS1K";
            file.Edition.Asin = "B000000001";
            file.Edition.Isbn13 = "9780593135204";
            file.Edition.Isbn10 = "0593135202";
            AssertSecondScrubIsNoop(file); // Missing identifiers never trigger a write, even under scrub.
            file.Edition.Title = "Edition title correction";
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
            var extracted = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(extracted);
            Assert.That(extracted["ASIN"], Does.Contain(file.Edition.AudibleASIN));
            Assert.That(extracted["ISBN"], Does.Contain(file.Edition.Isbn13));
            AssertSecondScrubIsNoop(file);
            file.Edition.AudibleASIN = "B000000002";
            _scrub = false;
            AssertSecondScrubIsNoop(file); // A different ID alone does not trigger an ordinary write.
            _scrub = true;
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            extracted = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(extracted);
            Assert.That(extracted["ASIN"], Is.EqualTo(new[] { "B000000002" }));
            AssertSecondScrubIsNoop(file);
        }

        [TestCaseSource(nameof(TagFormats))]
        public void edition_identifier_fallbacks_come_from_the_database(string extension)
        {
            var file = Add(extension);
            file.Edition.Asin = "B000000001";
            file.Edition.Isbn10 = "0593135202";
            _service.WriteTags(file, false);
            var extracted = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(extracted);
            Assert.That(extracted["ASIN"], Is.EqualTo(new[] { "B000000001" }));
            Assert.That(extracted["ISBN"], Is.EqualTo(new[] { "0593135202" }));
            AssertSecondScrubIsNoop(file);
        }


        [TestCase("AUDIBLE_ASIN", false)]
        [TestCase("AUDIBLE_ASIN", true)]
        [TestCase("asin", false)]
        [TestCase("asin", true)]
        public void edition_identity_updates_the_existing_alias_without_creating_a_second_identity(string key, bool scrub)
        {
            var file = Add();
            file.Edition.AudibleASIN = "B002V0QUOC";
            _scrub = true;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var id3 = (TagLib.Id3v2.Tag)audio.GetTag(TagTypes.Id3v2);
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().Where(frame => frame.Description == "ASIN").ToArray()) id3.RemoveFrame(frame);
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true).Text = new[] { file.Edition.AudibleASIN };
                audio.Save();
            }
            _scrub = scrub;
            AssertSecondScrubIsNoop(file);
            file.Edition.Title = "Corrected title";
            file.Edition.AudibleASIN = "B002V5ECMK";
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var frames = ((TagLib.Id3v2.Tag)audio.GetTag(TagTypes.Id3v2)).GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
                    .Where(frame => frame.Description.Equals("ASIN", StringComparison.OrdinalIgnoreCase) || frame.Description == "AUDIBLE_ASIN").ToArray();
                Assert.That(frames, Has.Length.EqualTo(1));
                Assert.That(frames[0].Description, Is.EqualTo(key));
                Assert.That(frames[0].Text, Is.EqualTo(new[] { "B002V5ECMK" }));
            }
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void a_write_removes_conflicting_identity_from_the_secondary_container()
        {
            var file = Add();
            file.Edition.AudibleASIN = "B002V5ECMK";
            using (var audio = TagLib.File.Create(file.Path))
            {
                TagLib.Id3v2.UserTextInformationFrame.Get((TagLib.Id3v2.Tag)audio.GetTag(TagTypes.Id3v2), "AUDIBLE_ASIN", true).Text = new[] { "B002V0QUOC" };
                ((TagLib.Ape.Tag)audio.GetTag(TagTypes.Ape, true)).SetValue("ASIN", "B000000001");
                audio.Save();
            }
            _service.WriteTags(file, false);
            var tags = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(tags);
            Assert.That(tags["ASIN"], Is.EqualTo(new[] { "B002V5ECMK" }));
            using var saved = TagLib.File.Create(file.Path);
            Assert.That(((TagLib.Ape.Tag)saved.GetTag(TagTypes.Ape)).GetItem("ASIN"), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void identifiers_use_cleaned_edition_values_not_provider_artifacts(bool artifact)
        {
            var file = Add();
            file.Edition.AudibleASIN = artifact ? "System.Collections.Generic.List`1[System.String]" : "asin:b002v5ecmk";
            file.Edition.Asin = artifact ? "B000000001" : null;
            file.Edition.Isbn13 = artifact ? "System.Collections.Generic.List`1[System.String]" : "isbn:9780593135204";
            file.Edition.Isbn10 = artifact ? "0593135202" : null;
            _service.WriteTags(file, false);
            var tags = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(tags);
            Assert.That(tags["ASIN"], Is.EqualTo(new[] { artifact ? "B000000001" : "B002V5ECMK" }));
            Assert.That(tags["ISBN"], Is.EqualTo(new[] { artifact ? "0593135202" : "9780593135204" }));
        }

        [Test]
        public void missing_identifier_alone_does_not_rewrite_an_ordinary_file()
        {
            var file = Add();
            _service.WriteTags(file, false);
            file.Edition.AudibleASIN = "B002V5ECMK";
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(".flac")]
        [TestCase(".ogg")]
        [TestCase(".opus")]
        public void scrub_preserves_the_embedded_cuesheet_comment(string extension)
        {
            var file = Add(extension);
            const string cue = "FILE \"recording.wav\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 01 00:01:00";
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Ogg.XiphComment)audio.GetTag(TagTypes.Xiph)).SetField("CUESHEET", cue);
                audio.Save();
            }
            _scrub = true;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
                Assert.That(((TagLib.Ogg.XiphComment)audio.GetTag(TagTypes.Xiph)).GetField("CUESHEET"), Is.EqualTo(new[] { cue }));
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(".wav")]
        [TestCase(".m4b")]
        public void a_file_without_a_tag_container_gets_art_and_identity(string extension)
        {
            var file = Add(extension);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.RemoveTags(audio.TagTypesOnDisk);
                audio.Save();
            }
            file.Edition.AudibleASIN = "B002V5ECMK";
            _coverPath = CreateCover("edition", 29);
            _service.WriteTags(file, false);
            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(_coverPath)));
            var tags = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(tags);
            Assert.That(tags["ASIN"], Is.EqualTo(new[] { "B002V5ECMK" }));
            AssertSecondScrubIsNoop(file);
        }

        private static byte[] ChapterAtom(byte[] bytes, string type)
        {
            var marker = Encoding.ASCII.GetBytes(type);
            for (var i = 4; i < bytes.Length - 4; i++)
            {
                if (!bytes.AsSpan(i, 4).SequenceEqual(marker)) continue;
                var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i - 4, 4));
                if (size >= 8 && i - 4 + size <= bytes.Length) return bytes.AsSpan(i - 4, size).ToArray();
            }
            return null;
        }

        private static byte[] CueSheet(string path)
        {
            var bytes = File.ReadAllBytes(path);
            for (var offset = 4; offset + 4 <= bytes.Length;)
            {
                var type = bytes[offset];
                var size = (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                if ((type & 127) == 5) return bytes.AsSpan(offset + 4, size).ToArray();
                offset += 4 + size;
                if ((type & 128) != 0) break;
            }
            return null;
        }

        private static string[] ChapterContents(string path)
        {
            using var audio = TagLib.File.Create(path);
            if (audio.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                return id3.GetFrames().Where(frame => frame.FrameId.ToString() is "CHAP" or "CTOC")
                    .Select(frame => Convert.ToHexString(frame.Render(id3.Version).Data)).ToArray();
            if (audio.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                return xiph.Where(key => key.StartsWith("CHAPTER", StringComparison.OrdinalIgnoreCase)).OrderBy(key => key)
                    .Select(key => key + "=" + string.Join("|", xiph.GetField(key))).ToArray();
            var bytes = File.ReadAllBytes(path);
            return new[] { "chap", "chpl" }.Select(type =>
            {
                var atom = ChapterAtom(bytes, type);
                Assert.That(atom, Is.Not.Null, "Real ffmpeg output must contain " + type);
                return Convert.ToHexString(atom);
            }).ToArray();
        }

        [TestCase(".mp3", true)]
        [TestCase(".opus", true)]
        [TestCase(".ogg", true)]
        [TestCase(".flac", true)]
        [TestCase(".m4b", true)]
        [TestCase(".m4b", false)]
        public void tag_write_keeps_real_chapters_and_then_converges(string extension, bool scrub)
        {
            var file = Add(extension);
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "chapters" + extension), file.Path, true);
            var before = ChapterContents(file.Path);
            var cueSheet = extension == ".flac" ? CueSheet(file.Path) : null;
            if (extension == ".flac") Assert.That(cueSheet, Is.Not.Null);
            Assert.That(before, Is.Not.Empty, "The fixture must really contain chapters");
            _scrub = scrub;
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
            Assert.That(ChapterContents(file.Path), Is.EqualTo(before));
            if (extension == ".flac") Assert.That(CueSheet(file.Path), Is.EqualTo(cueSheet));
            var saved = Path.Combine(TestContext.CurrentContext.WorkDirectory, "chapter-after" + extension);
            File.Copy(file.Path, saved, true);
            TestContext.AddTestAttachment(saved, "Chapter-preserving tag write");
            if (scrub) using (var audio = TagLib.File.Create(file.Path)) Assert.That(audio.Tag.Comment, Is.Null.Or.Empty);
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(".flac")]
        [TestCase(".ogg")]
        [TestCase(".opus")]
        public void chapter_field_names_include_zero_large_indices_and_case_variants(string extension)
        {
            var file = Add(extension);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var xiph = (TagLib.Ogg.XiphComment)audio.GetTag(TagTypes.Xiph);
                foreach (var key in new[] { "chapter000", "chapter1000", "chapter1000name", "chapter1000url" }) xiph.SetField(key, "fixture");
                xiph.SetField("CHAPTERCOMMENT", "not a chapter marker");
                audio.Save();
            }
            _scrub = true;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var xiph = (TagLib.Ogg.XiphComment)audio.GetTag(TagTypes.Xiph);
                foreach (var key in new[] { "chapter000", "chapter1000", "chapter1000name", "chapter1000url" })
                    Assert.That(xiph.GetField(key), Is.EqualTo(new[] { "fixture" }));
                Assert.That(xiph.GetField("CHAPTERCOMMENT"), Is.Empty);
            }
            AssertSecondScrubIsNoop(file);
        }
    }
}
