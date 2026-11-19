using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class NarratorRepository : BasicRepository<Narrator>, INarratorRepository
    {
        public NarratorRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override SqlBuilder Builder() => new SqlBuilder(_database.DatabaseType)
            .Join<Narrator, NarratorMetadata>((n, m) => n.NarratorMetadataId == m.Id);

        public List<Narrator> FindByCleanNames(IEnumerable<string> cleanNames)
        {
            if (cleanNames == null)
            {
                return new List<Narrator>();
            }

            var names = cleanNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim().ToLowerInvariant())
                .Distinct()
                .ToArray();

            if (names.Length == 0)
            {
                return new List<Narrator>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && names.Length > SqliteVariableLimit.MaxParameters)
            {
                var narrators = new List<Narrator>();
                foreach (var batch in names.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchNames = batch.ToArray();
                    narrators.AddRange(Query(n => Enumerable.Contains(batchNames, n.CleanName)));
                }

                return narrators.DistinctBy(n => n.Id).ToList();
            }

            return Query(n => Enumerable.Contains(names, n.CleanName)).ToList();
        }

        public List<Narrator> GetNarratorsByMetadataId(IEnumerable<int> narratorMetadataIds)
        {
            if (narratorMetadataIds == null)
            {
                return new List<Narrator>();
            }

            var ids = narratorMetadataIds.Distinct().ToArray();
            if (ids.Length == 0)
            {
                return new List<Narrator>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && ids.Length > SqliteVariableLimit.MaxParameters)
            {
                var narrators = new List<Narrator>();
                foreach (var batch in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    narrators.AddRange(Query(n => Enumerable.Contains(batchIds, n.NarratorMetadataId)));
                }

                return narrators.DistinctBy(n => n.Id).ToList();
            }

            return Query(n => Enumerable.Contains(ids, n.NarratorMetadataId)).ToList();
        }
    }
}
