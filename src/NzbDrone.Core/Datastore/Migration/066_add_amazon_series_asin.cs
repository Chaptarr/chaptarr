using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(66)]
    public class add_amazon_series_asin : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Series").Exists())
            {
                return;
            }

            if (!Schema.Table("Series").Column("AmazonSeriesAsin").Exists())
            {
                Alter.Table("Series").AddColumn("AmazonSeriesAsin").AsString().Nullable();
            }

            // Index for fast provider-id lookups.
            IfDatabase("sqlite")
                .Execute.Sql(@"CREATE INDEX IF NOT EXISTS IX_Series_AmazonSeriesAsin ON Series(AmazonSeriesAsin)");

            IfPostgres()
                .Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Series_AmazonSeriesAsin"" ON ""Series"" (""AmazonSeriesAsin"")");
        }
    }
}

