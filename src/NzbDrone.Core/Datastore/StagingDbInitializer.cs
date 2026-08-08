using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Datastore
{
    public class StagingDbInitializer : IHandle<ApplicationStartedEvent>
    {
        private readonly IStagingDbContext _stagingDbContext;
        private readonly StagingResidualQueueSweeper _stagingResidualQueueSweeper;
        private readonly Logger _logger;

        public StagingDbInitializer(
            IStagingDbContext stagingDbContext,
            StagingResidualQueueSweeper stagingResidualQueueSweeper,
            Logger logger)
        {
            _stagingDbContext = stagingDbContext;
            _stagingResidualQueueSweeper = stagingResidualQueueSweeper;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Info("Initializing staging database on application start");
            _stagingDbContext.InitializeDatabase();
            _stagingResidualQueueSweeper.SweepAllResidualItems();
        }
    }
}
