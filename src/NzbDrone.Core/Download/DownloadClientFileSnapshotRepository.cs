using System;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public interface IDownloadClientFileSnapshotRepository : IBasicRepository<DownloadClientFileSnapshot>
    {
        DownloadClientFileSnapshot Find(int downloadClientId, string downloadId);
        void Delete(int downloadClientId, string downloadId);
        void DeleteForDownloadClient(int downloadClientId);
        void DeleteOlderThan(DateTime cutoff);
    }

    public class DownloadClientFileSnapshotRepository : BasicRepository<DownloadClientFileSnapshot>, IDownloadClientFileSnapshotRepository
    {
        public DownloadClientFileSnapshotRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public DownloadClientFileSnapshot Find(int downloadClientId, string downloadId)
        {
            if (downloadClientId <= 0 || downloadId.IsNullOrWhiteSpace())
            {
                return null;
            }

            return Query(s => s.DownloadClientId == downloadClientId && s.DownloadId == downloadId)
                .SingleOrDefault();
        }

        public void Delete(int downloadClientId, string downloadId)
        {
            if (downloadClientId <= 0 || downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            Delete(s => s.DownloadClientId == downloadClientId && s.DownloadId == downloadId);
        }

        public void DeleteForDownloadClient(int downloadClientId)
        {
            if (downloadClientId <= 0)
            {
                return;
            }

            Delete(s => s.DownloadClientId == downloadClientId);
        }

        public void DeleteOlderThan(DateTime cutoff)
        {
            Delete(s => s.LastUpdated < cutoff);
        }
    }
}
