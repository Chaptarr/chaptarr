using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    internal static class Mp3DurationReader
    {
        private const int MaxFrameSearchBytes = 1024 * 1024;
        private const int MaxHeaderScanBytes = 256;
        private const int MaxSampleFrames = 64;
        private const int MinStableSampleFrames = 4;
        private const double StableCbrOverrideRatio = 0.05;
        private static readonly TimeSpan StableCbrOverrideMinimumDifference = TimeSpan.FromSeconds(60);

        internal enum DurationSource
        {
            None = 0,
            XingInfoHeader = 1,
            VbriHeader = 2,
            StableCbrEstimate = 3
        }

        internal readonly struct Mp3AudioInfo
        {
            public Mp3AudioInfo(TimeSpan duration, DurationSource source, int bitrateKbps, int sampleRate, int channels, string description)
            {
                Duration = duration;
                Source = source;
                BitrateKbps = bitrateKbps;
                SampleRate = sampleRate;
                Channels = channels;
                Description = description;
            }

            public TimeSpan Duration { get; }
            public DurationSource Source { get; }
            public int BitrateKbps { get; }
            public int SampleRate { get; }
            public int Channels { get; }
            public string Description { get; }
        }

        private static readonly int[] Mpeg1Layer1Bitrates = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 };
        private static readonly int[] Mpeg1Layer2Bitrates = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };
        private static readonly int[] Mpeg1Layer3Bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] Mpeg2Layer1Bitrates = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 };
        private static readonly int[] Mpeg2Layer23Bitrates = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

        public static bool TryGetDuration(string path, out TimeSpan duration)
        {
            return TryGetDuration(path, out duration, out _);
        }

        public static bool TryGetDuration(string path, out TimeSpan duration, out DurationSource source)
        {
            duration = TimeSpan.Zero;
            source = DurationSource.None;

            if (TryGetInfo(path, out var info))
            {
                duration = info.Duration;
                source = info.Source;
                return true;
            }

            return false;
        }

        internal static bool TryGetInfo(string path, out Mp3AudioInfo info)
        {
            info = default;

            if (!IsMp3Path(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return TryGetInfo(stream, out info);
            }
            catch
            {
                info = default;
                return false;
            }
        }

        public static bool TryGetDurationSeconds(string path, out int durationSeconds)
        {
            return TryGetDurationSeconds(path, out durationSeconds, out _);
        }

        public static bool TryGetDurationSeconds(string path, out int durationSeconds, out DurationSource source)
        {
            durationSeconds = 0;
            source = DurationSource.None;

            if (!TryGetDuration(path, out var duration, out source) || duration <= TimeSpan.Zero)
            {
                return false;
            }

            durationSeconds = (int)Math.Round(duration.TotalSeconds);
            return durationSeconds > 0;
        }

        public static bool IsMp3Path(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldUseDuration(TimeSpan existingDuration, TimeSpan candidateDuration, DurationSource source)
        {
            if (candidateDuration <= TimeSpan.Zero)
            {
                return false;
            }

            if (existingDuration <= TimeSpan.Zero || IsHeaderDerived(source))
            {
                return true;
            }

            return source == DurationSource.StableCbrEstimate &&
                   HasMeaningfulDurationDisagreement(existingDuration, candidateDuration);
        }

        public static bool ShouldUseDuration(int? existingDurationSeconds, int candidateDurationSeconds, DurationSource source)
        {
            var existingDuration = existingDurationSeconds.HasValue && existingDurationSeconds.Value > 0
                ? TimeSpan.FromSeconds(existingDurationSeconds.Value)
                : TimeSpan.Zero;

            return ShouldUseDuration(existingDuration, TimeSpan.FromSeconds(candidateDurationSeconds), source);
        }

        private static bool IsHeaderDerived(DurationSource source)
        {
            return source == DurationSource.XingInfoHeader ||
                   source == DurationSource.VbriHeader;
        }

        private static bool HasMeaningfulDurationDisagreement(TimeSpan existingDuration, TimeSpan candidateDuration)
        {
            var difference = TimeSpan.FromSeconds(Math.Abs((existingDuration - candidateDuration).TotalSeconds));
            if (difference > StableCbrOverrideMinimumDifference)
            {
                return true;
            }

            return existingDuration.TotalSeconds > 0 &&
                   difference.TotalSeconds / existingDuration.TotalSeconds > StableCbrOverrideRatio;
        }

        internal static bool TryGetDuration(Stream stream, out TimeSpan duration)
        {
            return TryGetDuration(stream, out duration, out _);
        }

        internal static bool TryGetDuration(Stream stream, out TimeSpan duration, out DurationSource source)
        {
            duration = TimeSpan.Zero;
            source = DurationSource.None;

            if (TryGetInfo(stream, out var info))
            {
                duration = info.Duration;
                source = info.Source;
                return true;
            }

            return false;
        }

        internal static bool TryGetInfo(Stream stream, out Mp3AudioInfo info)
        {
            info = default;

            if (stream == null || !stream.CanRead || !stream.CanSeek || stream.Length < 4)
            {
                return false;
            }

            var audioStart = SkipLeadingId3v2Tags(stream);
            if (!TryFindFrame(stream, audioStart, Math.Min(stream.Length - 4, audioStart + MaxFrameSearchBytes), out var firstFrameOffset, out var firstFrame))
            {
                return false;
            }

            TimeSpan duration;
            DurationSource source;

            if (TryReadHeaderDuration(stream, firstFrameOffset, firstFrame, out duration, out source))
            {
                info = BuildInfo(stream, firstFrameOffset, firstFrame, duration, source);
                return true;
            }

            if (TryEstimateStableCbrDuration(stream, firstFrameOffset, firstFrame, out duration))
            {
                source = DurationSource.StableCbrEstimate;
                info = BuildInfo(stream, firstFrameOffset, firstFrame, duration, source);
                return true;
            }

            return false;
        }

        private static Mp3AudioInfo BuildInfo(Stream stream, long firstFrameOffset, Mp3Frame frame, TimeSpan duration, DurationSource source)
        {
            return new Mp3AudioInfo(
                duration,
                source,
                EstimateAverageBitrateKbps(stream, firstFrameOffset, duration, frame.BitrateKbps),
                frame.SampleRate,
                frame.Channels,
                GetDescription(frame));
        }

        private static int EstimateAverageBitrateKbps(Stream stream, long firstFrameOffset, TimeSpan duration, int fallbackBitrateKbps)
        {
            if (stream == null || duration <= TimeSpan.Zero)
            {
                return fallbackBitrateKbps;
            }

            var audioBytes = stream.Length - firstFrameOffset - GetTrailingTagBytes(stream);
            if (audioBytes <= 0)
            {
                return fallbackBitrateKbps;
            }

            var average = (int)Math.Round(audioBytes * 8.0 / duration.TotalSeconds / 1000.0);
            return average > 0 ? average : fallbackBitrateKbps;
        }

        private static string GetDescription(Mp3Frame frame)
        {
            var version = frame.VersionBits switch
            {
                3 => "1",
                2 => "2",
                0 => "2.5",
                _ => "?"
            };

            var layer = frame.LayerBits switch
            {
                3 => "1",
                2 => "2",
                1 => "3",
                _ => "?"
            };

            return $"MPEG Version {version} Audio, Layer {layer}";
        }

        private static long SkipLeadingId3v2Tags(Stream stream)
        {
            var offset = 0L;
            var header = new byte[10];

            while (offset + header.Length <= stream.Length)
            {
                stream.Position = offset;
                if (stream.Read(header, 0, header.Length) != header.Length ||
                    header[0] != (byte)'I' ||
                    header[1] != (byte)'D' ||
                    header[2] != (byte)'3')
                {
                    break;
                }

                var size = ReadSyncSafeInt(header, 6);
                if (size < 0)
                {
                    break;
                }

                var footerSize = (header[5] & 0x10) != 0 ? 10 : 0;
                var next = offset + header.Length + size + footerSize;
                if (next <= offset || next > stream.Length)
                {
                    break;
                }

                offset = next;
            }

            return offset;
        }

        private static bool TryFindFrame(Stream stream, long start, long end, out long frameOffset, out Mp3Frame frame)
        {
            frameOffset = 0;
            frame = default;

            var header = new byte[4];
            var offset = Math.Max(0, start);
            end = Math.Min(end, stream.Length - 4);

            while (offset <= end)
            {
                stream.Position = offset;
                if (stream.Read(header, 0, header.Length) != header.Length)
                {
                    return false;
                }

                if (TryParseFrame(header, out frame))
                {
                    frameOffset = offset;
                    return true;
                }

                offset++;
            }

            return false;
        }

        private static bool TryReadHeaderDuration(Stream stream, long frameOffset, Mp3Frame frame, out TimeSpan duration, out DurationSource source)
        {
            duration = TimeSpan.Zero;
            source = DurationSource.None;

            var frameBytes = ReadFramePrefix(stream, frameOffset, frame);
            if (frameBytes.Length == 0)
            {
                return false;
            }

            if (TryReadXingOrInfoDuration(frameBytes, frame, out duration))
            {
                source = DurationSource.XingInfoHeader;
                return true;
            }

            if (TryReadVbriDuration(frameBytes, frame, out duration))
            {
                source = DurationSource.VbriHeader;
                return true;
            }

            return false;
        }

        private static byte[] ReadFramePrefix(Stream stream, long frameOffset, Mp3Frame frame)
        {
            var bytesToRead = Math.Min(Math.Min(frame.FrameLength, MaxHeaderScanBytes), stream.Length - frameOffset);
            if (bytesToRead <= 0)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[bytesToRead];
            stream.Position = frameOffset;
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == buffer.Length)
            {
                return buffer;
            }

            Array.Resize(ref buffer, read);
            return buffer;
        }

        private static bool TryReadXingOrInfoDuration(byte[] frameBytes, Mp3Frame frame, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            foreach (var marker in new[] { "Xing", "Info" })
            {
                var index = IndexOfAscii(frameBytes, marker);
                if (index < 0 || index + 12 > frameBytes.Length)
                {
                    continue;
                }

                var flags = ReadUInt32BigEndian(frameBytes, index + 4);
                if ((flags & 0x1) == 0)
                {
                    continue;
                }

                var frameCount = ReadUInt32BigEndian(frameBytes, index + 8);
                if (TryDurationFromFrameCount(frameCount, frame, out duration))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadVbriDuration(byte[] frameBytes, Mp3Frame frame, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            var index = IndexOfAscii(frameBytes, "VBRI");
            if (index < 0 || index + 18 > frameBytes.Length)
            {
                return false;
            }

            var frameCount = ReadUInt32BigEndian(frameBytes, index + 14);
            return TryDurationFromFrameCount(frameCount, frame, out duration);
        }

        private static bool TryEstimateStableCbrDuration(Stream stream, long firstFrameOffset, Mp3Frame firstFrame, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            var frames = new List<Mp3Frame> { firstFrame };
            var offset = firstFrameOffset + firstFrame.FrameLength;

            while (frames.Count < MaxSampleFrames && offset < stream.Length - 4)
            {
                if (!TryFindFrame(stream, offset, Math.Min(stream.Length - 4, offset + 2048), out var nextOffset, out var frame))
                {
                    break;
                }

                frames.Add(frame);
                offset = nextOffset + frame.FrameLength;
            }

            var sampledAudioFrames = frames.Skip(1).ToList();
            if (!TryGetStableCbrBitrate(sampledAudioFrames, firstFrame, out var bitrate))
            {
                return false;
            }

            if (!TryValidateTailCbrSample(stream, firstFrameOffset, firstFrame, bitrate))
            {
                return false;
            }

            var audioBytes = stream.Length - firstFrameOffset - GetTrailingTagBytes(stream);
            if (audioBytes <= 0)
            {
                return false;
            }

            duration = TimeSpan.FromSeconds(audioBytes * 8.0 / (bitrate * 1000.0));
            return duration > TimeSpan.Zero;
        }

        private static bool TryValidateTailCbrSample(Stream stream, long firstFrameOffset, Mp3Frame firstFrame, int expectedBitrate)
        {
            var trailingBytes = GetTrailingTagBytes(stream);
            var audioEnd = stream.Length - trailingBytes;
            var audioBytes = audioEnd - firstFrameOffset;

            if (audioBytes <= MaxFrameSearchBytes)
            {
                return true;
            }

            var tailStart = Math.Max(firstFrameOffset + firstFrame.FrameLength, audioEnd - MaxFrameSearchBytes);
            var tailFrames = new List<Mp3Frame>();
            var offset = tailStart;

            while (tailFrames.Count < MaxSampleFrames && offset < audioEnd - 4)
            {
                if (!TryFindFrame(stream, offset, audioEnd - 4, out var nextOffset, out var frame))
                {
                    break;
                }

                tailFrames.Add(frame);
                offset = nextOffset + frame.FrameLength;
            }

            return TryGetStableCbrBitrate(tailFrames, firstFrame, out var tailBitrate) &&
                   tailBitrate == expectedBitrate;
        }

        private static bool TryGetStableCbrBitrate(List<Mp3Frame> frames, Mp3Frame referenceFrame, out int bitrate)
        {
            bitrate = 0;

            if (frames == null || frames.Count < MinStableSampleFrames)
            {
                return false;
            }

            if (frames.Any(f => f.SampleRate != referenceFrame.SampleRate || f.SamplesPerFrame != referenceFrame.SamplesPerFrame))
            {
                return false;
            }

            var candidateBitrate = frames[0].BitrateKbps;
            if (candidateBitrate <= 0 || frames.Any(f => f.BitrateKbps != candidateBitrate))
            {
                return false;
            }

            bitrate = candidateBitrate;
            return true;
        }

        private static long GetTrailingTagBytes(Stream stream)
        {
            if (stream.Length < 128)
            {
                return 0;
            }

            var marker = new byte[3];
            stream.Position = stream.Length - 128;
            return stream.Read(marker, 0, marker.Length) == marker.Length &&
                   marker[0] == (byte)'T' &&
                   marker[1] == (byte)'A' &&
                   marker[2] == (byte)'G'
                ? 128
                : 0;
        }

        private static bool TryDurationFromFrameCount(uint frameCount, Mp3Frame frame, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            if (frameCount == 0 || frame.SampleRate <= 0 || frame.SamplesPerFrame <= 0)
            {
                return false;
            }

            duration = TimeSpan.FromSeconds(frameCount * frame.SamplesPerFrame / (double)frame.SampleRate);
            return duration > TimeSpan.Zero;
        }

        private static bool TryParseFrame(byte[] header, out Mp3Frame frame)
        {
            frame = default;

            if (header == null || header.Length < 4)
            {
                return false;
            }

            var raw = (uint)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);
            if (((raw >> 21) & 0x7FF) != 0x7FF)
            {
                return false;
            }

            var versionBits = (int)((raw >> 19) & 0x3);
            var layerBits = (int)((raw >> 17) & 0x3);
            var bitrateIndex = (int)((raw >> 12) & 0xF);
            var sampleRateIndex = (int)((raw >> 10) & 0x3);
            var padding = (int)((raw >> 9) & 0x1);

            if (versionBits == 1 || layerBits == 0 || bitrateIndex == 0 || bitrateIndex == 15 || sampleRateIndex == 3)
            {
                return false;
            }

            var sampleRate = GetSampleRate(versionBits, sampleRateIndex);
            var bitrate = GetBitrate(versionBits, layerBits, bitrateIndex);
            if (sampleRate <= 0 || bitrate <= 0)
            {
                return false;
            }

            var samplesPerFrame = GetSamplesPerFrame(versionBits, layerBits);
            var frameLength = GetFrameLength(versionBits, layerBits, bitrate, sampleRate, padding);
            if (samplesPerFrame <= 0 || frameLength <= 4)
            {
                return false;
            }

            var channelModeBits = (int)((raw >> 6) & 0x3);
            var channels = channelModeBits == 3 ? 1 : 2;

            frame = new Mp3Frame(bitrate, sampleRate, samplesPerFrame, frameLength, channels, versionBits, layerBits);
            return true;
        }

        private static int GetBitrate(int versionBits, int layerBits, int bitrateIndex)
        {
            var isMpeg1 = versionBits == 3;
            return layerBits switch
            {
                3 => (isMpeg1 ? Mpeg1Layer1Bitrates : Mpeg2Layer1Bitrates)[bitrateIndex],
                2 => (isMpeg1 ? Mpeg1Layer2Bitrates : Mpeg2Layer23Bitrates)[bitrateIndex],
                1 => (isMpeg1 ? Mpeg1Layer3Bitrates : Mpeg2Layer23Bitrates)[bitrateIndex],
                _ => 0
            };
        }

        private static int GetSampleRate(int versionBits, int sampleRateIndex)
        {
            var baseRate = sampleRateIndex switch
            {
                0 => 44100,
                1 => 48000,
                2 => 32000,
                _ => 0
            };

            return versionBits switch
            {
                3 => baseRate,
                2 => baseRate / 2,
                0 => baseRate / 4,
                _ => 0
            };
        }

        private static int GetSamplesPerFrame(int versionBits, int layerBits)
        {
            return layerBits switch
            {
                3 => 384,
                2 => 1152,
                1 => versionBits == 3 ? 1152 : 576,
                _ => 0
            };
        }

        private static int GetFrameLength(int versionBits, int layerBits, int bitrateKbps, int sampleRate, int padding)
        {
            if (layerBits == 3)
            {
                return ((12 * bitrateKbps * 1000 / sampleRate) + padding) * 4;
            }

            if (layerBits == 1 && versionBits != 3)
            {
                return (72 * bitrateKbps * 1000 / sampleRate) + padding;
            }

            return (144 * bitrateKbps * 1000 / sampleRate) + padding;
        }

        private static int ReadSyncSafeInt(byte[] bytes, int offset)
        {
            if (bytes == null || offset + 4 > bytes.Length)
            {
                return -1;
            }

            return ((bytes[offset] & 0x7F) << 21) |
                   ((bytes[offset + 1] & 0x7F) << 14) |
                   ((bytes[offset + 2] & 0x7F) << 7) |
                   (bytes[offset + 3] & 0x7F);
        }

        private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return (uint)((bytes[offset] << 24) |
                          (bytes[offset + 1] << 16) |
                          (bytes[offset + 2] << 8) |
                          bytes[offset + 3]);
        }

        private static int IndexOfAscii(byte[] bytes, string text)
        {
            if (bytes == null || string.IsNullOrEmpty(text) || bytes.Length < text.Length)
            {
                return -1;
            }

            for (var i = 0; i <= bytes.Length - text.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < text.Length; j++)
                {
                    if (bytes[i + j] != (byte)text[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return i;
                }
            }

            return -1;
        }

        private readonly struct Mp3Frame
        {
            public Mp3Frame(int bitrateKbps, int sampleRate, int samplesPerFrame, int frameLength, int channels, int versionBits, int layerBits)
            {
                BitrateKbps = bitrateKbps;
                SampleRate = sampleRate;
                SamplesPerFrame = samplesPerFrame;
                FrameLength = frameLength;
                Channels = channels;
                VersionBits = versionBits;
                LayerBits = layerBits;
            }

            public int BitrateKbps { get; }
            public int SampleRate { get; }
            public int SamplesPerFrame { get; }
            public int FrameLength { get; }
            public int Channels { get; }
            public int VersionBits { get; }
            public int LayerBits { get; }
        }
    }
}
