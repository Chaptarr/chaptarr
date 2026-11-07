using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListSyncHistoryRepository : IBasicRepository<ImportListSyncHistory>
    {
        List<ImportListSyncHistory> GetForImportList(int importListId, int limit);
        void TrimRetention(int importListId);
    }

    public class ImportListSyncHistoryRepository : BasicRepository<ImportListSyncHistory>, IImportListSyncHistoryRepository
    {
        private const int PerListRetention = 100;
        private const int GlobalRetention = 5000;

        public ImportListSyncHistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<ImportListSyncHistory> GetForImportList(int importListId, int limit)
        {
            var query = Builder()
                .Where<ImportListSyncHistory>(history => history.ImportListId == importListId)
                .OrderBy("StartedAt DESC")
                .OrderBy("Id DESC");

            return Query(query).Take(limit).ToList();
        }

        public void TrimRetention(int importListId)
        {
            var perListIds = _database.Query<int>(
                "SELECT \"Id\" FROM \"ImportListSyncHistory\" WHERE \"ImportListId\" = @ImportListId ORDER BY \"StartedAt\" DESC, \"Id\" DESC LIMIT 2147483647 OFFSET @Retention",
                new { ImportListId = importListId, Retention = PerListRetention }).ToList();

            if (perListIds.Count > 0)
            {
                DeleteMany(perListIds);
            }

            var globalIds = _database.Query<int>(
                "SELECT \"Id\" FROM \"ImportListSyncHistory\" ORDER BY \"StartedAt\" DESC, \"Id\" DESC LIMIT 2147483647 OFFSET @Retention",
                new { Retention = GlobalRetention }).ToList();

            if (globalIds.Count > 0)
            {
                DeleteMany(globalIds);
            }
        }
    }
}
