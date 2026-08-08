using System.Data;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(88)]
    public class enable_audiobookshelf_event_triggers : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                AudioBookShelfEventTriggerBackfill.Apply(connection, transaction);
            });
        }
    }

    internal static class AudioBookShelfEventTriggerBackfill
    {
        public static void Apply(IDbConnection connection, IDbTransaction transaction)
        {
            // Earlier builds forced these three events on for AudioBookShelf in the notification
            // factory and hid the event checkboxes in its edit modal, so existing rows store false
            // without any user intent behind it. Now that the factory honors the stored flags,
            // backfill them so existing connections keep receiving scans after the upgrade.
            connection.Execute(
                @"UPDATE ""Notifications""
                  SET ""OnReleaseImport"" = @Enabled,
                      ""OnRename"" = @Enabled,
                      ""OnBookFileDelete"" = @Enabled
                  WHERE ""Implementation"" = 'AudioBookShelf'
                     OR ""ConfigContract"" = 'AudioBookShelfSettings';",
                new
                {
                    Enabled = true
                },
                transaction: transaction);
        }
    }
}
