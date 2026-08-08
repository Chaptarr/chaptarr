using System;
using NLog;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupCompletedStagingQueue : IHousekeepingTask
    {
        private readonly IIngestQueueRepository _ingestQueue;
        private readonly IFileTagCacheRepository _fileTagCache;
        private readonly Logger _logger;

        public CleanupCompletedStagingQueue(IIngestQueueRepository ingestQueue, IFileTagCacheRepository fileTagCache, Logger logger)
        {
            _ingestQueue = ingestQueue;
            _fileTagCache = fileTagCache;
            _logger = logger;
        }

        public void Clean()
        {
            _logger.Debug("Cleaning up old completed staging queue items");
            
            // Keep completed items for 14 days for troubleshooting
            _ingestQueue.PurgeOldCompleted(daysToKeep: 14);

            // Download/manual-import tag scans are useful across retries, but should not grow forever.
            _fileTagCache.PurgeOld(daysToKeep: 30);
        }
    }
}
