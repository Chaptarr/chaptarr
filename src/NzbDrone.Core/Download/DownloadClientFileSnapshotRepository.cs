using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dapper;
using NLog;
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
        void RemoveMissingDownloads(int downloadClientId, IEnumerable<string> retainedDownloadIds);
        void DeleteOrphanedClients();
    }

    public class DownloadClientFileSnapshotRepository : BasicRepository<DownloadClientFileSnapshot>, IDownloadClientFileSnapshotRepository
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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

        public void RemoveMissingDownloads(int downloadClientId, IEnumerable<string> retainedDownloadIds)
        {
            var retained = retainedDownloadIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var timer = Stopwatch.StartNew();
            using var connection = _database.OpenConnection();
            var snapshots = connection.Query<(int Id, string DownloadId)>(
                "SELECT \"Id\", \"DownloadId\" FROM \"DownloadClientFileSnapshots\" WHERE \"DownloadClientId\" = @downloadClientId",
                new { downloadClientId }).ToList();
            var removed = snapshots.Where(snapshot => !retained.Contains(snapshot.DownloadId)).Select(snapshot => snapshot.Id).ToList();
            DeleteMany(removed);
            Logger.Debug("Download file inventory reconciliation: client={0}, retained={1}, snapshots={2}, deleted={3}, elapsedMs={4}",
                downloadClientId, retained.Count, snapshots.Count, removed.Count, timer.ElapsedMilliseconds);
        }

        public void DeleteOrphanedClients()
        {
            using var connection = _database.OpenConnection();
            connection.Execute("DELETE FROM \"DownloadClientFileSnapshots\" WHERE NOT EXISTS (SELECT 1 FROM \"DownloadClients\" WHERE \"DownloadClients\".\"Id\" = \"DownloadClientFileSnapshots\".\"DownloadClientId\")");
        }
    }
}
