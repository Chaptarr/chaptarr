using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists.Exclusions
{
    public interface IImportListExclusionRepository : IBasicRepository<ImportListExclusion>
    {
        void DeleteMany(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction);
        List<ImportListExclusion> FindByForeignId(List<string> ids);
        List<ImportListExclusion> FindByForeignId(List<string> ids, IDbConnection connection, IDbTransaction transaction);
    }

    public class ImportListExclusionRepository : BasicRepository<ImportListExclusion>, IImportListExclusionRepository
    {
        public ImportListExclusionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public new void DeleteMany(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction) =>
            base.DeleteMany(ids, connection, transaction);

        public List<ImportListExclusion> FindByForeignId(List<string> ids) => FindByForeignId(ids, null, null);

        public List<ImportListExclusion> FindByForeignId(List<string> ids, IDbConnection connection, IDbTransaction transaction)
        {
            if (ids == null || ids.Count == 0)
            {
                return new List<ImportListExclusion>();
            }

            var foreignIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim().ToLowerInvariant()).Distinct().ToArray();
            if (foreignIds.Length == 0)
            {
                return new List<ImportListExclusion>();
            }

            var predicate = _database.DatabaseType == DatabaseType.PostgreSQL
                ? "LOWER(TRIM(\"ImportListExclusions\".\"ForeignId\")) = ANY(@ForeignIds)"
                : "LOWER(TRIM(\"ImportListExclusions\".\"ForeignId\")) IN @ForeignIds";
            List<ImportListExclusion> Find(string[] values) => Query(Builder().Where(predicate, new { ForeignIds = values }), connection, transaction);

            if (_database.DatabaseType == DatabaseType.SQLite && foreignIds.Length > SqliteVariableLimit.MaxParameters)
            {
                var exclusions = new List<ImportListExclusion>();
                foreach (var batch in foreignIds.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    exclusions.AddRange(Find(batchIds));
                }

                return exclusions.DistinctBy(e => e.Id).ToList();
            }

            return Find(foreignIds);
        }

        private List<ImportListExclusion> Query(SqlBuilder builder, IDbConnection connection, IDbTransaction transaction)
        {
            if (connection == null) return Query(builder);
            var sql = builder.Select(typeof(ImportListExclusion)).AddSelectTemplate(typeof(ImportListExclusion));
            return connection.Query<ImportListExclusion>(sql.RawSql, sql.Parameters, transaction: transaction).ToList();
        }
    }
}
