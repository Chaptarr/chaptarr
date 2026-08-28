using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(105)]
    public class add_author_statistics_edition_lookup_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Editions").Exists() ||
                Schema.Table("Editions").Index("IX_Editions_BookId_Monitored_Id").Exists())
            {
                return;
            }

            // Author progress looks up the lowest-id monitored edition for each book.
            // Keep that seek ordered and selective on both SQLite and PostgreSQL.
            Create.Index("IX_Editions_BookId_Monitored_Id")
                .OnTable("Editions")
                .OnColumn("BookId").Ascending()
                .OnColumn("Monitored").Ascending()
                .OnColumn("Id").Ascending();
        }
    }
}
