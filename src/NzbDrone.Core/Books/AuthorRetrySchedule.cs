using System;

namespace NzbDrone.Core.Books
{
    internal static class AuthorRetrySchedule
    {
        // Pending author imports keep the original cadence for roughly the first hour
        // (three one-minute retries, then five-minute retries), then double the delay up
        // to this ceiling. A request the metadata server never fulfils still retries
        // forever, but a few times a day instead of 288 attempts (each a full author
        // refresh) a day.
        internal const int PendingImportFastAttempts = 15;
        internal const double PendingImportMaxDelaySeconds = 6 * 60 * 60;

        public static bool ShouldWarn(int attemptCount) => attemptCount == 1 || attemptCount == 4 || attemptCount % 10 == 0;

        public static DateTime NextAttempt(int attemptCount)
        {
            // Shared with pending author requests: three short retries, then a bounded
            // five-minute delay. Jitter keeps a recovered server from receiving a burst.
            var seconds = attemptCount <= 3 ? 60 : 300;
            return WithJitter(seconds);
        }

        public static DateTime NextPendingImportAttempt(int attemptCount)
        {
            return WithJitter(PendingImportDelaySeconds(attemptCount));
        }

        internal static double PendingImportDelaySeconds(int attemptCount)
        {
            if (attemptCount <= 3)
            {
                return 60;
            }

            if (attemptCount <= PendingImportFastAttempts)
            {
                return 300;
            }

            // Cap the exponent so very large legacy attempt counts cannot overflow.
            var doublings = Math.Min(attemptCount - PendingImportFastAttempts, 16);
            return Math.Min(300 * Math.Pow(2, doublings), PendingImportMaxDelaySeconds);
        }

        private static DateTime WithJitter(double seconds)
        {
            return DateTime.UtcNow.AddSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
        }
    }
}
