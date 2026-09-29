using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(108)]
    public class add_root_folder_calibre_gates : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("RootFolders").Column("ReapCalibreDuplicates").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("ReapCalibreDuplicates")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(false);
            }

            if (!Schema.Table("RootFolders").Column("AutoPushCalibreMetadata").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("AutoPushCalibreMetadata")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(false);
            }
        }
    }
}
