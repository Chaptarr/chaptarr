using System;

namespace NzbDrone.Core.Books
{
    internal static class AuthorRetrySchedule
    {
        public static bool ShouldWarn(int attemptCount) => attemptCount == 1 || attemptCount == 4 || attemptCount % 10 == 0;

        public static DateTime NextAttempt(int attemptCount)
        {
            // Shared with pending author requests: three short retries, then a bounded
            // five-minute delay. Jitter keeps a recovered server from receiving a burst.
            var seconds = attemptCount <= 3 ? 60 : 300;
            return DateTime.UtcNow.AddSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
        }
    }
}
