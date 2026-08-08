using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(61)]
    public class repair_author_media_settings_backfill_invalid_profile_ids : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                AuthorMediaSettingsBackfillRepair.Apply(connection, transaction);
            });
        }
    }
}
