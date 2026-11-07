using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(121)]
    public class add_supplemental_metadata_provider_ids : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("Books").Exists() && !Schema.Table("Books").Column("AdditionalMetadataProviderId").Exists())
            {
                Alter.Table("Books")
                    .AddColumn("AdditionalMetadataProviderId")
                    .AsString(AdditionalMetadataSources.MaxProviderIdLength)
                    .Nullable();
            }

            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Column("AdditionalMetadataProviderId").Exists())
            {
                Alter.Table("Authors")
                    .AddColumn("AdditionalMetadataProviderId")
                    .AsString(AdditionalMetadataSources.MaxProviderIdLength)
                    .Nullable();
            }

            if (Schema.Table("Books").Exists() && !Schema.Table("Books").Index("IX_Books_AdditionalMetadataProviderId_MediaType").Exists())
            {
                Create.Index("IX_Books_AdditionalMetadataProviderId_MediaType")
                    .OnTable("Books")
                    .OnColumn("AdditionalMetadataProviderId").Ascending()
                    .OnColumn("MediaType").Ascending();
            }

            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Index("IX_Authors_AdditionalMetadataProviderId").Exists())
            {
                Create.Index("IX_Authors_AdditionalMetadataProviderId")
                    .OnTable("Authors")
                    .OnColumn("AdditionalMetadataProviderId");
            }
        }
    }
}
