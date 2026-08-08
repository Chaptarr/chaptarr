using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(27)]
    public class fix_scheduledtasks_datetime_range_v2 : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
	UPDATE ""ScheduledTasks""
	SET ""LastExecution"" = CURRENT_TIMESTAMP
	WHERE ""LastExecution"" < TIMESTAMP '0001-01-01 00:00:00'
   OR ""LastExecution"" > TIMESTAMP '9999-12-31 23:59:59.999999';

UPDATE ""ScheduledTasks""
SET ""LastDuration"" = NULL
WHERE ""LastDuration"" IS NOT NULL
  AND (""LastDuration"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastDuration"" > TIMESTAMP '9999-12-31 23:59:59.999999');

UPDATE ""ScheduledTasks""
SET ""LastStartTime"" = NULL
WHERE ""LastStartTime"" IS NOT NULL
  AND (""LastStartTime"" < TIMESTAMP '0001-01-01 00:00:00'
       OR ""LastStartTime"" > TIMESTAMP '9999-12-31 23:59:59.999999');

UPDATE ""ScheduledTasks""
SET ""LastStartTime"" = NULL
WHERE ""LastStartTime"" IS NOT NULL
  AND ""LastStartTime"" > ""LastExecution"";
");
        }
    }
}
