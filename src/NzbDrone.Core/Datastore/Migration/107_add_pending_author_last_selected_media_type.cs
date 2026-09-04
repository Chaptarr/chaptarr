using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(107)]
    public class add_pending_author_last_selected_media_type : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            const string tableName = "PendingAuthorImport";
            if (Schema.Table(tableName).Exists() &&
                !Schema.Table(tableName).Column("LastSelectedMediaType").Exists())
            {
                Alter.Table(tableName)
                    .AddColumn("LastSelectedMediaType")
                    .AsString()
                    .Nullable();
            }
        }
    }
}
