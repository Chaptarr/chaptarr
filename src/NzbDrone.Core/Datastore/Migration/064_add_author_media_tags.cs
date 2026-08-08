using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(64)]
    public class add_author_media_tags : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Authors").Exists())
            {
                return;
            }

            if (!Schema.Table("Authors").Column("AudiobookTags").Exists())
            {
                Alter.Table("Authors").AddColumn("AudiobookTags").AsString().Nullable();
            }

            if (!Schema.Table("Authors").Column("EbookTags").Exists())
            {
                Alter.Table("Authors").AddColumn("EbookTags").AsString().Nullable();
            }

            // Backfill from the legacy "Tags" column so existing configurations continue to work.
            // Only copy tags onto media types that are actually configured (i.e., have a root folder path).
            if (Schema.Table("Authors").Column("AudiobookRootFolderPath").Exists())
            {
                Execute.Sql("UPDATE \"Authors\" SET \"AudiobookTags\" = \"Tags\" WHERE \"AudiobookTags\" IS NULL AND \"Tags\" IS NOT NULL AND \"AudiobookRootFolderPath\" IS NOT NULL AND TRIM(\"AudiobookRootFolderPath\") != ''");
            }

            if (Schema.Table("Authors").Column("EbookRootFolderPath").Exists())
            {
                Execute.Sql("UPDATE \"Authors\" SET \"EbookTags\" = \"Tags\" WHERE \"EbookTags\" IS NULL AND \"Tags\" IS NOT NULL AND \"EbookRootFolderPath\" IS NOT NULL AND TRIM(\"EbookRootFolderPath\") != ''");
            }
        }
    }
}
