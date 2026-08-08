using System.Threading;

namespace NzbDrone.Common.EnvironmentInfo
{
    /// <summary>
    /// Process-wide marker set once a factory reset has begun. The reset drops the database
    /// schema while the process is still serving requests, so the HTTP pipeline consults this
    /// to answer 503 until the restart completes instead of executing queries against a wiped
    /// database. Never cleared: the process restarts to finish the reset.
    /// </summary>
    public static class FactoryResetState
    {
        private static int _isResetting;

        public static bool IsResetting => Volatile.Read(ref _isResetting) == 1;

        public static void MarkResetting()
        {
            Interlocked.Exchange(ref _isResetting, 1);
        }
    }
}
