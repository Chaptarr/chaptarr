using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(58)]
    public class fix_nullable_omnibus_and_asin_filters : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Fresh installs create these booleans as NOT NULL with DEFAULT false (see baseline schema),
            // but historical incremental migrations added them as nullable with only a default.
            // Normalize any NULLs and (on PostgreSQL) enforce NOT NULL for consistent semantics.

            if (Schema.Table("Books").Column("IsOmnibus").Exists())
            {
                Execute.Sql("UPDATE \"Books\" SET \"IsOmnibus\" = false WHERE \"IsOmnibus\" IS NULL");
                IfPostgres().Execute.Sql("ALTER TABLE \"Books\" ALTER COLUMN \"IsOmnibus\" SET DEFAULT false");
                IfPostgres().Execute.Sql("ALTER TABLE \"Books\" ALTER COLUMN \"IsOmnibus\" SET NOT NULL");
            }

            if (Schema.Table("MetadataProfiles").Column("SkipOmnibus").Exists())
            {
                Execute.Sql("UPDATE \"MetadataProfiles\" SET \"SkipOmnibus\" = false WHERE \"SkipOmnibus\" IS NULL");
                IfPostgres().Execute.Sql("ALTER TABLE \"MetadataProfiles\" ALTER COLUMN \"SkipOmnibus\" SET DEFAULT false");
                IfPostgres().Execute.Sql("ALTER TABLE \"MetadataProfiles\" ALTER COLUMN \"SkipOmnibus\" SET NOT NULL");
            }

            if (Schema.Table("MetadataProfiles").Column("SkipMissingAsin").Exists())
            {
                Execute.Sql("UPDATE \"MetadataProfiles\" SET \"SkipMissingAsin\" = false WHERE \"SkipMissingAsin\" IS NULL");
                IfPostgres().Execute.Sql("ALTER TABLE \"MetadataProfiles\" ALTER COLUMN \"SkipMissingAsin\" SET DEFAULT false");
                IfPostgres().Execute.Sql("ALTER TABLE \"MetadataProfiles\" ALTER COLUMN \"SkipMissingAsin\" SET NOT NULL");
            }
        }
    }
}
