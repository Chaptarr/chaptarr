using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(24)]
    public class add_asins_array_to_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add Asins column to store JSON array of all ASINs (including regional variants)
            // Use int.MaxValue to ensure TEXT type on Postgres (not VARCHAR(255))
            if (!Schema.Table("Editions").Column("Asins").Exists())
            {
                Alter.Table("Editions")
                    .AddColumn("Asins").AsString(int.MaxValue).NotNullable().WithDefaultValue("[]");
            }

            // Migrate existing Asin values to JSON array format: B0123 → ["B0123"]
            // NOTE: Quote identifiers for Postgres compatibility (unquoted identifiers are folded to lowercase).
            // SQL: '["' || UPPER(TRIM("Asin")) || '"]' → produces ["B0123"]
            Execute.Sql("UPDATE \"Editions\" SET \"Asins\" = '[\"' || UPPER(TRIM(\"Asin\")) || '\"]' WHERE \"Asin\" IS NOT NULL AND TRIM(\"Asin\") != '' AND (\"Asins\" IS NULL OR TRIM(\"Asins\") = '' OR TRIM(\"Asins\") = '[]')");
        }
    }
}
