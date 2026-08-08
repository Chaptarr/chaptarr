using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(5)]
    public class add_skip_omnibus_to_metadata_profile : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add SkipOmnibus column to MetadataProfiles table
            // When enabled, books marked as omnibus/collections will be skipped during import
            if (!Schema.Table("MetadataProfiles").Column("SkipOmnibus").Exists())
            {
                Alter.Table("MetadataProfiles")
                    .AddColumn("SkipOmnibus").AsBoolean().WithDefaultValue(false);
            }
        }
    }
}
