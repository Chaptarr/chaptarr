using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMyAnonaMouseAccountSummaryThrottle
    {
        Task<MyAnonaMouseAccountStatus> SendAsync(MamConnection connection, Func<Task<MyAnonaMouseAccountStatus>> send);
    }

    public class MyAnonaMouseAccountSummaryThrottle : IMyAnonaMouseAccountSummaryThrottle
    {
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly TimeProvider _clock;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly Logger _logger;
        private long? _lastAttempt;
        private MamConnection _lastConnection;
        private MyAnonaMouseAccountStatus _lastStatus;

        public MyAnonaMouseAccountSummaryThrottle(Logger logger)
            : this(TimeProvider.System, Task.Delay, logger)
        {
        }

        internal MyAnonaMouseAccountSummaryThrottle(TimeProvider clock, Func<TimeSpan, Task> delay, Logger logger = null)
        {
            _clock = clock;
            _delay = delay;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
        }

        public async Task<MyAnonaMouseAccountStatus> SendAsync(MamConnection connection, Func<Task<MyAnonaMouseAccountStatus>> send)
        {
            await _gate.WaitAsync();
            try
            {
                while (_lastAttempt is { } lastAttempt)
                {
                    var remaining = Cooldown - _clock.GetElapsedTime(lastAttempt);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    if (_lastStatus != null &&
                        (connection == _lastConnection ||
                         (!string.IsNullOrWhiteSpace(_lastStatus.RotatedMamId) &&
                          connection == _lastConnection with { MamId = _lastStatus.RotatedMamId })))
                    {
                        return _lastStatus;
                    }

                    _logger.Debug("Waiting {0}s for MyAnonaMouse's one-a-minute account check", Math.Ceiling(remaining.TotalSeconds));
                    await _delay(remaining);
                }

                _lastConnection = connection;
                _lastStatus = null;
                try
                {
                    _lastStatus = await send();
                    return _lastStatus;
                }
                finally
                {
                    // Measure from completion so time spent reaching MAM cannot shorten the gap.
                    _lastAttempt = _clock.GetTimestamp();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
