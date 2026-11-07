// Source: BookshelfNG main-v0.4.21.71 (3fad9427d167ed69d30ca6ed55f40262c2846487), GPL-3.0.
// This isolated copy upgrades a disposable BookshelfNG source snapshot before import.
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.BookshelfMigration
{
    [Migration(014)]
    public class rename_blacklist_to_blocklist : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Rename.Table("Blacklist").To("Blocklist");
        }
    }
}
