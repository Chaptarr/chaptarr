using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(125)]
    public class add_bookshelf_legacy_author_quality_profile_ids : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Authors").Column("BookshelfNGQualityProfileId").Exists())
            {
                Alter.Table("Authors")
                    .AddColumn("BookshelfNGQualityProfileId").AsInt32().Nullable();
            }

            if (!Schema.Table("Authors").Column("BookshelfNGAudiobookQualityProfileId").Exists())
            {
                Alter.Table("Authors")
                    .AddColumn("BookshelfNGAudiobookQualityProfileId").AsInt32().Nullable();
            }

            if (!Schema.Table("Authors").Column("BookshelfNGEbookQualityProfileId").Exists())
            {
                Alter.Table("Authors")
                    .AddColumn("BookshelfNGEbookQualityProfileId").AsInt32().Nullable();
            }
        }
    }
}
