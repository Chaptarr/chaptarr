using System;
using System.IO.Abstractions;

namespace NzbDrone.Core.MediaFiles
{
    internal static class MediaFileFreshness
    {
        private const long UnixEpochTicks = 621355968000000000L;

        internal static DateTime GetLastWriteUtc(IFileInfo fileInfo)
        {
            var utc = fileInfo.LastWriteTimeUtc;
            return utc == default ? fileInfo.LastWriteTime.ToUniversalTime() : utc;
        }

        internal static bool HasChanged(BookFile file, IFileInfo fileInfo)
        {
            if (file == null || fileInfo == null)
            {
                return true;
            }

            return HasChanged(file, fileInfo.Length, GetLastWriteUtc(fileInfo));
        }

        internal static bool HasChanged(BookFile file, long observedSize, DateTime observedModified)
        {
            if (file == null || observedModified == default)
            {
                return true;
            }

            if (file.Size != observedSize)
            {
                return true;
            }

            if (file.Modified == default)
            {
                return true;
            }

            return Math.Abs((file.Modified.ToUniversalTime() - observedModified.ToUniversalTime()).TotalSeconds) > 1;
        }

        internal static bool IsUnchanged(BookFile file, IFileInfo fileInfo)
        {
            return !HasChanged(file, fileInfo);
        }

        internal static bool IsUnchanged(BookFile file, long observedSize, DateTime observedModified)
        {
            return !HasChanged(file, observedSize, observedModified);
        }

        internal static DateTime FromUnixNanoseconds(long nanoseconds, DateTime fallback = default)
        {
            if (nanoseconds <= 0)
            {
                return fallback;
            }

            var ticksSinceEpoch = nanoseconds / 100;
            if (ticksSinceEpoch > DateTime.MaxValue.Ticks - UnixEpochTicks)
            {
                return fallback;
            }

            return new DateTime(ticksSinceEpoch + UnixEpochTicks, DateTimeKind.Utc);
        }

        internal static long ToUnixNanoseconds(DateTime value)
        {
            var ticksSinceEpoch = value.ToUniversalTime().Ticks - UnixEpochTicks;
            if (ticksSinceEpoch <= 0)
            {
                return 0;
            }

            return ticksSinceEpoch > long.MaxValue / 100
                ? long.MaxValue
                : ticksSinceEpoch * 100;
        }
    }
}
