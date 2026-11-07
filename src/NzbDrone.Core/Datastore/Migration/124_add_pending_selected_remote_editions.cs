using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(124)]
    public class add_pending_selected_remote_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("PendingAuthorImport").Exists() &&
                !Schema.Table("PendingAuthorImport").Column("SelectedRemoteEditions").Exists())
            {
                Alter.Table("PendingAuthorImport")
                    .AddColumn("SelectedRemoteEditions").AsString().Nullable();
            }
        }
    }
}
