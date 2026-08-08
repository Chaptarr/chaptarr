using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(76)]
    public class add_quality_profile_convert_to_quality : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("QualityProfiles").Column("ConvertToQualityId").Exists())
            {
                Alter.Table("QualityProfiles")
                    .AddColumn("ConvertToQualityId")
                    .AsInt32()
                    .Nullable();
            }

            if (Schema.Table("QualityProfiles").Column("ConvertMp3ToM4b").Exists())
            {
                // The old checkbox existed before conversion was fully wired. Reset it so users
                // deliberately re-enable conversion after reviewing the new target-quality settings.
                Update.Table("QualityProfiles")
                    .Set(new { ConvertMp3ToM4b = false, ConvertToQualityId = (int?)null })
                    .AllRows();
            }
        }
    }
}
