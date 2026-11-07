using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(122)]
    public class add_bookshelf_legacy_identity_map : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("BookshelfLegacyIdentityMap").Exists())
            {
                Create.Table("BookshelfLegacyIdentityMap")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("Source").AsString(32).NotNullable()
                    .WithColumn("EntityType").AsString(32).NotNullable()
                    .WithColumn("SourceId").AsInt32().NotNullable()
                    .WithColumn("TargetId").AsInt32().NotNullable()
                    .WithColumn("MediaType").AsInt32().Nullable();
            }

            if (!Schema.Table("BookshelfLegacyIdentityMap").Index("IX_BookshelfLegacyIdentityMap_Unique").Exists())
            {
                Create.Index("IX_BookshelfLegacyIdentityMap_Unique")
                    .OnTable("BookshelfLegacyIdentityMap")
                    .OnColumn("Source").Ascending()
                    .OnColumn("EntityType").Ascending()
                    .OnColumn("SourceId").Ascending()
                    .OnColumn("TargetId").Ascending()
                    .WithOptions().Unique();
            }

            if (!Schema.Table("BookshelfLegacyIdentityMap").Index("IX_BookshelfLegacyIdentityMap_Lookup").Exists())
            {
                Create.Index("IX_BookshelfLegacyIdentityMap_Lookup")
                    .OnTable("BookshelfLegacyIdentityMap")
                    .OnColumn("Source").Ascending()
                    .OnColumn("EntityType").Ascending()
                    .OnColumn("SourceId").Ascending();
            }
        }
    }
}
