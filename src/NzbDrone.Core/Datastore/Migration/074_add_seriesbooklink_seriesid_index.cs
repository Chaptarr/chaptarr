using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(74)]
    public class add_seriesbooklink_seriesid_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("SeriesBookLink").Exists() &&
                !Schema.Table("SeriesBookLink").Index("IX_SeriesBookLink_SeriesId").Exists())
            {
                Create.Index("IX_SeriesBookLink_SeriesId")
                    .OnTable("SeriesBookLink")
                    .OnColumn("SeriesId").Ascending();
            }
        }
    }
}
