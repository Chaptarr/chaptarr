using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(109)]
    public class preserve_unknown_series_primary : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("SeriesBookLink").Exists())
            {
                return;
            }

            // Existing flags cannot reveal whether the provider originally sent unknown.
            // Preserve them until ordinary metadata reconciliation supplies a current value.
            IfDatabase("sqlite").Execute.Sql(@"
                ALTER TABLE ""SeriesBookLink"" ADD COLUMN ""IsPrimaryNullable"" INTEGER;
                UPDATE ""SeriesBookLink"" SET ""IsPrimaryNullable"" = ""IsPrimary"";
                ALTER TABLE ""SeriesBookLink"" DROP COLUMN ""IsPrimary"";
                ALTER TABLE ""SeriesBookLink"" RENAME COLUMN ""IsPrimaryNullable"" TO ""IsPrimary"";");
            IfPostgres().Execute.Sql(@"
                ALTER TABLE ""SeriesBookLink"" ALTER COLUMN ""IsPrimary"" DROP NOT NULL;
                ALTER TABLE ""SeriesBookLink"" ALTER COLUMN ""IsPrimary"" DROP DEFAULT;");
        }
    }
}
