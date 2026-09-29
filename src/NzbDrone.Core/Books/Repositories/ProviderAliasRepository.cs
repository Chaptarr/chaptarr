using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
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

        private const int MaxReplaceAttempts = 3;

        public void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<ProviderAlias> aliases)
        {
            var items = aliases?.ToList() ?? new List<ProviderAlias>();

            // Replacing an entity's aliases is DELETE-then-INSERT in a READ COMMITTED transaction. Two writers
            // replacing the SAME entity at once (a bulk author edit updates thousands of authors while async
            // handlers of each update's event refresh the same aliases) both delete nothing, one inserts and
            // commits, and the other's INSERT then violates IX_ProviderAliasIndex_Unique - which failed a whole
            // author-editor save. Retrying lets the loser's DELETE see the winner's committed rows and replace
            // them, so the last writer wins instead of the request failing.
            RetryOnUniqueViolation(
                () => ReplaceAliasesOnce(entityType, entityId, scope, items),
                MaxReplaceAttempts);
        }

        private void ReplaceAliasesOnce(string entityType, int entityId, string scope, List<ProviderAlias> items)
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

                if (items.Count > 0)
                {
                    // Fresh instances per attempt: a failed InsertMany must not leave ids on the caller's objects.
                    var toInsert = items.Select(item => new ProviderAlias
                    {
                        EntityType = item.EntityType,
                        EntityId = item.EntityId,
                        Scope = item.Scope,
                        Provider = item.Provider,
                        NormalizedProviderId = item.NormalizedProviderId,
                        CreatedAt = item.CreatedAt,
                        UpdatedAt = item.UpdatedAt
                    }).ToList();

                    InsertMany(toInsert, conn, transaction);
                }

                transaction.Commit();
            }
        }

        internal static void RetryOnUniqueViolation(Action action, int maxAttempts)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsUniqueViolation(ex))
                {
                    // Small growing pause so two colliding writers do not immediately collide again.
                    Thread.Sleep(attempt * 15);
                }
            }
        }

        internal static bool IsUniqueViolation(Exception exception)
        {
            for (var ex = exception; ex != null; ex = ex.InnerException)
            {
                if (ex is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    return true;
                }

                if (ex is SqliteException sqlite &&
                    sqlite.SqliteErrorCode == 19 &&
                    sqlite.Message.IndexOf("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
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
