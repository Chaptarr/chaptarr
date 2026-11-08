using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IProviderAliasRepository
    {
        List<ProviderAlias> GetAliases(string entityType, int entityId, string scope);
        List<ProviderAlias> GetAliases(string entityType, IEnumerable<int> entityIds, string scope);
        List<int> FindEntityIds(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases);
        List<ProviderAlias> FindAliases(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases);
    }

    public class ProviderAliasRepository : IProviderAliasRepository
    {
        private const string _table = "ProviderAliasIndex";
        private readonly IMainDatabase _database;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public ProviderAliasRepository(IMainDatabase database, IEventAggregator eventAggregator)
        {
            _database = database;
        }

        public List<ProviderAlias> GetAliases(string entityType, int entityId, string scope)
        {
            return GetAliases(entityType, new[] { entityId }, scope);
        }

        public List<ProviderAlias> GetAliases(string entityType, IEnumerable<int> entityIds, string scope)
        {
            var timer = Stopwatch.StartNew();
            var ids = entityIds.Distinct().ToArray();
            var result = new List<ProviderAlias>();
            using var connection = _database.OpenConnection();
            foreach (var chunk in ids.Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters - 2 : ushort.MaxValue))
            {
                var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                result.AddRange(connection.Query<ProviderAlias>($"SELECT * FROM \"{_table}\" WHERE \"EntityType\" = @entityType AND \"Scope\" = @scope AND \"EntityId\" {inClause}", new { entityType, scope, ids = chunk.ToArray() }));
            }
            _logger.Debug("Loaded {0}/{1} aliases for {2} entities: {3} aliases in {4} ms", entityType, scope, ids.Length, result.Count, timer.ElapsedMilliseconds);
            return result;
        }

        public List<int> FindEntityIds(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases)
        {
            return FindAliases(entityType, scope, aliases).Select(alias => alias.EntityId).Distinct().ToList();
        }

        public List<ProviderAlias> FindAliases(string entityType, string scope, IEnumerable<(string Provider, string NormalizedProviderId)> aliases)
        {
            var timer = Stopwatch.StartNew();
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
                    var lookup = $@"SELECT * FROM ""{_table}""
                             WHERE ""EntityType"" = @entityType AND ""Scope"" = @scope AND ""Provider"" = @provider{i}
                               AND ""NormalizedProviderId"" {inClause}";
                    if (entityType == "Book" && scope == "edition")
                    {
                        lookup = $@"SELECT a.""Id"", 'Book' AS ""EntityType"", e.""BookId"" AS ""EntityId"",
                            a.""Scope"", a.""Provider"", a.""NormalizedProviderId"", a.""CreatedAt"", a.""UpdatedAt""
                            FROM ""{_table}"" a JOIN ""Editions"" e ON e.""Id"" = a.""EntityId""
                            WHERE a.""EntityType"" = 'Edition' AND a.""Scope"" = @scope AND a.""Provider"" = @provider{i}
                              AND a.""NormalizedProviderId"" {inClause}";
                    }
                    return lookup;
                });
                result.AddRange(connection.Query<ProviderAlias>(string.Join(" UNION ALL ", branches), parameters));
            }

            _logger.Debug("Looked up {0}/{1} aliases for {2} provider IDs: {3} aliases in {4} ms", entityType, scope, pairs.Length, result.Count, timer.ElapsedMilliseconds);
            return result;
        }
    }
}
