using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(102)]
    public class add_pending_author_book_search_targets : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("PendingAuthorImport").Exists())
            {
                return;
            }

            if (!Schema.Table("PendingAuthorImport").Column("AudiobookBooksToSearch").Exists())
            {
                Alter.Table("PendingAuthorImport").AddColumn("AudiobookBooksToSearch").AsString().Nullable();
            }

            if (!Schema.Table("PendingAuthorImport").Column("EbookBooksToSearch").Exists())
            {
                Alter.Table("PendingAuthorImport").AddColumn("EbookBooksToSearch").AsString().Nullable();
            }
        }
    }
}
