using System;
using NzbDrone.Core.Download;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOldDownloadClientFileSnapshots : IHousekeepingTask
    {
        private readonly IDownloadClientFileSnapshotRepository _repository;

        public CleanupOldDownloadClientFileSnapshots(IDownloadClientFileSnapshotRepository repository)
        {
            _repository = repository;
        }

        public void Clean()
        {
            _repository.DeleteOlderThan(DateTime.UtcNow.AddDays(-14));
        }
    }
}
