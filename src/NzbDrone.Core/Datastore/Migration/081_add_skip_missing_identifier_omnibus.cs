using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(81)]
    public class add_skip_missing_identifier_omnibus : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("MetadataProfiles").Exists() ||
                Schema.Table("MetadataProfiles").Column("SkipMissingIdentifierOmnibus").Exists())
            {
                return;
            }

            Alter.Table("MetadataProfiles")
                .AddColumn("SkipMissingIdentifierOmnibus").AsBoolean().NotNullable().WithDefaultValue(false);

            // Existing strict omnibus blockers should remain visible in the UI, where
            // the strict option is nested under the looser missing-identifier option.
            Execute.Sql(@"UPDATE ""MetadataProfiles"" SET ""SkipMissingIdentifierOmnibus"" = true WHERE ""SkipOmnibus"" = true");
        }
    }
}
