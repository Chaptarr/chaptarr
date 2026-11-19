using NzbDrone.Core.Download;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedDownloadClientFileSnapshots : IHousekeepingTask
    {
        private readonly IDownloadClientFileSnapshotRepository _repository;

        public CleanupOrphanedDownloadClientFileSnapshots(IDownloadClientFileSnapshotRepository repository)
        {
            _repository = repository;
        }

        public void Clean()
        {
            _repository.DeleteOrphanedClients();
        }
    }
}
