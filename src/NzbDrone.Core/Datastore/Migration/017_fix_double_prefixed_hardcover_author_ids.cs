using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(17)]
    public class fix_double_prefixed_hardcover_author_ids : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Some V5 mapping paths accidentally stored HardcoverAuthorId as "hc:hc:{id}".
            // Normalize existing rows to a single "hc:" prefix.

            // Run multiple times to tolerate rare triple-prefix cases (hc:hc:hc:{id}).
            Execute.Sql("UPDATE \"Authors\" SET \"HardcoverAuthorId\" = REPLACE(\"HardcoverAuthorId\", 'hc:hc:', 'hc:') WHERE \"HardcoverAuthorId\" LIKE 'hc:hc:%';");
            Execute.Sql("UPDATE \"Authors\" SET \"HardcoverAuthorId\" = REPLACE(\"HardcoverAuthorId\", 'hc:hc:', 'hc:') WHERE \"HardcoverAuthorId\" LIKE 'hc:hc:%';");
            Execute.Sql("UPDATE \"Authors\" SET \"HardcoverAuthorId\" = REPLACE(\"HardcoverAuthorId\", 'hc:hc:', 'hc:') WHERE \"HardcoverAuthorId\" LIKE 'hc:hc:%';");
        }
    }
}

