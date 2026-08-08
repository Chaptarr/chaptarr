using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(45)]
    public class add_copy_unmanaged_downloads_to_download_clients : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("DownloadClients").Column("CopyUnmanagedDownloads").Exists())
            {
                Alter.Table("DownloadClients")
                    .AddColumn("CopyUnmanagedDownloads")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(false);
            }
        }
    }
}

