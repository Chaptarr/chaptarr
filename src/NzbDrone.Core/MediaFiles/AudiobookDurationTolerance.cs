using System;

namespace NzbDrone.Core.MediaFiles
{
    public static class AudiobookDurationTolerance
    {
        private const double MatchingPercent = 0.02d;
        private const double ConversionValidationPercent = 0.03d;
        private static readonly TimeSpan FiveMinuteFloor = TimeSpan.FromMinutes(5);

        public static TimeSpan ForConversionValidation(TimeSpan expectedDuration)
        {
            return GreaterOfPercentOrFloor(expectedDuration, ConversionValidationPercent, FiveMinuteFloor);
        }

        public static int ForMatchingSeconds(int referenceDurationSeconds)
        {
            var tolerance = GreaterOfPercentOrFloor(TimeSpan.FromSeconds(referenceDurationSeconds), MatchingPercent, FiveMinuteFloor);
            return (int)Math.Round(tolerance.TotalSeconds, MidpointRounding.AwayFromZero);
        }

        private static TimeSpan GreaterOfPercentOrFloor(TimeSpan referenceDuration, double percent, TimeSpan floor)
        {
            if (referenceDuration <= TimeSpan.Zero)
            {
                return floor > TimeSpan.Zero ? floor : TimeSpan.Zero;
            }

            var percentTolerance = TimeSpan.FromTicks((long)Math.Round(referenceDuration.Ticks * percent, MidpointRounding.AwayFromZero));
            return percentTolerance > floor ? percentTolerance : floor;
        }
    }
}
