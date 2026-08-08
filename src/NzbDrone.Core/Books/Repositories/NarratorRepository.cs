using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Common.Serializer;
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

        public bool NarratorPathExists(string path)
        {
            return Query(n => n.Path == path).Any();
        }

        public Narrator FindByName(string cleanName)
        {
            cleanName = cleanName.ToLowerInvariant();

            return Query(n => n.CleanName == cleanName)
                   .SingleOrDefault();
        }

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

        public Narrator FindById(string foreignNarratorId)
        {
            return Query(Builder().Where<NarratorMetadata>(n => n.Id.ToString() == foreignNarratorId)).SingleOrDefault();
        }

        public Narrator FindByNarratorTitleSlug(string narratorTitleSlug)
        {
            return Query(Builder().Where<NarratorMetadata>(n => n.TitleSlug == narratorTitleSlug)).SingleOrDefault();
        }

        public Dictionary<int, string> AllNarratorPaths()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\", \"Path\" FROM \"Narrators\" WHERE \"Path\" IS NOT NULL";
                return conn.Query<(int id, string path)>(strSql).ToDictionary(x => x.id, x => x.path);
            }
        }

        public Dictionary<int, List<int>> AllNarratorTags()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\", \"Tags\" FROM \"Narrators\" WHERE \"Tags\" IS NOT NULL";
                return conn.Query<(int id, string tags)>(strSql).ToDictionary(x => x.id, x => Json.Deserialize<List<int>>(x.tags));
            }
        }

        public Narrator GetNarratorByMetadataId(int narratorMetadataId)
        {
            return Query(n => n.NarratorMetadataId == narratorMetadataId).SingleOrDefault();
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
