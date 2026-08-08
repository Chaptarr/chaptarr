using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(26)]
    public class fix_scheduledtasks_datetime_range : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
	UPDATE ""ScheduledTasks""
	SET ""LastStartTime"" = ""LastExecution""
	WHERE ""LastStartTime"" IS NOT NULL
  AND (""LastStartTime"" < TIMESTAMP '0001-01-01' OR ""LastStartTime"" > TIMESTAMP '9999-12-31');
");
        }
    }
}
