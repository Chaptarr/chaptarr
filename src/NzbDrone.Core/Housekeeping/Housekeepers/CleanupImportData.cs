using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupImportData : IHousekeepingTask
    {
        private readonly IFileTagCacheRepository _fileTagCache;
        private readonly StagingResidualQueueSweeper _sweeper;
        private readonly IIngestQueueRepository _queue;
        private readonly IStagingDbContext _staging;

        public CleanupImportData(IFileTagCacheRepository fileTagCache, StagingResidualQueueSweeper sweeper,
            IIngestQueueRepository queue, IStagingDbContext staging)
        {
            _fileTagCache = fileTagCache;
            _sweeper = sweeper;
            _queue = queue;
            _staging = staging;
        }

        public void Clean()
        {
            _fileTagCache.PurgeOld(daysToKeep: 30);
            _sweeper.SweepAllResidualItems(completedOnly: true);
            if (_queue.GetActiveItems(limit: 1, includeCompleted: true).Count == 0)
            {
                new Database("staging", _staging.OpenConnection).Vacuum();
            }
        }
    }
}
