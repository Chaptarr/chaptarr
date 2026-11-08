using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(119)]
    public class add_pending_unmonitored_books : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("PendingAuthorImport").AddColumn("AudiobookBooksToAdd").AsString().Nullable();
            Alter.Table("PendingAuthorImport").AddColumn("EbookBooksToAdd").AsString().Nullable();
        }
    }
}
