// Source: BookshelfNG main-v0.4.21.71 (3fad9427d167ed69d30ca6ed55f40262c2846487), GPL-3.0.
// This isolated copy upgrades a disposable BookshelfNG source snapshot before import.
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.BookshelfMigration
{
    [Migration(005)]
    public class add_indexer_and_enabled_to_release_profiles : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("ReleaseProfiles").AddColumn("Enabled").AsBoolean().WithDefaultValue(true);
            Alter.Table("ReleaseProfiles").AddColumn("IndexerId").AsInt32().WithDefaultValue(0);
        }
    }
}
