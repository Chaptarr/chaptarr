using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IProviderAliasRepository : IBasicRepository<ProviderAlias>
    {
        void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<ProviderAlias> aliases);
        void DeleteAliases(string entityType, int entityId);
        List<int> FindEntityIds(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases);
    }

    public class ProviderAliasRepository : BasicRepository<ProviderAlias>, IProviderAliasRepository
    {
        public ProviderAliasRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<ProviderAlias> aliases)
        {
            using (var conn = _database.OpenConnection())
            using (var transaction = conn.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                conn.Execute($@"DELETE FROM ""{_table}""
                               WHERE ""EntityType"" = @entityType
                                 AND ""EntityId"" = @entityId
                                 AND ""Scope"" = @scope",
                    new { entityType, entityId, scope },
                    transaction);

                var items = aliases?.ToList() ?? new List<ProviderAlias>();
                if (items.Count > 0)
                {
                    InsertMany(items, conn, transaction);
                }

                transaction.Commit();
            }
        }

        public void DeleteAliases(string entityType, int entityId)
        {
            Delete(a => a.EntityType == entityType && a.EntityId == entityId);
        }

        public List<int> FindEntityIds(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases)
        {
            var pairs = aliases?
                .Where(a => !string.IsNullOrWhiteSpace(a.Provider) && !string.IsNullOrWhiteSpace(a.NormalizedProviderId))
                .Distinct()
                .ToList() ?? new List<(string Provider, string NormalizedProviderId)>();

            if (pairs.Count == 0)
            {
                return new List<int>();
            }

            var clauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("entityType", entityType);
            parameters.Add("scope", scope);

            for (var i = 0; i < pairs.Count; i++)
            {
                clauses.Add($@"(""Provider"" = @provider{i} AND ""NormalizedProviderId"" = @providerId{i})");
                parameters.Add($"provider{i}", pairs[i].Provider);
                parameters.Add($"providerId{i}", pairs[i].NormalizedProviderId);
            }

            var sql = $@"SELECT DISTINCT ""EntityId""
                         FROM ""{_table}""
                         WHERE ""EntityType"" = @entityType
                           AND ""Scope"" = @scope
                           AND ({string.Join(" OR ", clauses)})";

            return _database.Query<int>(sql, parameters).ToList();
        }
    }
}
