using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TagExtractionServiceMp3DurationFixture
    {
        private sealed class CountingExtractor : ITagExtractorWithDuration
        {
            public bool IsAvailable => true;
            public int Priority => 1;
            public string Name => "Counting";
            public int ExtractTagsCalls { get; private set; }
            public int ExtractTagsAndDurationCalls { get; private set; }

            public Dictionary<string, List<string>> ExtractTags(string path)
            {
                ExtractTagsCalls++;
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ID3v2:TIT2"] = new List<string> { "Fixture Title" }
                };
            }

            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
            {
                ExtractTagsAndDurationCalls++;
                return (ExtractTags(path), 123);
            }
        }

        [Test]
        public void should_use_tag_only_extractor_path_and_dedicated_duration_for_mp3()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mp3-duration-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, BuildStableCbrMp3());

            var extractor = new CountingExtractor();
            var externalTools = new DurationExternalToolsStub
            {
                FfprobeAvailable = true,
                FfprobeDuration = TimeSpan.FromHours(10)
            };
            var durationResolver = new AudioDurationResolver(externalTools, LogManager.GetCurrentClassLogger());
            var subject = new TagExtractionService(new[] { extractor }, durationResolver, LogManager.GetCurrentClassLogger());

            var (tags, durationSeconds) = subject.ExtractTagsAndDuration(path);

            Assert.That(tags, Does.ContainKey("title"));
            Assert.That(durationSeconds, Is.EqualTo((int)Math.Round(new FileInfo(path).Length * 8.0 / (64 * 1000.0))));
            Assert.That(extractor.ExtractTagsCalls, Is.EqualTo(1));
            Assert.That(extractor.ExtractTagsAndDurationCalls, Is.EqualTo(0));
            Assert.That(externalTools.AvailabilityChecks, Is.Zero);
            Assert.That(externalTools.DurationCalls, Is.Zero);
        }

        [Test]
        public void should_use_ffprobe_when_mp3_frames_cannot_prove_duration()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mp3-duration-invalid-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });

            var extractor = new CountingExtractor();
            var externalTools = new DurationExternalToolsStub
            {
                FfprobeAvailable = true,
                FfprobeDuration = TimeSpan.FromSeconds(456)
            };
            var durationResolver = new AudioDurationResolver(externalTools, LogManager.GetCurrentClassLogger());
            var subject = new TagExtractionService(new[] { extractor }, durationResolver, LogManager.GetCurrentClassLogger());

            var (_, durationSeconds) = subject.ExtractTagsAndDuration(path);

            Assert.That(durationSeconds, Is.EqualTo(456));
            Assert.That(extractor.ExtractTagsCalls, Is.EqualTo(1));
            Assert.That(extractor.ExtractTagsAndDurationCalls, Is.Zero);
            Assert.That(externalTools.AvailabilityChecks, Is.EqualTo(1));
            Assert.That(externalTools.DurationCalls, Is.EqualTo(1));
            Assert.That(externalTools.LastTimeoutMs, Is.EqualTo(20000));
        }

        [Test]
        public void should_leave_unproven_mp3_duration_unknown_instead_of_accepting_reader_estimate()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mp3-duration-unknown-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });

            var extractor = new CountingExtractor();
            var externalTools = new DurationExternalToolsStub { FfprobeAvailable = false };
            var durationResolver = new AudioDurationResolver(externalTools, LogManager.GetCurrentClassLogger());
            var subject = new TagExtractionService(new[] { extractor }, durationResolver, LogManager.GetCurrentClassLogger());

            var (_, durationSeconds) = subject.ExtractTagsAndDuration(path);

            Assert.That(durationSeconds, Is.Null);
            Assert.That(extractor.ExtractTagsCalls, Is.EqualTo(1));
            Assert.That(extractor.ExtractTagsAndDurationCalls, Is.Zero);
            Assert.That(externalTools.AvailabilityChecks, Is.EqualTo(1));
            Assert.That(externalTools.DurationCalls, Is.Zero);
        }

        private static byte[] BuildStableCbrMp3()
        {
            using var stream = new MemoryStream();
            stream.Write(Frame(56, 182), 0, 182);

            for (var i = 0; i < 200; i++)
            {
                stream.Write(Frame(64, 208), 0, 208);
            }

            return stream.ToArray();
        }

        private static byte[] Frame(int bitrateKbps, int frameLength)
        {
            var frame = new byte[frameLength];
            var header = Header(bitrateKbps);
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            return frame;
        }

        private static byte[] Header(int bitrateKbps)
        {
            var bitrateIndex = bitrateKbps switch
            {
                56 => 7,
                64 => 8,
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
    }
}
