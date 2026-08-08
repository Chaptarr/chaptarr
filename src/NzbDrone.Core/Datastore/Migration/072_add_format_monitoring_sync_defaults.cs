using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(72)]
    public class add_format_monitoring_sync_defaults : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Column("SyncMonitoredAcrossFormats").Exists())
            {
                Alter.Table("Authors")
                    .AddColumn("SyncMonitoredAcrossFormats")
                    .AsBoolean()
                    .Nullable();
            }

            if (Schema.Table("RootFolders").Exists() && !Schema.Table("RootFolders").Column("DefaultSyncMonitoredAcrossFormats").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("DefaultSyncMonitoredAcrossFormats")
                    .AsBoolean()
                    .Nullable();
            }
        }
    }
}
