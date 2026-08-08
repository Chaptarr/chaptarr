using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(86)]
    public class add_download_client_file_snapshots : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("DownloadClientFileSnapshots").Exists())
            {
                Create.Table("DownloadClientFileSnapshots")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("DownloadClientId").AsInt32().NotNullable()
                    .WithColumn("DownloadId").AsString().NotNullable()
                    .WithColumn("Protocol").AsInt32().NotNullable()
                    .WithColumn("Title").AsString().Nullable()
                    .WithColumn("Category").AsString().Nullable()
                    .WithColumn("OutputPath").AsString().Nullable()
                    .WithColumn("Source").AsString(32).NotNullable()
                    .WithColumn("Confidence").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("FilePaths").AsString().NotNullable()
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("LastUpdated").AsDateTime().NotNullable();
            }

            if (!Schema.Table("DownloadClientFileSnapshots").Index("IX_DownloadClientFileSnapshots_Client_Download").Exists())
            {
                Create.Index("IX_DownloadClientFileSnapshots_Client_Download")
                    .OnTable("DownloadClientFileSnapshots")
                    .OnColumn("DownloadClientId").Ascending()
                    .OnColumn("DownloadId").Ascending()
                    .WithOptions().Unique();
            }

            if (!Schema.Table("DownloadClientFileSnapshots").Index("IX_DownloadClientFileSnapshots_LastUpdated").Exists())
            {
                Create.Index("IX_DownloadClientFileSnapshots_LastUpdated")
                    .OnTable("DownloadClientFileSnapshots")
                    .OnColumn("LastUpdated").Ascending();
            }
        }
    }
}
