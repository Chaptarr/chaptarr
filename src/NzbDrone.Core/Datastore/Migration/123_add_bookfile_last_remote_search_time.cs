using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(123)]
    public class add_bookfile_last_remote_search_time : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("BookFiles").Exists() &&
                !Schema.Table("BookFiles").Column("LastRemoteSearchTime").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("LastRemoteSearchTime").AsDateTime().Nullable();
            }
        }
    }
}
