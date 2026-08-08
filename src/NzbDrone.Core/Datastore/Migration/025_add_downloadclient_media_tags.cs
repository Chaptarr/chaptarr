using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(25)]
    public class add_downloadclient_media_tags : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("DownloadClients").Column("AudiobookTags").Exists())
            {
                Alter.Table("DownloadClients").AddColumn("AudiobookTags").AsString().Nullable();
            }

            if (!Schema.Table("DownloadClients").Column("EbookTags").Exists())
            {
                Alter.Table("DownloadClients").AddColumn("EbookTags").AsString().Nullable();
            }

            // Backfill from the legacy "Tags" column so existing configurations continue to work.
            Execute.Sql("UPDATE \"DownloadClients\" SET \"AudiobookTags\" = \"Tags\" WHERE \"AudiobookTags\" IS NULL AND \"Tags\" IS NOT NULL");
            Execute.Sql("UPDATE \"DownloadClients\" SET \"EbookTags\" = \"Tags\" WHERE \"EbookTags\" IS NULL AND \"Tags\" IS NOT NULL");
        }
    }
}

