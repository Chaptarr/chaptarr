using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(33)]
    public class add_addoptions_to_narrators : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Narrators").Column("AddOptions").Exists())
            {
                Alter.Table("Narrators").AddColumn("AddOptions").AsString().Nullable();
            }
        }
    }
}
