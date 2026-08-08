using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists.Exclusions
{
    public interface IImportListExclusionRepository : IBasicRepository<ImportListExclusion>
    {
        ImportListExclusion FindByForeignId(string foreignId);
        List<ImportListExclusion> FindByForeignId(List<string> ids);
    }

    public class ImportListExclusionRepository : BasicRepository<ImportListExclusion>, IImportListExclusionRepository
    {
        public ImportListExclusionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public ImportListExclusion FindByForeignId(string foreignId)
        {
            return Query(m => m.ForeignId == foreignId).SingleOrDefault();
        }

        public List<ImportListExclusion> FindByForeignId(List<string> ids)
        {
            // Using Enumerable.Contains forces the builder to create an 'IN'
            // and not a string 'LIKE' expression
            if (ids == null || ids.Count == 0)
            {
                return new List<ImportListExclusion>();
            }

            var foreignIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
            if (foreignIds.Length == 0)
            {
                return new List<ImportListExclusion>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && foreignIds.Length > SqliteVariableLimit.MaxParameters)
            {
                var exclusions = new List<ImportListExclusion>();
                foreach (var batch in foreignIds.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    exclusions.AddRange(Query(x => Enumerable.Contains(batchIds, x.ForeignId)));
                }

                return exclusions.DistinctBy(e => e.Id).ToList();
            }

            return Query(x => Enumerable.Contains(foreignIds, x.ForeignId));
        }
    }
}
