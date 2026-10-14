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
        void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<ProviderAlias> aliases, IDbConnection connection, IDbTransaction transaction);
        void DeleteAliases(string entityType, int entityId);
        List<ProviderAlias> GetAliases(string entityType, int entityId, string scope);
        List<ProviderAlias> GetAliases(string entityType, IEnumerable<int> entityIds, string scope);
        List<int> FindEntityIds(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases);
        List<ProviderAlias> FindAliases(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases) => throw new NotImplementedException();
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
                ReplaceAliases(entityType, entityId, scope, aliases, conn, transaction);
                transaction.Commit();
            }
        }

        public void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<ProviderAlias> aliases, IDbConnection connection, IDbTransaction transaction)
        {
            connection.Execute($@"DELETE FROM ""{_table}""
                               WHERE ""EntityType"" = @entityType
                                 AND ""EntityId"" = @entityId
                                 AND ""Scope"" = @scope",
                new { entityType, entityId, scope },
                transaction);

            var items = aliases?.ToList() ?? new List<ProviderAlias>();
            if (items.Count > 0)
            {
                InsertMany(items, connection, transaction);
            }
        }

        public void DeleteAliases(string entityType, int entityId)
        {
            Delete(a => a.EntityType == entityType && a.EntityId == entityId);
        }

        public List<ProviderAlias> GetAliases(string entityType, int entityId, string scope)
        {
            return Query(alias => alias.EntityType == entityType && alias.EntityId == entityId && alias.Scope == scope);
        }

        public List<ProviderAlias> GetAliases(string entityType, IEnumerable<int> entityIds, string scope)
        {
            var result = new List<ProviderAlias>();
            using var connection = _database.OpenConnection();
            foreach (var chunk in entityIds.Distinct().Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters - 2 : ushort.MaxValue))
            {
                var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                result.AddRange(connection.Query<ProviderAlias>($"SELECT * FROM \"{_table}\" WHERE \"EntityType\" = @entityType AND \"Scope\" = @scope AND \"EntityId\" {inClause}", new { entityType, scope, ids = chunk.ToArray() }));
            }
            return result;
        }

        public List<int> FindEntityIds(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases)
        {
            return FindAliases(entityType, scope, aliases).Select(alias => alias.EntityId).Distinct().ToList();
        }

        public List<ProviderAlias> FindAliases(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases)
        {
            var pairs = aliases?
                .Where(a => !string.IsNullOrWhiteSpace(a.Provider) && !string.IsNullOrWhiteSpace(a.NormalizedProviderId))
                .Distinct().ToArray() ?? Array.Empty<(string Provider, string NormalizedProviderId)>();
            var result = new List<ProviderAlias>();
            if (pairs.Length == 0)
            {
                return result;
            }

            using var connection = _database.OpenConnection();
            var chunkSize = _database.DatabaseType == DatabaseType.SQLite
                ? SqliteVariableLimit.MaxParameters - 2 - pairs.Select(pair => pair.Provider).Distinct().Count()
                : pairs.Length;
            foreach (var chunk in pairs.Chunk(chunkSize))
            {
                var parameters = new DynamicParameters(new { scope, entityType });
                var branches = chunk.GroupBy(pair => pair.Provider).Select((group, i) =>
                {
                    parameters.Add($"provider{i}", group.Key);
                    parameters.Add($"providerIds{i}", group.Select(pair => pair.NormalizedProviderId).ToArray());
                    var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? $"= ANY(@providerIds{i})" : $"IN @providerIds{i}";
                    return $@"SELECT * FROM ""{_table}""
                             WHERE ""EntityType"" = @entityType AND ""Scope"" = @scope AND ""Provider"" = @provider{i}
                               AND ""NormalizedProviderId"" {inClause}";
                });
                result.AddRange(connection.Query<ProviderAlias>(string.Join(" UNION ALL ", branches), parameters));
            }

            return result;
        }
    }
}
