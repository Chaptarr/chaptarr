using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Reflection;
using NzbDrone.Common.Serializer;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ThingiProvider
{
    public class ProviderRepository<TProviderDefinition> : BasicRepository<TProviderDefinition>, IProviderRepository<TProviderDefinition>
        where TProviderDefinition : ProviderDefinition,
            new()
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(ProviderRepository<TProviderDefinition>));

        protected readonly JsonSerializerOptions _serializerSettings;

        protected ProviderRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
            var serializerSettings = new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

            serializerSettings.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, true));
            serializerSettings.Converters.Add(new STJTimeSpanConverter());
            serializerSettings.Converters.Add(new STJUtcConverter());

            _serializerSettings = serializerSettings;
        }

        protected override List<TProviderDefinition> Query(SqlBuilder builder)
        {
            var type = typeof(TProviderDefinition);
            var sql = builder.Select(type).AddSelectTemplate(type);

            var results = new List<TProviderDefinition>();

            using (var conn = _database.OpenConnection())
            using (var reader = conn.ExecuteReader(sql.RawSql, sql.Parameters))
            {
                var parser = reader.GetRowParser<TProviderDefinition>(typeof(TProviderDefinition));
                var settingsIndex = reader.GetOrdinal(nameof(ProviderDefinition.Settings));

                while (reader.Read())
                {
                    var body = reader.IsDBNull(settingsIndex) ? null : reader.GetString(settingsIndex);
                    var item = parser(reader);
                    var impType = ProviderConfigTypeCache.Find(item.ConfigContract);

                    if (impType == null)
                    {
                        item.Settings = NullConfig.Instance;
                    }
                    else if (body.IsNullOrWhiteSpace() || body.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Settings = (IProviderConfig)Activator.CreateInstance(impType);
                    }
                    else
                    {
                        try
                        {
                            item.Settings = (IProviderConfig)JsonSerializer.Deserialize(body, impType, _serializerSettings) ??
                                            (IProviderConfig)Activator.CreateInstance(impType);
                        }
                        catch (JsonException ex)
                        {
                            // Don't log the raw payload - provider settings may include secrets.
                            Logger.Warn(ex, "Failed to deserialize provider settings for {0} (Id={1}, Contract={2}). Falling back to defaults.", typeof(TProviderDefinition).Name, item.Id, item.ConfigContract);
                            item.Settings = (IProviderConfig)Activator.CreateInstance(impType);
                        }
                    }

                    results.Add(item);
                }
            }

            return results;
        }
    }
}
