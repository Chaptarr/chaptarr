using System;
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
