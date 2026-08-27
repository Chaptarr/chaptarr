using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(103)]
    public class add_quality_profile_merge_multi_part_files : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("QualityProfiles").Column("MergeMultiPartFiles").Exists())
            {
                Alter.Table("QualityProfiles")
                    .AddColumn("MergeMultiPartFiles")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(false);
            }
        }
    }
}
