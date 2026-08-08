using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(37)]
    public class add_owned_cursor_to_hardcover_library_import_list_state : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("HardcoverLibraryImportListState")
                .AddColumn("OwnedCursorListBookId").AsInt32().Nullable();
        }
    }
}

