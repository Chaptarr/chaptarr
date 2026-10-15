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
        IEnumerable<ImportListExclusion> All(IDbConnection connection, IDbTransaction transaction);
        void DeleteMany(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction);
        ImportListExclusion FindByForeignId(string foreignId);
        List<ImportListExclusion> FindByForeignId(List<string> ids);
        List<ImportListExclusion> FindByForeignId(List<string> ids, IDbConnection connection, IDbTransaction transaction);
    }

    public class ImportListExclusionRepository : BasicRepository<ImportListExclusion>, IImportListExclusionRepository
    {
        public ImportListExclusionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public IEnumerable<ImportListExclusion> All(IDbConnection connection, IDbTransaction transaction) =>
            Query(Builder(), connection, transaction);

        public new void DeleteMany(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction) =>
            base.DeleteMany(ids, connection, transaction);

        public ImportListExclusion FindByForeignId(string foreignId)
        {
            return Query(m => m.ForeignId == foreignId).SingleOrDefault();
        }

        public List<ImportListExclusion> FindByForeignId(List<string> ids) => FindByForeignId(ids, null, null);

        public List<ImportListExclusion> FindByForeignId(List<string> ids, IDbConnection connection, IDbTransaction transaction)
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
                    exclusions.AddRange(Query(Builder().Where<ImportListExclusion>(x => Enumerable.Contains(batchIds, x.ForeignId)), connection, transaction));
                }

                return exclusions.DistinctBy(e => e.Id).ToList();
            }

            return Query(Builder().Where<ImportListExclusion>(x => Enumerable.Contains(foreignIds, x.ForeignId)), connection, transaction);
        }

        private List<ImportListExclusion> Query(SqlBuilder builder, IDbConnection connection, IDbTransaction transaction)
        {
            if (connection == null) return Query(builder);
            var sql = builder.Select(typeof(ImportListExclusion)).AddSelectTemplate(typeof(ImportListExclusion));
            return connection.Query<ImportListExclusion>(sql.RawSql, sql.Parameters, transaction: transaction).ToList();
        }
    }
}
