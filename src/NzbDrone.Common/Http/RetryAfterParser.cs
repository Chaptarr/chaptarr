using System;
using System.Net.Http.Headers;

namespace NzbDrone.Common.Http
{
    public static class RetryAfterParser
    {
        // Parsing is shared; retry bounds and zero-delay policy belong to the caller.
        public static TimeSpan? GetDelay(HttpResponse response)
        {
            if (response?.Headers == null || !response.Headers.ContainsKey("Retry-After") ||
                !RetryConditionHeaderValue.TryParse(response.Headers["Retry-After"], out var value))
            {
                return null;
            }

            var delay = value.Delta ?? value.Date.Value - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
    }
}
