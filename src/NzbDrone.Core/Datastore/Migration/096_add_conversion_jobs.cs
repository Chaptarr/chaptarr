using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(96)]
    public class add_conversion_jobs : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ConversionJobs").Exists())
            {
                Create.Table("ConversionJobs")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("DownloadId").AsString().NotNullable()
                    .WithColumn("Status").AsInt32().NotNullable()
                    .WithColumn("RequestJson").AsString(int.MaxValue).NotNullable()
                    .WithColumn("WorkRoot").AsString().Nullable()
                    .WithColumn("WorkFolder").AsString().Nullable()
                    .WithColumn("OutputPath").AsString().Nullable()
                    .WithColumn("TargetQualityId").AsInt32().NotNullable()
                    .WithColumn("TargetQualityName").AsString().Nullable()
                    .WithColumn("Progress").AsDecimal().Nullable()
                    .WithColumn("Message").AsString().Nullable()
                    .WithColumn("Error").AsString(int.MaxValue).Nullable()
                    .WithColumn("AttemptCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("UpdatedAt").AsDateTime().NotNullable()
                    .WithColumn("StartedAt").AsDateTime().Nullable()
                    .WithColumn("HeartbeatAt").AsDateTime().Nullable()
                    .WithColumn("CompletedAt").AsDateTime().Nullable();
            }

            if (!Schema.Table("ConversionJobs").Index("IX_ConversionJobs_DownloadId").Exists())
            {
                Create.Index("IX_ConversionJobs_DownloadId")
                    .OnTable("ConversionJobs")
                    .OnColumn("DownloadId").Ascending()
                    .WithOptions().Unique();
            }

            if (!Schema.Table("ConversionJobs").Index("IX_ConversionJobs_Status_CreatedAt").Exists())
            {
                Create.Index("IX_ConversionJobs_Status_CreatedAt")
                    .OnTable("ConversionJobs")
                    .OnColumn("Status").Ascending()
                    .OnColumn("CreatedAt").Ascending();
            }
        }
    }
}
