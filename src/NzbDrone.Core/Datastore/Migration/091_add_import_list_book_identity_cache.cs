using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(91)]
    public class add_import_list_book_identity_cache : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ImportListBookIdentityCache").Exists())
            {
                Create.Table("ImportListBookIdentityCache")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("SourceProviderId").AsString().NotNullable()
                    .WithColumn("BookProviderId").AsString().NotNullable()
                    .WithColumn("AuthorProviderId").AsString().NotNullable()
                    .WithColumn("Book").AsString().Nullable()
                    .WithColumn("Author").AsString().Nullable()
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("UpdatedAt").AsDateTime().NotNullable();
            }

            if (!Schema.Table("ImportListBookIdentityCache").Index("UX_ImportListBookIdentityCache_SourceProviderId").Exists())
            {
                Create.Index("UX_ImportListBookIdentityCache_SourceProviderId")
                    .OnTable("ImportListBookIdentityCache")
                    .OnColumn("SourceProviderId").Ascending()
                    .WithOptions().Unique();
            }

            if (!Schema.Table("ImportListBookIdentityCache").Index("IX_ImportListBookIdentityCache_UpdatedAt").Exists())
            {
                Create.Index("IX_ImportListBookIdentityCache_UpdatedAt")
                    .OnTable("ImportListBookIdentityCache")
                    .OnColumn("UpdatedAt").Ascending();
            }
        }
    }
}
