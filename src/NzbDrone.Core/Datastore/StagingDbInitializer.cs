using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Datastore
{
    public class StagingDbInitializer : IHandle<ApplicationStartedEvent>
    {
        private readonly StagingResidualQueueSweeper _stagingResidualQueueSweeper;
        private readonly IFileTagCacheRepository _fileTagCache;
        private readonly IStagingDbContext _staging;
        private readonly Logger _logger;

        public StagingDbInitializer(
            StagingResidualQueueSweeper stagingResidualQueueSweeper,
            IFileTagCacheRepository fileTagCache,
            IStagingDbContext staging,
            Logger logger)
        {
            _stagingResidualQueueSweeper = stagingResidualQueueSweeper;
            _fileTagCache = fileTagCache;
            _staging = staging;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _fileTagCache.MoveFromStaging(_staging);
            _logger.Info("Checking staging queue residual items on application start");
            _stagingResidualQueueSweeper.SweepAllResidualItems();
        }
    }
}
