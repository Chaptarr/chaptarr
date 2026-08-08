using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(2)]
    public class add_identifier_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // This migration adds identifier indexes for existing databases that predate the baseline 001.
            // Fresh installs already have these indexes from 001; the IF NOT EXISTS guards keep this idempotent.

            // SQLite partial indexes
            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IF NOT EXISTS IX_Editions_Asin_Partial
                    ON Editions(Asin)
                    WHERE Asin IS NOT NULL AND LENGTH(Asin) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IF NOT EXISTS IX_Editions_AudibleASIN_Partial
                    ON Editions(AudibleASIN)
                    WHERE AudibleASIN IS NOT NULL AND LENGTH(AudibleASIN) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IF NOT EXISTS IX_Editions_Isbn10_Partial
                    ON Editions(Isbn10)
                    WHERE Isbn10 IS NOT NULL AND LENGTH(Isbn10) > 0");

            IfDatabase("sqlite")
                .Execute.Sql(@"
                    CREATE INDEX IF NOT EXISTS IX_Editions_Isbn13_Partial
                    ON Editions(Isbn13)
                    WHERE Isbn13 IS NOT NULL AND LENGTH(Isbn13) > 0");

            // PostgreSQL functional indexes for case-insensitive ID matches
            IfPostgres()
                .Execute.Sql(@"
                    CREATE INDEX IF NOT EXISTS IX_Editions_Asin_Upper
                    ON ""Editions"" (UPPER(""Asin""))
                    WHERE ""Asin"" IS NOT NULL AND LENGTH(""Asin"") > 0");

            IfPostgres()
                .Execute.Sql(@"
                    CREATE INDEX IF NOT EXISTS IX_Editions_AudibleASIN_Upper
                    ON ""Editions"" (UPPER(""AudibleASIN""))
                    WHERE ""AudibleASIN"" IS NOT NULL AND LENGTH(""AudibleASIN"") > 0");
        }
    }
}
