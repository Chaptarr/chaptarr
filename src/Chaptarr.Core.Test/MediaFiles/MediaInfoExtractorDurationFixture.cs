using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaInfoExtractorDurationFixture
    {
        private sealed class EvidenceExtractor : ITagExtractorWithDuration
        {
            public int DurationCalls { get; private set; }
            public bool IsAvailable => true;
            public int Priority => 1;
            public string Name => "Evidence";

            public Dictionary<string, List<string>> ExtractTags(string path)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = new List<string> { "Fixture Title" }
                };
            }

            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
            {
                DurationCalls++;
                return (ExtractTags(path), 123);
            }
        }

        [Test]
        public void should_use_one_mp3_duration_authority_across_tag_and_media_info_paths()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mp3-duration-parity-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });

            var externalTools = new DurationExternalToolsStub
            {
                FfprobeAvailable = true,
                FfprobeDuration = TimeSpan.FromSeconds(456)
            };
            var resolver = new AudioDurationResolver(externalTools, LogManager.GetCurrentClassLogger());
            var extractor = new EvidenceExtractor();
            var tagService = new TagExtractionService(new[] { extractor }, resolver, LogManager.GetCurrentClassLogger());
            var mediaInfo = new MediaInfoExtractor(externalTools, resolver);

            var tagResult = tagService.ExtractTagsAndDurationWithResult(path);
            var directDuration = mediaInfo.GetDuration(path);
            var mediaResult = mediaInfo.ExtractMediaInfo(path);

            Assert.That(tagResult.DurationSeconds, Is.EqualTo(456));
            Assert.That(directDuration, Is.EqualTo(TimeSpan.FromSeconds(456)));
            Assert.That(mediaResult.Duration, Is.EqualTo(TimeSpan.FromSeconds(456)));
            Assert.That(extractor.DurationCalls, Is.Zero);
        }

        [Test]
        public void should_never_use_taglib_duration_for_an_unproven_mp3()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mp3-duration-no-fallback-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, BuildUnstableMp3());

            using (var tagLibFile = TagLib.File.Create(path))
            {
                Assert.That(tagLibFile.Properties.Duration, Is.GreaterThan(TimeSpan.Zero),
                    "The fixture must expose the wrong-but-nonzero TagLib fallback this regression prevents");
            }

            var externalTools = new DurationExternalToolsStub { FfprobeAvailable = false };
            var resolver = new AudioDurationResolver(externalTools, LogManager.GetCurrentClassLogger());
            var subject = new MediaInfoExtractor(externalTools, resolver);

            Assert.That(subject.GetDuration(path), Is.EqualTo(TimeSpan.Zero));
            Assert.That(subject.ExtractMediaInfo(path).Duration, Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void should_keep_taglib_as_the_normal_non_mp3_duration_reader()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"duration-normal-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, BuildOneSecondWav());

            var resolver = new DurationResolverStub();
            var subject = new MediaInfoExtractor(new DurationExternalToolsStub(), resolver);

            Assert.That(subject.GetDuration(path).TotalSeconds, Is.EqualTo(1).Within(0.01));
            Assert.That(subject.ExtractMediaInfo(path).Duration.TotalSeconds, Is.EqualTo(1).Within(0.01));
            Assert.That(resolver.ResolveMp3Calls, Is.Zero);
            Assert.That(resolver.FfprobeDurationCalls, Is.Zero);
        }

        private static byte[] BuildUnstableMp3()
        {
            using var stream = new MemoryStream();
            foreach (var bitrate in new[] { 56, 64, 80, 64, 96, 64, 80, 64 })
            {
                var frameLength = 72 * bitrate * 1000 / 22050;
                var frame = new byte[frameLength];
                var header = Header(bitrate);
                Buffer.BlockCopy(header, 0, frame, 0, header.Length);
                stream.Write(frame, 0, frame.Length);
            }

            return stream.ToArray();
        }

        private static byte[] Header(int bitrateKbps)
        {
            var bitrateIndex = bitrateKbps switch
            {
                56 => 7,
                64 => 8,
                80 => 9,
                96 => 10,
                _ => throw new ArgumentOutOfRangeException(nameof(bitrateKbps), bitrateKbps, null)
            };

            const uint sync = 0x7FF;
            const uint version = 0x2;
            const uint layer = 0x1;
            const uint protection = 0x1;
            const uint sampleRate = 0x0;
            const uint channelMode = 0x1;

            var raw = (sync << 21) |
                      (version << 19) |
                      (layer << 17) |
                      (protection << 16) |
                      ((uint)bitrateIndex << 12) |
                      (sampleRate << 10) |
                      (channelMode << 6);

            return new[]
            {
                (byte)((raw >> 24) & 0xFF),
                (byte)((raw >> 16) & 0xFF),
                (byte)((raw >> 8) & 0xFF),
                (byte)(raw & 0xFF)
            };
        }

        private static byte[] BuildOneSecondWav()
        {
            const int sampleRate = 8000;
            const short channels = 1;
            const short bitsPerSample = 8;
            var audio = new byte[sampleRate];

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + audio.Length);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(audio.Length);
            writer.Write(audio);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
