using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public static class MediaInfoFormatter
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(MediaInfoFormatter));

        public static string FormatAudioBitrate(MediaInfoModel mediaInfo)
        {
            return mediaInfo.AudioBitrate + " kbps";
        }

        public static string FormatAudioBitsPerSample(MediaInfoModel mediaInfo)
        {
            if (mediaInfo.AudioBits == 0)
            {
                return string.Empty;
            }

            return mediaInfo.AudioBits + "bit";
        }

        public static string FormatAudioSampleRate(MediaInfoModel mediaInfo)
        {
            return $"{(double)mediaInfo.AudioSampleRate / 1000:0.#}kHz";
        }

        public static decimal FormatAudioChannels(MediaInfoModel mediaInfo)
        {
            return mediaInfo.AudioChannels;
        }

        public static string FormatDuration(MediaInfoModel mediaInfo)
        {
            if (mediaInfo.Duration == TimeSpan.Zero)
            {
                return string.Empty;
            }

            if (mediaInfo.Duration.TotalHours >= 1)
            {
                return mediaInfo.Duration.ToString(@"h\:mm\:ss");
            }

            return mediaInfo.Duration.ToString(@"m\:ss");
        }

        public static readonly Dictionary<Codec, string> CodecNames = new Dictionary<Codec, string>
        {
            { Codec.MP1, "MP1" },
            { Codec.MP2, "MP2" },
            { Codec.AAC, "AAC" },
            { Codec.AACVBR, "AAC" },
            { Codec.ALAC, "ALAC" },
            { Codec.APE, "APE" },
            { Codec.FLAC, "FLAC" },
            { Codec.MP3CBR, "MP3" },
            { Codec.MP3VBR, "MP3" },
            { Codec.OGG, "OGG" },
            { Codec.OPUS, "OPUS" },
            { Codec.WAV, "PCM" },
            { Codec.WAVPACK, "WavPack" },
            { Codec.WMA, "WMA" }
        };

        public static string FormatAudioCodec(MediaInfoModel mediaInfo)
        {
            // Handle null or empty audio format
            if (string.IsNullOrWhiteSpace(mediaInfo?.AudioFormat))
            {
                Logger.Trace("Audio format is empty or null, skipping codec parsing");
                return "Unknown";
            }

            var codec = QualityParser.ParseCodec(mediaInfo.AudioFormat, null);

            if (CodecNames.ContainsKey(codec))
            {
                return CodecNames[codec];
            }
            else
            {
                Logger.ForDebugEvent()
                    .Message("Unknown audio format: '{0}'.", mediaInfo.AudioFormat)
                    .WriteSentryWarn("UnknownAudioFormat", mediaInfo.AudioFormat)
                    .Log();

                return "Unknown";
            }
        }
    }
}
