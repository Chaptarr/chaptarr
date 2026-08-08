using System;
using System.Threading;
using NLog;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Jobs
{
    // Scheduler-only class to periodically enqueue ProcessPendingImportsCommand.
    // The actual execution is handled by Books/Commands/ProcessPendingImportsCommandHandler.
    public class PendingImportsScheduler : IHandle<ApplicationStartedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;
        private Timer _timer;

        public PendingImportsScheduler(
            IManageCommandQueue commandQueueManager,
            Logger logger)
        {
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            // Start processing pending imports with a timer that runs every 5 minutes
            _logger.Debug("[AUTHOR-PENDING] Starting periodic scheduler (every 5 minutes)");

            // Initial delay of 1 minute to let services initialize
            _timer = new Timer(_ =>
            {
                try
                {
                    _commandQueueManager.Push(new ProcessPendingImportsCommand());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[AUTHOR-PENDING] Error scheduling pending import command");
                }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }
    }
}
