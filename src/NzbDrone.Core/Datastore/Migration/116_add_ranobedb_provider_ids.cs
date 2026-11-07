using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(116)]
    public class add_ranobedb_provider_ids : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("Books").Exists() && !Schema.Table("Books").Column("RanobeDbBookId").Exists())
            {
                Alter.Table("Books")
                    .AddColumn("RanobeDbBookId")
                    .AsString(128)
                    .Nullable();
            }

            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Column("RanobeDbAuthorId").Exists())
            {
                Alter.Table("Authors")
                    .AddColumn("RanobeDbAuthorId")
                    .AsString(128)
                    .Nullable();
            }

            if (Schema.Table("Books").Exists() && !Schema.Table("Books").Index("UX_Books_RanobeDbBookId_MediaType").Exists())
            {
                Create.Index("UX_Books_RanobeDbBookId_MediaType")
                    .OnTable("Books")
                    .OnColumn("RanobeDbBookId").Ascending()
                    .OnColumn("MediaType").Ascending()
                    .WithOptions().Unique();
            }

            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Index("UX_Authors_RanobeDbAuthorId").Exists())
            {
                Create.Index("UX_Authors_RanobeDbAuthorId")
                    .OnTable("Authors")
                    .OnColumn("RanobeDbAuthorId").Ascending()
                    .WithOptions().Unique();
            }
        }
    }
}
