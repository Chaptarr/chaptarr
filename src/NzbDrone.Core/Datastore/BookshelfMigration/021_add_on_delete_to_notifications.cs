// Source: BookshelfNG main-v0.4.21.71 (3fad9427d167ed69d30ca6ed55f40262c2846487), GPL-3.0.
// This isolated copy upgrades a disposable BookshelfNG source snapshot before import.
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.BookshelfMigration
{
    [Migration(21)]
    public class add_on_delete_to_notifications : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("Notifications").AddColumn("OnAuthorDelete").AsBoolean().WithDefaultValue(false);
            Alter.Table("Notifications").AddColumn("OnBookDelete").AsBoolean().WithDefaultValue(false);
            Alter.Table("Notifications").AddColumn("OnBookFileDelete").AsBoolean().WithDefaultValue(false);
            Alter.Table("Notifications").AddColumn("OnBookFileDeleteForUpgrade").AsBoolean().WithDefaultValue(false);
        }
    }
}
