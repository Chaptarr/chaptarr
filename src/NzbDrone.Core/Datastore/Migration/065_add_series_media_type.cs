using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(65)]
    public class add_series_media_type : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Series").Exists())
            {
                return;
            }

            if (!Schema.Table("Series").Column("MediaType").Exists())
            {
                Alter.Table("Series").AddColumn("MediaType").AsInt32().NotNullable().WithDefaultValue(0);
            }

            // Historical behavior: "InstanceType" was incorrectly used to encode audiobook/ebook.
            // Move that value to the new MediaType column and restore InstanceType semantics for narrator variants.
            if (Schema.Table("Series").Column("InstanceType").Exists())
            {
                Execute.Sql("UPDATE \"Series\" SET \"MediaType\" = 0, \"InstanceType\" = 'original' WHERE LOWER(\"InstanceType\") = 'audiobook'");
                Execute.Sql("UPDATE \"Series\" SET \"MediaType\" = 1, \"InstanceType\" = 'original' WHERE LOWER(\"InstanceType\") = 'ebook'");
            }

            // SeriesBookLink.SeriesInstanceType is reserved for narrator-variant semantics ("original"/"narrator_variant").
            // If any rows were stamped with media types, normalize them back to "original".
            if (Schema.Table("SeriesBookLink").Exists() && Schema.Table("SeriesBookLink").Column("SeriesInstanceType").Exists())
            {
                Execute.Sql("UPDATE \"SeriesBookLink\" SET \"SeriesInstanceType\" = 'original' WHERE LOWER(\"SeriesInstanceType\") IN ('audiobook', 'ebook')");
            }
        }
    }
}

