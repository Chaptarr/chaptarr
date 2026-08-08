using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(35)]
    public class add_hardcover_library_import_list_state : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("HardcoverLibraryImportListState")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ImportListId").AsInt32().NotNullable()
                .WithColumn("HardcoverUserId").AsInt32().NotNullable()
                .WithColumn("SettingsSignature").AsString().NotNullable()
                .WithColumn("CursorUpdatedAt").AsDateTime().Nullable()
                .WithColumn("CursorUserBookId").AsInt32().Nullable()
                .WithColumn("LastFullSyncAt").AsDateTime().Nullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("UpdatedAt").AsDateTime().Nullable();

            Create.Index()
                .OnTable("HardcoverLibraryImportListState")
                .OnColumn("ImportListId").Ascending()
                .WithOptions().Unique();
        }
    }
}

