using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Common
{
    // Fixtures may model a pre-upgrade snapshot (including legacy scopes/orphans).
    // Production has no index writer: persisted owner snapshots drive the triggers.
    public sealed class ProviderAliasSeed : ProviderAliasService
    {
        private readonly IMainDatabase _database;
        public ProviderAliasSeed(IMainDatabase database, IEventAggregator events)
            : base(new ProviderAliasRepository(database, events)) => _database = database;

        public void SeedAliases(string entityType, int entityId, string scope, IEnumerable<string> ids,
            IDbConnection connection = null, IDbTransaction transaction = null)
        {
            if (ids == null) return;
            var ownsConnection = connection == null;
            connection ??= _database.OpenConnection();
            try
            {
                var table = entityType switch { "Author" => "Authors", "Book" => "Books", "Edition" => "Editions", _ => throw new ArgumentException(entityType) };
                var aliases = NormalizeProviderIds(ids);
                var stored = connection.Query<string>($"SELECT \"ProviderIds\" FROM \"{table}\" WHERE \"Id\" = @entityId", new { entityId }, transaction).ToList();
                if (stored.Count == 0)
                {
                    // Deliberately invalid legacy rows exercise orphan-tolerant read paths.
                    connection.Execute("INSERT INTO \"ProviderAliasIndex\" (\"EntityType\",\"EntityId\",\"Scope\",\"Provider\",\"NormalizedProviderId\",\"CreatedAt\",\"UpdatedAt\") VALUES (@entityType,@entityId,@scope,@Provider,@NormalizedProviderId,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP)",
                        aliases.Select(alias => new { entityType, entityId, scope, alias.Provider, alias.NormalizedProviderId }), transaction);
                    return;
                }
                var snapshot = stored[0] == null ? new Dictionary<string, string[]>() : JsonSerializer.Deserialize<Dictionary<string, string[]>>(stored[0]);
                snapshot[scope] = aliases.Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").ToArray();
                connection.Execute($"UPDATE \"{table}\" SET \"ProviderIds\" = @snapshot WHERE \"Id\" = @entityId", new { snapshot = JsonSerializer.Serialize(snapshot), entityId }, transaction);
            }
            finally { if (ownsConnection) connection.Dispose(); }
        }
    }
}
