using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(83)]
    public class add_download_client_id_to_remote_path_mappings : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("RemotePathMappings").Exists() ||
                Schema.Table("RemotePathMappings").Column("DownloadClientId").Exists())
            {
                return;
            }

            Alter.Table("RemotePathMappings")
                .AddColumn("DownloadClientId")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(0);
        }
    }
}
