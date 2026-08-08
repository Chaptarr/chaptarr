using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(73)]
    public class add_media_type_to_import_list_exclusions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("ImportListExclusions").Exists() && !Schema.Table("ImportListExclusions").Column("MediaType").Exists())
            {
                Alter.Table("ImportListExclusions")
                    .AddColumn("MediaType")
                    .AsInt32()
                    .Nullable();
            }
        }
    }
}
