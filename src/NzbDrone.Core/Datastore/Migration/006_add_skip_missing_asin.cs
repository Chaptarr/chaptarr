using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(6)]
    public class add_skip_missing_asin : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add SkipMissingAsin column to MetadataProfiles table
            // For ebook profiles: additional filter requiring ASIN (most aggressive, highest quality)
            if (!Schema.Table("MetadataProfiles").Column("SkipMissingAsin").Exists())
            {
                Alter.Table("MetadataProfiles")
                    .AddColumn("SkipMissingAsin").AsBoolean().WithDefaultValue(false);
            }
        }
    }
}
