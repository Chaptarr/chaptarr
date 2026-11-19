using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(116)]
    public class add_pending_author_sync_options : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("PendingAuthorImport").AddColumn("SyncMonitoredAcrossFormats").AsBoolean().Nullable();
            Alter.Table("PendingAuthorImport").AddColumn("IsAuthorSettingsRequest").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }
}
