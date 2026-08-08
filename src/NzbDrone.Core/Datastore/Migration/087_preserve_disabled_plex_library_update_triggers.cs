using System;
using System.Data;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(87)]
    public class preserve_disabled_plex_library_update_triggers : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                PlexUpdateLibraryDisabledTriggerBackfill.Apply(connection, transaction);
            });
        }
    }

    internal static class PlexUpdateLibraryDisabledTriggerBackfill
    {
        public static void Apply(IDbConnection connection, IDbTransaction transaction)
        {
            var rows = connection.Query<NotificationRow>(
                @"SELECT ""Id"", ""Settings""
                  FROM ""Notifications""
                  WHERE ""Implementation"" = 'PlexServer'
                     OR ""ConfigContract"" = 'PlexServerSettings';",
                transaction: transaction);

            foreach (var row in rows)
            {
                if (!HasDisabledUpdateLibrary(row.Settings))
                {
                    continue;
                }

                connection.Execute(
                    @"UPDATE ""Notifications""
                      SET ""OnReleaseImport"" = @Disabled,
                          ""OnRename"" = @Disabled,
                          ""OnBookRetag"" = @Disabled,
                          ""OnBookDelete"" = @Disabled,
                          ""OnBookFileDelete"" = @Disabled,
                          ""OnBookFileDeleteForUpgrade"" = @Disabled,
                          ""OnAuthorDelete"" = @Disabled
                      WHERE ""Id"" = @Id;",
                    new
                    {
                        row.Id,
                        Disabled = false
                    },
                    transaction: transaction);
            }
        }

        internal static bool HasDisabledUpdateLibrary(string settings)
        {
            if (string.IsNullOrWhiteSpace(settings))
            {
                return false;
            }

            try
            {
                var settingsObj = JObject.Parse(settings);
                var property = settingsObj.Property("updateLibrary", StringComparison.OrdinalIgnoreCase);
                if (property == null)
                {
                    return false;
                }

                if (property.Value.Type == JTokenType.Boolean)
                {
                    return property.Value.Value<bool>() == false;
                }

                if (property.Value.Type == JTokenType.String &&
                    bool.TryParse(property.Value.Value<string>(), out var parsed))
                {
                    return parsed == false;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            return false;
        }

        private sealed class NotificationRow
        {
            public int Id { get; set; }
            public string Settings { get; set; }
        }
    }
}
