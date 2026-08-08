using System;
using System.IO;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class Mp3DurationReaderFixture
    {
        [Test]
        public void should_use_info_frame_count_when_first_frame_bitrate_differs_from_audio_frames()
        {
            var stream = new MemoryStream();
            WriteId3v2Header(stream, 0);

            var infoFrame = Frame(56, 182);
            WriteAscii(infoFrame, 21, "Info");
            WriteUInt32BigEndian(infoFrame, 25, 0x1);
            WriteUInt32BigEndian(infoFrame, 29, 262722);
            stream.Write(infoFrame, 0, infoFrame.Length);

            for (var i = 0; i < 8; i++)
            {
                var audioFrame = Frame(64, 208);
                stream.Write(audioFrame, 0, audioFrame.Length);
            }

            stream.Position = 0;

            Assert.That(Mp3DurationReader.TryGetDuration(stream, out var duration, out var source), Is.True);
            Assert.That(source, Is.EqualTo(Mp3DurationReader.DurationSource.XingInfoHeader));
            Assert.That(duration.TotalSeconds, Is.EqualTo(6862.942040816327).Within(0.001));
        }

        [Test]
        public void should_skip_first_frame_when_estimating_stable_cbr_duration()
        {
            var stream = new MemoryStream();

            var firstFrame = Frame(56, 182);
            stream.Write(firstFrame, 0, firstFrame.Length);

            for (var i = 0; i < 12; i++)
            {
                var audioFrame = Frame(64, 208);
                stream.Write(audioFrame, 0, audioFrame.Length);
            }

            stream.Position = 0;

            Assert.That(Mp3DurationReader.TryGetDuration(stream, out var duration, out var source), Is.True);
            Assert.That(source, Is.EqualTo(Mp3DurationReader.DurationSource.StableCbrEstimate));
            stream.Position = 0;
            Assert.That(Mp3DurationReader.TryGetInfo(stream, out var info), Is.True);
            Assert.That(info.Source, Is.EqualTo(Mp3DurationReader.DurationSource.StableCbrEstimate));
            Assert.That(info.BitrateKbps, Is.EqualTo(64));
            Assert.That(info.SampleRate, Is.EqualTo(22050));
            Assert.That(info.Channels, Is.EqualTo(2));
            Assert.That(info.Description, Is.EqualTo("MPEG Version 2 Audio, Layer 3"));

            var expectedFromSampledAudioBitrate = stream.Length * 8.0 / (64 * 1000.0);
            var wrongFromFirstFrameBitrate = stream.Length * 8.0 / (56 * 1000.0);

            Assert.That(duration.TotalSeconds, Is.EqualTo(expectedFromSampledAudioBitrate).Within(0.001));
            Assert.That(duration.TotalSeconds, Is.Not.EqualTo(wrongFromFirstFrameBitrate).Within(0.001));
        }

        [Test]
        public void should_reject_unstable_bitrate_samples_without_frame_count_header()
        {
            var stream = new MemoryStream();

            foreach (var bitrate in new[] { 56, 64, 80, 64, 96, 64, 80, 64 })
            {
                var frame = Frame(bitrate, FrameLength(bitrate));
                stream.Write(frame, 0, frame.Length);
            }

            stream.Position = 0;

            Assert.That(Mp3DurationReader.TryGetDuration(stream, out _), Is.False);
        }

        [Test]
        public void should_reject_constant_prefix_vbr_without_frame_count_header()
        {
            var stream = new MemoryStream();

            var firstFrame = Frame(56, 182);
            stream.Write(firstFrame, 0, firstFrame.Length);

            for (var i = 0; i < 20; i++)
            {
                var frame = Frame(64, 208);
                stream.Write(frame, 0, frame.Length);
            }

            for (var i = 0; i < 5000; i++)
            {
                var frame = Frame(80, FrameLength(80));
                stream.Write(frame, 0, frame.Length);
            }

            stream.Position = 0;

            Assert.That(Mp3DurationReader.TryGetDuration(stream, out _), Is.False);
        }

        [Test]
        public void should_override_existing_duration_only_for_exact_headers_or_meaningful_stable_cbr_disagreement()
        {
            Assert.That(Mp3DurationReader.ShouldUseDuration(TimeSpan.Zero, TimeSpan.FromMinutes(10), Mp3DurationReader.DurationSource.StableCbrEstimate), Is.True);
            Assert.That(Mp3DurationReader.ShouldUseDuration(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10.1), Mp3DurationReader.DurationSource.XingInfoHeader), Is.True);
            Assert.That(Mp3DurationReader.ShouldUseDuration(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10.1), Mp3DurationReader.DurationSource.VbriHeader), Is.True);
            Assert.That(Mp3DurationReader.ShouldUseDuration(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(629), Mp3DurationReader.DurationSource.StableCbrEstimate), Is.False);
            Assert.That(Mp3DurationReader.ShouldUseDuration(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(631), Mp3DurationReader.DurationSource.StableCbrEstimate), Is.True);
            Assert.That(Mp3DurationReader.ShouldUseDuration(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(91.1), Mp3DurationReader.DurationSource.StableCbrEstimate), Is.True);
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
                80 => 9,
                96 => 10,
                _ => throw new ArgumentOutOfRangeException(nameof(bitrateKbps), bitrateKbps, null)
            };

            const uint sync = 0x7FF;
            const uint version = 0x2; // MPEG 2
            const uint layer = 0x1; // Layer III
            const uint protection = 0x1; // no CRC
            const uint sampleRate = 0x0; // 22050 for MPEG 2
            const uint channelMode = 0x1; // joint stereo

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

        private static int FrameLength(int bitrateKbps)
        {
            return 72 * bitrateKbps * 1000 / 22050;
        }

        private static void WriteId3v2Header(Stream stream, int tagSize)
        {
            var header = new byte[10];
            header[0] = (byte)'I';
            header[1] = (byte)'D';
            header[2] = (byte)'3';
            header[3] = 3;
            header[6] = (byte)((tagSize >> 21) & 0x7F);
            header[7] = (byte)((tagSize >> 14) & 0x7F);
            header[8] = (byte)((tagSize >> 7) & 0x7F);
            header[9] = (byte)(tagSize & 0x7F);
            stream.Write(header, 0, header.Length);
        }

        private static void WriteAscii(byte[] buffer, int offset, string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                buffer[offset + i] = (byte)value[i];
            }
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
