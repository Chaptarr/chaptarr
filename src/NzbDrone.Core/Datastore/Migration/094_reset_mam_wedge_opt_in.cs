using System;
using System.Data;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(94)]
    public class reset_mam_wedge_opt_in : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Indexers").Exists())
            {
                return;
            }

            Execute.WithConnection((connection, transaction) =>
            {
                MyAnonaMouseWedgeOptInReset.Apply(connection, transaction);
            });
        }
    }

    internal static class MyAnonaMouseWedgeOptInReset
    {
        public static void Apply(IDbConnection connection, IDbTransaction transaction)
        {
            var rows = connection.Query<IndexerRow>(
                @"SELECT ""Id"", ""Settings""
                  FROM ""Indexers""
                  WHERE ""Implementation"" = 'MyAnonaMouse'
                     OR ""ConfigContract"" = 'MyAnonaMouseSettings';",
                transaction: transaction);

            foreach (var row in rows)
            {
                if (!TryResetWedgePreference(row.Settings, out var settings))
                {
                    continue;
                }

                connection.Execute(
                    @"UPDATE ""Indexers""
                      SET ""Settings"" = @Settings
                      WHERE ""Id"" = @Id;",
                    new
                    {
                        row.Id,
                        Settings = settings
                    },
                    transaction: transaction);
            }
        }

        internal static bool TryResetWedgePreference(string settings, out string updatedSettings)
        {
            updatedSettings = settings;

            if (string.IsNullOrWhiteSpace(settings))
            {
                return false;
            }

            try
            {
                var settingsObj = JObject.Parse(settings);
                var property = settingsObj.Property("useFreeleechWedge", StringComparison.OrdinalIgnoreCase);
                if (property == null || !TryReadInt(property.Value, out var value) || (value != 1 && value != 2))
                {
                    return false;
                }

                property.Value = 0;
                updatedSettings = settingsObj.ToString(Formatting.None);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;

            if (token?.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            return token?.Type == JTokenType.String && int.TryParse(token.Value<string>(), out value);
        }

        private sealed class IndexerRow
        {
            public int Id { get; set; }
            public string Settings { get; set; }
        }
    }
}
