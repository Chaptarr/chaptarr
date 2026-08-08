using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(49)]
    public class fix_books_added_datetime_range : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
	SET TIME ZONE 'UTC';

	UPDATE ""Books""
SET ""Added"" = NOW()
WHERE ""Added"" <= TIMESTAMP '0001-01-01 00:00:00';
");
        }
    }
}
