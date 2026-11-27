using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Conversion jobs could only belong to a download. A conversion of files that are
    // already in the library has no download behind it, so a job now records what owns it:
    // Origin 0 is a download (every existing row), Origin 1 is a library edition. Library
    // jobs are found by their book and edition, and the download lifecycle never reads them.
    [Migration(130)]
    public class add_conversion_job_origin : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ConversionJobs").Exists())
            {
                return;
            }

            if (!Schema.Table("ConversionJobs").Column("Origin").Exists())
            {
                Alter.Table("ConversionJobs").AddColumn("Origin").AsInt32().NotNullable().WithDefaultValue(0);
            }

            if (!Schema.Table("ConversionJobs").Column("BookId").Exists())
            {
                Alter.Table("ConversionJobs").AddColumn("BookId").AsInt32().NotNullable().WithDefaultValue(0);
            }

            if (!Schema.Table("ConversionJobs").Column("EditionId").Exists())
            {
                Alter.Table("ConversionJobs").AddColumn("EditionId").AsInt32().NotNullable().WithDefaultValue(0);
            }

            if (!Schema.Table("ConversionJobs").Index("IX_ConversionJobs_Origin_EditionId").Exists())
            {
                Create.Index("IX_ConversionJobs_Origin_EditionId")
                    .OnTable("ConversionJobs")
                    .OnColumn("Origin").Ascending()
                    .OnColumn("EditionId").Ascending();
            }
        }
    }
}
