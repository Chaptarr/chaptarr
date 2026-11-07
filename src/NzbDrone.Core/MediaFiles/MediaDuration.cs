using System;
using System.IO;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public static class MediaDuration
    {
        public static bool HasDuration(int? durationSeconds)
        {
            return durationSeconds.HasValue && durationSeconds.Value > 0;
        }

        public static int? FromTimeSpan(TimeSpan duration)
        {
            return duration > TimeSpan.Zero ? (int?)Math.Round(duration.TotalSeconds) : null;
        }

        public static int? GetStoredDurationSeconds(BookFile file)
        {
            if (HasDuration(file?.DurationSeconds))
            {
                return file.DurationSeconds;
            }

            return FromTimeSpan(file?.MediaInfo?.Duration ?? TimeSpan.Zero);
        }

        public static MediaInfoModel CreateMediaInfo(int? durationSeconds)
        {
            return new MediaInfoModel
            {
                Duration = HasDuration(durationSeconds) ? TimeSpan.FromSeconds(durationSeconds.Value) : TimeSpan.Zero
            };
        }

        public static MediaInfoModel CreateMediaInfo(IMediaInfoExtractor extractor, string path, int? durationSeconds)
        {
            var mediaInfo = !string.IsNullOrWhiteSpace(path) && MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(path))
                ? extractor?.ExtractMediaInfo(path)
                : null;

            return ApplyToMediaInfo(mediaInfo, durationSeconds);
        }

        public static MediaInfoModel MergeMediaInfo(MediaInfoModel current, MediaInfoModel extracted)
        {
            return new MediaInfoModel
            {
                AudioFormat = current != null &&
                              !string.IsNullOrWhiteSpace(current.AudioFormat) &&
                              !string.Equals(current.AudioFormat, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? current.AudioFormat
                    : extracted?.AudioFormat,
                AudioBitrate = current != null && current.AudioBitrate > 0 ? current.AudioBitrate : extracted?.AudioBitrate ?? 0,
                AudioChannels = current != null && current.AudioChannels > 0 ? current.AudioChannels : extracted?.AudioChannels ?? 0,
                AudioBits = current != null && current.AudioBits > 0 ? current.AudioBits : extracted?.AudioBits ?? 0,
                AudioSampleRate = current != null && current.AudioSampleRate > 0 ? current.AudioSampleRate : extracted?.AudioSampleRate ?? 0,
                Duration = current != null && current.Duration > TimeSpan.Zero
                    ? current.Duration
                    : extracted?.Duration ?? TimeSpan.Zero
            };
        }

        public static MediaInfoModel ApplyToMediaInfo(MediaInfoModel mediaInfo, int? durationSeconds)
        {
            mediaInfo ??= new MediaInfoModel();

            if (HasDuration(durationSeconds) && mediaInfo.Duration <= TimeSpan.Zero)
            {
                mediaInfo.Duration = TimeSpan.FromSeconds(durationSeconds.Value);
            }

            return mediaInfo;
        }
    }
}
