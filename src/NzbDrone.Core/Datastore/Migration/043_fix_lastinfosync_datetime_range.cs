using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(43)]
    public class fix_lastinfosync_datetime_range : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
	SET TIME ZONE 'UTC';

	UPDATE ""Authors""
SET ""LastInfoSync"" = NULL
WHERE ""LastInfoSync"" IS NOT NULL
  AND (""LastInfoSync"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastInfoSync"" > TIMESTAMP '9999-12-31 23:59:59.999999');

UPDATE ""Books""
SET ""LastInfoSync"" = NULL
WHERE ""LastInfoSync"" IS NOT NULL
  AND (""LastInfoSync"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastInfoSync"" > TIMESTAMP '9999-12-31 23:59:59.999999');

UPDATE ""NarratorMetadata""
SET ""LastInfoSync"" = NULL
WHERE ""LastInfoSync"" IS NOT NULL
  AND (""LastInfoSync"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastInfoSync"" > TIMESTAMP '9999-12-31 23:59:59.999999');

UPDATE ""Narrators""
SET ""LastInfoSync"" = NULL
WHERE ""LastInfoSync"" IS NOT NULL
  AND (""LastInfoSync"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastInfoSync"" > TIMESTAMP '9999-12-31 23:59:59.999999');

UPDATE ""ImportListStatus""
SET ""LastInfoSync"" = NULL
WHERE ""LastInfoSync"" IS NOT NULL
  AND (""LastInfoSync"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastInfoSync"" > TIMESTAMP '9999-12-31 23:59:59.999999');
");
        }
    }
}
